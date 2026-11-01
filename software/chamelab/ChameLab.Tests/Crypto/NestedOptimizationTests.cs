using System;
using System.Linq;
using System.Threading;
using ChameLab.Crypto;
using ChameLab.Services;
using Xunit;

namespace ChameLab.Tests.Crypto;

public class NestedOptimizationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(8191)]
    [InlineData(8192)]
    [InlineData(100000)]
    public void RadixSort_MatchesComparisonSortIncludingDuplicatesAndExtremes(int length)
    {
        var random = new Random(123456);
        var keys = Enumerable.Range(0, length)
            .Select(i => (i % 4) switch
            {
                0 => 0UL,
                1 => 0xFFFFFFFFFFFFUL,
                _ => (ulong)random.NextInt64(1L << 48)
            }).ToArray();
        var expected = keys.Order().ToArray();
        NestedCore.Sort48BitKeys(keys);
        Assert.Equal(expected, keys);
        NestedCore.Sort48BitKeys(keys);
        Assert.Equal(expected, keys);
        Array.Reverse(keys);
        NestedCore.Sort48BitKeys(keys);
        Assert.Equal(expected, keys);
    }

    [Fact]
    public void RadixSort_RejectsCancellation()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            NestedCore.Sort48BitKeys(new ulong[8192], stop.Token));
    }

    [Fact]
    public void TableRollback_MatchesBitwiseForEveryByteContribution()
    {
        for (var component = 0; component < 10; component++)
        for (var value = 0; value < 256; value++)
        {
            var state = new Crypto1State();
            uint input = 0;
            if (component < 3) state.Odd = (uint)value << (component * 8);
            else if (component < 6) state.Even = (uint)value << ((component - 3) * 8);
            else input = (uint)value << ((component - 6) * 8);
            CheckRollback(state, input);
        }
    }

    [Fact]
    public void TableRollback_MatchesBitwiseForMixedStatesAndInputs()
    {
        var random = new Random(0x123456);
        CheckRollback(new Crypto1State(uint.MaxValue, uint.MaxValue), uint.MaxValue);
        for (var i = 0; i < 4096; i++)
            CheckRollback(new Crypto1State((uint)random.NextInt64(1L << 32),
                (uint)random.NextInt64(1L << 32)), (uint)random.NextInt64(1L << 32));
    }

    private static void CheckRollback(Crypto1State state, uint input)
    {
        var expected = state;
        expected.LfsrRollbackWord(input, false);
        Assert.Equal(expected.Lfsr,
            NestedKeyTransform.GetKey(state, NestedKeyTransform.GetInputContribution(input)));
    }
}
