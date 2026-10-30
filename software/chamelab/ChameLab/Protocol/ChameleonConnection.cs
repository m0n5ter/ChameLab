using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Transport;

namespace ChameLab.Protocol;

public sealed class ChameleonException(string message, Status status) : Exception(message)
{
    public Status Status { get; } = status;
}

/// <summary>The link to the device is gone (unplugged, out of range or closed); no command can succeed.</summary>
public sealed class ChameleonDisconnectedException(Exception? inner = null) : IOException("The Chameleon was disconnected.", inner);

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
    private readonly ConcurrentDictionary<Command, SemaphoreSlim> _commandLocks = new();
    private Task? _readLoop;
    private volatile bool _closed;

    public ITransport Transport => transport;
    public bool IsConnected => transport.IsConnected;

    /// <summary>The link dropped or was disposed: every further command fails at once.</summary>
    public bool IsClosed => _closed;

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
            // Normal shutdown: DisposeAsync canceled, or closed the transport under a pending read.
        }
        catch (Exception ex)
        {
            _closed = true;
            FailAllPending(new ChameleonDisconnectedException(ex));
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
        // Responses carry only the command id, so two callers sending the same command at once
        // would get each other's answer (or one would wait forever): they take turns.
        if (_closed) throw new ChameleonDisconnectedException();
        var turn = _commandLocks.GetOrAdd(cmd, _ => new SemaphoreSlim(1, 1));
        await turn.WaitAsync(ct);
        try
        {
            if (_closed) throw new ChameleonDisconnectedException();
            var tcs = new TaskCompletionSource<ChameleonFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[cmd] = tcs;

            // The timeout covers the write too: a write to an unplugged port may never complete.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);
            await using var reg = timeoutCts.Token.Register(() =>
            {
                // Only this request's entry: a later one for the same command must not be failed.
                _pending.TryRemove(new KeyValuePair<Command, TaskCompletionSource<ChameleonFrame>>(cmd, tcs));
                if (ct.IsCancellationRequested) tcs.TrySetCanceled(ct);
                else tcs.TrySetException(new TimeoutException($"Command {cmd} timed out after {timeoutMs} ms."));
            });

            var frame = FrameCodec.Encode(cmd, data ?? Array.Empty<byte>(), status);
            await _writeLock.WaitAsync(timeoutCts.Token);
            try
            {
                await transport.WriteAsync(frame, timeoutCts.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _pending.TryRemove(new KeyValuePair<Command, TaskCompletionSource<ChameleonFrame>>(cmd, tcs));
                // A write timing out (or failing) on its own: report it like a reply that never came.
                if (_closed || ex is IOException or InvalidOperationException or ObjectDisposedException)
                    throw new ChameleonDisconnectedException(ex);
                throw new TimeoutException($"Command {cmd} timed out after {timeoutMs} ms.", ex);
            }
            finally
            {
                _writeLock.Release();
            }

            return await tcs.Task;
        }
        finally
        {
            turn.Release();
        }
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
        _closed = true;
        _cts.Cancel();
        // Close the transport before awaiting the read loop: some streams (SerialPort on
        // Windows) ignore the cancellation token, so only closing unblocks a pending read.
        await transport.DisposeAsync();
        if (_readLoop is { } loop)
        {
            try { await loop.WaitAsync(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
        }
        FailAllPending(new ChameleonDisconnectedException());
        _cts.Dispose();
    }
}
