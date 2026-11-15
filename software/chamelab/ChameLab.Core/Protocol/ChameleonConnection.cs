using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Transport;
using ChameLab.Localization;

namespace ChameLab.Protocol;

public sealed class ChameleonException(string message, Status status) : Exception(message)
{
    public Status Status { get; } = status;
}

/// <summary>The link to the device is gone (unplugged, out of range or closed); no command can succeed.</summary>
public sealed class ChameleonDisconnectedException(Exception? inner = null) : IOException(Strings.Device_Disconnected, inner);

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
    // Taken after the command's own lock, for transports that allow one request in flight.
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private Task? _readLoop;
    private volatile bool _closed;

    // Exclusive use (RunExclusiveAsync): the flow that holds it, requests of others in flight, and the
    // signals for "exclusive use ended" and "the others' requests are done".
    private static readonly AsyncLocal<ChameleonConnection?> ExclusiveOwner = new();
    private readonly object _exclusiveSync = new();
    private int _sharedInFlight;
    private TaskCompletionSource? _exclusiveEnded;
    private TaskCompletionSource? _sharedDrained;

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
        var shared = !ReferenceEquals(ExclusiveOwner.Value, this);
        if (shared) await EnterSharedAsync(ct);
        try
        {
            return await SendCoreAsync(cmd, data, status, timeoutMs, ct);
        }
        finally
        {
            if (shared) ExitShared();
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> with the device to itself: requests from other flows wait until it ends, and
    /// it starts once theirs in flight are answered. Requests made by <paramref name="work"/> (and whatever it
    /// awaits) go straight through; a nested call just runs.
    /// </summary>
    public async Task RunExclusiveAsync(Func<Task> work, CancellationToken ct = default)
    {
        if (ReferenceEquals(ExclusiveOwner.Value, this))
        {
            await work();
            return;
        }
        TaskCompletionSource ended;
        Task? drained;
        while (true)
        {
            Task busy;
            lock (_exclusiveSync)
            {
                if (_exclusiveEnded is null)
                {
                    ended = _exclusiveEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _sharedDrained = _sharedInFlight > 0 ? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) : null;
                    drained = _sharedDrained?.Task;
                    break;
                }
                busy = _exclusiveEnded.Task;
            }
            await busy.WaitAsync(ct);
        }
        try
        {
            if (drained is not null) await drained.WaitAsync(ct);
            ExclusiveOwner.Value = this;
            await work();
        }
        finally
        {
            ExclusiveOwner.Value = null;
            lock (_exclusiveSync)
            {
                _exclusiveEnded = null;
                _sharedDrained = null;
            }
            ended.TrySetResult();
        }
    }

    private async Task EnterSharedAsync(CancellationToken ct)
    {
        while (true)
        {
            Task busy;
            lock (_exclusiveSync)
            {
                if (_exclusiveEnded is null)
                {
                    _sharedInFlight++;
                    return;
                }
                busy = _exclusiveEnded.Task;
            }
            await busy.WaitAsync(ct);
        }
    }

    private void ExitShared()
    {
        lock (_exclusiveSync)
        {
            if (--_sharedInFlight == 0) _sharedDrained?.TrySetResult();
        }
    }

    private async Task<ChameleonFrame> SendCoreAsync(Command cmd, byte[]? data, ushort status, int timeoutMs, CancellationToken ct)
    {
        var turn = _commandLocks.GetOrAdd(cmd, _ => new SemaphoreSlim(1, 1));
        await turn.WaitAsync(ct);
        var gated = false;
        try
        {
            // The timeout starts once the request may go out: waiting behind a long command
            // (a nested attack, an LF scan) must not use it up.
            if (transport.OneRequestAtATime)
            {
                await _requestGate.WaitAsync(ct);
                gated = true;
            }
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
                else tcs.TrySetException(new TimeoutException(string.Format(Strings.Device_CommandTimeout, cmd, timeoutMs)));
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
                throw new TimeoutException(string.Format(Strings.Device_CommandTimeout, cmd, timeoutMs), ex);
            }
            finally
            {
                _writeLock.Release();
            }

            return await tcs.Task;
        }
        finally
        {
            if (gated) _requestGate.Release();
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
                string.Format(Strings.Device_CommandFailed, cmd, frame.Status.Describe()), frame.Status);
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
