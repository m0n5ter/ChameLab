using System;
using System.Threading;
using System.Threading.Tasks;

namespace ChameLab.Services;

/// <summary>Keeps the host awake until the last overlapping operation has finished.</summary>
public sealed class WakeLockService(Func<Task<IAsyncDisposable?>>? acquire = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _users;
    private IAsyncDisposable? _platformLock;

    public async Task<IAsyncDisposable> AcquireAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_users == 0 && acquire is not null)
            {
                try { _platformLock = await acquire(); }
                catch (Exception ex) { Console.WriteLine($"[WakeLock] {ex.Message}"); }
            }
            _users++;
            return ReleaseWith(ReleaseAsync);
        }
        finally { _gate.Release(); }
    }

    private async ValueTask ReleaseAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (--_users != 0) return;
            var platformLock = _platformLock;
            _platformLock = null;
            if (platformLock is not null)
            {
                try { await platformLock.DisposeAsync(); }
                catch (Exception ex) { Console.WriteLine($"[WakeLock] {ex.Message}"); }
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>Creates a lease whose release runs at most once.</summary>
    public static IAsyncDisposable ReleaseWith(Func<ValueTask> release) => new Lease(release);

    private sealed class Lease(Func<ValueTask> release) : IAsyncDisposable
    {
        private Func<ValueTask>? _release = release;
        public ValueTask DisposeAsync() => Interlocked.Exchange(ref _release, null)?.Invoke() ?? default;
    }
}
