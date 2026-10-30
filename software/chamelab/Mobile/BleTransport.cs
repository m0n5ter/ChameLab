using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ChameLab.Transport;
using Plugin.BLE;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;

namespace ChameLab.Mobile;

/// <summary>
/// Well-known Nordic UART Service (NUS) UUIDs. The Chameleon firmware exposes
/// its command channel over NUS when connected via BLE.
/// </summary>
internal static class NordicUart
{
    public static readonly Guid Service = new("6E400001-B5A3-F393-E0A9-E50E24DCCA9E");
    /// <summary>Central → peripheral (write).</summary>
    public static readonly Guid Rx = new("6E400002-B5A3-F393-E0A9-E50E24DCCA9E");
    /// <summary>Peripheral → central (notify).</summary>
    public static readonly Guid Tx = new("6E400003-B5A3-F393-E0A9-E50E24DCCA9E");
}

/// <summary>
/// BLE transport over the Nordic UART Service, for Android and iOS. Notifications
/// on the TX characteristic are buffered into a channel that <see cref="ReadAsync"/>
/// drains; <see cref="WriteAsync"/> chunks frames to the negotiated MTU on RX.
/// </summary>
public sealed class BleTransport(Guid deviceId, string displayName) : ITransport
{
    private readonly IAdapter _adapter = CrossBluetoothLE.Current.Adapter;

    private IDevice? _device;
    private ICharacteristic? _rx;
    private ICharacteristic? _tx;
    private int _mtuPayload = 20; // conservative default until MTU is negotiated

    private readonly Channel<byte[]> _incoming =
        Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private byte[] _pending = Array.Empty<byte>();
    private int _pendingOffset;

    public TransportKind Kind => TransportKind.Ble;
    public string DisplayName => displayName;
    public bool IsConnected => _device?.State == DeviceState.Connected;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        // StopWatching cancels the window, but native scan cleanup is asynchronous.
        await BleScanner.WaitForScanAsync(ct);
        // Android's default transport may pick BR/EDR for a device it has not cached as LE.
        _device = await _adapter.ConnectToKnownDeviceAsync(deviceId,
            new ConnectParameters(autoConnect: false, forceBleTransport: true), ct);

        // Larger MTU greatly improves throughput; ignore if unsupported.
        try
        {
            var mtu = await _device.RequestMtuAsync(247, ct);
            if (mtu > 3) _mtuPayload = mtu - 3; // minus ATT header
        }
        catch { /* best effort */ }

        // Android starts on a slow, power-saving interval; commands are request/response round trips.
        try { _device.UpdateConnectionInterval(ConnectionInterval.High); }
        catch { /* not supported on this platform */ }

        var service = await _device.GetServiceAsync(NordicUart.Service, ct)
            ?? throw new InvalidOperationException("Nordic UART service not found on device.");
        _rx = await service.GetCharacteristicAsync(NordicUart.Rx, ct)
            ?? throw new InvalidOperationException("NUS RX characteristic not found.");
        _tx = await service.GetCharacteristicAsync(NordicUart.Tx, ct)
            ?? throw new InvalidOperationException("NUS TX characteristic not found.");

        // Acknowledged writes: a write command the device rejects (e.g. BLE pairing is enabled and
        // the link is not encrypted yet) is dropped silently, and the reply then never comes. A
        // request surfaces the error, and Android starts pairing on it.
        _rx.WriteType = CharacteristicWriteType.WithResponse;

        _tx.ValueUpdated += OnTxValueUpdated;
        await _tx.StartUpdatesAsync(ct);

        _adapter.DeviceDisconnected += OnDeviceDisconnected;
        _adapter.DeviceConnectionLost += OnDeviceConnectionLost;
    }

    private void OnTxValueUpdated(object? sender, CharacteristicUpdatedEventArgs e)
    {
        var value = e.Characteristic.Value;
        if (value is { Length: > 0 })
            _incoming.Writer.TryWrite(value);
    }

    private void OnDeviceDisconnected(object? sender, DeviceEventArgs e) => HandleDrop(e.Device);
    private void OnDeviceConnectionLost(object? sender, DeviceErrorEventArgs e) => HandleDrop(e.Device);

    private void HandleDrop(IDevice? device)
    {
        if (device?.Id == deviceId)
            _incoming.Writer.TryComplete();
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (_rx is null) throw new InvalidOperationException("Not connected.");
        var offset = 0;
        while (offset < data.Length)
        {
            var len = Math.Min(_mtuPayload, data.Length - offset);
            var chunk = data.Slice(offset, len).ToArray();
            await _rx.WriteAsync(chunk, ct);
            offset += len;
        }
    }

    public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        // Serve any leftover from a previous oversized notification first.
        if (_pendingOffset < _pending.Length)
            return DrainPending(buffer);

        byte[] chunk;
        try
        {
            chunk = await _incoming.Reader.ReadAsync(ct);
        }
        catch (ChannelClosedException)
        {
            throw new InvalidOperationException("BLE link closed.");
        }

        _pending = chunk;
        _pendingOffset = 0;
        return DrainPending(buffer);
    }

    private int DrainPending(Memory<byte> buffer)
    {
        var n = Math.Min(buffer.Length, _pending.Length - _pendingOffset);
        _pending.AsSpan(_pendingOffset, n).CopyTo(buffer.Span);
        _pendingOffset += n;
        return n;
    }

    public async ValueTask DisposeAsync()
    {
        _adapter.DeviceDisconnected -= OnDeviceDisconnected;
        _adapter.DeviceConnectionLost -= OnDeviceConnectionLost;
        if (_tx is not null)
        {
            _tx.ValueUpdated -= OnTxValueUpdated;
            try { await _tx.StopUpdatesAsync(); } catch { /* ignore */ }
        }
        if (_device is not null)
        {
            try { await _adapter.DisconnectDeviceAsync(_device); } catch { /* ignore */ }
        }
        _incoming.Writer.TryComplete();
    }
}

/// <summary>
/// Scans for nearby Chameleon devices advertising over BLE. Matches devices that
/// advertise the Nordic UART service or whose name begins with "Chameleon".
/// </summary>
public sealed class BleScanner : ITransportScanner, ITransportWatcher
{
    private readonly IAdapter _adapter;
    private readonly IBluetoothLE _ble;

    public BleScanner() : this(CrossBluetoothLE.Current) { }

    internal BleScanner(IBluetoothLE ble)
    {
        _ble = ble;
        _adapter = ble.Adapter;
    }

    public TransportKind Kind => TransportKind.Ble;
    public string DisplayName => "Bluetooth LE";

    // The adapter scans for one caller at a time: a second StartScanning returns at once and its
    // StopScanning would cut the first one short, so overlapping callers share the running scan.
    private static readonly object Gate = new();
    private static Task<IReadOnlyList<TransportCandidate>>? _running;
    private static readonly Stopwatch ScanClock = Stopwatch.StartNew();
    private static TimeSpan _nextScan;
    // Also covers short/cancelled windows and repeated Rescan taps: stay below Android's
    // five scan starts per 30 seconds even when the page is repeatedly reopened.
    private static readonly TimeSpan MinimumScanInterval = TimeSpan.FromSeconds(7);

    internal static async Task WaitForScanAsync(CancellationToken ct)
    {
        Task? running;
        lock (Gate) running = _running;
        if (running is null) return;
        try { await running.WaitAsync(ct); }
        catch (Exception) when (!ct.IsCancellationRequested) { /* a failed scan must not block connection */ }
    }

    /// <summary>Scan window for an explicit Rescan.</summary>
    private static readonly TimeSpan RescanWindow = TimeSpan.FromSeconds(6);

    // Watching scans in back-to-back windows. Android silently blocks an app that starts more than
    // five scans in 30 s, so windows are long, with a short pause between them.
    private static readonly TimeSpan WatchWindow = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan WatchPause = TimeSpan.FromSeconds(1);

    // A watched device stays listed while it was seen in the current or the previous window, so it
    // drops out about one window after it goes to sleep or out of range.
    private readonly object _watchGate = new();
    private CancellationTokenSource? _watch;
    private Action<IReadOnlyList<TransportCandidate>>? _changed;
    private Dictionary<Guid, TransportCandidate> _lastWindow = new();
    private Dictionary<Guid, TransportCandidate> _thisWindow = new();

    public Task<IReadOnlyList<TransportCandidate>> ScanAsync(CancellationToken ct = default) =>
        ScanAsync(RescanWindow, ct);

    private Task<IReadOnlyList<TransportCandidate>> ScanAsync(TimeSpan window, CancellationToken ct)
    {
        lock (Gate)
        {
            var running = _running is { IsCompleted: false } ? _running : _running = ScanCoreAsync(window, ct);
            return running.WaitAsync(ct);
        }
    }

    public void StartWatching(Action<IReadOnlyList<TransportCandidate>> changed)
    {
        lock (_watchGate)
        {
            _changed = changed;
            if (_watch is not null) return;
            _watch = new CancellationTokenSource();
            _ = WatchLoopAsync(_watch.Token);
        }
    }

    public void StopWatching()
    {
        lock (_watchGate)
        {
            // Not disposed: the running window may still be linking to its token.
            _watch?.Cancel();
            _watch = null;
            _changed = null;
            _lastWindow = new();
            _thisWindow = new();
        }
    }

    private async Task WatchLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var found = await ScanAsync(WatchWindow, ct);
                    foreach (var candidate in found) Seen(candidate, Guid.Parse(candidate.Id));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    // Permission may still be pending on first launch; Bluetooth may be toggled
                    // during a window. Keep watching so granting/enabling it recovers automatically.
                    Console.WriteLine($"[BLE] Scan failed, retrying: {ex}");
                    await Task.Delay(WatchPause, ct);
                    continue;
                }
                lock (_watchGate)
                {
                    if (ct.IsCancellationRequested) return;
                    _lastWindow = _thisWindow;
                    _thisWindow = new();
                }
                Report();
                await Task.Delay(WatchPause, ct);
            }
        }
        catch (OperationCanceledException) { /* stopped */ }
        catch (Exception ex) { Console.WriteLine($"[BLE] Watch failed: {ex}"); }
    }

    private void Seen(TransportCandidate candidate, Guid id)
    {
        bool isNew;
        lock (_watchGate)
        {
            if (_watch is null) return;
            isNew = !_lastWindow.ContainsKey(id) && !_thisWindow.ContainsKey(id);
            _thisWindow[id] = candidate;
        }
        if (isNew) Report();
    }

    private void Report()
    {
        Action<IReadOnlyList<TransportCandidate>>? changed;
        List<TransportCandidate> list;
        lock (_watchGate)
        {
            changed = _changed;
            if (changed is null) return;
            var merged = new Dictionary<Guid, TransportCandidate>(_lastWindow);
            foreach (var (id, c) in _thisWindow) merged[id] = c;
            list = merged.Values.OrderBy(c => c.DisplayName).ThenBy(c => c.Id).ToList();
        }
        changed(list);
    }

    private async Task<IReadOnlyList<TransportCandidate>> ScanCoreAsync(TimeSpan window, CancellationToken ct)
    {
        var found = new ConcurrentDictionary<Guid, TransportCandidate>();
#if ANDROID
        // MainActivity requests permissions asynchronously. Do not start the plugin's native
        // scanner until the result arrives (a native startup exception can leave it marked busy).
        var permission = OperatingSystem.IsAndroidVersionAtLeast(31)
            ? global::Android.Manifest.Permission.BluetoothScan
            : global::Android.Manifest.Permission.AccessFineLocation;
        if (global::Android.App.Application.Context.CheckSelfPermission(permission) != global::Android.Content.PM.Permission.Granted ||
            OperatingSystem.IsAndroidVersionAtLeast(31) &&
            global::Android.App.Application.Context.CheckSelfPermission(global::Android.Manifest.Permission.BluetoothConnect) != global::Android.Content.PM.Permission.Granted)
            throw new InvalidOperationException("Bluetooth permission has not been granted.");
#endif
        if (!_ble.IsAvailable || !_ble.IsOn)
            return Array.Empty<TransportCandidate>();

        // ScanCore is shared process-wide. A cancelled watch can still be cleaning up when
        // a new watch starts; rate limiting and native cleanup must finish before the next start.
        var delay = _nextScan - ScanClock.Elapsed;
        if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
        ct.ThrowIfCancellationRequested();

        void OnDiscovered(object? sender, DeviceEventArgs e)
        {
            var d = e.Device;
            if (d is null || !IsChameleon(d)) return;
            var candidate = new TransportCandidate(
                TransportKind.Ble,
                d.Id.ToString(),
                string.IsNullOrWhiteSpace(d.Name) ? "Chameleon (BLE)" : d.Name!,
                $"BLE · RSSI {d.Rssi}");
            found[d.Id] = candidate;
            Seen(candidate, d.Id);
        }

        // Re-evaluate every packet: a first advertisement may lack the name or the NUS
        // UUID carried in a later scan response. Also refresh sightings throughout the window.
        _adapter.DeviceAdvertised += OnDiscovered;
        try
        {
            using var scanCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            scanCts.CancelAfter(window);
            _adapter.ScanMode = ScanMode.LowLatency;
            _nextScan = ScanClock.Elapsed + MinimumScanInterval;
            await _adapter.StartScanningForDevicesAsync(
                new ScanFilterOptions(),
                allowDuplicatesKey: true,
                cancellationToken: scanCts.Token);
        }
        catch (OperationCanceledException) { /* scan window elapsed */ }
        finally
        {
            try { await _adapter.StopScanningForDevicesAsync(); } catch { /* ignore */ }
            _adapter.DeviceAdvertised -= OnDiscovered;
        }

        return new List<TransportCandidate>(found.Values);
    }

    public ITransport Create(TransportCandidate candidate) =>
        new BleTransport(Guid.Parse(candidate.Id), candidate.DisplayName);

    private static bool IsChameleon(IDevice d)
    {
        if (!string.IsNullOrEmpty(d.Name) &&
            d.Name!.StartsWith("Chameleon", StringComparison.OrdinalIgnoreCase))
            return true;
        foreach (var record in d.AdvertisementRecords ?? new List<AdvertisementRecord>())
        {
            if (record.Type is AdvertisementRecordType.UuidsComplete128Bit
                    or AdvertisementRecordType.UuidsIncomplete128Bit &&
                ListsNus(record.Data))
                return true;
        }
        return false;
    }

    // NUS UUID as written in advertising data (little-endian), and in the big-endian order iOS
    // reports it in after Plugin.BLE rebuilds the records from CBUUIDs.
    private static readonly byte[] NusLittleEndian =
        Convert.FromHexString("9ECADC240EE5A9E093F3A3B50100406E");
    private static readonly byte[] NusBigEndian =
        Convert.FromHexString("6E400001B5A3F393E0A9E50E24DCCA9E");

    /// <summary>True if a list of 128-bit service UUIDs contains the Nordic UART service.</summary>
    private static bool ListsNus(byte[]? data)
    {
        if (data is null) return false;
        for (var i = 0; i + 16 <= data.Length; i += 16)
        {
            var uuid = data.AsSpan(i, 16);
            if (uuid.SequenceEqual(NusLittleEndian) || uuid.SequenceEqual(NusBigEndian))
                return true;
        }
        return false;
    }
}
