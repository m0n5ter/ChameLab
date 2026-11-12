using System.Threading;
using System.Threading.Tasks;
using ChameLab.Models;
using ChameLab.Protocol;

namespace ChameLab.Services;

/// <summary>Fast passive inspection. Vulnerability is confirmed only by the selected attack or Auto.</summary>
public sealed record MifareRecoveryProfile(MifareClassicPrngType Prng, bool CanTryDarkside)
{
    public static async Task<MifareRecoveryProfile> ReadAsync(ChameleonClient client,
        MifareRecoverySession session, CancellationToken ct = default)
    {
        var prng = session.Prng ?? await client.DetectNtLevelAsync(ct);
        session.Prng = prng;
        // The firmware's "Darkside check" performs a full parity collection.
        // Scanning must not spend a minute on it before Auto can attempt SEN.
        return new(prng, prng == MifareClassicPrngType.Weak &&
            (!session.Darkside.TryGetValue((3, MfKeyType.B), out var result) || result == DarksideResult.Vulnerable));
    }
}
