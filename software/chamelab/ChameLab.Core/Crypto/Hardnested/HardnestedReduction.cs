// Derived from Proxmark3 cmdhfmfhard.c, GPL-3.0-or-later. See NOTICE.md.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using ChameLab.Services;

namespace ChameLab.Crypto.Hardnested;

internal sealed class HardnestedReduction
{
    internal readonly HardnestedCapture Capture;
    internal readonly HardnestedSample[][] Nonces;
    internal readonly int Sum0;
    internal double[] SumWeights = [];
    internal readonly HardnestedBitSet[][] States = [new HardnestedBitSet[256], new HardnestedBitSet[256]];
    internal readonly uint[][] Domains = [new uint[1 << 19], new uint[1 << 19]];
    private readonly bool[][] properties = new bool[256][];

    internal bool HasBitflip(int first, int flip) => properties[first][flip];

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal HardnestedReduction(HardnestedCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(capture.Samples);
        // Own the input: callers may reuse their acquisition buffers.
        Capture = capture with { Samples = capture.Samples.ToArray() };
        var byPrefix = new HardnestedSample?[65536];
        var firstParity = Enumerable.Repeat(-1, 256).ToArray();
        foreach (var sample in Capture.Samples)
        {
            if (sample.EncryptedParity > 15) throw new ArgumentException("Invalid encrypted nonce parity.", nameof(capture));
            int first = (int)(sample.EncryptedNonce >> 24), prefix = (int)(sample.EncryptedNonce >> 16);
            int parity = sample.EncryptedParity >> 3;
            if (firstParity[first] >= 0 && firstParity[first] != parity)
                throw new ArgumentException("Inconsistent first-byte parity in hardnested capture.", nameof(capture));
            firstParity[first] = parity;
            if (byPrefix[prefix] is { } previous && (previous.EncryptedParity >> 2) != (sample.EncryptedParity >> 2))
                throw new ArgumentException("Inconsistent second-byte parity in hardnested capture.", nameof(capture));
            byPrefix[prefix] ??= sample;
        }
        if (firstParity.Contains(-1)) throw new ArgumentException("Hardnested requires all 256 encrypted first bytes.", nameof(capture));
        for (int i = 0; i < 256; i++) Sum0 += BitOperations.PopCount((uint)(i ^ firstParity[i])) & 1;
        if (!HardnestedTables.Sums.Contains(Sum0)) throw new ArgumentException("Invalid hardnested first-byte sum.", nameof(capture));
        Nonces = new HardnestedSample[256][];
        for (int first = 0; first < 256; first++)
        {
            Nonces[first] = byPrefix.AsSpan(first * 256, 256).ToArray().Where(n => n.HasValue).Select(n => n!.Value).ToArray();
            var flags = properties[first] = new bool[1024];
            for (int diff = 1; diff < 256; diff++)
            {
                flags[diff | ((firstParity[first] ^ firstParity[first ^ diff]) << 8)] = true;
                foreach (var sample in Nonces[first])
                {
                    int other = ((int)(sample.EncryptedNonce >> 16) ^ diff);
                    if (byPrefix[other] is { } mate)
                        flags[0x200 | diff | (((sample.EncryptedParity ^ mate.EncryptedParity) & 4) << 6)] = true;
                }
            }
        }
    }

    internal void Reduce(HardnestedTables tables, IProgress<string>? progress, CancellationToken ct)
    {
        // Each domain admits or rejects a whole group of 16 low-nibble states.
        // Projecting each byte's constraints to those groups commutes with
        // intersecting domains. Workers can therefore reduce independent byte
        // ranges, then AND their final domains without removing any valid state.
        // Earlier maps may retain extra groups; every candidate is masked by the
        // merged domain before the cross-byte checks (which only vary low bits).
        int chunks = OperatingSystem.IsBrowser() ? 1 : Math.Min(8, Math.Max(1, Environment.ProcessorCount / 2));
        var localDomains = new uint[2 * chunks][];
        // Extreme sums already constrain the domain strongly. Otherwise seed a
        // single projection before splitting, so workers start with fewer groups.
        bool seeded = Sum0 is not (0 or 256);
        bool seedStage = seeded;
        int anchor = 0;
        for (int b = 1; b < 256; b++)
            if (Nonces[b].Length > Nonces[anchor].Length) anchor = b;
        int completed = 0;
        var progressGate = new object();
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void ReduceHalf(int job)
        {
            int half = seedStage ? job : job / chunks;
            int begin = seedStage ? anchor : job % chunks * 256 / chunks;
            int end = seedStage ? anchor + 1 : (job % chunks + 1) * 256 / chunks;
            var pool = new HardnestedBitSet.PagePool();
            var domain = seedStage ? Domains[half] : seeded ? (uint[])Domains[half].Clone() : new uint[1 << 19];
            if (!seedStage) localDomains[job] = domain;
            if (seedStage || !seeded)
            {
                var parts = tables.PartialSums[half];
                var allowed = new bool[9];
                for (int p = 0; p < 9; p++)
                    for (int q = 0; q < 9; q++)
                        if (HardnestedTables.Sum(p, q) == Sum0) allowed[p] = true;
                for (int i = 0; i < domain.Length; i++)
                    domain[i] = (allowed[parts[2 * i]] ? 0xffff0000u : 0) | (allowed[parts[2 * i + 1]] ? 0xffffu : 0);
            }
            var work = new uint[domain.Length];
            var activePages = new int[8192];
            var seenFilters = new int[tables.Words.Length / 64];
            int epoch = 0;
            var tableVectors = MemoryMarshal.Cast<uint, Vector<uint>>(tables.Words.AsSpan());
            Span<int> offsets = stackalloc int[8];
            for (int first = begin; first < end; first++)
            {
                if (seeded && !seedStage && first == anchor) continue;
                ct.ThrowIfCancellationRequested();
                domain.CopyTo(work, 0);
                var selected = tables.Tables[half].Where(t => HasBitflip(first, t.Flip)).ToArray();
                int pageCount = 0;
                for (int page = 0; page < 8192; page++)
                {
                    if ((page & 255) == 0) ct.ThrowIfCancellationRequested();
                    var destination = work.AsSpan(page * 64, 64);
                    var vectors = MemoryMarshal.Cast<uint, Vector<uint>>(destination);
                    uint any = 0;
                    if (Vector.IsHardwareAccelerated)
                    {
                        var union = Vector<uint>.Zero;
                        foreach (var vector in vectors) union |= vector;
                        if (union == Vector<uint>.Zero) continue;
                    }
                    else
                    {
                        foreach (uint word in destination) any |= word;
                        if (any == 0) continue;
                    }
                    bool live = true;
                    int stamp = ++epoch; // At most 256 * 8192 pages per worker.
                    int f = 0;
                    while (f < selected.Length)
                    {
                        int count = 0;
                        bool empty = false;
                        while (f < selected.Length && count < 8)
                        {
                            int id = selected[f++].Pages[page];
                            if (id == tables.FullPage) continue;
                            if (id == 0) { empty = true; break; }
                            // Packed tables share identical pages. AND is
                            // idempotent, so load each distinct page only once.
                            if (seenFilters[id] == stamp) continue;
                            seenFilters[id] = stamp;
                            offsets[count++] = id * 64;
                        }
                        if (empty) { destination.Clear(); live = false; break; }
                        if (count == 0) continue;
                        if (Vector.IsHardwareAccelerated)
                        {
                            // Pad with the identity page, then intersect eight filters per load/store.
                            for (int i = count; i < 8; i++) offsets[i] = tables.FullPage * 64;
                            int width = Vector<uint>.Count;
                            var s0 = tableVectors.Slice(offsets[0] / width, vectors.Length);
                            var s1 = tableVectors.Slice(offsets[1] / width, vectors.Length);
                            var s2 = tableVectors.Slice(offsets[2] / width, vectors.Length);
                            var s3 = tableVectors.Slice(offsets[3] / width, vectors.Length);
                            var s4 = tableVectors.Slice(offsets[4] / width, vectors.Length);
                            var s5 = tableVectors.Slice(offsets[5] / width, vectors.Length);
                            var s6 = tableVectors.Slice(offsets[6] / width, vectors.Length);
                            var s7 = tableVectors.Slice(offsets[7] / width, vectors.Length);
                            var combined = Vector<uint>.Zero;
                            for (int v = 0; v < vectors.Length; v++)
                            {
                                var value = vectors[v] & ((s0[v] & s1[v]) & (s2[v] & s3[v]))
                                    & ((s4[v] & s5[v]) & (s6[v] & s7[v]));
                                vectors[v] = value;
                                combined |= value;
                            }
                            if (combined == Vector<uint>.Zero) { live = false; break; }
                        }
                        else
                        {
                            any = 0;
                            for (int j = 0; j < 64; j++)
                            {
                                uint word = destination[j];
                                for (int i = 0; i < count; i++) word &= tables.Words[offsets[i] + j];
                                any |= destination[j] = word;
                            }
                            if (any == 0) { live = false; break; }
                        }
                    }
                    if (live) activePages[pageCount++] = page;
                }
                States[half][first] = new(work, pool, activePages.AsSpan(0, pageCount));
                if (Vector.IsHardwareAccelerated)
                {
                    var dv = MemoryMarshal.Cast<uint, Vector<uint>>(domain.AsSpan());
                    var wv = MemoryMarshal.Cast<uint, Vector<uint>>(work.AsSpan());
                    var high = new Vector<uint>(0xffff0000u);
                    var low = new Vector<uint>(65535u);
                    for (int i = 0; i < dv.Length; i++)
                        dv[i] = (~Vector.Equals(wv[i] & high, Vector<uint>.Zero) & high)
                               | (~Vector.Equals(wv[i] & low, Vector<uint>.Zero) & low);
                }
                else for (int i = 0; i < domain.Length; i++)
                {
                    uint word = work[i];
                    domain[i] = (word >> 16 != 0 ? 0xffff0000u : 0) | ((word & 65535) != 0 ? 0xffffu : 0);
                }
                lock (progressGate)
                {
                    if ((++completed & 31) == 0) progress?.Report($"Reducing hardnested states: {completed}/512…");
                }
            }
            pool.Freeze();
        }
        if (seeded)
        {
            if (OperatingSystem.IsBrowser()) { ReduceHalf(0); ReduceHalf(1); }
            else Parallel.For(0, 2, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = 2 }, ReduceHalf);
            seedStage = false;
        }
        if (OperatingSystem.IsBrowser()) { ReduceHalf(0); ReduceHalf(1); }
        else Parallel.For(0, 2 * chunks, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = 2 * chunks }, ReduceHalf);
        for (int half = 0; half < 2; half++)
        {
            localDomains[half * chunks].CopyTo(Domains[half], 0);
            for (int chunk = 1; chunk < chunks; chunk++)
                for (int i = 0; i < Domains[half].Length; i++) Domains[half][i] &= localDomains[half * chunks + chunk][i];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal uint[][][] Candidates(HardnestedTables tables, out int bestByte, CancellationToken ct)
    {
        var active = Domains.Select(d => Enumerable.Range(0, d.Length).Where(i => d[i] != 0).ToArray()).ToArray();
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        uint[][][] BuildGroups(int first)
        {
            var result = new uint[2][][];
            for (int half = 0; half < 2; half++)
            {
                var lists = Enumerable.Range(0, 81).Select(_ => new List<uint>()).ToArray();
                foreach (int index in active[half])
                {
                    if ((index & 1023) == 0) ct.ThrowIfCancellationRequested();
                    uint word = States[half][first].Word(index) & Domains[half][index];
                    while (word != 0)
                    {
                        int bit = BitOperations.LeadingZeroCount(word);
                        uint state = (uint)(index * 32 + bit);
                        lists[tables.PartialSums[half][state >> 4] * 9 + tables.PartialSums[half][state & 0xfffff]].Add(state);
                        word &= ~(0x80000000u >> bit);
                    }
                }
                result[half] = lists.Select(l => l.ToArray()).ToArray();
            }
            return result;
        }
        double[] WorkCounts(long[][] groups)
        {
            var costs = new double[HardnestedTables.Sums.Length];
            for (int a = 0; a < 81; a++)
                for (int b = 0; b < 81; b++)
                    if (HardnestedTables.Sum(a / 9, b / 9) == Sum0)
                    {
                        int sum = Array.IndexOf(HardnestedTables.Sums, HardnestedTables.Sum(a % 9, b % 9));
                        if (sum >= 0) costs[sum] += (double)groups[1][a] * groups[0][b];
                    }
            return costs;
        }
        // Learn the sum prior from the surviving common domain, rather than
        // treating the nineteen sums as equally probable.
        var coarse = new[] { new long[81], new long[81] };
        for (int half = 0; half < 2; half++)
            foreach (int index in active[half])
            {
                uint word = Domains[half][index];
                while (word != 0)
                {
                    int bit = BitOperations.LeadingZeroCount(word);
                    uint state = (uint)(index * 32 + bit);
                    coarse[half][tables.PartialSums[half][state >> 4] * 9 + tables.PartialSums[half][state & 0xfffff]]++;
                    word &= ~(0x80000000u >> bit);
                }
            }
        SumWeights = WorkCounts(coarse);
        var expectedCache = new Dictionary<(int Drawn, int Observed), double>();
        double Expected(int first)
        {
            var costs = SumWeights;
            int n = Nonces[first].Length, observed = 0;
            foreach (var sample in Nonces[first])
                observed += BitOperations.PopCount(((sample.EncryptedNonce >> 16) & 255) ^ (uint)((sample.EncryptedParity >> 2) & 1)) & 1;
            if (expectedCache.TryGetValue((n, observed), out double cached)) return cached;
            var probabilities = new double[costs.Length];
            double total = 0;
            for (int i = 0; i < probabilities.Length; i++)
                total += probabilities[i] = costs[i] == 0 ? 0 : SumWeights[i] * HardnestedSearch.Hypergeometric(HardnestedTables.Sums[i], n, observed);
            if (total == 0) return double.MaxValue;
            double prefix = 0, expected = 0;
            foreach (int i in Enumerable.Range(0, costs.Length).OrderByDescending(i => probabilities[i]))
            {
                expected += probabilities[i] / total * (prefix + costs[i] * 0.5);
                prefix += costs[i];
            }
            expectedCache[(n, observed)] = expected;
            return expected;
        }
        bestByte = Enumerable.Range(0, 256).OrderBy(Expected).First();
        return BuildGroups(bestByte);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal uint[] Refine(int first, int half, uint[] states, CancellationToken ct, int start = 0, int end = -1)
    {
        var matches = new List<uint>();
        if (end < 0) end = states.Length;
        for (int i = start; i < end; i++)
        {
            if ((i & 1023) == 0) ct.ThrowIfCancellationRequested();
            if (MatchesAll(first, states[i], half)) matches.Add(states[i]);
        }
        return matches.ToArray();
    }

    private static readonly uint[][] Masks = [
        [0xfffff0, 0xfffff8, 0xfffff8, 0xfffffc, 0xfffffc, 0xfffffe, 0xfffffe, 0xffffff],
        [0xfffff0, 0xfffff0, 0xfffff8, 0xfffff8, 0xfffffc, 0xfffffc, 0xfffffe, 0xfffffe]];

    private static readonly (int Diff, int Common, uint EvenMask, uint OddMask)[] MatchSteps = BuildMatchSteps();

    private static (int, int, uint, uint)[] BuildMatchSteps()
    {
        var steps = new (int, int, uint, uint)[255];
        for (uint i = 1; i < 256; i++)
        {
            int diff = (int)((i * 0x0202020202UL & 0x010884422010UL) % 1023);
            int common = BitOperations.TrailingZeroCount((uint)diff);
            steps[i - 1] = (diff, common, Masks[0][common], Masks[1][common]);
        }
        return steps;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal bool MatchesAll(int first, uint state, int half)
    {
        var maps = States[half];
        foreach (var step in MatchSteps)
        {
            int diff = step.Diff, common = step.Common;
            uint mask = half == 0 ? step.EvenMask : step.OddMask;
            uint start = state & mask;
            // All possible low nibbles lie in a single bitmap word. Load it once
            // and visit only its set bits, in the original low-nibble order.
            uint allowed = (uint.MaxValue << (31 - (int)(~mask & 255))) >> (int)(start & 31);
            uint possible = maps[first ^ diff].Word((int)(start >> 5)) & allowed;
            bool found = false;
            while (possible != 0)
            {
                int bit = BitOperations.LeadingZeroCount(possible);
                uint other = (start & ~31u) | (uint)bit;
                if (RemainingMatch(common, diff, state, other, half))
                { found = true; break; }
                possible &= ~(0x80000000u >> bit);
            }
            if (!found) return false;
        }
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool RemainingMatch(int common, int diff, uint a, uint b, int half)
        => half == 1 ? RemainingOdd(common, diff, a, b) : RemainingEven(common, diff, a, b);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool RemainingEven(int common, int diff, uint a, uint b)
    {
        uint delta = a ^ b;
        switch (common)
        {
            case 0:
                if ((BitOperations.PopCount((uint)(diff & 1) ^ (delta & 72u)) & 1) != 0) return false;
                goto case 1;
            case 1:
                if ((BitOperations.PopCount((uint)(diff & 2) ^ (delta & 96u) ^ Crypto1.Filter(a >> 3) ^ Crypto1.Filter(b >> 3)) & 1) != 0) return true;
                goto case 2;
            case 2:
                if ((BitOperations.PopCount((uint)(diff & 4) ^ (delta & 36u)) & 1) != 0) return false;
                goto case 3;
            case 3:
                if ((BitOperations.PopCount((uint)(diff & 8) ^ (delta & 48u) ^ Crypto1.Filter(a >> 2) ^ Crypto1.Filter(b >> 2)) & 1) != 0) return true;
                goto case 4;
            case 4:
                if ((BitOperations.PopCount((uint)(diff & 16) ^ (delta & 18u)) & 1) != 0) return false;
                goto case 5;
            case 5:
                if ((BitOperations.PopCount((uint)(diff & 32) ^ (delta & 24u) ^ Crypto1.Filter(a >> 1) ^ Crypto1.Filter(b >> 1)) & 1) != 0) return true;
                goto case 6;
            case 6:
                if ((BitOperations.PopCount((uint)(diff & 64) ^ (delta & 9u)) & 1) != 0) return false;
                break;
        }
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool RemainingOdd(int common, int diff, uint a, uint b)
    {
        uint delta = a ^ b;
        switch (common)
        {
            case 0:
                if ((BitOperations.PopCount((uint)(diff & 1) ^ (delta & 192u) ^ Crypto1.Filter(a >> 4) ^ Crypto1.Filter(b >> 4)) & 1) != 0) return true;
                goto case 1;
            case 1:
                if ((BitOperations.PopCount((uint)(diff & 2) ^ (delta & 72u)) & 1) != 0) return false;
                goto case 2;
            case 2:
                if ((BitOperations.PopCount((uint)(diff & 4) ^ (delta & 96u) ^ Crypto1.Filter(a >> 3) ^ Crypto1.Filter(b >> 3)) & 1) != 0) return true;
                goto case 3;
            case 3:
                if ((BitOperations.PopCount((uint)(diff & 8) ^ (delta & 36u)) & 1) != 0) return false;
                goto case 4;
            case 4:
                if ((BitOperations.PopCount((uint)(diff & 16) ^ (delta & 48u) ^ Crypto1.Filter(a >> 2) ^ Crypto1.Filter(b >> 2)) & 1) != 0) return true;
                goto case 5;
            case 5:
                if ((BitOperations.PopCount((uint)(diff & 32) ^ (delta & 18u)) & 1) != 0) return false;
                goto case 6;
            case 6:
                if ((BitOperations.PopCount((uint)(diff & 64) ^ (delta & 24u) ^ Crypto1.Filter(a >> 1) ^ Crypto1.Filter(b >> 1)) & 1) != 0) return true;
                goto case 7;
            case 7:
                if ((BitOperations.PopCount((uint)(diff & 128) ^ (delta & 9u)) & 1) != 0) return false;
                break;
        }
        return true;
    }
}
