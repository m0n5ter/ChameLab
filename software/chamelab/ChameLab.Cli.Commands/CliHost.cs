using ChameLab.Protocol;
using ChameLab.Services;
using ChameLab.Transport;

namespace ChameLab.Cli;

/// <summary>
/// Where a <see cref="CliSession"/> runs: who holds the device link, how <c>hw connect</c> opens one, where the
/// session reads typed input from and where relative file names point. The terminal owns its link; the app's
/// console borrows the one the app already has open.
/// </summary>
public abstract class CliHost : IAsyncDisposable
{
    /// <summary>The link commands talk over; null while nothing is connected.</summary>
    public abstract ChameleonConnection? Connection { get; }

    /// <summary>
    /// <c>hw connect</c>: opens <paramref name="port"/> (a serial port or <c>tcp://host:port</c>), the device behind
    /// share <paramref name="code"/>, or the only device found. Returns what got connected, for the message.
    /// </summary>
    public abstract Task<string> ConnectAsync(string? port, string? code, CancellationToken ct);

    /// <summary><c>hw disconnect</c>, and after <c>hw dfu</c> sends the device to its bootloader.</summary>
    public abstract Task DisconnectAsync();

    /// <summary><c>hw share</c>: shares a device until <paramref name="ct"/> is canceled.</summary>
    public virtual Task ShareAsync(string? port, TextWriter output, CancellationToken ct) =>
        throw new NotSupportedException("Sharing is not available here.");

    /// <summary><c>data plot</c>: shows LF samples starting at <paramref name="offset"/> of the capture.</summary>
    public virtual Task ShowWaveformAsync(byte[] samples, int offset, CancellationToken ct) =>
        throw new NotSupportedException("No waveform viewer here; use data plot --ascii.");

    /// <summary>A line typed while a command runs (manual APDU responses); null at end of input.</summary>
    public virtual async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        if (Console.IsInputRedirected) throw new InvalidOperationException("Manual APDU responses require an interactive terminal.");
        return await Console.In.ReadLineAsync(ct);
    }

    /// <summary><c>clear</c>.</summary>
    public virtual void Clear()
    {
        if (!Console.IsOutputRedirected) Console.Clear();
    }

    /// <summary>The file a command's file name stands for; relative names resolve against the host's folder.</summary>
    public virtual string ResolvePath(string path) => path;

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// A host that owns its link: opens TCP and shared devices itself, serial ports through <see cref="SerialScanner"/>
/// when it has one, and shares a device over the relay. Disposing it closes the link.
/// </summary>
public class StandaloneCliHost : CliHost
{
    private ChameleonConnection? _connection;

    public override ChameleonConnection? Connection => _connection;

    /// <summary>Finds and opens serial ports; null where there are none.</summary>
    protected virtual ITransportScanner? SerialScanner => null;

    /// <summary>Takes <paramref name="transport"/> as the link, replacing any open one.</summary>
    public async Task AttachAsync(ITransport transport, CancellationToken ct = default)
    {
        await DisconnectAsync();
        var connection = new ChameleonConnection(transport);
        try { await connection.OpenAsync(ct); _connection = connection; }
        catch { await connection.DisposeAsync(); throw; }
    }

    public override async Task<string> ConnectAsync(string? port, string? code, CancellationToken ct)
    {
        ITransport transport;
        string what;
        if (code is not null)
        {
            var candidate = RelayScanner.Candidate(code);
            transport = new RelayScanner().Create(candidate);
            what = "shared " + candidate.Id;
        }
        else
        {
            var candidate = await FindAsync(port, "hw connect", ct);
            transport = candidate.Kind == TransportKind.Tcp ? new TcpScanner().Create(candidate) : Serial().Create(candidate);
            what = port ?? candidate.Id;
        }
        await AttachAsync(transport, ct);
        return what;
    }

    /// <summary><paramref name="port"/> as a candidate, or the only serial device found when it is null.</summary>
    private async Task<TransportCandidate> FindAsync(string? port, string command, CancellationToken ct)
    {
        if (port is not null && port.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(port);
            return TcpScanner.Candidate($"{uri.Host}:{uri.Port}");
        }
        if (port is not null)
        {
            Serial();
            return new TransportCandidate(TransportKind.Serial, port, port);
        }
        var candidates = await Serial().ScanAsync(ct);
        return candidates.Count switch
        {
            0 => throw new IOException($"Chameleon not found. Connect it or specify {command} -p PORT."),
            1 => candidates[0],
            _ => throw new ArgumentException("Multiple devices found; use -p: " + string.Join(", ", candidates.Select(c => c.Id)))
        };
    }

    private ITransportScanner Serial() => SerialScanner
        ?? throw new NotSupportedException("Serial ports are not available here; use -p tcp://HOST:PORT.");

    public override async Task ShareAsync(string? port, TextWriter output, CancellationToken ct)
    {
        var candidate = await FindAsync(port, "hw share", ct);

        // The share opens the device itself and needs the port to itself; drop any local hold on it.
        await DisconnectAsync();

        ITransportScanner[] scanners = SerialScanner is { } serial ? [serial, new TcpScanner()] : [new TcpScanner()];
        var sharing = new DeviceSharingService(new DeviceService(scanners));
        await sharing.StartAsync(candidate, ct: ct);
        output.WriteLine($"Sharing {candidate.Id}. Share code: {sharing.Code}");
        output.WriteLine($"Join it with: hw connect --code {sharing.Code}");
        output.WriteLine("Press Ctrl+C to stop sharing.");

        var lastPeer = sharing.PeerName;
        void OnChanged()
        {
            if (sharing.State == SharingState.PeerConnected && sharing.PeerName != lastPeer)
                output.WriteLine("Connected: " + (sharing.PeerName ?? "someone"));
            else if (sharing.State == SharingState.WaitingForPeer && lastPeer is not null)
                output.WriteLine("Peer left.");
            lastPeer = sharing.PeerName;
        }
        sharing.Changed += OnChanged;
        try { await WaitForShareEndAsync(sharing, ct); }
        finally
        {
            sharing.Changed -= OnChanged;
            await sharing.StopAsync();
        }
        if (sharing.Error is { } error) throw new IOException(error);
    }

    private static async Task WaitForShareEndAsync(DeviceSharingService sharing, CancellationToken ct)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged() { if (sharing.State == SharingState.Idle) done.TrySetResult(); }
        sharing.Changed += OnChanged;
        try
        {
            await using var reg = ct.Register(() => done.TrySetResult());
            // The share may already have ended between starting and subscribing.
            if (sharing.State == SharingState.Idle) done.TrySetResult();
            await done.Task;
        }
        finally { sharing.Changed -= OnChanged; }
    }

    public override async Task DisconnectAsync()
    {
        var c = _connection; _connection = null;
        if (c is not null) await c.DisposeAsync();
    }

    public override async ValueTask DisposeAsync() => await DisconnectAsync();
}
