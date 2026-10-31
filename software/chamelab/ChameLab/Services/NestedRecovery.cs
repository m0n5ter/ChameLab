using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ChameLab.Crypto;

namespace ChameLab.Services;

/// <summary>
/// MIFARE Classic "nested" key recovery for weak-PRNG cards, ported from the pm3
/// reference (<c>nested.c</c> / <c>nested_util.c</c>, as wrapped by ChameleonUltraGUI's
/// <c>recovery.c</c>) onto the crapto1 primitives in <see cref="ChameLab.Crypto"/>.
///
/// One sector key must already be known; the firmware collects two (nt, ntEnc, parity)
/// samples of the target sector while authenticating through the known one, and this turns
/// them into candidate keys. The caller verifies each candidate by authenticating on the card.
/// </summary>
public static class NestedRecovery
{
    /// <summary>
    /// Recovers candidate 48-bit keys from two nested nonce samples. <paramref name="dist"/>
    /// is the PRNG distance from the known key's nonce (device <c>Mf1DetectNtDist</c>); the
    /// <c>par</c> bytes carry the three low parity bits of each sample. Candidates are ordered
    /// most-frequent first; try them in that order.
    /// </summary>
    public static IReadOnlyList<ulong> Recover(
        uint uid, uint dist,
        uint nt0, uint nt0Enc, byte par0,
        uint nt1, uint nt1Enc, byte par1,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var pnk = new List<(uint Ntp, uint Ks1)>();

        for (var i = 0; i < 2; i++)
        {
            var nt = i == 0 ? nt0 : nt1;
            var ntEnc = i == 0 ? nt0Enc : nt1Enc;
            var parInt = i == 0 ? par0 : par1;

            // Three low parity bits of this sample.
            var par = new byte[3];
            for (var m = 0; m < 3; m++) par[m] = (byte)((parInt >> m) & 1);

            // Sweep +/-14 PRNG steps around the measured distance and keep nonces whose
            // predicted keystream parity matches the observed encrypted parity.
            var ntTest = Crypto1.PrngSuccessor(nt, (int)dist - 14);
            for (var m = (int)dist - 14; m <= (int)dist + 14; m++)
            {
                var ks1 = ntEnc ^ ntTest;
                if (ValidNonce(ntTest, ntEnc, ks1, par))
                    pnk.Add((ntTest, ks1));
                ntTest = Crypto1.PrngSuccessor(ntTest, 1);
            }
        }

        return NestedCore.Run(pnk, uid, ct);
    }

    /// <summary>
    /// Whether a candidate recovered nonce is consistent with the observed encrypted parity
    /// bits (pm3 <c>valid_nonce</c>): each byte's odd parity must equal the encrypted parity
    /// XOR the matching keystream bit.
    /// </summary>
    internal static bool ValidNonce(uint nt, uint ntEnc, uint ks1, byte[] parity) =>
        Crypto1.OddParity8((byte)((nt >> 24) & 0xFF)) == (parity[0] ^ Crypto1.OddParity8((byte)((ntEnc >> 24) & 0xFF)) ^ (int)((ks1 >> 16) & 1)) &&
        Crypto1.OddParity8((byte)((nt >> 16) & 0xFF)) == (parity[1] ^ Crypto1.OddParity8((byte)((ntEnc >> 16) & 0xFF)) ^ (int)((ks1 >> 8) & 1)) &&
        Crypto1.OddParity8((byte)((nt >> 8) & 0xFF)) == (parity[2] ^ Crypto1.OddParity8((byte)((ntEnc >> 8) & 0xFF)) ^ (int)(ks1 & 1));
}

/// <summary>
/// Shared back half of the nested attacks (pm3 <c>nested_recover</c> + <c>most_frequent</c>):
/// turn (ntp, ks1) samples into LFSR states, roll them back over the plain nonce to the key,
    /// and return up to 50 keys occurring at least twice, ordered by frequency.
/// </summary>
internal static class NestedCore
{
    public static IReadOnlyList<ulong> Run(IReadOnlyList<(uint Ntp, uint Ks1)> pnk, uint authUid,
        CancellationToken ct = default)
    {
        var counts = new Dictionary<ulong, int>();

        foreach (var (ntp, ks1) in pnk)
        {
            ct.ThrowIfCancellationRequested();
            var seed = ntp ^ authUid;
            foreach (var found in Crapto1.LfsrRecovery32(ks1, seed))
            {
                ct.ThrowIfCancellationRequested();
                var state = found;
                // The pm3 recovery list is terminated by the empty state; skip it so a
                // spurious all-zero key is not counted.
                if (state is { Odd: 0, Even: 0 }) continue;
                state.LfsrRollbackWord(seed, false);
                var key = state.Lfsr;
                counts.TryGetValue(key, out var c);
                counts[key] = c + 1;
            }
        }

        if (counts.Count == 0) return System.Array.Empty<ulong>();

        // C nested_util.c counts repetitions after the first occurrence (count > 0)
        // and tries the first 50. A less frequent repeated key can still be correct;
        // a list of singletons means the captures did not corroborate a key.
        return counts.Where(kv => kv.Value > 1)
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key)
            .Take(50)
            .Select(kv => kv.Key)
            .ToArray();
    }
}
