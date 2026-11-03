// Derived from Proxmark3, GPL-3.0-or-later. See NOTICE.md.
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace ChameLab.Crypto.Hardnested;

/// <summary>Immutable sparse bitmap with shared pages and constant-time membership.</summary>
internal sealed class HardnestedBitSet
{
    internal readonly int[] Pages = new int[8192];
    private readonly PagePool pool;
    internal readonly int Count;

    internal HardnestedBitSet(uint[] words, PagePool pool)
    {
        this.pool = pool;
        for (int page = 0; page < Pages.Length; page++)
        {
            uint any = 0;
            for (int j = 0; j < 64; j++)
            {
                uint word = words[page * 64 + j];
                any |= word;
                Count += BitOperations.PopCount(word);
            }
            if (any != 0) Pages[page] = pool.Intern(words.AsSpan(page * 64, 64));
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

        internal int Intern(ReadOnlySpan<uint> page)
        {
            ulong hash = 14695981039346656037;
            foreach (uint word in page) hash = unchecked((hash ^ word) * 1099511628211);
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
