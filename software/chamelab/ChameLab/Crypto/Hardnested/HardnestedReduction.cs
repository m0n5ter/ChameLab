// Derived from Proxmark3 cmdhfmfhard.c, GPL-3.0-or-later. See NOTICE.md.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using ChameLab.Services;

namespace ChameLab.Crypto.Hardnested;

internal sealed class HardnestedReduction
{
    internal readonly HardnestedCapture Capture;
    internal readonly HardnestedSample[][] Nonces;
    internal readonly int Sum0;
    internal readonly HardnestedBitSet[][] States = [new HardnestedBitSet[256], new HardnestedBitSet[256]];
    internal readonly uint[][] Domains = [new uint[1 << 19], new uint[1 << 19]];
    private readonly bool[][] properties = new bool[256][];

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
        // Each half owns its domain; no mutable process-wide state or native locks.
        for (int half = 0; half < 2; half++)
        {
            var pool = new HardnestedBitSet.PagePool();
            var domain = Domains[half];
            var parts = tables.PartialSums[half];
            var allowed = new bool[9];
            for (int p = 0; p < 9; p++)
                for (int q = 0; q < 9; q++)
                    if (HardnestedTables.Sum(p, q) == Sum0) allowed[p] = true;
            for (int i = 0; i < domain.Length; i++)
                domain[i] = (allowed[parts[2 * i]] ? 0xffff0000u : 0) | (allowed[parts[2 * i + 1]] ? 0xffffu : 0);
            var work = new uint[domain.Length];
            var active = new int[domain.Length];
            for (int first = 0; first < 256; first++)
            {
                ct.ThrowIfCancellationRequested();
                domain.CopyTo(work, 0);
                int length = 0;
                for (int i = 0; i < work.Length; i++) if (work[i] != 0) active[length++] = i;
                foreach (var table in tables.Tables[half])
                {
                    if (!properties[first][table.Flip]) continue;
                    ct.ThrowIfCancellationRequested();
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
                States[half][first] = new(work, pool);
                // All first bytes share the high 20 state bits. Eliminate an entire
                // group of 16 if this byte has no possible low nibble for it.
                for (int i = 0; i < domain.Length; i++)
                {
                    uint word = work[i];
                    domain[i] &= (word >> 16 != 0 ? 0xffff0000u : 0) | ((word & 65535) != 0 ? 0xffffu : 0);
                }
                if ((first & 31) == 31) progress?.Report($"Reducing hardnested states: {half * 256 + first + 1}/512…");
            }
            pool.Freeze();
        }
    }

    internal uint[][][] Candidates(HardnestedTables tables, out int bestByte, CancellationToken ct)
    {
        bestByte = 0;
        long best = long.MaxValue;
        for (int b = 0; b < 256; b++)
        {
            ct.ThrowIfCancellationRequested();
            long even = Count(States[0][b], Domains[0]), odd = Count(States[1][b], Domains[1]);
            if (even * odd < best) { best = even * odd; bestByte = b; }
        }
        var result = new uint[2][][];
        for (int half = 0; half < 2; half++)
        {
            var lists = Enumerable.Range(0, 81).Select(_ => new List<uint>()).ToArray();
            int checkedStates = 0;
            foreach (uint state in States[half][bestByte].States(Domains[half]))
            {
                if ((checkedStates++ & 1023) == 0) ct.ThrowIfCancellationRequested();
                lists[tables.PartialSums[half][state >> 4] * 9 + tables.PartialSums[half][state & 0xfffff]].Add(state);
            }
            result[half] = lists.Select(l => l.ToArray()).ToArray();
        }
        return result;
    }

    internal uint[] Refine(int first, int half, uint[] states, CancellationToken ct)
    {
        var matches = new List<uint>();
        for (int i = 0; i < states.Length; i++)
        {
            if ((i & 1023) == 0) ct.ThrowIfCancellationRequested();
            if (MatchesAll(first, states[i], half)) matches.Add(states[i]);
        }
        return matches.ToArray();
    }

    private static long Count(HardnestedBitSet bits, uint[] domain)
    {
        long count = 0;
        for (int i = 0; i < domain.Length; i++) count += BitOperations.PopCount(bits.Word(i) & domain[i]);
        return count;
    }

    private static readonly uint[][] Masks = [
        [0xfffff0, 0xfffff8, 0xfffff8, 0xfffffc, 0xfffffc, 0xfffffe, 0xfffffe, 0xffffff],
        [0xfffff0, 0xfffff0, 0xfffff8, 0xfffff8, 0xfffffc, 0xfffffc, 0xfffffe, 0xfffffe]];

    internal bool MatchesAll(int first, uint state, int half)
    {
        for (uint i = 1; i < 256; i++)
        {
            int diff = (int)((i * 0x0202020202UL & 0x010884422010UL) % 1023);
            int common = BitOperations.TrailingZeroCount((uint)diff);
            uint mask = Masks[half][common];
            bool found = false;
            for (uint low = 0; low <= (~mask & 255); low++)
            {
                uint other = (state & mask) | low;
                if (States[half][first ^ diff].Contains(other) && RemainingMatch(common, diff, state, other, half))
                { found = true; break; }
            }
            if (!found) return false;
        }
        return true;
    }

    internal static bool RemainingMatch(int common, int diff, uint a, uint b, int half)
    {
        // Preserve the upstream fall-through switch: a broken invariant permits
        // remaining bits; an invalid state rejects them.
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
