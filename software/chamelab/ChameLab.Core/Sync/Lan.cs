using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ChameLab.Sync;

/// <summary>Another ChameLab announcing itself on the local network: where to reach it and what it said.</summary>
public sealed record LanAnnouncement(IReadOnlyList<IPEndPoint> Endpoints, IReadOnlyDictionary<string, string> Text);

/// <summary>Announcing this device and finding others on the local network.</summary>
public interface ILanDiscovery : IDisposable
{
    /// <summary>Starts announcing <paramref name="port"/> with these TXT values and browsing for others.</summary>
    void Start(int port, IReadOnlyDictionary<string, string> text);

    /// <summary>Changes what the announcement says (e.g. a new room secret).</summary>
    void Update(IReadOnlyDictionary<string, string> text);

    /// <summary>Asks the network again who is there.</summary>
    void Browse();

    event Action<LanAnnouncement>? Found;
}

/// <summary>
/// A small mDNS / DNS-SD responder and browser (RFC 6762/6763) for <c>_chamelab-sync._tcp.local</c>,
/// in managed code: announces PTR, SRV, TXT and A records for this device and reads other devices'
/// answers. It speaks the same records as Apple's Bonjour, so it finds iPhones announcing through it.
/// </summary>
public sealed class MdnsDiscovery : ILanDiscovery
{
    public const string ServiceType = "_chamelab-sync._tcp.local";
    private static readonly IPAddress Group = IPAddress.Parse("224.0.0.251");
    private const int Port = 5353;
    private const ushort A = 1, Ptr = 12, Txt = 16, Srv = 33;

    private readonly string _instance = $"{Convert.ToHexString(SyncCrypto.Random(6)).ToLowerInvariant()}.{ServiceType}";
    private readonly string _host = $"chamelab-{Convert.ToHexString(SyncCrypto.Random(4)).ToLowerInvariant()}.local";
    private readonly CancellationTokenSource _stop = new();
    private Socket? _socket;
    private int _port;
    private IReadOnlyDictionary<string, string> _text = new Dictionary<string, string>();

    public event Action<LanAnnouncement>? Found;

    public void Start(int port, IReadOnlyDictionary<string, string> text)
    {
        _port = port;
        _text = text;
        try
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(IPAddress.Any, Port));
            foreach (var address in LocalAddresses())
            {
                try { socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(Group, address)); }
                catch (SocketException) { /* that interface takes no multicast */ }
            }
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
            _socket = socket;
        }
        catch (SocketException ex)
        {
            Console.WriteLine($"[Sync] mDNS unavailable: {ex.Message}");
            return;
        }
        _ = Task.Run(ReceiveLoopAsync);
        _ = Task.Run(AnnounceLoopAsync);
    }

    public void Update(IReadOnlyDictionary<string, string> text)
    {
        _text = text;
        SendAll(Response());
    }

    public void Browse() => SendAll(Query());

    /// <summary>The IPv4 addresses of this device's network interfaces that are up (no loopback).</summary>
    public static IReadOnlyList<IPAddress> LocalAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)
                            && !a.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                .Distinct()
                .ToList();
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
        {
            return Array.Empty<IPAddress>();
        }
    }

    private async Task AnnounceLoopAsync()
    {
        // Announce and ask at once, again a second later (RFC 6762 §8.3), then every minute.
        for (var round = 0; !_stop.IsCancellationRequested; round++)
        {
            SendAll(Response());
            SendAll(Query());
            try { await Task.Delay(round == 0 ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(60), _stop.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[9000];
        while (!_stop.IsCancellationRequested && _socket is { } socket)
        {
            SocketReceiveFromResult r;
            try { r = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), _stop.Token); }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException) { return; }
            Handle(buffer.AsSpan(0, r.ReceivedBytes), (IPEndPoint)r.RemoteEndPoint);
        }
    }

    internal void Handle(ReadOnlySpan<byte> packet, IPEndPoint from)
    {
        if (packet.Length < 12) return;
        var flags = BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);
        int qd = BinaryPrimitives.ReadUInt16BigEndian(packet[4..]), an = BinaryPrimitives.ReadUInt16BigEndian(packet[6..]),
            ns = BinaryPrimitives.ReadUInt16BigEndian(packet[8..]), ar = BinaryPrimitives.ReadUInt16BigEndian(packet[10..]);
        var at = 12;
        var asked = false;
        for (var i = 0; i < qd; i++)
        {
            if (!TryReadName(packet, ref at, out var name) || at + 4 > packet.Length) return;
            var type = BinaryPrimitives.ReadUInt16BigEndian(packet[at..]);
            at += 4;
            if ((flags & 0x8000) == 0 && Same(name, ServiceType) && type is Ptr or 255) asked = true;
        }
        if (asked)
        {
            SendAll(Response());
            return;
        }
        if ((flags & 0x8000) == 0) return;

        var instances = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var srv = new Dictionary<string, (int Port, string Target)>(StringComparer.OrdinalIgnoreCase);
        var txt = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var hosts = new Dictionary<string, List<IPAddress>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < an + ns + ar; i++)
        {
            if (!TryReadName(packet, ref at, out var name) || at + 10 > packet.Length) return;
            var type = BinaryPrimitives.ReadUInt16BigEndian(packet[at..]);
            var length = BinaryPrimitives.ReadUInt16BigEndian(packet[(at + 8)..]);
            at += 10;
            if (at + length > packet.Length) return;
            var data = packet.Slice(at, length);
            var dataAt = at;
            switch (type)
            {
                case Ptr when Same(name, ServiceType):
                    if (TryReadName(packet, ref dataAt, out var instance)) instances.Add(instance);
                    break;
                case Srv when length >= 6:
                    var srvAt = dataAt + 6;
                    if (TryReadName(packet, ref srvAt, out var target))
                        srv[name] = (BinaryPrimitives.ReadUInt16BigEndian(data[4..]), target);
                    break;
                case Txt:
                    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    for (var t = 0; t < data.Length;)
                    {
                        var n = data[t++];
                        if (t + n > data.Length) break;
                        var entry = Encoding.UTF8.GetString(data.Slice(t, n));
                        t += n;
                        var eq = entry.IndexOf('=');
                        if (eq > 0) values[entry[..eq]] = entry[(eq + 1)..];
                    }
                    txt[name] = values;
                    break;
                case A when length == 4:
                    if (!hosts.TryGetValue(name, out var list)) hosts[name] = list = new List<IPAddress>();
                    list.Add(new IPAddress(data));
                    break;
            }
            at += length;
        }
        foreach (var instance in instances.Count > 0 ? instances : srv.Keys.Where(k => k.EndsWith(ServiceType, StringComparison.OrdinalIgnoreCase)).ToHashSet())
        {
            if (Same(instance, _instance) || !srv.TryGetValue(instance, out var s) || !txt.TryGetValue(instance, out var values)) continue;
            var addresses = hosts.TryGetValue(s.Target, out var found) ? found : new List<IPAddress> { from.Address };
            Found?.Invoke(new LanAnnouncement(addresses.Select(a => new IPEndPoint(a, s.Port)).ToList(), values));
        }
    }

    private static bool Same(string a, string b) => string.Equals(a.TrimEnd('.'), b.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);

    internal static bool TryReadName(ReadOnlySpan<byte> packet, ref int at, out string name)
    {
        var labels = new List<string>();
        var jumped = false;
        var position = at;
        for (var hops = 0; hops < 128; hops++)
        {
            if ((uint)position >= (uint)packet.Length)
            {
                name = string.Empty;
                return false;
            }
            var length = packet[position];
            if (length == 0)
            {
                if (!jumped) at = position + 1;
                name = string.Join('.', labels);
                return true;
            }
            if ((length & 0xC0) == 0xC0)
            {
                if (position + 1 >= packet.Length)
                {
                    name = string.Empty;
                    return false;
                }
                var pointer = BinaryPrimitives.ReadUInt16BigEndian(packet[position..]) & 0x3FFF;
                if (pointer >= packet.Length)
                {
                    name = string.Empty;
                    return false;
                }
                if (!jumped) at = position + 2;
                jumped = true;
                position = pointer;
                continue;
            }
            if ((length & 0xC0) != 0 || position + 1 + length > packet.Length)
            {
                name = string.Empty;
                return false;
            }
            labels.Add(Encoding.UTF8.GetString(packet.Slice(position + 1, length)));
            position += 1 + length;
        }
        name = string.Empty;
        return false;
    }

    private static byte[] Query()
    {
        var packet = new List<byte>();
        Header(packet, 0, 1, 0);
        Name(packet, ServiceType);
        U16(packet, Ptr);
        U16(packet, 1);
        return packet.ToArray();
    }

    private byte[] Response()
    {
        var addresses = LocalAddresses();
        var packet = new List<byte>();
        Header(packet, 0x8400, 0, 3 + addresses.Count);
        Record(packet, ServiceType, Ptr, false, 4500, d => Name(d, _instance));
        Record(packet, _instance, Srv, true, 120, d => { U16(d, 0); U16(d, 0); U16(d, (ushort)_port); Name(d, _host); });
        Record(packet, _instance, Txt, true, 4500, d =>
        {
            foreach (var (key, value) in _text)
            {
                var entry = Encoding.UTF8.GetBytes($"{key}={value}");
                d.Add((byte)Math.Min(entry.Length, 255));
                d.AddRange(entry.Take(255));
            }
        });
        foreach (var address in addresses)
            Record(packet, _host, A, true, 120, d => d.AddRange(address.GetAddressBytes()));
        return packet.ToArray();
    }

    private static void Header(List<byte> packet, ushort flags, ushort questions, int answers)
    {
        U16(packet, 0);
        U16(packet, flags);
        U16(packet, questions);
        U16(packet, (ushort)answers);
        U16(packet, 0);
        U16(packet, 0);
    }

    private static void Record(List<byte> packet, string name, ushort type, bool unique, uint ttl, Action<List<byte>> data)
    {
        Name(packet, name);
        U16(packet, type);
        U16(packet, (ushort)(unique ? 0x8001 : 0x0001));
        packet.Add((byte)(ttl >> 24));
        packet.Add((byte)(ttl >> 16));
        packet.Add((byte)(ttl >> 8));
        packet.Add((byte)ttl);
        var rdata = new List<byte>();
        data(rdata);
        U16(packet, (ushort)rdata.Count);
        packet.AddRange(rdata);
    }

    private static void Name(List<byte> packet, string name)
    {
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            packet.Add((byte)bytes.Length);
            packet.AddRange(bytes);
        }
        packet.Add(0);
    }

    private static void U16(List<byte> packet, ushort value)
    {
        packet.Add((byte)(value >> 8));
        packet.Add((byte)value);
    }

    private void SendAll(byte[] packet)
    {
        if (_socket is not { } socket) return;
        var target = new IPEndPoint(Group, Port);
        var addresses = LocalAddresses();
        if (addresses.Count == 0) addresses = [IPAddress.Any];
        foreach (var address in addresses)
        {
            try
            {
                if (!address.Equals(IPAddress.Any))
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
                socket.SendTo(packet, target);
            }
            catch (SocketException) { /* that interface is down or takes no multicast */ }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _socket?.Dispose();
        _socket = null;
    }
}

/// <summary>
/// The TCP side of local sync: a listener others connect to, and connecting to them. Links are
/// <see cref="StreamPacketLink"/>s; what runs over them is the same Noise channel as over the relay.
/// </summary>
public sealed class LanListener : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Any, 0);
    private readonly CancellationTokenSource _stop = new();

    public LanListener(Func<IPacketLink, Task> accepted)
    {
        _listener.Start();
        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException) { return; }
                client.NoDelay = true;
                _ = Task.Run(() => accepted(new StreamPacketLink(client.GetStream())));
            }
        });
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>"192.168.1.5:41234" for each local address: where others on the network can reach this device.</summary>
    public IReadOnlyList<string> Endpoints => MdnsDiscovery.LocalAddresses().Select(a => $"{a}:{Port}").ToList();

    /// <summary>Connects to the first of <paramref name="endpoints"/> that answers within the timeout; null when none does.</summary>
    public static async Task<IPacketLink?> ConnectAsync(IEnumerable<IPEndPoint> endpoints, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var attempts = endpoints.Select(async endpoint =>
        {
            var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
            try
            {
                await client.ConnectAsync(endpoint, cts.Token);
                return client;
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                client.Dispose();
                return null;
            }
        }).ToList();
        TcpClient? winner = null;
        while (attempts.Count > 0)
        {
            var done = await Task.WhenAny(attempts);
            attempts.Remove(done);
            if (await done is { } client)
            {
                if (winner is null) { winner = client; cts.Cancel(); }
                else client.Dispose();
            }
        }
        return winner is null ? null : new StreamPacketLink(winner.GetStream());
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }
}
