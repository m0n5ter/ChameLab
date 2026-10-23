using System;
using System.Threading;
using System.Threading.Tasks;

namespace ChameLab.Transport;

/// <summary>How a device is physically reached.</summary>
public enum TransportKind
{
    Serial,
    Ble,
    Tcp,
}

/// <summary>
/// A discoverable, connectable endpoint (a COM port, a BLE peripheral, a TCP host).
/// Produced by an <see cref="ITransportScanner"/>.
/// </summary>
public sealed record TransportCandidate(
    TransportKind Kind,
    string Id,
    string DisplayName,
    string? Detail = null)
{
    public override string ToString() => DisplayName;
}

/// <summary>
/// A bidirectional byte channel to the device. Implementations wrap a serial
/// port, a BLE Nordic-UART characteristic pair, or a TCP socket.
/// </summary>
public interface ITransport : IAsyncDisposable
{
    TransportKind Kind { get; }
    string DisplayName { get; }
    bool IsConnected { get; }

    Task ConnectAsync(CancellationToken ct = default);

    Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);

    /// <summary>
    /// Reads whatever bytes are currently available into <paramref name="buffer"/>,
    /// returning the count. Returns 0 only on a clean timeout; throws on disconnect.
    /// </summary>
    Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default);
}

/// <summary>When a scanner may run without the user pressing Rescan.</summary>
public enum AutoScan
{
    /// <summary>Only on an explicit Rescan (e.g. needs a user gesture to open a chooser).</summary>
    Never,
    /// <summary>Once when the connect page appears, then only on Rescan (slow or power-hungry).</summary>
    OnOpen,
    /// <summary>Cheap and silent: safe to poll continuously to catch hot-plugged devices.</summary>
    Poll,
}

/// <summary>Enumerates candidates for one transport kind and opens connections to them.</summary>
public interface ITransportScanner
{
    TransportKind Kind { get; }
    string DisplayName { get; }

    AutoScan AutoScan => AutoScan.OnOpen;

    Task<System.Collections.Generic.IReadOnlyList<TransportCandidate>> ScanAsync(CancellationToken ct = default);

    /// <summary>
    /// Scans for the reason given by <paramref name="trigger"/> (<see cref="AutoScan.Never"/> is an
    /// explicit Rescan). Lets a scanner do something only a user action allows, e.g. open a chooser.
    /// </summary>
    Task<System.Collections.Generic.IReadOnlyList<TransportCandidate>> ScanAsync(AutoScan trigger, CancellationToken ct = default) =>
        ScanAsync(ct);

    ITransport Create(TransportCandidate candidate);
}

/// <summary>
/// A scanner that keeps discovering in the background while the connect page is shown and reports
/// devices as they come and go (e.g. BLE, where a device only advertises while awake). Automatic
/// scans leave it out; an explicit Rescan still runs it.
/// </summary>
public interface ITransportWatcher
{
    /// <summary>
    /// Starts watching (no-op if already watching). <paramref name="changed"/> receives the full
    /// current list of this scanner's candidates whenever it changes, on any thread.
    /// </summary>
    void StartWatching(Action<System.Collections.Generic.IReadOnlyList<TransportCandidate>> changed);

    void StopWatching();
}
