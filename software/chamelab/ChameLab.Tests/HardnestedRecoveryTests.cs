using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Services;
using Xunit;

namespace ChameLab.Tests;

public class HardnestedRecoveryTests
{
    [Fact]
    public async Task Collector_UsesBothSamplesAndPreservesAuthenticationUid()
    {
        var samples = Enumerable.Range(0, 256)
            .Select(value => new HardnestedSample((uint)value << 24, 0)).ToArray();
        var source = new Source((_, _) => samples);

        var capture = await HardnestedCollector.CollectAsync(source, 0x12345678, null, default, minimumSamples: 256);

        Assert.Equal(0x12345678u, capture.AuthenticationUid);
        Assert.Equal(samples, capture.Samples);
        Assert.Equal(new[] { false }, source.SlowModes);
    }

    [Fact]
    public async Task Collector_DuplicateFirstBytesDoNotCompleteCoverage()
    {
        var source = new Source((_, _) => new[] { new HardnestedSample(0, 0) });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HardnestedCollector.CollectAsync(source, 1, null, default, minimumSamples: 2, maximumBatches: 3));

        Assert.Equal(new[] { false, false, false, true, true, true }, source.SlowModes);
    }

    [Fact]
    public async Task Collector_InconsistentParityRetriesWithFreshSlowCapture()
    {
        var good = Enumerable.Range(0, 256)
            .Select(value => new HardnestedSample((uint)value << 24, 0)).ToArray();
        var source = new Source((slow, _) => slow ? good :
            new[] { new HardnestedSample(0, 0), new HardnestedSample(1, 8) });

        var capture = await HardnestedCollector.CollectAsync(source, 1, null, default, minimumSamples: 256);

        Assert.Equal(good, capture.Samples);
        Assert.Equal(new[] { false, true }, source.SlowModes);
    }

    [Fact]
    public async Task Collector_RejectsImpossibleSum()
    {
        var samples = Enumerable.Range(0, 256)
            .Select(value => new HardnestedSample((uint)value << 24, value == 0 ? (byte)8 : (byte)0)).ToArray();
        var source = new Source((_, _) => samples);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HardnestedCollector.CollectAsync(source, 1, null, default, minimumSamples: 256));

        Assert.Equal(new[] { false, true }, source.SlowModes);
    }

    [Fact]
    public async Task Collector_CancellationStopsBeforeNextBatch()
    {
        using var stop = new CancellationTokenSource();
        var source = new Source((_, _) =>
        {
            stop.Cancel();
            return new[] { new HardnestedSample(0, 0) };
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            HardnestedCollector.CollectAsync(source, 1, null, stop.Token));

        Assert.Single(source.SlowModes);
    }

    [Fact]
    public async Task UnsupportedEngine_ReportsMissingComponent()
    {
        var engine = new UnavailableHardnestedEngine();
        Assert.Contains("not available", engine.UnavailableReason, StringComparison.OrdinalIgnoreCase);
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            engine.RecoverAsync(new HardnestedCapture(1, Array.Empty<HardnestedSample>()), null, default));
    }

    [Fact]
    public void CaptureCodec_UsesUnsignedDecimalAndFourParityBits()
    {
        var capture = new HardnestedCapture(1, new[] {
            new HardnestedSample(0xFFFFFFFF, 15), new HardnestedSample(0, 0)
        });
        Assert.Equal("4294967295|15\n0|0\n", HardnestedCaptureCodec.Encode(capture));
    }

    [Fact]
    public void CaptureCodec_RejectsEmptyAndInvalidParity()
    {
        Assert.Throws<ArgumentException>(() => HardnestedCaptureCodec.Encode(
            new HardnestedCapture(1, Array.Empty<HardnestedSample>())));
        Assert.Throws<ArgumentException>(() => HardnestedCaptureCodec.Encode(
            new HardnestedCapture(1, new[] { new HardnestedSample(1, 16) })));
    }

    private sealed class Source(Func<bool, CancellationToken, IReadOnlyList<HardnestedSample>> read) : IHardnestedNonceSource
    {
        public List<bool> SlowModes { get; } = new();
        public Task<IReadOnlyList<HardnestedSample>> ReadBatchAsync(bool slow, CancellationToken ct)
        {
            SlowModes.Add(slow);
            return Task.FromResult(read(slow, ct));
        }
    }
}
