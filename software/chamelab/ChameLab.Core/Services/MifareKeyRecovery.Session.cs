using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Localization;
using ChameLab.Models;
using ChameLab.Protocol;

namespace ChameLab.Services;

public sealed partial class MifareKeyRecovery
{
    private static readonly string[] SenBackdoorKeys = ["A396EFA4E24F", "A31667A8CEC1", "518B3354E760", "73B9836CF168"];

    private async Task ReadableKeysBAsync(MifareDump dump, Action<int, MfKeyType, byte[]> store,
        MifareRecoverySession session, CancellationToken ct)
    {
        for (var sector = 0; sector < dump.SectorCount; sector++)
        {
            if (dump.KeyA[sector] is not { } a || dump.KeyB[sector] is not null || session.TrailersChecked.Contains(sector)) continue;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var trailer = await client.Mf1ReadBlockAsync(MifareClassic.SectorTrailerBlock(sector), MfKeyType.A, a, ct);
                    if (trailer.Length != 16 || !MifareClassic.AccessBitsValid(trailer)) break;
                    session.TrailersChecked.Add(sector);
                    dump.SetTrailerFromCard(sector, trailer);
                    if (MifareClassic.KeyBReadable(trailer)) store(sector, MfKeyType.B, trailer[10..16]);
                    break;
                }
                catch (ChameleonException ex) when (ex.Status is not Status.HfTagNo)
                {
                    if (ex.Status == Status.MfErrAuth) break;
                }
            }
        }
    }

    private async Task KnownUidKeysAsync(MifareDump dump, MifareRecoverySession session,
        Action<int, MfKeyType, byte[]> store, IProgress<KeyRecoveryProgress>? progress, CancellationToken ct)
    {
        if (knownKeys is null) return;
        foreach (var p in knownKeys.PositionsForUid(dump.Uid).Where(p => p.Sector < dump.SectorCount))
        {
            if ((p.KeyType == MfKeyType.A ? dump.KeyA : dump.KeyB)[p.Sector] is not null) continue;
            progress?.Report(new(Strings.Recovery_CheckingPrevious, p.Sector, p.KeyType, KeyMark.Checking));
            if (await TryAuthAsync(MifareClassic.SectorTrailerBlock(p.Sector), p.KeyType, p.Key, ct, session.Rejected, session))
            {
                store(p.Sector, p.KeyType, p.Key);
                if (p.KeyType == MfKeyType.A) await ReadableKeysBAsync(dump, store, session, ct);
            }
        }
        var saved = knownKeys.ForUid(dump.Uid);
        if (saved.Count == 0 || dump.KeyA.Concat(dump.KeyB).All(k => k is not null)) return;
        var updates = new AutoDictionaryProgress(p =>
        {
            if (p.Phase == MifareKeyPhase.Found && p.Key is { } key) store(p.Sector, p.KeyType, key);
            else progress?.Report(new(Strings.Recovery_CheckingPrevious, p.Sector, p.KeyType, KeyMark.Checking));
        });
        await client.FindMifareKeysAsync(dump, saved, updates, ct,
            new() { Session = session });
    }
}
