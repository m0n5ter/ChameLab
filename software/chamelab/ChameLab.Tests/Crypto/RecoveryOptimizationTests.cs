using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ChameLab.Crypto;
using ChameLab.Services;
using Xunit;

namespace ChameLab.Tests.Crypto;

public class RecoveryOptimizationTests
{
    [Fact]
    public void CachedSeeds_MatchIndependentExtensionForAllPrefixes()
    {
        for (var prefix = 0; prefix < 32; prefix++)
        {
            var values = new List<uint> { 0 };
            for (var i = 1 << 20; i >= 0; i--)
                if (Crypto1.Filter((uint)i) == (prefix & 1)) values.Add((uint)i);
            for (var bit = 1; bit < 5; bit++)
            {
                var next = new List<uint>();
                foreach (var value in values)
                for (uint low = 0; low < 2; low++)
                {
                    var candidate = (value << 1) | low;
                    if (Crypto1.Filter(candidate) == ((prefix >> bit) & 1)) next.Add(candidate);
                }
                values = next;
            }
            values.Sort();
            Assert.Equal(values, Crapto1.GetRecoverySeed(prefix).Order());
        }
    }

    [Theory]
    [InlineData(0u, 0u)]
    [InlineData(uint.MaxValue, uint.MaxValue)]
    [InlineData(0x12345678u, 0xABCDEF01u)]
    public void ParallelGroups_PreserveCompleteStateMultiset(uint stream, uint input)
    {
        using var workspace = new Crypto1RecoveryWorkspace();
        var expected = Crapto1.LfsrRecovery32(stream, input, workspace);
        var batches = Enumerable.Range(0, 4).Select(_ => new List<Crypto1State>()).ToArray();
        var sinks = batches.Select(batch => new Func<Crypto1State, bool>(state =>
            { batch.Add(state); return true; })).ToArray();
        workspace.Enter();
        try
        {
            Assert.True(Crapto1.Recover32Core(stream, input, workspace, sinks[0],
                new uint[Crypto1RecoveryWorkspace.Recovery32BufferLength], true, sinks));
        }
        finally { workspace.Exit(); }
        static ulong Pack(Crypto1State state) => ((ulong)state.Odd << 32) | state.Even;
        Assert.Equal(expected.Select(Pack).Order(), batches.SelectMany(x => x).Select(Pack).Order());
    }

    [Fact]
    public void ParallelGroups_PropagateCancellation()
    {
        using var workspace = new Crypto1RecoveryWorkspace();
        using var stop = new CancellationTokenSource();
        var sinks = Enumerable.Range(0, 4).Select(_ => new Func<Crypto1State, bool>(state =>
        {
            stop.Cancel();
            stop.Token.ThrowIfCancellationRequested();
            return true;
        })).ToArray();
        workspace.Enter();
        try
        {
            Assert.ThrowsAny<OperationCanceledException>(() => Crapto1.Recover32Core(0x12345678, 0,
                workspace, sinks[0], new uint[Crypto1RecoveryWorkspace.Recovery32BufferLength],
                true, sinks, stop.Token));
        }
        finally { workspace.Exit(); }
    }

    [Fact]
    public void DarksideStagedParityAndZeroRollback_MatchFullWordReference()
    {
        var random = new Random(0x321ABC);
        for (var i = 0; i < 4096; i++)
        {
            var odd = (uint)random.NextInt64(1L << 32);
            var even = (uint)random.NextInt64(1L << 32);
            var prefix = (uint)random.NextInt64(1L << 32);
            var response = (uint)random.NextInt64(1L << 32);
            var parities = new byte[8];
            // Construct parity consistent with all eight trials so every byte
            // rejection stage and the entire successful path are exercised.
            Crypto1State expected = default;
            for (var c = 0; c < 8; c++)
            {
                var state = new Crypto1State(odd ^ Fast[1][c], even ^ Fast[0][c]);
                state.LfsrRollbackBit(); state.LfsrRollbackBit();
                var ks3 = state.LfsrRollbackBit();
                var ks2 = state.LfsrRollbackWord();
                var nr = state.LfsrRollbackWord(prefix | ((uint)c << 5), true) ^ (prefix | ((uint)c << 5));
                var rr = ks2 ^ response;
                parities[c] = (byte)(
                    ((Crypto1.EvenParity32((byte)nr) ^ (ks2 >> 24) ^ 1) & 1) << 3 |
                    ((Crypto1.EvenParity32((byte)(rr >> 24)) ^ (ks2 >> 16) ^ 1) & 1) << 4 |
                    ((Crypto1.EvenParity32((byte)(rr >> 16)) ^ (ks2 >> 8) ^ 1) & 1) << 5 |
                    ((Crypto1.EvenParity32((byte)(rr >> 8)) ^ ks2 ^ 1) & 1) << 6 |
                    ((Crypto1.EvenParity32((byte)rr) ^ (uint)ks3 ^ 1) & 1) << 7);
                if (c == 0) Assert.Equal(state, DarksideRecovery.CheckPfxParity(prefix, response, parities, odd, even, true));
                expected = state;
            }
            Assert.Equal(expected, DarksideRecovery.CheckPfxParity(prefix, response, parities, odd, even, false));
            // Flip each relevant parity bit in each trial, including late trials.
            for (var c = 0; c < 8; c++)
            for (var bit = 3; bit < 8; bit++)
            {
                parities[c] ^= (byte)(1 << bit);
                Assert.Null(DarksideRecovery.CheckPfxParity(prefix, response, parities, odd, even, false));
                parities[c] ^= (byte)(1 << bit);
            }
        }
    }

    private static readonly uint[][] Fast =
    [
        [0, 0x4BC53, 0xECB1, 0x450E2, 0x25E29, 0x6E27A, 0x2B298, 0x60ECB],
        [0, 0x1D962, 0x4BC53, 0x56531, 0xECB1, 0x135D3, 0x450E2, 0x58980],
    ];

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(0, 2)]
    [InlineData(1, 2)]
    public void DarksideBitPlanes_MatchOriginalPrefixScan(int half, int pattern)
    {
        byte[] ks = pattern switch
        {
            1 => new byte[8],
            2 => Enumerable.Repeat((byte)255, 8).ToArray(),
            _ => [0xC, 9, 5, 0xB, 2, 0xD, 0xB, 0xA]
        };
        var expected = new List<uint>();
        for (uint i = 0; i < 1 << 21; i++)
        {
            var good = true;
            for (var c = 0; good && c < 8; c++)
            {
                var entry = i ^ Fast[half][c];
                good = ((ks[c] >> half) & 1) == Crypto1.Filter(entry >> 1) &&
                    ((ks[c] >> (half + 2)) & 1) == Crypto1.Filter(entry);
            }
            if (good) expected.Add(i);
        }
        Assert.Equal(expected, DarksideRecovery.LfsrPrefixKs(ks, half, CancellationToken.None));
    }
}
