using System;

namespace ChameLab.Services;

/// <summary>Session-local costs for choosing key reuse or a fresh Nested attack.</summary>
internal sealed class NestedRecoveryTiming
{
    private double _keyMilliseconds;
    private int _keyAttempts;
    private double _recoveryMilliseconds;
    private int _recoveries;

    internal TimeSpan? RecoveryTime => _recoveries == 0 ? null :
        TimeSpan.FromMilliseconds(_recoveryMilliseconds / _recoveries);

    internal TimeSpan? EstimateKnownKeys(int untestedKeys) => _keyAttempts == 0 ? null :
        TimeSpan.FromMilliseconds(_keyMilliseconds / _keyAttempts * untestedKeys);

    internal bool ShouldCheckKnownKeys(int untestedKeys)
    {
        if (untestedKeys <= 0) return false;
        // Keep reusing keys until both paths have real measurements. Equality
        // favors authentication, which can finish before the full list is tried.
        return EstimateKnownKeys(untestedKeys) is not { } known ||
               RecoveryTime is not { } recovery || known <= recovery;
    }

    internal void RecordKeyAttempt(TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero) return;
        _keyMilliseconds += elapsed.TotalMilliseconds;
        _keyAttempts++;
    }

    // Only authenticated recoveries calibrate this estimate. Empty captures,
    // failed attacks and cancellations must not make Nested appear cheap.
    internal void RecordRecovery(TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero) return;
        _recoveryMilliseconds += elapsed.TotalMilliseconds;
        _recoveries++;
    }
}
