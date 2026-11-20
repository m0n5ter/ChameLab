using System;
using System.Collections.Generic;
using System.Threading;
using ChameLab.Protocol;
using ChameLab.Crypto;

namespace ChameLab.Services;

/// <summary>Recovers static-nonce keys locally, retaining only keys that explain both captures.</summary>
public static class StaticNestedRecovery
{
    /// <summary>
    /// Static cards use nonce offsets 160/320 or 161/321. The seed key type
    /// orders the guesses, but cannot determine the offset: a card can return
    /// either nonce generation depending on the authenticated sector/key.
    /// Keep the original result when both captures agree; otherwise try the
    /// alternative offset, again requiring agreement between both captures.
    /// </summary>
    public static IReadOnlyList<ulong> Recover(
        uint uid, MfKeyType knownKeyType,
        uint nt0, uint nt0Enc,
        uint nt1, uint nt1Enc,
        CancellationToken ct = default)
    {
        if (knownKeyType is not (MfKeyType.A or MfKeyType.B)) return Array.Empty<ulong>();
        if (nt0 != nt1 || nt0 is not (0x01200145 or 0x009080A2)) return Array.Empty<ulong>();
        var keys = new HashSet<ulong>();
        var preferredDistance = nt0 == 0x009080A2 && knownKeyType == MfKeyType.B ? 161 : 160;
        RecoverAtDistance(preferredDistance);
        if (keys.Count == 0)
            RecoverAtDistance(preferredDistance == 160 ? 161 : 160);
        return new List<ulong>(keys);

        void RecoverAtDistance(int distance)
        {
            ct.ThrowIfCancellationRequested();
            var ntp0 = Crypto1.PrngSuccessor(nt0, distance);
            var ntp1 = Crypto1.PrngSuccessor(nt1, distance + 160);
            var seed0 = uid ^ ntp0;
            var seed1 = uid ^ ntp1;
            var ks0 = nt0Enc ^ ntp0;
            var ks1 = nt1Enc ^ ntp1;
            foreach (var recovered in Crapto1.LfsrRecovery32(ks0, seed0))
            {
                ct.ThrowIfCancellationRequested();
                var state = recovered;
                if (state is { Odd: 0, Even: 0 }) continue;
                state.LfsrRollbackWord(seed0);
                var key = state.Lfsr;
                // A key matching only one sample is not a candidate. Checking the second
                // sample forward is cheaper than recovering and intersecting two full lists.
                var check = new Crypto1State(key);
                if (check.Crypto1Word(seed1) == ks1)
                    keys.Add(key);
            }
        }
    }
}
