using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Localization;
using ChameLab.Models;
using ChameLab.Services;
using ChameLab.Transport;

namespace ChameLab.Sync;

public enum SyncStatus
{
    /// <summary>This device is in no group.</summary>
    Off,
    /// <summary>In a group, but not connecting now (the app is in the background, or sync is paused).</summary>
    Paused,
    /// <summary>Looking for a relay.</summary>
    Connecting,
    /// <summary>In the group's room (other devices may or may not be there).</summary>
    Online,
    /// <summary>No relay could be reached; the local network still works.</summary>
    Offline,
}

/// <summary>A device of the group as the devices list shows it.</summary>
public sealed record SyncMember(string Id, string Name, bool IsThis, bool IsOnline, string? Link, DateTimeOffset? LastSeen, string? Platform = null);

/// <summary>An entry of a card's or dictionary's history, for the history list.</summary>
public sealed record SyncHistoryItem(Hlc Time, string Device, IReadOnlyList<string> Fields, bool Deleted);

/// <summary>A deleted card or dictionary that can be brought back.</summary>
public sealed record SyncDeletedItem(string Id, string Name, DateTimeOffset Deleted);

/// <summary>
/// Device sync: keeps the card and dictionary libraries the same on all devices of a group. The
/// data lives only on the devices; relays bring devices together (and devices on the same network
/// find each other directly), and everything between devices is encrypted end to end.
/// <para>
/// The library is edited on the UI thread; that is where local changes are captured and merged
/// state is put back (<see cref="Ui"/>). Network work runs on the thread pool. The document is
/// guarded by one lock.
/// </para>
/// </summary>
public sealed class SyncService : ISyncHost, IAsyncDisposable
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Runs an action on the thread that edits the library (the app sets the UI dispatcher).</summary>
    public static Action<Action> Ui { get; set; } = SerialQueue.Post;

    /// <summary>The local network finder; null where there is none (the browser).</summary>
    public static Func<ILanDiscovery?> LanDiscovery { get; set; } =
        () => OperatingSystem.IsBrowser() ? null : new MdnsDiscovery();

    /// <summary>Android keeps multicast (mDNS) packets only while asked to; the head sets this.</summary>
    public static Action<bool>? MulticastLock { get; set; }

    public const int MaxDeviceNameLength = 64;

    /// <summary>The relays to use (all of them; the room picks one by its id).</summary>
    public static Func<IReadOnlyList<RelayServer>> Relays { get; set; } = () => RelayEndpoint.Servers;

    /// <summary>What this device is called in other devices' lists unless renamed.</summary>
    public static string DefaultDeviceName { get; set; } = DefaultName();

    /// <summary>Platform tag reported to sync peers (windows, linux, macos, android, ios, browser).</summary>
    public static string CurrentPlatform { get; set; } = DetectPlatform();

    private readonly object _lock = new();
    private readonly SyncStorage _storage;
    private readonly IKeyValueStore _store;
    private readonly ISyncedLibrary[] _sources;
    private LibrarySync[] _libraries;
    private readonly ConcurrentDictionary<string, SessionEntry> _sessions = new();
    private readonly Dictionary<string, DateTimeOffset> _lastSeen = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lanTried = new();

    private DeviceIdentity? _device;
    private SyncDocument? _doc;
    private CancellationTokenSource? _run;
    private CancellationTokenSource? _roomCts;
    private string? _roomSecret;
    private LanListener? _lan;
    private ILanDiscovery? _discovery;
    private bool _applying;
    private bool _joining;
    private int _materializePending;
    private TaskCompletionSource? _caughtUp;
    private Dictionary<string, long>? _catchUpTo;

    public SyncService(IKeyValueStore store, CardLibrary cards, DictionaryLibrary dictionaries)
    {
        _store = store;
        _storage = new SyncStorage(store);
        _sources = [new CardLibrarySync(cards), new DictionaryLibrarySync(dictionaries)];
        _libraries = _sources.Select(s => new LibrarySync(s)).ToArray();
        foreach (var source in _sources) source.Changed += OnLibraryChanged;
        LoadLastSeen();
        try
        {
            _device = _storage.LoadDevice();
            if (_device is not null) _doc = _storage.LoadDocument(_device.Id);
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException or IOException)
        {
            Error = ex.Message;
            _device = null;
            _doc = null;
        }
        Status = _device is null ? SyncStatus.Off : SyncStatus.Paused;
        if (_doc is not null) Ui(() => Capture(CaptureMode.Startup, materialize: true));
    }

    /// <summary>Raised on the UI thread when the status, the devices or the connections change.</summary>
    public event Action? Changed;

    public bool IsEnabled => _device is not null;

    public SyncStatus Status { get; private set; } = SyncStatus.Off;

    /// <summary>Why the last attempt failed, for the status line.</summary>
    public string? Error { get; private set; }

    public string? DeviceName => _device?.Name;

    public string? DeviceId => _device?.Id;

    /// <summary>Whether the background node (tray, start with the system) is wanted; desktop only.</summary>
    public static bool RunInBackground
    {
        get => AppSettings.Get("syncBackground") is JsonValue v && v.TryGetValue<bool>(out var on) ? on : true;
        set => AppSettings.Set("syncBackground", value);
    }

    // ---- Devices -------------------------------------------------------------

    public IReadOnlyList<SyncMember> Members
    {
        get
        {
            lock (_lock)
            {
                if (_doc is null || _device is null) return Array.Empty<SyncMember>();
                return _doc.Entities
                    .Where(e => e.Type == EntityTypes.Member && !e.IsDeleted && e.Value("revoked") is null)
                    .Select(e =>
                    {
                        var online = _sessions.TryGetValue(e.Id, out var s);
                        var platform = CleanPlatform((string?)e.Value("platform"))
                            ?? (e.Id == _device.Id ? CleanPlatform(CurrentPlatform) : null);
                        return new SyncMember(e.Id, (string?)e.Value("name") ?? "?", e.Id == _device.Id,
                            e.Id == _device.Id || online, online ? s!.Session.Kind : null,
                            e.Id == _device.Id ? null : _lastSeen.TryGetValue(e.Id, out var seen) ? seen : null,
                            platform);
                    })
                    .OrderByDescending(m => m.IsThis)
                    .ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }
        }
    }

    /// <summary>Devices connected now.</summary>
    public int ConnectedCount => _sessions.Count;

    public void Rename(string name)
    {
        name = SanitizeDeviceName(name);
        if (_device is null || name.Length == 0 || string.Equals(name, _device.Name, StringComparison.Ordinal)) return;
        _device = _device with { Name = name };
        _storage.SaveDevice(_device);
        Commit(doc => doc.Change(EntityTypes.Member, _device.Id, new Dictionary<string, JsonNode?> { ["name"] = name }));
    }

    /// <summary>
    /// Removes a device from the group: it gets nothing new, and the group moves to a new room so it
    /// cannot even find it. What it already has stays on it.
    /// </summary>
    public void Remove(string deviceId)
    {
        if (_device is null || deviceId == _device.Id) return;
        Commit(doc =>
        {
            doc.Change(EntityTypes.Member, deviceId, new Dictionary<string, JsonNode?> { ["revoked"] = doc.Seq(deviceId) });
            return doc.Change(EntityTypes.Group, "group", new Dictionary<string, JsonNode?> { ["room"] = SyncCrypto.B64(SyncCrypto.Random(32)) });
        });
        if (_sessions.TryRemove(deviceId, out var entry)) entry.Cancel();
        CheckRoom();
    }

    // ---- Group ---------------------------------------------------------------

    /// <summary>Starts a group with this device and its library.</summary>
    public void CreateGroup(string name)
    {
        if (_device is not null) throw new InvalidOperationException(Strings.Sync_AlreadyInGroup);
        var device = new DeviceIdentity(SyncCrypto.B64(SyncCrypto.Random(16)), Clean(name), KeyPair.Generate());
        var doc = new SyncDocument(device.Id);
        doc.Change(EntityTypes.Group, "group", new Dictionary<string, JsonNode?> { ["room"] = SyncCrypto.B64(SyncCrypto.Random(32)) });
        doc.Change(EntityTypes.Member, device.Id, MemberFields(device.Name, device.Key.Public, CurrentPlatform));
        _storage.SaveDevice(device);
        _storage.SaveSnapshot(doc);
        lock (_lock)
        {
            _libraries = _sources.Select(s => new LibrarySync(s)).ToArray();
            _device = device;
            _doc = doc;
        }
        Capture(CaptureMode.Normal, materialize: false);
        lock (_lock) _storage.SaveSnapshot(doc);
        Start();
    }

    /// <summary>Leaves the group: tells the others (when connected), stops and forgets it here. The library stays.</summary>
    public async Task LeaveAsync()
    {
        if (_device is not { } device) return;
        Commit(doc => doc.Change(EntityTypes.Member, device.Id, new Dictionary<string, JsonNode?> { ["revoked"] = doc.Seq(device.Id) + 1 }));
        // Give connected devices a moment to get the news.
        if (!_sessions.IsEmpty) await Task.Delay(TimeSpan.FromSeconds(1));
        await StopAsync();
        lock (_lock)
        {
            _storage.Clear();
            _device = null;
            _doc = null;
            _libraries = _sources.Select(s => new LibrarySync(s)).ToArray();
        }
        Status = SyncStatus.Off;
        RaiseChanged();
    }

    private static Dictionary<string, JsonNode?> MemberFields(string name, byte[] key, string? platform = null)
    {
        var fields = new Dictionary<string, JsonNode?>
        {
            ["name"] = name,
            ["key"] = SyncCrypto.B64(key),
            ["added"] = DateTimeOffset.UtcNow.ToString("O"),
        };
        if (CleanPlatform(platform) is { } p) fields["platform"] = p;
        return fields;
    }

    /// <summary>
    /// Cleans a device name: trims, collapses whitespace runs to single spaces, removes control,
    /// format, replacement and filename-illegal characters, and cuts to <see cref="MaxDeviceNameLength"/> UTF-16 chars.
    /// </summary>
    public static string SanitizeDeviceName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var sb = new StringBuilder(Math.Min(name.Length, MaxDeviceNameLength));
        Span<char> buf = stackalloc char[2];
        var pendingSpace = false;
        foreach (var rune in name.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                if (sb.Length > 0) pendingSpace = true;
                continue;
            }
            if (IsIllegalDeviceNameRune(rune)) continue;
            if (pendingSpace)
            {
                if (sb.Length + 1 > MaxDeviceNameLength) break;
                sb.Append(' ');
                pendingSpace = false;
            }
            var len = rune.EncodeToUtf16(buf);
            if (sb.Length + len > MaxDeviceNameLength) break;
            sb.Append(buf[..len]);
        }
        return sb.ToString().TrimEnd();
    }

    private static bool IsIllegalDeviceNameRune(Rune rune) =>
        Rune.IsControl(rune)
        || Rune.GetUnicodeCategory(rune) is UnicodeCategory.Format or UnicodeCategory.Control
            or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse
        || rune.Value is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' or 0xFFFD;

    private static string Clean(string? name) =>
        SanitizeDeviceName(name) is { Length: > 0 } clean ? clean
        : SanitizeDeviceName(DefaultDeviceName) is { Length: > 0 } fallback ? fallback
        : "ChameLab";

    private static string DetectPlatform()
    {
        if (OperatingSystem.IsWindows()) return "windows";
        if (OperatingSystem.IsMacOS()) return "macos";
        if (OperatingSystem.IsLinux()) return "linux";
        if (OperatingSystem.IsAndroid()) return "android";
        if (OperatingSystem.IsIOS()) return "ios";
        if (OperatingSystem.IsBrowser()) return "browser";
        return "unknown";
    }

    private static string? CleanPlatform(string? platform) => platform?.Trim().ToLowerInvariant() switch
    {
        "windows" or "linux" or "macos" or "android" or "ios" or "browser" => platform.Trim().ToLowerInvariant(),
        _ => null,
    };

    private static string DefaultName()
    {
        if (OperatingSystem.IsAndroid()) return "Android";
        if (OperatingSystem.IsIOS()) return "iPhone";
        if (OperatingSystem.IsBrowser()) return "Browser";
        try { return Environment.MachineName; }
        catch { return "ChameLab"; }
    }

    // ---- Running ---------------------------------------------------------------

    /// <summary>Connects (relay room and local network) and keeps connecting until stopped.</summary>
    public void Start()
    {
        if (_device is null || _run is not null || _joining) return;
        EnsurePlatform();
        _run = new CancellationTokenSource();
        var ct = _run.Token;
        MulticastLock?.Invoke(true);
        StartLan(ct);
        _ = Task.Run(() => RoomLoopAsync(ct));
        Status = SyncStatus.Connecting;
        RaiseChanged();
    }

    /// <summary>Disconnects (the app went to the background on a phone, or the group is being left).</summary>
    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _run, null) is not { } run) return;
        run.Cancel();
        _roomCts?.Cancel();
        foreach (var entry in _sessions.Values) entry.Cancel();
        _sessions.Clear();
        _discovery?.Dispose();
        _discovery = null;
        _lan?.Dispose();
        _lan = null;
        MulticastLock?.Invoke(false);
        if (_device is not null) Status = SyncStatus.Paused;
        RaiseChanged();
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private string? RoomSecret()
    {
        lock (_lock) return (string?)_doc?.Find(EntityTypes.Group, "group")?.Value("room");
    }

    private static string RoomId(string secret) =>
        SyncCrypto.B64(SyncCrypto.Hkdf(SyncCrypto.FromB64(secret), [], "chamelab-sync/room/v1", 32));

    private async Task RoomLoopAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested)
        {
            var secret = RoomSecret();
            if (secret is null) return;
            _roomSecret = secret;
            using var roomCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _roomCts = roomCts;
            try
            {
                SetStatus(SyncStatus.Connecting, null);
                var roomId = RoomId(secret);
                var server = await PickRelayAsync(roomId, roomCts.Token);
                await using var room = await RelayRoom.JoinAsync(server, roomId, roomCts.Token);
                SetStatus(SyncStatus.Online, null);
                delay = TimeSpan.FromSeconds(2);
                await foreach (var arrival in room.Arrivals.ReadAllAsync(roomCts.Token))
                    _ = Task.Run(() => ArrivedAsync(arrival.Link, arrival.Initiate, roomCts.Token));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The room changed (a device was removed): go to the new one right away.
                continue;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                SetStatus(SyncStatus.Offline, ex.Message);
            }
            if (ct.IsCancellationRequested) return;
            SetStatus(SyncStatus.Offline, Error);
            try { await Task.Delay(delay, ct); }
            catch (OperationCanceledException) { return; }
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60));
        }
    }

    /// <summary>
    /// The relay of the room: every device orders the relays by a hash of the room id and region
    /// (rendezvous hashing), so all of them pick the same one without talking, and moves on to
    /// the next one only when it is down.
    /// </summary>
    private static async Task<RelayServer> PickRelayAsync(string roomId, CancellationToken ct)
    {
        var ordered = Relays()
            .GroupBy(s => s.Region, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .OrderBy(s => Convert.ToHexString(SyncCrypto.Sha256(Encoding.UTF8.GetBytes($"{roomId}|{s.Region}"))), StringComparer.Ordinal)
            .ToList();
        foreach (var server in ordered)
            if (await RelayEndpoint.IsUpAsync(server, ct)) return server;
        throw new IOException(Strings.Relay_NoServers);
    }

    private void SetStatus(SyncStatus status, string? error)
    {
        if (Status == status && Error == error) return;
        Status = status;
        Error = error;
        RaiseChanged();
    }

    private void EnsurePlatform()
    {
        if (CleanPlatform(CurrentPlatform) is not { } platform) return;
        string? deviceId;
        lock (_lock)
        {
            if (_device is null || _doc is null) return;
            var current = (string?)_doc.Find(EntityTypes.Member, _device.Id)?.Value("platform");
            if (string.Equals(current, platform, StringComparison.Ordinal)) return;
            deviceId = _device.Id;
        }
        Commit(doc => _device?.Id == deviceId
            ? doc.Change(EntityTypes.Member, deviceId, new Dictionary<string, JsonNode?> { ["platform"] = platform })
            : null);
    }

    /// <summary>Room changed (a device was removed): reconnect to the new one, announce the new one locally.</summary>
    private void CheckRoom()
    {
        var secret = RoomSecret();
        if (secret is null || secret == _roomSecret) return;
        _roomCts?.Cancel();
        _discovery?.Update(Announcement(secret));
    }

    // ---- Local network ---------------------------------------------------------

    private void StartLan(CancellationToken ct)
    {
        if (OperatingSystem.IsBrowser()) return;
        try
        {
            _lan = new LanListener(link => ArrivedAsync(link, initiate: false, ct));
            _discovery = LanDiscovery();
            if (_discovery is null || RoomSecret() is not { } secret) return;
            _discovery.Found += announcement => _ = Task.Run(() => FoundAsync(announcement, ct));
            _discovery.Start(_lan.Port, Announcement(secret));
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            Console.WriteLine($"[Sync] local network unavailable: {ex.Message}");
        }
    }

    /// <summary>
    /// What the mDNS announcement says: a random nonce and its HMAC under the room secret, so members
    /// recognize their group while others cannot even tell two announcements of one group apart.
    /// </summary>
    private static Dictionary<string, string> Announcement(string secret)
    {
        var nonce = SyncCrypto.Random(8);
        return new Dictionary<string, string>
        {
            ["v"] = "1",
            ["n"] = SyncCrypto.B64(nonce),
            ["h"] = SyncCrypto.B64(SyncCrypto.HmacSha256(SyncCrypto.FromB64(secret), nonce)[..12]),
        };
    }

    private async Task FoundAsync(LanAnnouncement announcement, CancellationToken ct)
    {
        if (RoomSecret() is not { } secret) return;
        try
        {
            if (!announcement.Text.TryGetValue("n", out var n) || !announcement.Text.TryGetValue("h", out var h)) return;
            var expected = SyncCrypto.HmacSha256(SyncCrypto.FromB64(secret), SyncCrypto.FromB64(n))[..12];
            if (!SyncCrypto.Equal(expected, SyncCrypto.FromB64(h))) return;
        }
        catch (FormatException) { return; }
        // Everyone already here over the local network: nothing to gain.
        var others = Members.Count(m => !m.IsThis);
        if (others > 0 && _sessions.Values.Count(s => s.Session.Kind == "lan") >= others) return;
        var key = string.Join(",", announcement.Endpoints);
        var now = DateTimeOffset.UtcNow;
        if (_lanTried.TryGetValue(key, out var last) && now - last < TimeSpan.FromSeconds(30)) return;
        _lanTried[key] = now;
        if (await LanListener.ConnectAsync(announcement.Endpoints, TimeSpan.FromSeconds(3), ct) is { } link)
            await ArrivedAsync(link, initiate: true, ct);
    }

    // ---- Sessions --------------------------------------------------------------

    private sealed class SessionEntry(SyncSession session, string initiator, CancellationTokenSource cts,
        TaskCompletionSource? joined = null)
    {
        public SyncSession Session => session;
        public string Initiator => initiator;
        public bool IsJoining => joined is not null && !joined.Task.IsCompletedSuccessfully;
        public void Cancel()
        {
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    private async Task ArrivedAsync(IPacketLink link, bool initiate, CancellationToken ct)
    {
        if (_device is not { } device) { await link.DisposeAsync(); return; }
        SecureChannel channel;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(HandshakeTimeout);
            channel = initiate
                ? await SecureChannel.ConnectAsync(link, device.Key, new JsonObject { ["p"] = "sync" }, timeout.Token)
                : await SecureChannel.AcceptAsync(link, device.Key, timeout.Token);
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or OperationCanceledException
                                       or System.Net.WebSockets.WebSocketException or System.Text.Json.JsonException)
        {
            await link.DisposeAsync();
            return;
        }
        if (!initiate && (string?)channel.Hello?["p"] != "sync")
        {
            await channel.DisposeAsync();
            return;
        }
        var key = SyncCrypto.B64(channel.RemoteKey);
        string? member;
        lock (_lock)
            member = _doc?.Entities.FirstOrDefault(e => e.Type == EntityTypes.Member && !e.IsDeleted
                                                        && e.Value("revoked") is null && e.Id != device.Id
                                                        && (string?)e.Value("key") == key)?.Id;
        if (member is null)
        {
            await channel.DisposeAsync();
            return;
        }
        await RunSessionAsync(channel, member, initiate ? device.Id : member, ct);
    }

    /// <summary>Runs a sync session with a member; keeps one connection per device (local network first).</summary>
    private async Task RunSessionAsync(SecureChannel channel, string member, string initiator, CancellationToken ct,
        TaskCompletionSource? joined = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var session = new SyncSession(channel, member, this, () => joined?.TrySetResult());
        var entry = new SessionEntry(session, initiator, cts, joined);
        if (!Register(member, entry))
        {
            joined?.TrySetException(new IOException(Strings.Sync_PairingFailed));
            await channel.DisposeAsync();
            return;
        }
        RaiseChanged();
        try
        {
            await session.RunAsync(cts.Token);
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or OperationCanceledException
                                       or System.Net.WebSockets.WebSocketException or System.Text.Json.JsonException
                                       or System.Net.Sockets.SocketException or ObjectDisposedException)
        {
            // The connection ended.
        }
        finally
        {
            joined?.TrySetException(new IOException(Strings.Sync_PairingFailed));
            _sessions.TryRemove(KeyValuePair.Create(member, entry));
            await channel.DisposeAsync();
            lock (_lock) _lastSeen[member] = DateTimeOffset.UtcNow;
            SaveLastSeen();
            RaiseChanged();
        }
    }

    private bool Register(string member, SessionEntry entry)
    {
        while (true)
        {
            if (_sessions.TryAdd(member, entry)) return true;
            if (!_sessions.TryGetValue(member, out var existing)) continue;
            if (!Prefer(entry, existing)) return false;
            if (_sessions.TryUpdate(member, entry, existing))
            {
                existing.Cancel();
                return true;
            }
        }
    }

    /// <summary>A local link beats a relayed one; between two of a kind, both devices keep the one the smaller device id opened.</summary>
    private static bool Prefer(SessionEntry candidate, SessionEntry existing)
    {
        // Let the final pairing acknowledgment arrive before replacing its channel with a room/LAN link.
        if (existing.IsJoining) return false;
        var (a, b) = (candidate.Session.Kind == "lan", existing.Session.Kind == "lan");
        if (a != b) return a;
        return string.CompareOrdinal(candidate.Initiator, existing.Initiator) < 0;
    }

    private void PushAll()
    {
        foreach (var entry in _sessions.Values)
            _ = Task.Run(async () =>
            {
                try { await entry.Session.PushAsync(CancellationToken.None); }
                catch (Exception ex) when (ex is IOException or CryptographicException or OperationCanceledException
                                               or System.Net.WebSockets.WebSocketException or ObjectDisposedException
                                               or System.Net.Sockets.SocketException)
                {
                    entry.Cancel();
                }
            });
    }

    // ---- ISyncHost ---------------------------------------------------------------

    public T Locked<T>(Func<SyncDocument, T> action)
    {
        lock (_lock) return action(_doc ?? throw new ObjectDisposedException(nameof(SyncService)));
    }

    public void Received(IReadOnlyList<SyncOp> applied, bool snapshot)
    {
        lock (_lock)
        {
            if (_doc is null) return;
            if (snapshot) _storage.SaveSnapshot(_doc);
            else _storage.Append(applied);
            Compact();
            if (_catchUpTo is not null && _doc.Vector.Count > 0
                && _catchUpTo.All(v => _doc.Seq(v.Key) >= v.Value))
                _caughtUp?.TrySetResult();
            // Removed devices lose their connection.
            foreach (var (member, entry) in _sessions)
                if (_doc.RevokedAfter(member) is not null) { _sessions.TryRemove(member, out _); entry.Cancel(); }
        }
        CheckRoom();
        if (!_joining) ScheduleMaterialize();
        PushAll();
    }

    /// <summary>Writes a snapshot once the log on disk grows long; keeps the most recent operations in memory for peers.</summary>
    private void Compact()
    {
        if (_doc is null || _storage.Logged < 1000) return;
        _storage.SaveSnapshot(_doc);
        _doc.TrimLog(5000);
    }

    // ---- Library ---------------------------------------------------------------

    private void OnLibraryChanged()
    {
        if (_applying || _joining || _doc is null) return;
        Capture(CaptureMode.Normal, materialize: false);
    }

    /// <summary>Records local edits as operations, saves them, sends them; then optionally shows the merged state.</summary>
    private void Capture(CaptureMode mode, bool materialize)
    {
        List<SyncOp> ops;
        lock (_lock)
        {
            if (_doc is null) return;
            ops = _libraries.SelectMany(l => l.Capture(_doc, mode)).ToList();
            _storage.Append(ops);
            Compact();
            if (materialize)
            {
                _applying = true;
                try { foreach (var library in _libraries) library.Materialize(_doc); }
                finally { _applying = false; }
            }
        }
        if (ops.Count > 0) PushAll();
        if (materialize) RaiseChanged();
    }

    private void ScheduleMaterialize()
    {
        if (Interlocked.Exchange(ref _materializePending, 1) == 1) return;
        Ui(() =>
        {
            Interlocked.Exchange(ref _materializePending, 0);
            Capture(CaptureMode.Normal, materialize: true);
        });
    }

    /// <summary>Makes a change through the document (not the library), saves and sends it, and shows it.</summary>
    private void Commit(Func<SyncDocument, SyncOp?> change)
    {
        lock (_lock)
        {
            if (_doc is null) return;
            var before = _doc.Log.Count;
            change(_doc);
            _storage.Append(_doc.Log.Skip(before).ToList());
        }
        ScheduleMaterialize();
        PushAll();
        RaiseChanged();
    }

    // ---- History ---------------------------------------------------------------

    public IReadOnlyList<SyncHistoryItem> History(string type, string id)
    {
        lock (_lock)
        {
            if (_doc is null) return Array.Empty<SyncHistoryItem>();
            var names = _doc.Entities.Where(e => e.Type == EntityTypes.Member)
                .ToDictionary(e => e.Id, e => (string?)e.Value("name") ?? "?");
            return _doc.History(type, id)
                .Select(h => new SyncHistoryItem(h.Time, names.GetValueOrDefault(h.Op.Author, "?"),
                    h.Fields.Where(f => f is not ("order")).ToList(), h.Deleted))
                .Where(h => h.Deleted || h.Fields.Count > 0)
                .ToList();
        }
    }

    /// <summary>Puts a card or dictionary back as it was right after <paramref name="time"/>.</summary>
    public void Restore(string type, string id, Hlc time) =>
        Commit(doc => doc.Change(type, id, doc.ValuesAt(type, id, time).ToDictionary(p => p.Key, p => p.Value)));

    public IReadOnlyList<SyncDeletedItem> Deleted(string type)
    {
        lock (_lock)
        {
            if (_doc is null) return Array.Empty<SyncDeletedItem>();
            return _doc.Deleted(type)
                .Select(e => new SyncDeletedItem(e.Id, (string?)e.Value("name") ?? e.Id, e.Delete!.Time.WallTime))
                .ToList();
        }
    }

    public void Undelete(string type, string id) =>
        Commit(doc => doc.Find(type, id) is { } e ? doc.Change(type, id, e.Values().ToDictionary(p => p.Key, p => p.Value)) : null);

    // ---- Adding a device: the device already in the group ---------------------

    /// <summary>Shows a code / QR and waits for the new device (see <see cref="Invitation"/>).</summary>
    public async Task<Invitation> InviteAsync(CancellationToken ct)
    {
        if (_device is not { } device) throw new InvalidOperationException();
        var rendezvous = await Rendezvous.OpenAsync("join", device.Key, Relays(), ct);
        return new Invitation(this, rendezvous);
    }

    /// <summary>A new device asking to join: its name, and the digits to compare unless the QR proved it.</summary>
    public sealed record JoinRequest(string Name, string? Digits);

    public sealed class Invitation : IAsyncDisposable
    {
        private readonly SyncService _service;
        private readonly Rendezvous _rendezvous;
        private SecureChannel? _channel;
        private JsonObject? _join;

        internal Invitation(SyncService service, Rendezvous rendezvous)
        {
            _service = service;
            _rendezvous = rendezvous;
        }

        public string? TypedCode => _rendezvous.TypedCode;
        public string Qr => _rendezvous.Qr;

        /// <summary>Waits for a device to connect and introduce itself.</summary>
        public async Task<JoinRequest> WaitAsync(CancellationToken ct)
        {
            _channel = await _rendezvous.WaitAsync(ct);
            _join = await _channel.ReceiveAsync(ct);
            if (_join is null || (string?)_join["type"] != "join" || (string?)_join["device"] is not { Length: > 0 }
                || (string?)_join["key"] is not { } key || !SyncCrypto.Equal(SyncCrypto.FromB64(key), _channel.RemoteKey))
                throw new IOException(Strings.Sync_PairingFailed);
            var proven = _rendezvous.IsProven(_channel, _join);
            return new JoinRequest(Clean((string?)_join["name"]), proven ? null : _channel.ShortAuthenticationString());
        }

        /// <summary>Lets the device in and waits for it to finish joining; an interrupted join is revoked.</summary>
        public async Task AcceptAsync(CancellationToken ct)
        {
            if (_channel is null || _join is null || _service._device is not { } device) throw new InvalidOperationException();
            ct.ThrowIfCancellationRequested();
            var id = (string)_join["device"]!;
            if (_service.Locked(doc => doc.Find(EntityTypes.Member, id) is not null))
                throw new IOException(Strings.Sync_PairingFailed);
            var channel = _channel;
            var joined = (bool?)_join["confirmJoin"] == true
                ? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) : null;
            Task? session = null;
            try
            {
                _service.Commit(doc => doc.Change(EntityTypes.Member, id,
                    MemberFields(Clean((string?)_join["name"]), channel.RemoteKey, CleanPlatform((string?)_join["platform"]))));
                var vector = _service.Locked(doc => SyncDocument.VectorJson(doc.Vector));
                var welcome = new JsonObject { ["type"] = "welcome", ["device"] = device.Id, ["vector"] = vector };
                if (joined is not null) welcome["confirmJoin"] = true;
                await channel.SendAsync(welcome, ct);
                session = Task.Run(() => _service.RunSessionAsync(channel, id, id,
                    _service._run?.Token ?? CancellationToken.None, joined));
                if (joined is not null) await joined.Task.WaitAsync(TimeSpan.FromMinutes(5), ct);
                _channel = null;
            }
            catch
            {
                // The device may already have received the room secret, so revoke it and rotate the room.
                _service.Remove(id);
                await channel.DisposeAsync();
                if (session is not null) await session;
                throw;
            }
        }

        public async Task RejectAsync()
        {
            if (_channel is null) return;
            try { await _channel.SendAsync(new JsonObject { ["type"] = "refused" }, CancellationToken.None); }
            catch (Exception ex) when (ex is IOException or System.Net.WebSockets.WebSocketException) { }
            await _channel.DisposeAsync();
            _channel = null;
        }

        public async ValueTask DisposeAsync()
        {
            if (_channel is not null) await _channel.DisposeAsync();
            await _rendezvous.DisposeAsync();
        }
    }

    // ---- Adding a device: the new device ---------------------------------------

    /// <summary>
    /// Joins the group of the device showing <paramref name="code"/>. <paramref name="showDigits"/>
    /// gets the six digits to compare when the code was typed (a scanned QR needs none). Returns once
    /// this device has the group's data and its own library is merged in.
    /// </summary>
    public async Task JoinAsync(string code, string name, Action<string>? showDigits, CancellationToken ct)
    {
        if (_device is not null) throw new InvalidOperationException(Strings.Sync_AlreadyInGroup);
        var pairing = PairingCode.Parse(code) ?? throw new FormatException(Strings.Sync_NotACode);
        var device = new DeviceIdentity(SyncCrypto.B64(SyncCrypto.Random(16)), Clean(name), KeyPair.Generate());
        var (channel, verified) = await Rendezvous.ConnectAsync(pairing, "join", device.Key, Relays(), ct);
        using var sessionStop = new CancellationTokenSource();
        Task? session = null;
        try
        {
            var join = new JsonObject
            {
                ["type"] = "join",
                ["device"] = device.Id,
                ["name"] = device.Name,
                ["key"] = SyncCrypto.B64(device.Key.Public),
                ["confirmJoin"] = true,
            };
            if (CleanPlatform(CurrentPlatform) is { } platform) join["platform"] = platform;
            if (pairing.Secret is { } secret) join["proof"] = PairingCode.Proof(secret, channel);
            await channel.SendAsync(join, ct);
            if (!verified) showDigits?.Invoke(channel.ShortAuthenticationString());
            var welcome = await channel.ReceiveAsync(ct);
            if ((string?)welcome?["type"] != "welcome" || (string?)welcome["device"] is not { Length: > 0 } inviter)
                throw new IOException(Strings.Sync_Refused);

            // Take the group's data first, then merge this device's library into it.
            var target = SyncDocument.ReadVector(welcome["vector"]);
            lock (_lock)
            {
                _joining = true;
                _libraries = _sources.Select(s => new LibrarySync(s)).ToArray();
                _device = device;
                _doc = new SyncDocument(device.Id);
                _catchUpTo = target;
                _caughtUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            _storage.SaveDevice(device);
            // The session outlives this call: it is the first connection to the group.
            session = Task.Run(() => RunSessionAsync(channel, inviter, device.Id, sessionStop.Token));
            await _caughtUp.Task.WaitAsync(TimeSpan.FromMinutes(5), ct);
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // Cancellation wins until UI finalization starts. Once saving/merging begins,
            // complete the join and its acknowledgment so both devices make the same decision.
            var finalization = 0;
            using var cancelFinalization = ct.Register(() =>
            {
                if (Interlocked.CompareExchange(ref finalization, 2, 0) == 0) done.TrySetCanceled(ct);
            });
            Ui(() =>
            {
                if (Interlocked.CompareExchange(ref finalization, 1, 0) != 0) return;
                try
                {
                    lock (_lock) _joining = false;
                    Capture(CaptureMode.Joining, materialize: true);
                    lock (_lock) _storage.SaveSnapshot(_doc!);
                    done.TrySetResult();
                }
                catch (Exception ex) { done.TrySetException(ex); }
            });
            await done.Task;
            if ((bool?)welcome["confirmJoin"] == true)
            {
                // The local join is saved. A late UI cancel must not abort an acknowledgment
                // that the inviter may already have received and leave the two sides disagreeing.
                using var confirmation = new CancellationTokenSource(HandshakeTimeout);
                await channel.SendAsync(new JsonObject { ["type"] = "joined" }, confirmation.Token);
            }
            _catchUpTo = null;
            Start();
        }
        catch
        {
            sessionStop.Cancel();
            await channel.DisposeAsync();
            if (session is not null) await session;
            lock (_lock)
            {
                _joining = false;
                _catchUpTo = null;
                _caughtUp = null;
                if (_doc is not null && _doc.DeviceId == device.Id)
                {
                    _device = null;
                    _doc = null;
                    _storage.Clear();
                }
            }
            throw;
        }
    }

    // ---- Sending cards to someone else -----------------------------------------

    /// <summary>Offers cards to another ChameLab (no group needed); see <see cref="CardOffer"/>.</summary>
    public static async Task<CardOffer> OfferCardsAsync(IReadOnlyList<CardSave> cards, CancellationToken ct)
    {
        var rendezvous = await Rendezvous.OpenAsync("cards", KeyPair.Generate(), Relays(), ct);
        return new CardOffer(rendezvous, cards);
    }

    public sealed class CardOffer : IAsyncDisposable
    {
        private readonly Rendezvous _rendezvous;
        private readonly IReadOnlyList<CardSave> _cards;
        private SecureChannel? _channel;

        internal CardOffer(Rendezvous rendezvous, IReadOnlyList<CardSave> cards)
        {
            _rendezvous = rendezvous;
            _cards = cards;
        }

        public string? TypedCode => _rendezvous.TypedCode;
        public string Qr => _rendezvous.Qr;

        /// <summary>Waits for the receiver; the digits to compare unless it scanned the QR (null then).</summary>
        public async Task<(string Name, string? Digits)> WaitAsync(CancellationToken ct)
        {
            _channel = await _rendezvous.WaitAsync(ct);
            var hello = await _channel.ReceiveAsync(ct);
            if ((string?)hello?["type"] != "receive") throw new IOException(Strings.Sync_PairingFailed);
            return (Clean((string?)hello["name"]), _rendezvous.IsProven(_channel, hello) ? null : _channel.ShortAuthenticationString());
        }

        public async Task SendAsync(CancellationToken ct)
        {
            if (_channel is null) throw new InvalidOperationException();
            var cards = new JsonArray(_cards.Select(c => (JsonNode)c.ToJson()).ToArray());
            await _channel.SendAsync(new JsonObject { ["type"] = "cards", ["cards"] = cards }, ct);
            // Wait for the receiver to have them (it closes the channel).
            try { await _channel.ReceiveAsync(ct); }
            catch (Exception ex) when (ex is IOException or System.Net.WebSockets.WebSocketException or CryptographicException) { }
        }

        public async ValueTask DisposeAsync()
        {
            if (_channel is not null) await _channel.DisposeAsync();
            await _rendezvous.DisposeAsync();
        }
    }

    /// <summary>Receives cards offered by another ChameLab; <paramref name="showDigits"/> as for joining.</summary>
    public static async Task<IReadOnlyList<CardSave>> ReceiveCardsAsync(string code, Action<string>? showDigits, CancellationToken ct)
    {
        var pairing = PairingCode.Parse(code) ?? throw new FormatException(Strings.Sync_NotACode);
        var (channel, verified) = await Rendezvous.ConnectAsync(pairing, "cards", KeyPair.Generate(), Relays(), ct);
        await using (channel)
        {
            var hello = new JsonObject { ["type"] = "receive", ["name"] = DefaultDeviceName };
            if (pairing.Secret is { } secret) hello["proof"] = PairingCode.Proof(secret, channel);
            await channel.SendAsync(hello, ct);
            if (!verified) showDigits?.Invoke(channel.ShortAuthenticationString());
            var message = await channel.ReceiveAsync(ct);
            if ((string?)message?["type"] != "cards" || message["cards"] is not JsonArray list)
                throw new IOException(Strings.Sync_Refused);
            var cards = new List<CardSave>();
            foreach (var node in list)
                if (node is not null)
                {
                    var card = CardSave.FromJson(node);
                    card.Id = Guid.NewGuid().ToString();
                    cards.Add(card);
                }
            await channel.SendAsync(new JsonObject { ["type"] = "thanks" }, ct);
            return cards;
        }
    }

    // ---- Last seen ---------------------------------------------------------------

    private const string LastSeenKey = "sync/seen";

    private void LoadLastSeen()
    {
        try
        {
            if (JsonNode.Parse(_store.Read(LastSeenKey) is { Length: > 0 } text ? text : "{}") is JsonObject o)
                foreach (var (id, value) in o)
                    if (DateTimeOffset.TryParse((string?)value, System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.RoundtripKind, out var seen))
                        _lastSeen[id] = seen;
        }
        catch (System.Text.Json.JsonException) { }
    }

    private void SaveLastSeen()
    {
        string json;
        lock (_lock) json = new JsonObject(_lastSeen.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value.ToString("O")))).ToJsonString();
        try { _store.Write(LastSeenKey, json); }
        catch (IOException) { }
    }

    private void RaiseChanged() => Ui(() => Changed?.Invoke());
}

/// <summary>Runs actions one at a time on the thread pool, in order: the "UI thread" where there is none (the CLI).</summary>
public static class SerialQueue
{
    private static readonly object Gate = new();
    private static Task _tail = Task.CompletedTask;

    public static void Post(Action action)
    {
        lock (Gate)
            _tail = _tail.ContinueWith(_ =>
            {
                try { action(); }
                catch (Exception ex) { Console.WriteLine($"[Sync] {ex.Message}"); }
            }, TaskScheduler.Default);
    }
}
