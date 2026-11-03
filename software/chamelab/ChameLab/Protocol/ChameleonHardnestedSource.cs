using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Services;
using ChameLab.Localization;

namespace ChameLab.Protocol;

public sealed class ChameleonHardnestedSource(
    ChameleonClient client, byte[] expectedUid,
    int knownBlock, MfKeyType knownType, byte[] knownKey,
    int targetBlock, MfKeyType targetType) : IHardnestedNonceSource
{
    public async Task<IReadOnlyList<HardnestedSample>> ReadBatchAsync(bool slow, CancellationToken ct)
    {
        var tags = await client.Hf14aScanAsync(ct);
        if (tags.Count != 1 || !expectedUid.AsSpan().SequenceEqual(tags[0].Uid))
            throw new InvalidOperationException(Strings.Recovery_CardRemoved);
        return await client.GetHardnestedNoncesAsync(
            slow, knownBlock, knownType, knownKey, targetBlock, targetType, ct);
    }
}
