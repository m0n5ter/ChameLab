using System;
using System.Linq;
using System.Numerics;
using ChameLab.Crypto;
using ChameLab.Crypto.Hardnested;
using Xunit;

namespace ChameLab.Tests.Crypto;

public class HardnestedOptimizationTests
{
    [Fact]
    public void CachedHypergeometric_MatchesDirectLogProducts()
    {
        static double Choose(int n, int k)
        {
            double value = 0;
            for (int i = 1; i <= Math.Min(k, n - k); i++) value += Math.Log(n - i + 1) - Math.Log(i);
            return value;
        }
        foreach (int population in HardnestedTables.Sums)
            foreach (int drawn in new[] { 0, 1, 17, 128, 256 })
            {
                double total = 0;
                for (int observed = 0; observed <= drawn; observed++)
                {
                    double expected = observed > population || drawn - observed > 256 - population ? 0 :
                        Math.Exp(Choose(population, observed) + Choose(256 - population, drawn - observed) - Choose(256, drawn));
                    double actual = HardnestedSearch.Hypergeometric(population, drawn, observed);
                    Assert.True(Math.Abs(expected - actual) <= 1e-10 * Math.Max(expected, 1e-100));
                    total += actual;
                }
                Assert.InRange(total, 1 - 1e-10, 1 + 1e-10);
            }
    }

    [Theory]
    [InlineData(0x112233445566UL, 0x12345678u, false)]
    [InlineData(0UL, 0u, false)]
    [InlineData(0xffffffffffffUL, 0x12345678u, false)]
    [InlineData(0xffffffffffffUL, 0x12345678u, true)]
    public void ParallelReduction_PreservesEveryEffectiveState(ulong key, uint uid, bool complementParity)
    {
        var tables = HardnestedTables.Shared.Value;
        var capture = ManagedHardnestedTests.Generate(key, uid, 4096);
        if (complementParity) capture = capture with {
            Samples = capture.Samples.Select(s => s with { EncryptedParity = (byte)(s.EncryptedParity ^ 8) }).ToArray()
        };
        var actual = new HardnestedReduction(capture);
        actual.Reduce(tables, null, default);
        // Preserve the original scalar sequential algorithm as the oracle.
        for (int half = 0; half < 2; half++)
        {
            var pool = new HardnestedBitSet.PagePool();
            var maps = new HardnestedBitSet[256];
            var domain = new uint[1 << 19];
            var work = new uint[domain.Length];
            var active = new int[domain.Length];
            var allowed = new bool[9];
            for (int p = 0; p < 9; p++)
                for (int q = 0; q < 9; q++)
                    if (HardnestedTables.Sum(p, q) == actual.Sum0) allowed[p] = true;
            var parts = tables.PartialSums[half];
            for (int i = 0; i < domain.Length; i++)
                domain[i] = (allowed[parts[2 * i]] ? 0xffff0000u : 0) | (allowed[parts[2 * i + 1]] ? 0xffffu : 0);
            for (int first = 0; first < 256; first++)
            {
                domain.CopyTo(work, 0);
                int length = 0;
                for (int i = 0; i < work.Length; i++) if (work[i] != 0) active[length++] = i;
                foreach (var table in tables.Tables[half])
                {
                    if (!actual.HasBitflip(first, table.Flip)) continue;
                    int remaining = 0;
                    for (int j = 0; j < length; j++)
                    {
                        int index = active[j];
                        uint word = work[index] & tables.Words[table.Pages[index >> 6] * 64 + (index & 63)];
                        work[index] = word;
                        if (word != 0) active[remaining++] = index;
                    }
                    length = remaining;
                }
                maps[first] = new(work, pool);
                for (int i = 0; i < domain.Length; i++)
                {
                    uint word = work[i];
                    domain[i] &= (word >> 16 != 0 ? 0xffff0000u : 0) | ((word & 65535) != 0 ? 0xffffu : 0);
                }
            }
            pool.Freeze();
            Assert.True(domain.AsSpan().SequenceEqual(actual.Domains[half]));
            var nonzero = Enumerable.Range(0, domain.Length).Where(i => domain[i] != 0).ToArray();
            for (int first = 0; first < 256; first++)
                foreach (int i in nonzero)
                    Assert.Equal(maps[first].Word(i) & domain[i], actual.States[half][first].Word(i) & domain[i]);
            foreach (int first in new[] { 0, 127, 255 })
                foreach (uint state in actual.States[half][first].States(domain).Take(512))
                    Assert.Equal(ReferenceAll(actual, first, state, half), actual.MatchesAll(first, state, half));
        }
    }

    [Fact]
    public void RefineRanges_PreserveOrderAndBoundarySurvivors()
    {
        const ulong key = 0x112233445566;
        const uint uid = 0x12345678;
        var reduction = new HardnestedReduction(ManagedHardnestedTests.Generate(key, uid, 4096));
        reduction.Reduce(HardnestedTables.Shared.Value, null, default);
        var state = new Crypto1State(key);
        state.Crypto1Byte((byte)(uid >> 24), true);
        uint valid = state.Even & 0xffffff;
        var input = Enumerable.Range(0, 32770).Select(i => (uint)(i * 12345) & 0xffffff).ToArray();
        foreach (int i in new[] { 0, 16383, 16384, 16385, 32769 }) input[i] = valid;
        var whole = reduction.Refine(0, 0, input, default);
        var split = reduction.Refine(0, 0, input, default, 0, 16384)
            .Concat(reduction.Refine(0, 0, input, default, 16384, 32768))
            .Concat(reduction.Refine(0, 0, input, default, 32768, input.Length)).ToArray();
        Assert.Contains(valid, whole);
        Assert.True(whole.AsSpan().SequenceEqual(split));
    }

    private static bool ReferenceAll(HardnestedReduction reduction, int first, uint state, int half)
    {
        uint[][] masks = [
            [0xfffff0, 0xfffff8, 0xfffff8, 0xfffffc, 0xfffffc, 0xfffffe, 0xfffffe, 0xffffff],
            [0xfffff0, 0xfffff0, 0xfffff8, 0xfffff8, 0xfffffc, 0xfffffc, 0xfffffe, 0xfffffe]];
        for (uint i = 1; i < 256; i++)
        {
            int diff = (int)((i * 0x0202020202UL & 0x010884422010UL) % 1023);
            int common = BitOperations.TrailingZeroCount((uint)diff);
            uint mask = masks[half][common];
            bool found = false;
            for (uint low = 0; low <= (~mask & 255); low++)
            {
                uint other = (state & mask) | low;
                if (reduction.States[half][first ^ diff].Contains(other) && Reference(common, diff, state, other, half))
                { found = true; break; }
            }
            if (!found) return false;
        }
        return true;
    }

    [Fact]
    public void UnrolledInvariants_MatchOriginalLoop()
    {
        uint random = 0x9325abcd;
        for (int half = 0; half < 2; half++)
            for (int common = 0; common < 8; common++)
                for (int diff = 1; diff < 256; diff++)
                    for (int sample = 0; sample < 64; sample++)
                    {
                        random ^= random << 13; random ^= random >> 17; random ^= random << 5;
                        uint a = random & 0xffffff;
                        uint b = (sample & 1) == 0 ? a ^ (random >> 24) : random >> 8;
                        Assert.Equal(Reference(common, diff, a, b, half), HardnestedReduction.RemainingMatch(common, diff, a, b, half));
                    }
    }

    private static bool Reference(int common, int diff, uint a, uint b, int half)
    {
        for (int step = common; step < (half == 1 ? 8 : 7); step++)
        {
            bool invariant = (step & 1) == (half == 1 ? 0 : 1);
            int stateBit = half == 1 ? step / 2 : (step + 1) / 2;
            int bit = half == 1 ? 2 * stateBit + 1 : 2 * stateBit;
            if (invariant)
            {
                uint x = (uint)(diff & (1 << (bit - 1))) ^ ((a ^ b) & (0xc0u >> stateBit))
                    ^ Crypto1.Filter(a >> (4 - stateBit)) ^ Crypto1.Filter(b >> (4 - stateBit));
                if ((BitOperations.PopCount(x) & 1) != 0) return true;
            }
            else if ((BitOperations.PopCount((uint)(diff & (1 << bit)) ^ ((a ^ b) & (0x48u >> stateBit))) & 1) != 0)
                return false;
        }
        return true;
    }
}
