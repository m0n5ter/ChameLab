using ChameLab.Crypto.Extensions;
using ChameLab.Crypto;
using Xunit;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ChameLab.Tests.Crypto;

public class OptimizationContractTest
{
    [Fact]
    public void Recovery32_Characterization_IsUnchanged()
    {
        var states = Crapto1.LfsrRecovery32(0, 0);
        Assert.Equal(186674, states.Count);
        Assert.Equal(0xAF7765E0518350C9UL, Hash(states));
    }

    [Fact]
    public void Recovery64_Characterization_IsUnchanged()
    {
        var states = Crapto1.LfsrRecovery64(0, 0);
        Assert.Single(states);
        Assert.Equal(0x08328807B4EB6FEDUL, Hash(states));
    }

    [Fact]
    public void VisitorAndListRecovery_AreIdenticalAcrossWorkspaceReuse()
    {
        var publicResult = Crapto1.LfsrRecovery32(0, 0);
        using var workspace = new Crypto1RecoveryWorkspace();
        var expected = Crapto1.LfsrRecovery32(0, 0, workspace);
        var actual = new List<Crypto1State>(expected.Count);

        var visit = Crapto1.VisitLfsrRecovery32(
            0,
            0,
            workspace,
            (in Crypto1State state) =>
            {
                actual.Add(state);
                return true;
            });

        Assert.True(visit.Completed);
        Assert.Equal(expected.Count, visit.VisitedCount);
        Assert.Equal(publicResult.ToArray(), expected.ToArray());
        Assert.Equal(expected.ToArray(), actual.ToArray());
    }

    [Fact]
    public void VisitorAndListRecovery64_AreIdenticalAcrossWorkspaceReuse()
    {
        var publicResult = Crapto1.LfsrRecovery64(0, 0);
        using var workspace = new Crypto1RecoveryWorkspace();
        var workspaceResult = Crapto1.LfsrRecovery64(0, 0, workspace);
        var visited = new List<Crypto1State>();

        var visit = Crapto1.VisitLfsrRecovery64(
            0,
            0,
            workspace,
            (in Crypto1State state) =>
            {
                visited.Add(state);
                return true;
            });

        Assert.True(visit.Completed);
        Assert.Equal(publicResult.ToArray(), workspaceResult.ToArray());
        Assert.Equal(workspaceResult.ToArray(), visited.ToArray());
    }

    [Fact]
    public void Visitor_CanStopRecovery()
    {
        using var workspace = new Crypto1RecoveryWorkspace();
        var visit = Crapto1.VisitLfsrRecovery32(
            0,
            0,
            workspace,
            (in Crypto1State _) => false);

        Assert.False(visit.Completed);
        Assert.Equal(1, visit.VisitedCount);
    }

    [Fact]
    public void RecoveryFilterTable_MatchesScalarFilterExhaustively()
    {
        for (var i = 0u; i < 1u << 20; i++)
            Assert.Equal(Crypto1.Filter(i), Crapto1.GetRecoveryFilterForTesting(i));
    }

    [Fact]
    public void Workspace_DisposeClearsUsedRegionOnly()
    {
        var pool = new TrackingUIntPool();
        var workspace = new Crypto1RecoveryWorkspace(pool);
        _ = Crapto1.VisitLfsrRecovery32(
            0,
            0,
            workspace,
            (in Crypto1State _) => false);
        workspace.Dispose();

        Assert.Equal(2, pool.Returned.Count);
        foreach (var array in pool.Returned)
        {
            var firstUnused = Array.IndexOf(array, TrackingUIntPool.Sentinel);
            Assert.True(firstUnused > 0);
            for (var i = 0; i < firstUnused; i++)
                Assert.Equal(0u, array[i]);
            for (var i = firstUnused; i < array.Length; i++)
                Assert.Equal(TrackingUIntPool.Sentinel, array[i]);
        }
        Assert.Throws<ObjectDisposedException>(
            () => Crapto1.LfsrRecovery32(0, 0, workspace));
    }

    [Fact]
    public void SpanEncrypt_MatchesArrayApi()
    {
        var arrayState = new Crypto1State(0x1234567890ABUL);
        var spanState = arrayState;
        var arrayData = new byte[] { 1, 2, 3, 4, 5 };
        var spanData = (byte[])arrayData.Clone();
        var arrayParity = new byte[arrayData.Length];
        var spanParity = new byte[arrayData.Length];

        arrayState.Encrypt(arrayData, arrayParity, 1, 3, true);
        spanState.Encrypt(
            spanData.AsSpan(1, 3),
            spanParity.AsSpan(1, 3),
            true);

        Assert.Equal(arrayData, spanData);
        Assert.Equal(arrayParity, spanParity);
        Assert.Equal(arrayState.Odd, spanState.Odd);
        Assert.Equal(arrayState.Even, spanState.Even);
    }

    [Fact]
    public void SpanIntegerApis_MatchArrayApis()
    {
        const uint uintValue = 0x89ABCDEF;
        const ulong ulongValue = 0x0123456789ABCDEF;
        Span<byte> uintBytes = stackalloc byte[6];
        Span<byte> ulongBytes = stackalloc byte[10];
        uintValue.WriteBytes(uintBytes);
        ulongValue.WriteBytes(ulongBytes);

        Assert.Equal(uintValue.GetBytes(6), uintBytes.ToArray());
        Assert.Equal(ulongValue.GetBytes(10), ulongBytes.ToArray());
        Assert.Equal(uintValue, uintBytes.Slice(2).ToUInt32());
        Assert.Equal(ulongValue, ulongBytes.Slice(2).ToUInt64());
    }

    [Fact]
    public void NonceDistance_FirstUseIsThreadSafe()
    {
        var results = new int[64];
        Parallel.For(
            0,
            results.Length,
            i => results[i] = Crapto1.NonceDistance(0xB830049B, 0xE9FAD974));
        Assert.True(results.All(value => value == results[0]));
    }

    [Fact]
    public void ValidationPipeline_StopsAfterSecondMatch()
    {
        var options = new MfKeyExecutionOptions { MaxDegreeOfParallelism = 1 };
        using var pipeline = new CandidateValidationPipeline(
            (Crypto1State _, out ulong key) =>
            {
                key = 0x1234;
                return true;
            },
            options);

        Assert.True(pipeline.Emit(default));
        Assert.False(pipeline.Emit(default));
        pipeline.Finish();
        Assert.Equal(2, pipeline.MatchCount);
    }

    [Fact]
    public void ValidationPipeline_ProducerBlockedOnFullQueue_StopsAfterSecondMatch()
    {
        // Workers hold their first batches until the gate opens, so the producer fills the
        // bounded queue and blocks adding the next batch. The second match then cancels the
        // stop token under that blocked Add, which must end production instead of throwing.
        // Three slots: the producer and two workers, whose queue holds four batches.
        using var gate = new ManualResetEventSlim();
        var options = new MfKeyExecutionOptions { MaxDegreeOfParallelism = 3 };
        using var pipeline = new CandidateValidationPipeline(
            (Crypto1State _, out ulong key) =>
            {
                gate.Wait();
                key = 0x1234;
                return true;
            },
            options);
        using var release = new Timer(_ => gate.Set(), null, 500, Timeout.Infinite);

        var emitted = 0;
        while (pipeline.Emit(default))
            emitted++;
        pipeline.Finish();

        // At least the four queued batches went in before the producer blocked (more when the
        // workers had already taken theirs).
        Assert.True(emitted >= 4096 * 4, $"the producer stopped after {emitted} states");
        Assert.True(pipeline.MatchCount >= 2);
    }

    [Fact]
    public void ValidationPipeline_PropagatesCancellation()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        var options = new MfKeyExecutionOptions
        {
            MaxDegreeOfParallelism = 1,
            CancellationToken = source.Token
        };
        using var pipeline = new CandidateValidationPipeline(
            (Crypto1State _, out ulong key) =>
            {
                key = 0;
                return false;
            },
            options);

        Assert.Throws<OperationCanceledException>(
            () => pipeline.Emit(default));
    }

    [Fact]
    public void MfKeyOptions_PropagatePreCanceledToken()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        var options = new MfKeyExecutionOptions
        {
            CancellationToken = source.Token
        };

        Assert.Throws<OperationCanceledException>(
            () => MfKey.MfKey32(0, 0, 0, 0, 0, 0, options));
        Assert.Throws<OperationCanceledException>(
            () => MfKey.MfKey64(0, 0, 0, 0, 0, options));
    }

    [Fact]
    public void ValidationPipeline_PropagatesWorkerException()
    {
        var options = new MfKeyExecutionOptions { MaxDegreeOfParallelism = 2 };
        using var pipeline = new CandidateValidationPipeline(
            (Crypto1State _, out ulong key) =>
            {
                key = 0;
                throw new InvalidOperationException("worker failure");
            },
            options);

        Assert.Throws<InvalidOperationException>(
            () =>
            {
                for (var i = 0; i < 4096; i++)
                    pipeline.Emit(default);
                pipeline.Finish();
            });
    }

    [Fact]
    public void RecoveryRangeSort_HandlesSortedReverseAndRandomInputs()
    {
        var random = new Random(123456);
        var randomValues = Enumerable.Range(0, 256)
            .Select(_ => (uint)random.Next())
            .ToArray();
        var cases = new[]
        {
            Enumerable.Range(0, 256).Select(i => (uint)i).ToArray(),
            Enumerable.Range(0, 256).Reverse().Select(i => (uint)i).ToArray(),
            randomValues
        };

        foreach (var values in cases)
        {
            var expected = values.OrderBy(value => value).ToArray();
            var ranged = new uint[values.Length + 2];
            ranged[0] = uint.MaxValue;
            ranged[ranged.Length - 1] = uint.MaxValue;
            Array.Copy(values, 0, ranged, 1, values.Length);

            Crapto1.SortRecoveryRange(ranged, 1, values.Length);

            Assert.Equal(uint.MaxValue, ranged[0]);
            Assert.Equal(uint.MaxValue, ranged[ranged.Length - 1]);
            Assert.Equal(
                expected,
                ranged.Skip(1).Take(values.Length).ToArray());
        }
    }

    private static ulong Hash(IReadOnlyList<Crypto1State> states)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        for (var i = 0; i < states.Count; i++)
        {
            hash = (hash ^ states[i].Odd) * prime;
            hash = (hash ^ states[i].Even) * prime;
        }
        return hash;
    }

    private sealed class TrackingUIntPool : ArrayPool<uint>
    {
        internal const uint Sentinel = 0xDEADBEEF;
        internal List<uint[]> Returned { get; } = new();

        public override uint[] Rent(int minimumLength)
        {
            var result = new uint[minimumLength];
            for (var i = 0; i < result.Length; i++)
                result[i] = Sentinel;
            return result;
        }

        public override void Return(uint[] array, bool clearArray = false)
        {
            Assert.False(clearArray);
            Returned.Add(array);
        }
    }
}
