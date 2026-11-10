using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Models;
using ChameLab.Crypto;

namespace ChameLab.Services;

/// <summary>
/// MIFARE Classic "darkside" key recovery, ported from the pm3 reference
/// (<c>crapto1.c</c> common-prefix attack + <c>mfkey.c</c> <c>nonce2key</c>, as wrapped by
/// ChameleonUltraGUI's <c>recovery.c</c>). It needs no known key: the firmware coaxes NACKs
/// out of the card by varying the low reader-nonce bits (device <c>Mf1DarksideAcquire</c>),
/// and this turns the captured keystream/parity into candidate keys.
///
/// With non-zero parity one round is usually enough; the parity-zero variant needs several
/// rounds and keeps only the keys common to all of them.
/// </summary>
public static class DarksideRecovery
{
    // pm3 fast-forward constants for the odd (index 1) and even (index 0) LFSR halves.
    private static readonly uint[][] FastFwd =
    [
        [0, 0x4BC53, 0xECB1, 0x450E2, 0x25E29, 0x6E27A, 0x2B298, 0x60ECB],
        [0, 0x1D962, 0x4BC53, 0x56531, 0xECB1, 0x135D3, 0x450E2, 0x58980],
    ];

    // Four paired-filter classes, pre-permuted for the eight distinct low-six-
    // bit XOR masks. Eight MiB shared data evaluates 64 prefixes per word.
    // These tables depend only on the cipher, never on a capture or key.
    private static readonly Lazy<ulong[][]> PrefixPlanes = new(() =>
    {
        var filters = Crapto1.RecoveryFilterTable;
        var classes = new ulong[4][];
        for (var i = 0; i < classes.Length; i++) classes[i] = new ulong[1 << 15];
        for (var i = 0; i < 1 << 21; i++)
            classes[filters[i >> 1] | (filters[i & 0xfffff] << 1)][i >> 6] |= 1UL << (i & 63);
        var result = new ulong[64 * 4][];
        foreach (var half in FastFwd)
        foreach (var forward in half)
        {
            var mask = (int)(forward & 63);
            if (result[mask * 4] != null) continue;
            for (var pair = 0; pair < 4; pair++)
            {
                if (mask == 0) { result[pair] = classes[pair]; continue; }
                var words = result[mask * 4 + pair] = new ulong[1 << 15];
                for (var i = 0; i < words.Length; i++) words[i] = PermutePrefixWord(classes[pair][i], mask);
            }
        }
        return result;
    });

    private static ulong PermutePrefixWord(ulong word, int mask)
    {
        if ((mask & 1) != 0) word = ((word >> 1) & 0x5555555555555555UL) | ((word & 0x5555555555555555UL) << 1);
        if ((mask & 2) != 0) word = ((word >> 2) & 0x3333333333333333UL) | ((word & 0x3333333333333333UL) << 2);
        if ((mask & 4) != 0) word = ((word >> 4) & 0x0F0F0F0F0F0F0F0FUL) | ((word & 0x0F0F0F0F0F0F0F0FUL) << 4);
        if ((mask & 8) != 0) word = ((word >> 8) & 0x00FF00FF00FF00FFUL) | ((word & 0x00FF00FF00FF00FFUL) << 8);
        if ((mask & 16) != 0) word = ((word >> 16) & 0x0000FFFF0000FFFFUL) | ((word & 0x0000FFFF0000FFFFUL) << 16);
        if ((mask & 32) != 0) word = (word >> 32) | (word << 32);
        return word;
    }
    private static readonly Lazy<ulong[][]> PlainRollback35 = new(() =>
    {
        var tables = new ulong[6][];
        for (var component = 0; component < 6; component++)
        {
            var table = tables[component] = new ulong[256];
            for (var bit = 0; bit < 8; bit++)
            {
                var state = component < 3 ? new Crypto1State(1u << (8 * component + bit), 0)
                    : new Crypto1State(0, 1u << (8 * (component - 3) + bit));
                for (var step = 0; step < 35; step++) state.LfsrRollbackBit(0, false);
                table[1 << bit] = state.Odd | ((ulong)state.Even << 24);
            }
            for (var value = 1; value < 256; value++)
                table[value] = table[value & (value - 1)] ^ table[value & -value];
        }
        return tables;
    });
    /// <summary>
    /// Recovers candidate 48-bit keys from one or more darkside rounds. Returns an empty list
    /// when nothing validated (collect more rounds, especially for the parity-zero case).
    /// </summary>
    public static IReadOnlyList<ulong> Recover(uint uid, IReadOnlyList<DarksideItem> items,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        List<ulong>? last = null;

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            var keys = Nonce2Key(uid, item.Nt, item.Nr, item.Ar, item.Par, item.Ks1, ct);
            if (keys.Count == 0) continue;

            if (item.Par == 0)
            {
                // Parity-zero attack: a single round yields a huge set; the real key is the
                // one shared by consecutive rounds.
                if (last is null) { last = keys; continue; }
                var inter = Intersect(last, keys, ct);
                if (inter.Count == 0) { last = keys; continue; }
                return inter;
            }

            return keys;
        }

        return System.Array.Empty<ulong>();
    }

    /// <summary>pm3 <c>nonce2key</c>: unpack the keystream/parity, run the common-prefix attack, roll each state back to its key.</summary>
    private static List<ulong> Nonce2Key(uint uid, uint nt, uint nr, uint ar, ulong parInfo, ulong ksInfo,
        CancellationToken ct)
    {
        // The low three bits of nr are the ones the attack sweeps; clear them.
        nr &= 0xFFFFFF1F;

        var ks3x = new byte[8];
        var par = new byte[8];
        for (var pos = 0; pos < 8; pos++)
        {
            ks3x[7 - pos] = (byte)((ksInfo >> (pos * 8)) & 0x0F);
            par[7 - pos] = (byte)(parInfo >> (pos * 8));
        }

        return LfsrCommonPrefix(nr, ar, ks3x, par, parInfo == 0,
            NestedKeyTransform.GetInputContribution(uid ^ nt), ct);
    }

    /// <summary>pm3 <c>lfsr_common_prefix</c>: combine the odd/even 21-bit prefix candidates and keep the states that survive the parity check.</summary>
    private static List<ulong> LfsrCommonPrefix(uint pfx, uint rr, byte[] ks, byte[] par, bool noPar,
        ulong inputContribution, CancellationToken ct)
    {
        List<uint> odd = null!, even = null!;
        var workers = OperatingSystem.IsBrowser() ? 1 : Math.Min(8, Environment.ProcessorCount);
        if (workers > 1)
            Parallel.Invoke(new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = ct },
                () => odd = LfsrPrefixKs(ks, 1, ct), () => even = LfsrPrefixKs(ks, 0, ct));
        else { odd = LfsrPrefixKs(ks, 1, ct); even = LfsrPrefixKs(ks, 0, ct); }
        var rows = new List<ulong>[odd.Count];
        void RecoverRow(int index)
        {
            var row = new List<ulong>(noPar ? checked(even.Count * 64) : 0);
            // `o` accumulates across the whole even sweep, exactly as the pm3 pointer does.
            var o = odd[index];
            foreach (var eCand in even)
            {
                ct.ThrowIfCancellationRequested();
                var e = eCand;
                for (var top = 0; top < 64; top++)
                {
                    o += 1u << 21;
                    e += (uint)(((top & 7) == 0 ? 1 : 0) + 1) << 21;
                    if (CheckPfxParity(pfx, rr, par, o, e, noPar) is { } state)
                        row.Add(NestedKeyTransform.GetKey(state, inputContribution));
                }
            }
            rows[index] = row;
        }
        if (workers > 1 && odd.Count > 1)
            Parallel.For(0, odd.Count, new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = ct }, RecoverRow);
        else for (var i = 0; i < odd.Count; i++) RecoverRow(i);
        // Merge in prefix order, independent of worker scheduling.
        var count = 0;
        foreach (var row in rows) count = checked(count + row.Count);
        var result = new List<ulong>(count);
        foreach (var row in rows) result.AddRange(row);
        return result;
    }

    /// <summary>pm3 <c>lfsr_prefix_ks</c>: the 21-bit partial states whose filtered output matches the observed keystream.</summary>
    internal static List<uint> LfsrPrefixKs(byte[] ks, int isOdd, CancellationToken ct)
    {
        var planes = PrefixPlanes.Value;
        var fastFwd = FastFwd[isOdd];
        var constraints = new ulong[8][];
        Span<int> blocks = stackalloc int[8];
        for (var c = 0; c < 8; c++)
        {
            var pair = ((ks[c] >> isOdd) & 1) | (((ks[c] >> (isOdd + 2)) & 1) << 1);
            constraints[c] = planes[(int)(fastFwd[c] & 63) * 4 + pair];
            blocks[c] = (int)(fastFwd[c] >> 6);
        }
        var candidates = new List<uint>();
        for (var block = 0; block < 1 << 15; block++)
        {
            if ((block & 255) == 0) ct.ThrowIfCancellationRequested();
            var matches = constraints[0][block];
            for (var c = 1; matches != 0 && c < 8; c++) matches &= constraints[c][block ^ blocks[c]];
            while (matches != 0)
            {
                candidates.Add((uint)((block << 6) | BitOperations.TrailingZeroCount(matches)));
                matches &= matches - 1;
            }
        }
        return candidates;
    }

    /// <summary>pm3 <c>check_pfx_parity</c>: eliminate candidate states using the reader-response parity bits; returns the surviving state or null.</summary>
    internal static Crypto1State? CheckPfxParity(uint prefix, uint rResp, byte[] par, uint odd, uint even, bool noPar)
    {
        if (noPar)
        {
            // No output/parity is needed for the first 35 plaintext clocks.
            // They are a linear GF(2) map; retain only the encrypted rollback loop.
            var t = PlainRollback35.Value;
            var packed = t[0][(byte)odd] ^ t[1][(byte)(odd >> 8)] ^ t[2][(byte)(odd >> 16)] ^
                t[3][(byte)even] ^ t[4][(byte)(even >> 8)] ^ t[5][(byte)(even >> 16)];
            var o = (uint)packed & 0xffffff;
            var e = (uint)(packed >> 24);
            var filters = Crapto1.RecoveryFilterTable;
            for (var bit = 31; bit >= 0; bit--)
            {
                var restored = o >> 1;
                var feedback = (o & 1) ^ (restored & Crypto1.LF_POLY_EVEN) ^ (e & Crypto1.LF_POLY_ODD) ^
                    ((prefix >> (bit ^ 24)) & 1) ^ filters[e & 0xfffff];
                o = e;
                e = restored | ((uint)(BitOperations.PopCount(feedback) & 1) << 23);
            }
            return new Crypto1State(o, e);
        }
        var sl = new Crypto1State(0, 0);

        for (var c = 0u; c < 8; c++)
        {
            sl.Odd = odd ^ FastFwd[1][c];
            sl.Even = even ^ FastFwd[0][c];

            sl.LfsrRollbackBit(0, false);
            sl.LfsrRollbackBit(0, false);

            var ks3 = sl.LfsrRollbackBit(0, false);
            var reader = prefix | (c << 5);
            // Rollback reconstructs the low byte first. Reject as soon as each
            // response parity is available, before doing the remaining rollback.
            var bits = par[c];
            var b0 = sl.LfsrRollbackByte(0, false);
            if ((Crypto1.EvenParity32((byte)(b0 ^ rResp)) ^ (uint)(bits >> 7) ^ ks3) == 0) return null;
            var b1 = sl.LfsrRollbackByte(0, false);
            if (((Crypto1.EvenParity32((byte)(b1 ^ (rResp >> 8))) ^ (uint)(bits >> 6) ^ b0) & 1) == 0) return null;
            var b2 = sl.LfsrRollbackByte(0, false);
            if (((Crypto1.EvenParity32((byte)(b2 ^ (rResp >> 16))) ^ (uint)(bits >> 5) ^ b1) & 1) == 0) return null;
            var b3 = sl.LfsrRollbackByte(0, false);
            if (((Crypto1.EvenParity32((byte)(b3 ^ (rResp >> 24))) ^ (uint)(bits >> 4) ^ b2) & 1) == 0) return null;

            var nr0 = sl.LfsrRollbackByte((byte)reader, true);
            if (((Crypto1.EvenParity32((byte)(nr0 ^ reader)) ^ (uint)(bits >> 3) ^ b3) & 1) == 0) return null;
            sl.LfsrRollbackByte((byte)(reader >> 8), true);
            sl.LfsrRollbackByte((byte)(reader >> 16), true);
            sl.LfsrRollbackByte((byte)(reader >> 24), true);
        }

        return sl;
    }

    private static List<ulong> Intersect(List<ulong> a, List<ulong> b, CancellationToken ct)
    {
        var set = new HashSet<ulong>(a);
        var outp = new List<ulong>();
        foreach (var k in b)
        {
            ct.ThrowIfCancellationRequested();
            if (set.Remove(k)) outp.Add(k);
        }
        return outp;
    }
}
