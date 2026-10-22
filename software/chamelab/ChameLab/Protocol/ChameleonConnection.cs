using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Transport;

namespace ChameLab.Protocol;

public sealed class ChameleonException(string message, Status status) : Exception(message)
{
    public Status Status { get; } = status;
}

/// <summary>
/// Owns a transport, runs a background read loop, and exposes an async
/// request/response API. Responses are matched to requests by command id,
/// mirroring the firmware's one-outstanding-request-per-command contract.
/// </summary>
public sealed class ChameleonConnection(ITransport transport) : IAsyncDisposable
{
    private readonly FrameParser _parser = new();
    private readonly ConcurrentDictionary<Command, TaskCompletionSource<ChameleonFrame>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private Task? _readLoop;

    public ITransport Transport => transport;
    public bool IsConnected => transport.IsConnected;

    /// <summary>Raised (on a background thread) when the link drops unexpectedly.</summary>
    public event Action<Exception?>? Disconnected;

    public async Task OpenAsync(CancellationToken ct = default)
    {
        await transport.ConnectAsync(ct);
        _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token));
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[2048];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var n = await transport.ReadAsync(buffer, ct);
                if (n <= 0) continue;
                foreach (var frame in _parser.Push(buffer.AsMemory(0, n)))
                {
                    if (_pending.TryRemove(frame.Command, out var tcs))
                        tcs.TrySetResult(frame);
                }
            }
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // Normal shutdown: DisposeAsync cancelled, or closed the transport under a pending read.
        }
        catch (Exception ex)
        {
            FailAllPending(ex);
            Disconnected?.Invoke(ex);
        }
    }

    private void FailAllPending(Exception ex)
    {
        foreach (var kv in _pending)
            if (_pending.TryRemove(kv.Key, out var tcs))
                tcs.TrySetException(ex);
    }

    /// <summary>Send a command and await its response frame.</summary>
    public async Task<ChameleonFrame> SendAsync(
        Command cmd, byte[]? data = null, ushort status = 0,
        int timeoutMs = 3000, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<ChameleonFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[cmd] = tcs; // replaces any stale entry, as the reference client does

        var frame = FrameCodec.Encode(cmd, data ?? Array.Empty<byte>(), status);
        await _writeLock.WaitAsync(ct);
        try
        {
            await transport.WriteAsync(frame, ct);
        }
        finally
        {
            _writeLock.Release();
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);
        await using var reg = timeoutCts.Token.Register(() =>
        {
            if (_pending.TryRemove(cmd, out var t))
                t.TrySetException(new TimeoutException($"Command {cmd} timed out after {timeoutMs} ms."));
        });

        return await tcs.Task;
    }

    /// <summary>Send a command and require one of the accepted statuses.</summary>
    public async Task<ChameleonFrame> SendExpectAsync(
        Command cmd, Status expected, byte[]? data = null, ushort status = 0,
        int timeoutMs = 3000, CancellationToken ct = default)
    {
        var frame = await SendAsync(cmd, data, status, timeoutMs, ct);
        if (frame.Status != expected)
            throw new ChameleonException(
                $"{cmd} failed: {frame.Status.Describe()}", frame.Status);
        return frame;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        // Close the transport before awaiting the read loop: some streams (SerialPort on
        // Windows) ignore the cancellation token, so only closing unblocks a pending read.
        await transport.DisposeAsync();
        if (_readLoop is { } loop)
        {
            try { await loop.WaitAsync(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
        }
        FailAllPending(new ObjectDisposedException(nameof(ChameleonConnection)));
        _cts.Dispose();
    }
}
