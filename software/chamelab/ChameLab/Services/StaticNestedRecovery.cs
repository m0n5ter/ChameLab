using System.Collections.Generic;
using ChameLab.Protocol;
using Crapto1Sharp;

namespace ChameLab.Services;

/// <summary>
/// MIFARE Classic "static nested" key recovery, for cards whose PRNG always returns the
/// same tag nonce. Ported from the pm3 reference (<c>staticnested.c</c> via
/// ChameleonUltraGUI's <c>recovery.c</c>). There is no distance to measure: the keystream is
/// derived from a fixed PRNG offset that depends on the known static-nonce generation.
/// </summary>
public static class StaticNestedRecovery
{
    /// <summary>
    /// Recovers candidate 48-bit keys from two static nested samples. Returns an empty list
    /// when the card's static nonce is not a known vulnerable generation. <paramref name="targetKeyType"/>
    /// is the key being recovered (gen2 needs a different offset for A vs B).
    /// </summary>
    public static IReadOnlyList<ulong> Recover(
        uint uid, MfKeyType targetKeyType,
        uint nt0, uint nt0Enc,
        uint nt1, uint nt1Enc)
    {
        var pnk = new List<(uint Ntp, uint Ks1)>();
        uint dist = 0;
        var typeByte = targetKeyType.ToWire(); // 0x60 (A) / 0x61 (B)
        var resolved = false;

        for (var i = 0; i < 2; i++)
        {
            var nt = i == 0 ? nt0 : nt1;
            var ntEnc = i == 0 ? nt0Enc : nt1Enc;

            if (!resolved)
            {
                if (nt == 0x01200145)
                {
                    dist = 160; // static gen1
                }
                else if (nt == 0x009080A2)
                {
                    // static gen2: the offset depends on which key is being attacked.
                    if (typeByte == 0x61) dist = 161;
                    else if (typeByte == 0x60) dist = 160;
                    else return System.Array.Empty<ulong>();
                }
                else
                {
                    return System.Array.Empty<ulong>(); // not a known static generation
                }
                resolved = true;
            }

            var ntTest = Crypto1.PrngSuccessor(nt, (int)dist);
            var ks1 = ntEnc ^ ntTest;
            pnk.Add((ntTest, ks1));
            dist += 160;
        }

        return NestedCore.Run(pnk, uid);
    }
}
