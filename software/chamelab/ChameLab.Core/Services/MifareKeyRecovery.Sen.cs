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
    // SEN follows Proxmark3 cmdhfmfsen.c at c7b80e1bc89ed03983832f93b1d2b5f980c8bc9b (GPLv3).
    // Device I/O and confirmed-key persistence stay here; candidate preparation runs in the CPU engine.
    private async Task RecoverSenAsync(MifareDump dump, IReadOnlyList<byte[]> dictionary,
        MifareRecoverySession session, Action<int, MfKeyType, byte[]> store, IProgress<KeyRecoveryProgress>? progress,
        Func<Task> spread, IReadOnlyList<ushort> capabilities, CancellationToken ct, bool manual = false)
    {
        bool Complete() => dump.KeyA.Concat(dump.KeyB).All(k => k is not null);
        ulong? Known(int sector, MfKeyType type) => sector < dump.SectorCount
            ? (type == MfKeyType.A ? dump.KeyA : dump.KeyB)[sector] is { } key ? KeyValue(key) : null
            : session.SenExtraKeys.GetValueOrDefault(type);
        void Store(int sector, MfKeyType type, byte[] key)
        {
            if (sector < dump.SectorCount) store(sector, type, key);
            else session.SenExtraKeys[type] = KeyValue(key);
        }
        if (session.SenCapture is null)
        {
            Func<Task<(uint Uid, IReadOnlyList<(NestedNonce A, NestedNonce B)> Sectors)>>? collect = null;
            (int Sector, MfKeyType Type, byte[] Key)? normalSeed = null;
            byte[]? acceptedBackdoor = null;
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
                        acceptedBackdoor = backdoor;
                        break;
                    }
                    catch (ChameleonException ex) when (ex.Status == Status.MfErrAuth)
                    { session.RejectedBackdoors.Add(KeyValue(backdoor)); collect = null; }
                }
                session.BackdoorsChecked = true;
            }
            if (captured is null && !manual && capabilities.Contains((ushort)Command.Mf1EncNestedWithKey))
            {
                var seeds = Enumerable.Range(0, dump.SectorCount)
                    .SelectMany(s => new[] { (Sector: s, Type: MfKeyType.A, Key: dump.KeyA[s]), (Sector: s, Type: MfKeyType.B, Key: dump.KeyB[s]) })
                    .Where(p => p.Key is not null);
                foreach (var seed in seeds)
                {
                    var block = MifareClassic.SectorFirstBlock(seed.Sector);
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
                    catch (ChameleonException ex) when (ex.Status == Status.HfErrStat) { return; }
                }
            }
            if (captured is not { } data || collect is null)
            {
                if (manual) throw new InvalidOperationException(Strings.Recovery_NoBackdoorAccepted);
                return;
            }
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
                if (!StaticEncryptedNestedRecovery.Matches(data.Uid, nonce, KeyValue(proof.Key))) return;
            }
            for (var s = 0; s < dump.SectorCount; s++)
                if ((Known(s, MfKeyType.A) is null && data.Sectors[s].A != confirmation.Sectors[s].A) ||
                    (Known(s, MfKeyType.B) is null && data.Sectors[s].B != confirmation.Sectors[s].B))
                {
                    if (manual) throw new InvalidOperationException(Strings.Recovery_StaticEncNotStatic);
                    return;
                }
            (NestedNonce A, NestedNonce B)? extra = null;
            // FM11RF08S has an additional sector 32 at block 128. Older firmware
            // addressed trailer 143 instead; failure of this optional capture must
            // not discard the independently confirmed normal-sector evidence.
            if (dump.Type == MifareClassicType.Classic1K && acceptedBackdoor is not null)
            {
                try
                {
                    var first = await client.GetStaticEncryptedNestedNoncesAsync(acceptedBackdoor, 33, ct, startingSector: 32);
                    var second = await client.GetStaticEncryptedNestedNoncesAsync(acceptedBackdoor, 33, ct, startingSector: 32);
                    await VerifyCard();
                    if (first.Uid != data.Uid || second.Uid != data.Uid)
                        throw new InvalidOperationException(Strings.Recovery_CardRemoved);
                    if (first.Sectors[0] == second.Sectors[0]) extra = first.Sectors[0];
                }
                catch (ChameleonException ex) when (ex.Status is Status.MfErrAuth or Status.HfErrStat) { }
            }
            session.SenCapture = new(data.Uid, data.Sectors) { Extra = extra };
        }
        var capture = session.SenCapture;
        var targets = Enumerable.Range(0, dump.SectorCount).Concat(capture.Extra is null ? [] : new[] { 32 }).ToArray();
        (NestedNonce A, NestedNonce B) Pair(int sector) => sector == 32 && capture.Extra is { } extra ? extra : capture.Sectors[sector];
        NestedNonce Nonce(int sector, MfKeyType type) => type == MfKeyType.A ? Pair(sector).A : Pair(sector).B;
        var values = dictionary.Where(k => k.Length == 6).Select(KeyValue).Distinct().ToArray();
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var value in values) digest.AppendData(KeyBytes(value));
        var dictionaryId = Convert.ToHexString(digest.GetHashAndReset());
        var matchesByNonce = new Dictionary<NestedNonce, IReadOnlyList<ulong>>();

        async Task<bool> Check(int sector, MfKeyType type, IReadOnlyList<ulong> candidates)
        {
            var block = MifareClassic.SectorFirstBlock(sector);
            var remaining = candidates.Where(k => !session.Rejected.Contains((block, type, k))).ToArray();
            for (var i = 0; i < remaining.Length; i += 64)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new(Strings.Recovery_CheckStaticEnc, sector, type, KeyMark.Checking,
                    Detail: string.Format(Strings.Recovery_CandidatesRange, i + 1, Math.Min(i + 64, remaining.Length), remaining.Length))
                    { Tried = i, Total = remaining.Length });
                var keys = remaining.Skip(i).Take(64).Select(KeyBytes).ToArray();
                byte[]? hit;
                for (var attempt = 0; ; attempt++)
                    try
                    {
                        hit = await client.CheckMifareKeysOnBlockAsync(block, type, keys, ct, session,
                            key => Store(sector, type, key));
                        break;
                    }
                    catch (ChameleonException ex) when (attempt < 1 && ex.Status is
                        Status.HfErrStat or Status.HfErrCrc or Status.HfCollision or Status.HfErrBcc or Status.HfErrParity or Status.HfErrAts) { }
                if (hit is not null) { await spread(); return true; }
            }
            return false;
        }

        // Only the user's selected dictionary is used; manual SEN supplies none.
        if (values.Length > 0)
            foreach (var s in targets)
                foreach (var type in new[] { MfKeyType.A, MfKeyType.B })
                {
                    if (Known(s, type) is not null || session.SenDictionaries.Contains((s, type, dictionaryId))) continue;
                    var nonce = Nonce(s, type);
                    if (!matchesByNonce.TryGetValue(nonce, out var matches))
                    {
                        var found = new List<ulong>();
                        for (var i = 0; i < values.Length; i += 4096)
                        {
                            progress?.Report(new(Strings.Common_CheckingDictKeys, s, type, KeyMark.Checking) { Tried = i, Total = values.Length });
                            found.AddRange(await _keyRecovery.RecoverAsync(new(KeyRecoveryOperation.MatchStaticEncrypted)
                                { Uid = capture.Uid, Nonces = [nonce], Keys = values.Skip(i).Take(4096).ToArray() }, ct));
                        }
                        matchesByNonce[nonce] = matches = found;
                    }
                    await Check(s, type, matches);
                    session.SenDictionaries.Add((s, type, dictionaryId));
                }

        if (Complete()) return;

        var planner = new SenRecoveryPlanner();
        foreach (var s in targets)
        {
            foreach (var type in new[] { MfKeyType.A, MfKeyType.B })
            {
                ct.ThrowIfCancellationRequested();
                var nonce = Nonce(s, type);
                IReadOnlyList<ulong> keys = [];
                if (Known(s, type) is null && !session.SenExhausted.Contains((s, type)))
                {
                    if (!session.SenCandidates.TryGetValue((s, type), out keys!))
                    {
                        progress?.Report(new(Strings.Recovery_CalcStaticEnc, s, type, KeyMark.Checking));
                        keys = await _keyRecovery.RecoverAsync(new(KeyRecoveryOperation.StaticEncryptedNested)
                            { Uid = capture.Uid, Nonces = [nonce] }, ct);
                        session.SenCandidates[(s, type)] = keys;
                    }
                }
                planner.Add(new(s, type, nonce, keys) { KnownKey = Known(s, type) });
            }
            var a = planner.Get(s, MfKeyType.A)!;
            var b = planner.Get(s, MfKeyType.B)!;
            if (a.KnownKey is null && b.KnownKey is null && !session.SenPrepared.Contains(s))
            {
                var orderedA = await _keyRecovery.RecoverAsync(new(KeyRecoveryOperation.OrderStaticEncrypted)
                    { Nonces = [a.Nonce, b.Nonce], Keys = a.Keys.ToArray(), OtherKeys = b.Keys.ToArray(), PreferredKeys = values }, ct);
                var orderedB = await _keyRecovery.RecoverAsync(new(KeyRecoveryOperation.OrderStaticEncrypted)
                    { Nonces = [b.Nonce, a.Nonce], Keys = b.Keys.ToArray(), OtherKeys = a.Keys.ToArray(), PreferredKeys = values }, ct);
                if (s == 32) orderedB = orderedB.Order().ToArray(); // fm11_prioritize_0000_prefix
                planner.ReplaceKeys(a, orderedA);
                planner.ReplaceKeys(b, orderedB);
                session.SenCandidates[(s, MfKeyType.A)] = orderedA;
                session.SenCandidates[(s, MfKeyType.B)] = orderedB;
                session.SenPrepared.Add(s);
            }
        }

        // All lists participate, including later sectors and the hidden sector.
        var propagated = planner.Paths.Where(p => p.KnownKey is not null).Select(p => p.KnownKey!.Value).ToHashSet();
        var priority = planner.PrioritizeGlobal();
        foreach (var p in planner.Paths.Where(p => p.KnownKey is null)) session.SenCandidates[(p.Sector, p.Type)] = p.Keys;
        async Task CheckGlobal(IReadOnlyList<ulong> keys)
        {
            if (keys.Count == 0) return;
            var wanted = Enumerable.Range(0, dump.SectorCount)
                .SelectMany(s => new[] { (s, MfKeyType.A), (s, MfKeyType.B) })
                .Where(p => Known(p.s, p.Item2) is null).ToArray();
            if (wanted.Length == 0) return;
            var remaining = keys.Where(k => wanted.Any(p => !session.Rejected.Contains(
                (MifareClassic.SectorFirstBlock(p.s), p.Item2, k)))).ToArray();
            if (remaining.Length == 0) return;
            if (capabilities.Contains((ushort)Command.Mf1CheckKeysOfSectors))
                await client.CheckMifareKeysOfSectorsAsync(wanted, remaining.Select(KeyBytes).ToArray(),
                    keysChecked: new SenBatchProgress(n => progress?.Report(new(Strings.Recovery_CheckStaticEnc)
                        { Tried = n, Total = remaining.Length })),
                    onFound: (p, key) => Store(p.Sector, p.Type, key), ct: ct, session: session);
            else
                foreach (var p in wanted)
                    if (Known(p.s, p.Item2) is null) await Check(p.s, p.Item2, remaining);
            await spread();
        }
        await CheckGlobal(priority.Reuse);
        if (Complete()) return;
        await CheckGlobal(priority.Duplicates);
        if (Complete()) return;

        async Task Cascade()
        {
            // RF-confirm every inferred/reused key, including hidden-sector keys.
            // A seed reduction narrows the paired list; it never confirms a key by itself.
            while (true)
            {
                planner.UpdateKnown(Known);
                var known = planner.Paths.Where(p => p.KnownKey is not null).Select(p => p.KnownKey!.Value)
                    .Distinct().Where(k => !propagated.Contains(k)).ToArray();
                if (known.Length == 0) break;
                foreach (var key in known)
                {
                    propagated.Add(key);
                    await CheckGlobal([key]);
                    if (Complete()) return;
                    foreach (var p in planner.Paths.Where(p => Known(p.Sector, p.Type) is null && p.Keys.Contains(key)).ToArray())
                        await Check(p.Sector, p.Type, [key]);
                }
            }
            planner.UpdateKnown(Known);
            foreach (var p in planner.Paths.Where(p => p.KnownKey is null).ToArray())
            {
                var other = planner.Get(p.Sector, p.Type == MfKeyType.A ? MfKeyType.B : MfKeyType.A)!;
                if (other.KnownKey is not { } key) continue;
                IReadOnlyList<ulong> reduced;
                if (p.Nonce.Nt != other.Nonce.Nt)
                {
                    reduced = await _keyRecovery.RecoverAsync(new(KeyRecoveryOperation.FilterStaticEncrypted)
                        { Nonces = [p.Nonce, other.Nonce], Keys = p.Keys.ToArray(), OtherKeys = [key] }, ct);
                    // PM3 leaves the list intact if the known seed has no matches.
                    if (reduced.Count == 0) continue;
                }
                else reduced = p.Keys.OrderByDescending(k => k == key).ToArray();
                planner.ReplaceKeys(p, reduced);
                session.SenCandidates[(p.Sector, p.Type)] = reduced;
            }
        }
        await Cascade();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            // The hidden sector helps recovery; reading a normal dump does not
            // require exhausting its keys after every visible key is confirmed.
            if (Complete()) return;
            var best = planner.Next(session);
            if (best is null) break;
            if (await Check(best.Sector, best.Type, best.Keys)) await Cascade();
            else session.SenExhausted.Add((best.Sector, best.Type));
        }
    }

    private sealed class SenBatchProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }
}
