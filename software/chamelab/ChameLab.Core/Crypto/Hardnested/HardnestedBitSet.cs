// Derived from Proxmark3, GPL-3.0-or-later. See NOTICE.md.
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ChameLab.Crypto.Hardnested;

/// <summary>Immutable sparse bitmap with shared pages and constant-time membership.</summary>
internal sealed class HardnestedBitSet
{
    internal readonly int[] Pages = new int[8192];
    private readonly PagePool pool;

    // Reduction already knows which pages survived; avoid scanning them twice.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal HardnestedBitSet(uint[] words, PagePool pool, ReadOnlySpan<int> activePages)
    {
        this.pool = pool;
        foreach (int page in activePages) Pages[page] = pool.Intern(words.AsSpan(page * 64, 64));
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal HardnestedBitSet(uint[] words, PagePool pool)
    {
        this.pool = pool;
        for (int page = 0; page < Pages.Length; page++)
        {
            var data = words.AsSpan(page * 64, 64);
            bool nonzero;
            if (Vector.IsHardwareAccelerated)
            {
                var vectors = MemoryMarshal.Cast<uint, Vector<uint>>(data);
                var any = Vector<uint>.Zero;
                foreach (var vector in vectors) any |= vector;
                nonzero = any != Vector<uint>.Zero;
            }
            else nonzero = data.IndexOfAnyExcept(0u) >= 0;
            if (nonzero) Pages[page] = pool.Intern(data);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal uint Word(int index) => pool.Words[Pages[index >> 6]][index & 63];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool Contains(uint state) => (Word((int)(state >> 5)) & (0x80000000u >> (int)(state & 31))) != 0;

    internal IEnumerable<uint> States(uint[] domain)
    {
        for (int page = 0; page < Pages.Length; page++)
        {
            if (Pages[page] == 0) continue;
            for (int j = 0; j < 64; j++)
            {
                int index = page * 64 + j;
                uint word = Word(index) & domain[index];
                while (word != 0)
                {
                    int bit = BitOperations.LeadingZeroCount(word);
                    yield return (uint)(index * 32 + bit);
                    word &= ~(0x80000000u >> bit);
                }
            }
        }
    }

    // Intermediate bitmaps also repeat many pages. Share their immutable data
    // across all 512 sets, rather than retaining hundreds of megabytes.
    internal sealed class PagePool
    {
        private readonly Dictionary<ulong, int> hashes = new();
        private readonly List<uint[]> pages = [new uint[64]];
        internal uint[][] Words = [];

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal int Intern(ReadOnlySpan<uint> page)
        {
            // Four independent chains avoid a 64-multiply dependency chain.
            ulong a = 14695981039346656037, b = a, c = a, d = a;
            for (int i = 0; i < 64; i += 4)
            {
                a = unchecked((a ^ page[i]) * 1099511628211);
                b = unchecked((b ^ page[i + 1]) * 1099511628211);
                c = unchecked((c ^ page[i + 2]) * 1099511628211);
                d = unchecked((d ^ page[i + 3]) * 1099511628211);
            }
            ulong hash = a ^ BitOperations.RotateLeft(b, 17) ^ BitOperations.RotateLeft(c, 33) ^ BitOperations.RotateLeft(d, 49);
            while (hashes.TryGetValue(hash, out int existing))
            {
                if (page.SequenceEqual(pages[existing])) return existing;
                hash = unchecked(hash + 1);
            }
            int id = pages.Count;
            pages.Add(page.ToArray());
            hashes.Add(hash, id);
            return id;
        }

        internal void Freeze()
        {
            Words = pages.ToArray();
            pages.Clear();
            hashes.Clear();
        }
    }
}
