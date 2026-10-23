using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Transport;

namespace ChameLab.Services;

public enum ConnectionState { Disconnected, Connecting, Connected }

/// <summary>
/// Central connection state holder shared by all view models. Owns the active
/// <see cref="ChameleonConnection"/>/<see cref="ChameleonClient"/> and the set of
/// platform-provided transport scanners.
/// </summary>
public sealed class DeviceService
{
    private readonly IReadOnlyList<ITransportScanner> _scanners;
    private ChameleonConnection? _conn;

    public DeviceService(IEnumerable<ITransportScanner> scanners)
    {
        _scanners = scanners.ToList();
    }

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public ChameleonClient? Client { get; private set; }
    public DeviceInfo? DeviceInfo { get; private set; }
    /// <summary>What the current (or last) connection was made to.</summary>
    public TransportCandidate? Candidate { get; private set; }

    public IReadOnlyList<ITransportScanner> Scanners => _scanners;

    public event Action<ConnectionState>? StateChanged;

    private void SetState(ConnectionState s)
    {
        State = s;
        StateChanged?.Invoke(s);
    }

    /// <summary>
    /// Scans the registered transports and returns a flat candidate list. <paramref name="trigger"/>
    /// says why the scan runs: <see cref="AutoScan.Never"/> is an explicit Rescan and scans
    /// everything; automatic scans skip scanners that don't allow running for that reason.
    /// </summary>
    public async Task<IReadOnlyList<TransportCandidate>> ScanAsync(
        AutoScan trigger = AutoScan.Never, CancellationToken ct = default)
    {
        var all = new List<TransportCandidate>();
        foreach (var scanner in _scanners)
        {
            if (!RunsOn(scanner, trigger)) continue;
            try { all.AddRange(await scanner.ScanAsync(trigger, ct)); }
            catch (Exception ex) { Console.WriteLine($"[Scan] {scanner.DisplayName} failed: {ex}"); }
        }
        return all;
    }

    /// <summary>Whether a scan for <paramref name="trigger"/> runs <paramref name="scanner"/>; watchers report on their own.</summary>
    public static bool RunsOn(ITransportScanner scanner, AutoScan trigger) =>
        trigger == AutoScan.Never || (scanner.AutoScan >= trigger && scanner is not ITransportWatcher);

    /// <summary>Scans only the transport of <paramref name="kind"/>, whatever its auto-scan policy.</summary>
    public async Task<IReadOnlyList<TransportCandidate>> ScanAsync(TransportKind kind, CancellationToken ct = default)
    {
        var scanner = _scanners.FirstOrDefault(s => s.Kind == kind);
        return scanner is null ? Array.Empty<TransportCandidate>() : await scanner.ScanAsync(ct);
    }

    public async Task ConnectAsync(TransportCandidate candidate, CancellationToken ct = default)
    {
        await DisconnectAsync();
        var scanner = _scanners.First(s => s.Kind == candidate.Kind);
        var transport = scanner.Create(candidate);

        SetState(ConnectionState.Connecting);
        try
        {
            var conn = new ChameleonConnection(transport);
            conn.Disconnected += ex => { _ = HandleDropAsync(conn); };
            await conn.OpenAsync(ct);

            var client = new ChameleonClient(conn);
            var info = await client.GetDeviceInfoAsync(ct);

            _conn = conn;
            Client = client;
            DeviceInfo = info;
            Candidate = candidate;
            SetState(ConnectionState.Connected);
        }
        catch
        {
            await transport.DisposeAsync();
            SetState(ConnectionState.Disconnected);
            throw;
        }
    }

    private async Task HandleDropAsync(ChameleonConnection conn)
    {
        // A drop during the handshake fails ConnectAsync itself; a late drop from an older
        // connection must not tear down the current one.
        if (!ReferenceEquals(_conn, conn)) return;
        Console.WriteLine($"[Device] {Candidate?.Id} disconnected unexpectedly.");
        await DisconnectAsync();
    }

    public async Task DisconnectAsync()
    {
        var conn = _conn;
        _conn = null;
        Client = null;
        DeviceInfo = null;
        if (conn is not null)
            await conn.DisposeAsync();
        SetState(ConnectionState.Disconnected);
    }
}
