using System;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Crypto.Hardnested;
using ChameLab.Localization;

namespace ChameLab.Services;

/// <summary>Portable managed Proxmark3 hardnested solver. No native libraries or processes.</summary>
public sealed class ManagedHardnestedEngine : IHardnestedEngine
{
    // Serialize expensive recoveries to bound working memory on mobile devices.
    private static readonly SemaphoreSlim RunLock = new(1, 1);
    public string? UnavailableReason => null;

    public async Task<ulong?> RecoverAsync(HardnestedCapture capture, IProgress<string>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await RunLock.WaitAsync(ct).ConfigureAwait(false);
        try { return await Task.Run(() => Recover(capture, progress, ct), ct).ConfigureAwait(false); }
        finally { RunLock.Release(); }
    }

    // Browser workers call this directly: their own runtime supplies isolation and cancellation.
    public static ulong? Recover(HardnestedCapture capture, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var reduction = new HardnestedReduction(capture);
        progress?.Report(Strings.Recovery_LoadingTables);
        var tables = HardnestedTables.Shared.Value;
        ct.ThrowIfCancellationRequested();
        reduction.Reduce(tables, progress, ct);
        progress?.Report(Strings.Recovery_MatchingConstraints);
        var candidates = reduction.Candidates(tables, out int first, ct);
        progress?.Report($"Hardnested candidates: {System.Linq.Enumerable.Sum(candidates[1], c => c.Length):N0} odd, " +
            $"{System.Linq.Enumerable.Sum(candidates[0], c => c.Length):N0} even states…");
        var search = new HardnestedSearch(reduction.Capture, first, reduction.Nonces[first], ct);
        return search.Run(candidates, reduction.Sum0, progress, (half, states, start, end) => reduction.Refine(first, half, states, ct, start, end), prior: reduction.SumWeights);
    }
}
