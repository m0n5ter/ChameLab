using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Localization;
using ChameLab.Models;
using ChameLab.Protocol;

namespace ChameLab.Services;

public sealed partial class MifareKeyRecovery
{
    private async Task RecoverSenAdaptiveAsync(MifareDump dump, IReadOnlyList<byte[]> dictionary,
        MifareRecoverySession session, Action<int, MfKeyType, byte[]> store, IProgress<KeyRecoveryProgress>? progress,
        Func<Task> spread, IReadOnlyList<ushort> capabilities, CancellationToken ct)
    {
        bool Known(int sector, MfKeyType type) => (type == MfKeyType.A ? dump.KeyA : dump.KeyB)[sector] is not null;
        if (session.SenCapture is null)
        {
            Func<Task<(uint Uid, IReadOnlyList<(NestedNonce A, NestedNonce B)> Sectors)>>? collect = null;
            (int Sector, MfKeyType Type, byte[] Key)? normalSeed = null;
            (uint Uid, IReadOnlyList<(NestedNonce A, NestedNonce B)> Sectors)? captured = null;
            if (!session.BackdoorsChecked && capabilities.Contains((ushort)Command.Mf1EncNestedAcquire))
            {
                foreach (var hex in SenBackdoorKeys)
                {
                    var backdoor = Convert.FromHexString(hex);
                    if (session.RejectedBackdoors.Contains(KeyValue(backdoor))) continue;
                    progress?.Report(new(Strings.Recovery_CollectingStaticEnc, Detail: string.Format(Strings.Recovery_BackdoorKey, hex)));
                    try
                    {
                        collect = () => client.GetStaticEncryptedNestedNoncesAsync(backdoor, dump.SectorCount, ct);
                        captured = await collect();
                        break;
                    }
                    catch (ChameleonException ex) when (ex.Status == Status.MfErrAuth)
                    { session.RejectedBackdoors.Add(KeyValue(backdoor)); collect = null; }
                }
                session.BackdoorsChecked = true;
            }
            if (captured is null && capabilities.Contains((ushort)Command.Mf1EncNestedWithKey))
            {
                var seeds = Enumerable.Range(0, dump.SectorCount)
                    .SelectMany(s => new[] { (Sector: s, Type: MfKeyType.A, Key: dump.KeyA[s]), (Sector: s, Type: MfKeyType.B, Key: dump.KeyB[s]) })
                    .Where(p => p.Key is not null);
                foreach (var seed in seeds)
                {
                    var block = MifareClassic.SectorTrailerBlock(seed.Sector);
                    var id = (block, seed.Type, KeyValue(seed.Key!));
                    if (session.NormalSenSeedsChecked.Contains(id)) continue;
                    progress?.Report(new(Strings.Recovery_CollectingStaticEnc));
                    collect = () => client.GetStaticEncryptedNestedNoncesWithKeyAsync(seed.Key!, block, seed.Type, dump.SectorCount, ct);
                    try
                    {
                        captured = await collect();
                        normalSeed = (seed.Sector, seed.Type, seed.Key!);
                        session.NormalSenSeedsChecked.Add(id);
                        break;
                    }
                    catch (ChameleonException ex) when (ex.Status == Status.MfErrAuth)
                    { session.NormalSenSeedsChecked.Add(id); collect = null; }
                    // A calibration failure is inconclusive. Do not try the same timing
                    // experiment on every known sector or remember it as a rejected key.
                    catch (ChameleonException ex) when (ex.Status == Status.HfErrStat) { return; }
                }
            }
            if (captured is not { } data || collect is null) return;
            async Task VerifyCard()
            {
                var tags = await client.Hf14aScanAsync(ct);
                if (tags.Count != 1 || !dump.Uid.AsSpan().SequenceEqual(tags[0].Uid))
                    throw new InvalidOperationException(Strings.Recovery_CardRemoved);
            }
            await VerifyCard();
            var confirmation = await collect();
            await VerifyCard();
            if (confirmation.Uid != data.Uid || data.Uid != ReadUid(dump.Uid))
                throw new InvalidOperationException(Strings.Recovery_CardRemoved);
            if (normalSeed is { } proof)
            {
                var pair = data.Sectors[proof.Sector];
                var nonce = proof.Type == MfKeyType.A ? pair.A : pair.B;
                // Repeated timing errors can also be stable. The inferred nonce
                // must explain the key used to calibrate this capture.
                if (!StaticEncryptedNestedRecovery.Matches(data.Uid, nonce, KeyValue(proof.Key))) return;
            }
            for (var s = 0; s < dump.SectorCount; s++)
                if ((!Known(s, MfKeyType.A) && data.Sectors[s].A != confirmation.Sectors[s].A) ||
                    (!Known(s, MfKeyType.B) && data.Sectors[s].B != confirmation.Sectors[s].B)) return;
            session.SenCapture = new(data.Uid, data.Sectors);
        }
        var capture = session.SenCapture;
        var values = dictionary.Where(k => k.Length == 6).Select(KeyValue).Distinct().ToArray();
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var value in values) digest.AppendData(KeyBytes(value));
        var dictionaryId = Convert.ToHexString(digest.GetHashAndReset());
        var matchesByNonce = new Dictionary<NestedNonce, IReadOnlyList<ulong>>();

        async Task<bool> Check(int sector, MfKeyType type, IReadOnlyList<ulong> candidates)
        {
            var block = MifareClassic.SectorTrailerBlock(sector);
            var remaining = candidates.Where(k => !session.Rejected.Contains((block, type, k))).ToArray();
            for (var i = 0; i < remaining.Length; i += 64)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new(Strings.Recovery_CheckStaticEnc, sector, type, KeyMark.Checking,
                    Detail: string.Format(Strings.Recovery_CandidatesRange, i + 1, Math.Min(i + 64, remaining.Length), remaining.Length)));
                var keys = remaining.Skip(i).Take(64).Select(KeyBytes).ToArray();
                byte[]? hit = null;
                for (var attempt = 0; ; attempt++)
                    try
                    {
                        hit = await client.CheckMifareKeysOnBlockAsync(block, type, keys, ct, session,
                            key => store(sector, type, key));
                        break;
                    }
                    catch (ChameleonException ex) when (attempt < 1 && ex.Status is
                        Status.HfErrStat or Status.HfErrCrc or Status.HfCollision or Status.HfErrBcc or Status.HfErrParity or Status.HfErrAts)
                    { /* RF failures say nothing about these candidates. Retry once. */ }
                if (hit is not null) { await spread(); return true; }
            }
            return false;
        }

        // Local evidence checks are CPU work in the same engine/worker as recovery.
        // Only matching keys cross the radio link, and each still needs authentication.
        if (values.Length > 0)
            for (var s = 0; s < dump.SectorCount; s++)
                foreach (var type in new[] { MfKeyType.A, MfKeyType.B })
                {
                    if (Known(s, type) || session.SenDictionaries.Contains((s, type, dictionaryId))) continue;
                    var nonce = type == MfKeyType.A ? capture.Sectors[s].A : capture.Sectors[s].B;
                    if (!matchesByNonce.TryGetValue(nonce, out var matches))
                    {
                        var found = new List<ulong>();
                        for (var i = 0; i < values.Length; i += 4096)
                        {
                            progress?.Report(new(Strings.Common_CheckingDictKeys, s, type, KeyMark.Checking)
                                { Tried = i, Total = values.Length });
                            found.AddRange(await _keyRecovery.RecoverAsync(new(KeyRecoveryOperation.MatchStaticEncrypted)
                                { Uid = capture.Uid, Nonces = [nonce], Keys = values.Skip(i).Take(4096).ToArray() }, ct));
                        }
                        matchesByNonce[nonce] = matches = found;
                    }
                    await Check(s, type, matches);
                    session.SenDictionaries.Add((s, type, dictionaryId));
                }

        var planner = new SenRecoveryPlanner();
        async Task Reprioritize()
        {
            foreach (var path in planner.Paths.ToArray())
            {
                if (Known(path.Sector, path.Type)) { planner.Remove(path.Sector, path.Type); session.SenCandidates.Remove((path.Sector, path.Type)); continue; }
                var otherType = path.Type == MfKeyType.A ? MfKeyType.B : MfKeyType.A;
                var otherKnown = (otherType == MfKeyType.A ? dump.KeyA : dump.KeyB)[path.Sector];
                var otherNonce = otherType == MfKeyType.A ? capture.Sectors[path.Sector].A : capture.Sectors[path.Sector].B;
                var other = otherKnown is not null ? new[] { KeyValue(otherKnown) }
                    : planner.Paths.FirstOrDefault(p => p.Sector == path.Sector && p.Type == otherType)?.Keys.ToArray() ?? [];
                var all = planner.Prioritize(path).Where(k => !session.Rejected.Contains((MifareClassic.SectorTrailerBlock(path.Sector), path.Type, k))).ToArray();
                IReadOnlyList<ulong> preferred = [];
                if (other.Length > 0 && otherNonce.Nt != path.Nonce.Nt)
                    preferred = await _keyRecovery.RecoverAsync(new(KeyRecoveryOperation.FilterStaticEncrypted)
                        { Nonces = [path.Nonce, otherNonce], Keys = all, OtherKeys = other }, ct);
                else if (otherKnown is not null && all.Contains(KeyValue(otherKnown))) preferred = [KeyValue(otherKnown)];
                var order = preferred.Concat(all.Except(preferred)).ToArray();
                // Keep all evidence-valid candidates: compatible backdoored chips need
                // not share the RF08S seed relationship used for prioritization.
                var updated = path with { Keys = order, PreferredCount = preferred.Count,
                    CascadeBenefit = 1 + (other.Length > 0 ? 1 : 0) + planner.Paths.Count(p => p != path && p.Keys.Intersect(order.Take(64)).Any()) };
                planner.Add(updated);
                session.SenCandidates[(path.Sector, path.Type)] = order;
            }
        }

        foreach (var cached in session.SenCandidates.ToArray())
        {
            if (!planner.HasRoom) break;
            var (s, type) = cached.Key;
            if (Known(s, type)) { session.SenCandidates.Remove(cached.Key); continue; }
            planner.Add(new(s, type, type == MfKeyType.A ? capture.Sectors[s].A : capture.Sectors[s].B, cached.Value));
        }
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var prepared = false;
            for (var s = 0; s < dump.SectorCount && planner.HasRoom; s++)
                foreach (var type in new[] { MfKeyType.A, MfKeyType.B })
                {
                    if (!planner.HasRoom) break;
                    if (Known(s, type) || planner.Contains(s, type) || session.SenExhausted.Contains((s, type))) continue;
                    var nonce = type == MfKeyType.A ? capture.Sectors[s].A : capture.Sectors[s].B;
                    progress?.Report(new(Strings.Recovery_CalcStaticEnc, s, type, KeyMark.Checking));
                    var candidates = planner.Paths.FirstOrDefault(p => p.Nonce == nonce)?.Keys
                        ?? await _keyRecovery.RecoverAsync(new(KeyRecoveryOperation.StaticEncryptedNested)
                            { Uid = capture.Uid, Nonces = [nonce] }, ct);
                    planner.Add(new(s, type, nonce, candidates));
                    session.SenCandidates[(s, type)] = candidates;
                    prepared = true;
                }
            if (prepared) await Reprioritize();
            var best = planner.Next(dump, session);
            if (best is null) break;
            if (best.Keys.Count == 0)
            {
                session.SenExhausted.Add((best.Sector, best.Type));
                session.SenCandidates.Remove((best.Sector, best.Type));
                planner.Remove(best.Sector, best.Type);
                continue;
            }
            // A short batch is a scheduling quantum, not a dictionary prefix.
            // Re-evaluate all prepared paths after it and after each key-reuse cascade.
            var batch = best.Keys.Take(64).ToArray();
            var hit = await Check(best.Sector, best.Type, batch);
            if (hit)
            {
                planner.Remove(best.Sector, best.Type);
                session.SenCandidates.Remove((best.Sector, best.Type));
                await Reprioritize();
            }
            else
            {
                var remaining = best.Keys.Where(k => !session.Rejected.Contains((MifareClassic.SectorTrailerBlock(best.Sector), best.Type, k))).ToArray();
                planner.Add(best with { Keys = remaining, PreferredCount = Math.Max(0, best.PreferredCount - batch.Length) });
                session.SenCandidates[(best.Sector, best.Type)] = remaining;
            }
        }
    }
}
