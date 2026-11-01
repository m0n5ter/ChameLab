using System;
using ChameLab.Services;
using Xunit;

namespace ChameLab.Tests;

public class NestedRecoveryTimingTests
{
    [Fact]
    public void ChecksKnownKeysUntilBothPathsHaveMeasurements()
    {
        var timing = new NestedRecoveryTiming();
        Assert.False(timing.ShouldCheckKnownKeys(0));
        Assert.True(timing.ShouldCheckKnownKeys(80));
        timing.RecordKeyAttempt(TimeSpan.FromMilliseconds(40));
        Assert.True(timing.ShouldCheckKnownKeys(80));
    }

    [Fact]
    public void GrowingKeyListSwitchesToNestedAtMeasuredCrossover()
    {
        var timing = new NestedRecoveryTiming();
        timing.RecordKeyAttempt(TimeSpan.FromMilliseconds(40));
        timing.RecordRecovery(TimeSpan.FromMilliseconds(100));
        Assert.True(timing.ShouldCheckKnownKeys(2));
        Assert.Equal(TimeSpan.FromMilliseconds(80), timing.EstimateKnownKeys(2));
        Assert.False(timing.ShouldCheckKnownKeys(3));
        Assert.True(timing.ShouldCheckKnownKeys(1)); // Already-rejected keys need no retry.
    }

    [Fact]
    public void UsesMeasuredAveragesAndPrefersReuseOnEqualCosts()
    {
        var timing = new NestedRecoveryTiming();
        timing.RecordKeyAttempt(TimeSpan.FromMilliseconds(20));
        timing.RecordKeyAttempt(TimeSpan.FromMilliseconds(60));
        timing.RecordRecovery(TimeSpan.FromMilliseconds(80));
        timing.RecordRecovery(TimeSpan.FromMilliseconds(160));
        Assert.Equal(TimeSpan.FromMilliseconds(120), timing.RecoveryTime);
        Assert.True(timing.ShouldCheckKnownKeys(3));
        Assert.False(timing.ShouldCheckKnownKeys(4));
        timing.RecordRecovery(TimeSpan.FromMilliseconds(360));
        Assert.True(timing.ShouldCheckKnownKeys(4)); // Adapts when Nested becomes slower.
    }

    [Fact]
    public void ZeroDurationDoesNotCalibrateEitherPath()
    {
        var timing = new NestedRecoveryTiming();
        timing.RecordKeyAttempt(TimeSpan.Zero);
        timing.RecordRecovery(TimeSpan.Zero);
        Assert.Null(timing.EstimateKnownKeys(3));
        Assert.Null(timing.RecoveryTime);
        Assert.True(timing.ShouldCheckKnownKeys(3));
    }
}
