using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace ChameLab.Transport;

/// <summary>
/// Cross-platform TCP transport. Useful for a USB-serial→TCP bridge (e.g. on
/// Android) and for development against a firmware simulator.
/// Candidate ids are "host:port".
/// </summary>
public sealed class TcpTransport(string host, int port) : ITransport
{
    private TcpClient? _client;
    private NetworkStream? _stream;

    public TransportKind Kind => TransportKind.Tcp;
    public string DisplayName => $"tcp:{host}:{port}";
    public bool IsConnected => _client?.Connected ?? false;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _client = new TcpClient { NoDelay = true };
        await _client.ConnectAsync(host, port, ct);
        _stream = _client.GetStream();
    }

    public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (_stream is null) throw new InvalidOperationException("Not connected.");
        return _stream.WriteAsync(data, ct).AsTask();
    }

    public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_stream is null) throw new InvalidOperationException("Not connected.");
        return await _stream.ReadAsync(buffer, ct);
    }

    public ValueTask DisposeAsync()
    {
        _stream?.Dispose();
        _client?.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>TCP scanner: cannot enumerate, but parses a user-entered "host:port" id.</summary>
public sealed class TcpScanner : ITransportScanner
{
    public TransportKind Kind => TransportKind.Tcp;
    public string DisplayName => "Network (TCP)";
    public AutoScan AutoScan => AutoScan.Never;

    public Task<IReadOnlyList<TransportCandidate>> ScanAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TransportCandidate>>(Array.Empty<TransportCandidate>());

    public ITransport Create(TransportCandidate candidate)
    {
        var (host, port) = Parse(candidate.Id);
        return new TcpTransport(host, port);
    }

    public static TransportCandidate Candidate(string hostPort)
    {
        var (host, port) = Parse(hostPort);
        return new TransportCandidate(TransportKind.Tcp, $"{host}:{port}", $"tcp:{host}:{port}", "Manual network endpoint");
    }

    private static (string host, int port) Parse(string id)
    {
        var idx = id.LastIndexOf(':');
        if (idx <= 0 || !int.TryParse(id[(idx + 1)..], out var port))
            throw new FormatException($"Expected host:port, got '{id}'.");
        return (id[..idx], port);
    }
}
