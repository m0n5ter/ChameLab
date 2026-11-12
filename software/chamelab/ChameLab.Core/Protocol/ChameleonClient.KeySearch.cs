using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Models;

namespace ChameLab.Protocol;

public sealed partial class ChameleonClient
{
    private async Task SearchConfiguredMifareKeysAsync(MifareDump dump, IReadOnlyList<byte[]> keyDict,
        MifareKeySearchOptions options, bool onlyMissingData, IProgress<MifareKeyProgress>? progress, CancellationToken ct)
    {
        bool SeedFound() => options.StopAfterFirstKey && dump.KeyA.Concat(dump.KeyB).Any(k => k is not null);
        ct.ThrowIfCancellationRequested();
        if (SeedFound()) return;
        var dictionary = keyDict.Where(k => k.Length == 6).DistinctBy(Convert.ToHexString).ToArray();
        var sectors = options.GetSectorOrder(dump.SectorCount).Where(s => !onlyMissingData || !Enumerable
            .Range(MifareClassic.SectorFirstBlock(s), MifareClassic.BlocksInSector(s))
            .All(b => dump.GetBlock(b) is { Length: 16 })).ToArray();
        byte[]? Known((int Sector, MfKeyType Type) pair) =>
            pair.Type == MfKeyType.A ? dump.KeyA[pair.Sector] : dump.KeyB[pair.Sector];
        (int Sector, MfKeyType Type)[] Pending() => sectors
            .SelectMany(s => new[] { (Sector: s, Type: MfKeyType.A), (Sector: s, Type: MfKeyType.B) })
            .Where(p => Known(p) is null).ToArray();
        void Found((int Sector, MfKeyType Type) pair, byte[] key)
        {
            if (Known(pair) is not null) return;
            if (pair.Type == MfKeyType.A) dump.KeyA[pair.Sector] = key;
            else dump.KeyB[pair.Sector] = key;
            progress?.Report(new(pair.Sector, pair.Type, MifareKeyPhase.Found, 0, 0, key));
        }
        void Searching(int done, bool batch)
        {
            foreach (var pair in Pending())
                progress?.Report(new(pair.Sector, pair.Type, MifareKeyPhase.Searching, done, dictionary.Length, null) { IsBatch = batch });
        }

        async Task Singles()
        {
            foreach (var s in sectors)
                foreach (var type in new[] { MfKeyType.A, MfKeyType.B })
                {
                    ct.ThrowIfCancellationRequested();
                    if (SeedFound()) return;
                    if (type == MfKeyType.B && options.Session is { } session && dump.KeyA[s] is { } a
                        && dump.KeyB[s] is null && !session.TrailersChecked.Contains(s))
                    {
                        for (var attempt = 0; attempt < 2; attempt++)
                            try
                            {
                                var trailer = await Mf1ReadBlockAsync(MifareClassic.SectorTrailerBlock(s), MfKeyType.A, a, ct);
                                if (trailer.Length != 16 || !MifareClassic.AccessBitsValid(trailer)) break;
                                session.TrailersChecked.Add(s);
                                dump.SetTrailerFromCard(s, trailer);
                                if (MifareClassic.KeyBReadable(trailer)) Found((s, MfKeyType.B), trailer[10..16]);
                                break;
                            }
                            catch (ChameleonException ex) when (ex.Status != Status.HfTagNo)
                            { if (ex.Status == Status.MfErrAuth) break; }
                    }
                    if (Known((s, type)) is not null) continue;
                    var own = (type == MfKeyType.A ? dump.KeyA : dump.KeyB).OfType<byte[]>().ToArray();
                    var other = (type == MfKeyType.A ? dump.KeyB : dump.KeyA).OfType<byte[]>().ToArray();
                    if (await FindSectorKeyAsync(MifareClassic.SectorFirstBlock(s), type, s, dictionary, own, other, progress, ct, options.Session) is { } key)
                    {
                        if (type == MfKeyType.A) dump.KeyA[s] = key;
                        else dump.KeyB[s] = key;
                    }
                }
        }

        if (options.Method == MifareKeySearchMethod.SingleKey || !await SupportsBatchKeyCheckAsync(ct))
        {
            await Singles();
            return;
        }

        // Firmware 2012 always walks sectors in ascending order. Split a rotated/shuffled order
        // into ascending runs, preserving its order without requiring a different firmware.
        static IEnumerable<(int Sector, MfKeyType Type)[]> Runs((int Sector, MfKeyType Type)[] pairs)
        {
            var run = new List<(int Sector, MfKeyType Type)>();
            foreach (var pair in pairs)
            {
                if (run.Count > 0 && pair.Sector < run[^1].Sector) { yield return run.ToArray(); run.Clear(); }
                run.Add(pair);
            }
            if (run.Count > 0) yield return run.ToArray();
        }
        var reused = new HashSet<string>();
        async Task BatchInOrder(IReadOnlyList<byte[]> keys)
        {
            if (keys.Count == 0) return;
            foreach (var run in Runs(Pending()))
            {
                ct.ThrowIfCancellationRequested();
                if (SeedFound()) return;
                var groups = run.Where(p => Known(p) is null).GroupBy(p => string.Join(",", keys
                    .Where(k => options.Session?.Rejected.Contains((MifareClassic.SectorTrailerBlock(p.Sector), p.Type, MifareRecoverySession.Value(k))) != true)
                    .Select(Convert.ToHexString)));
                foreach (var group in groups)
                {
                    if (SeedFound()) return;
                    var candidates = group.Key.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Convert.FromHexString).ToArray();
                    if (candidates.Length == 0) continue;
                    await CheckMifareKeysOfSectorsAsync(group.ToArray(), candidates, onFound: Found, ct: ct,
                        stopAfterFirstKey: options.StopAfterFirstKey, session: options.Session);
                }
            }
            foreach (var key in keys) reused.Add(Convert.ToHexString(key));
        }
        async Task ReuseFoundKeys()
        {
            // A newly discovered A or trailer B gets priority over the rest of the dictionary.
            // Trying each distinct key only once also prevents repeated work on sectors it misses.
            while (Pending().Length > 0 && !SeedFound())
            {
                var key = dump.KeyA.Concat(dump.KeyB).OfType<byte[]>()
                    .FirstOrDefault(k => !reused.Contains(Convert.ToHexString(k)));
                if (key is null) break;
                reused.Add(Convert.ToHexString(key));
                await BatchInOrder([key]);
            }
        }

        try
        {
            if (options.Method == MifareKeySearchMethod.AllSectors) Searching(0, batch: true);
            await ReuseFoundKeys();
            if (options.Method == MifareKeySearchMethod.OneSector)
            {
                foreach (var s in sectors)
                    foreach (var type in new[] { MfKeyType.A, MfKeyType.B })
                    {
                        ct.ThrowIfCancellationRequested();
                        if (SeedFound()) return;
                        if (Known((s, type)) is not null) continue;
                        var candidates = dictionary.Where(k => !reused.Contains(Convert.ToHexString(k)) && options.Session?.Rejected
                            .Contains((MifareClassic.SectorTrailerBlock(s), type, MifareRecoverySession.Value(k))) != true).ToArray();
                        progress?.Report(new(s, type, MifareKeyPhase.Searching, 0, candidates.Length, null));
                        await CheckMifareKeysOfSectorsAsync([(s, type)], candidates,
                            keysChecked: new DelegateProgress<int>(done =>
                            {
                                if (Known((s, type)) is null)
                                    progress?.Report(new(s, type, MifareKeyPhase.Searching, done, candidates.Length, null));
                            }), onFound: Found, ct: ct, stopAfterFirstKey: options.StopAfterFirstKey, session: options.Session);
                        if (SeedFound()) return;
                        if (Known((s, type)) is null)
                            progress?.Report(new(s, type, MifareKeyPhase.NotFound, candidates.Length, candidates.Length, null));
                        await ReuseFoundKeys();
                    }
            }
            else
            {
                var done = 0;
                while (done < dictionary.Length && Pending().Length > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    var chunk = dictionary.Skip(done).Take(BatchKeyCheckChunkSize).ToArray();
                    await BatchInOrder(chunk.Where(k => !reused.Contains(Convert.ToHexString(k))).ToArray());
                    if (SeedFound()) return;
                    done += chunk.Length;
                    await ReuseFoundKeys();
                    Searching(done, batch: true);
                }
                foreach (var pair in Pending())
                    progress?.Report(new(pair.Sector, pair.Type, MifareKeyPhase.NotFound, dictionary.Length, dictionary.Length, null));
            }
        }
        catch (Exception ex) when (ex is NotSupportedException || ex is ChameleonException { Status: not Status.HfTagNo })
        {
            // Rejected commands fall back; lost cards, cancellation and failed links propagate.
            await Singles();
        }
    }
}
