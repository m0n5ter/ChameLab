using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ChameLab.Models;

/// <summary>Sector skips belong to one operation, independently of its Stop token.</summary>
public sealed class MifareSectorControl
{
    private readonly object _gate = new();
    private readonly HashSet<int> _skipped = [];
    private readonly List<Scope> _active = [];
    public event Action? Changed;

    public int CurrentSector
    {
        get
        {
            lock (_gate)
                return _active.Count > 0 && !_skipped.Contains(_active[^1].Sector) ? _active[^1].Sector : -1;
        }
    }

    public bool IsSkipped(int sector) { lock (_gate) return _skipped.Contains(sector); }

    public int SkipCurrent()
    {
        int sector;
        lock (_gate)
        {
            sector = CurrentSector;
            if (sector < 0) return -1;
            _skipped.Add(sector);
            foreach (var scope in _active.ToArray())
                if (scope.Sector == sector) scope.Cancellation?.Cancel();
        }
        Changed?.Invoke();
        return sector;
    }

    /// <summary>Batch commands cannot be interrupted; their next request excludes skipped sectors.</summary>
    internal IDisposable Watch(int sector) => Enter(sector, null);

    internal async Task<bool> RunAsync(int sector, CancellationToken ct, Func<CancellationToken, Task> operation)
    {
        ct.ThrowIfCancellationRequested();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var scope = Enter(sector, cancellation);
        try
        {
            cancellation.Token.ThrowIfCancellationRequested();
            await operation(cancellation.Token);
            return !IsSkipped(sector);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && IsSkipped(sector)) { return false; }
    }

    private Scope Enter(int sector, CancellationTokenSource? cancellation)
    {
        var scope = new Scope(this, sector, cancellation);
        lock (_gate)
        {
            _active.Add(scope);
            if (_skipped.Contains(sector)) cancellation?.Cancel();
        }
        Changed?.Invoke();
        return scope;
    }

    private sealed class Scope(MifareSectorControl owner, int sector, CancellationTokenSource? cancellation) : IDisposable
    {
        internal int Sector => sector;
        internal CancellationTokenSource? Cancellation => cancellation;
        public void Dispose()
        {
            lock (owner._gate) owner._active.Remove(this);
            owner.Changed?.Invoke();
        }
    }
}
