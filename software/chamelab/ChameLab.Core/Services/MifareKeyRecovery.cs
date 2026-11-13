using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Localization;

namespace ChameLab.Services;

/// <summary>Independently selectable MIFARE Classic recovery methods.</summary>
public enum MifareRecoveryMethod { Darkside, Nested, StaticNested, Hardnested, StaticEncryptedNested }

/// <summary>State of one sector key during recovery, surfaced to the per-sector grid.</summary>
public enum KeyMark { None, Checking, Found }

/// <summary>A live recovery update: a status line and, when it concerns one key, which sector/key changed.</summary>
public sealed record KeyRecoveryProgress(
    string Status, int Sector = -1, MfKeyType KeyType = MfKeyType.A, KeyMark Mark = KeyMark.None, byte[]? Key = null, string? Detail = null)
{
    public int Tried { get; init; }
    public int Total { get; init; }
    public MifareClassicPrngType? Prng { get; init; }
}

/// <summary>Runs one selected recovery method, preserving keys already found in the shared dump.</summary>
public sealed partial class MifareKeyRecovery(ChameleonClient client, IHardnestedEngine? hardnested = null,
    KnownCardKeys? knownKeys = null, IKeyRecoveryEngine? keyRecovery = null, TimeProvider? timeProvider = null)
{
    private readonly IHardnestedEngine _hardnested = hardnested ?? new UnavailableHardnestedEngine();
    private readonly IKeyRecoveryEngine _keyRecovery = keyRecovery ?? AppServices.KeyRecovery;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public Task RecoverAsync(
        MifareDump dump, MifareRecoveryMethod method,
        IProgress<KeyRecoveryProgress>? progress = null, CancellationToken ct = default) =>
        RecoverCoreAsync(dump, method, progress, ct, spreadKeys: false, new());

    private async Task RecoverCoreAsync(MifareDump dump, MifareRecoveryMethod method,
        IProgress<KeyRecoveryProgress>? progress, CancellationToken ct, bool spreadKeys,
        MifareKeyRejections rejected, MifareRecoverySession? session = null)
    {
        if (method == MifareRecoveryMethod.Hardnested && _hardnested.UnavailableReason is { } unavailable)
            throw new NotSupportedException(unavailable);
        await client.SetDeviceModeAsync(DeviceMode.Reader, ct);
        var tags = await client.Hf14aScanAsync(ct);
        if (tags.Count == 0)
            throw new ChameleonException(Strings.Recovery_NoHfTag, Status.HfTagNo);
        if (!dump.Uid.AsSpan().SequenceEqual(tags[0].Uid))
            throw new InvalidOperationException(Strings.Recovery_CardChanged);
        var authUid = ReadUid(tags[0].Uid);
        var reusable = dump.KeyA.Concat(dump.KeyB).OfType<byte[]>()
            .DistinctBy(KeyValue).ToDictionary(KeyValue);
        var successes = new Dictionary<ulong, int>();
        var timing = method == MifareRecoveryMethod.Nested ? new NestedRecoveryTiming() : null;

        bool Known(int s, MfKeyType kt) => (kt == MfKeyType.A ? dump.KeyA : dump.KeyB)[s] is not null;
        void Mark(int s, MfKeyType kt, KeyMark mark, string status) =>
            progress?.Report(new KeyRecoveryProgress(status, s, kt, mark));
        void Store(int s, MfKeyType kt, byte[] key)
        {
            (kt == MfKeyType.A ? dump.KeyA : dump.KeyB)[s] = key;
            var value = KeyValue(key);
            reusable.TryAdd(value, key);
            successes[value] = successes.GetValueOrDefault(value) + 1;
            progress?.Report(new KeyRecoveryProgress(Strings.Recovery_FoundKey, s, kt, KeyMark.Found, key));
            knownKeys?.RememberPosition(dump.Uid, s, kt, key, MifareClassic.ToSlotTagType(dump.Type));
        }
        async Task<bool> TryKnownKeysAsync(int s, MfKeyType kt)
        {
            var block = MifareClassic.SectorTrailerBlock(s);
            var keys = reusable.Where(k => !rejected.Contains((block, kt, k.Key)))
                .OrderByDescending(k => successes.GetValueOrDefault(k.Key)).ToArray();
            if (keys.Length == 0) return false;
            for (var i = 0; i < keys.Length; i++)
            {
                progress?.Report(new KeyRecoveryProgress(Strings.Recovery_CheckingPrevious, s, kt,
                    KeyMark.Checking, Detail: string.Format(Strings.Recovery_KnownKeyProgress, i + 1, keys.Length)));
                var started = timing is null ? 0 : _timeProvider.GetTimestamp();
                var confirmed = await TryAuthAsync(block, kt, keys[i].Value, ct, rejected, session);
                timing?.RecordKeyAttempt(_timeProvider.GetElapsedTime(started));
                if (!confirmed) continue;
                Store(s, kt, keys[i].Value);
                return true;
            }
            Mark(s, kt, KeyMark.None, Strings.Recovery_NoKnownConfirmed);
            return false;
        }
        async Task SpreadKnownKeysAsync()
        {
            if (!spreadKeys) return;
            if (session is not null) await ReadableKeysBAsync(dump, Store, session, ct);
            await SpreadKeysAsync(dump, Store, progress, rejected, ct, session);
            if (session is not null) await ReadableKeysBAsync(dump, Store, session, ct);
        }

        if (method == MifareRecoveryMethod.Darkside)
        {
            await TryDarksideAsync(dump, authUid, Store, progress, ct);
            return;
        }

        if (method == MifareRecoveryMethod.StaticEncryptedNested)
        {
            await StaticEncryptedNestedAsync(dump, Store, progress, ct);
            return;
        }

        var prng = session?.Prng ?? await client.DetectNtLevelAsync(ct);
        if (session is not null) session.Prng = prng;
        var isStatic = method == MifareRecoveryMethod.StaticNested;
        var isHardnested = method == MifareRecoveryMethod.Hardnested;
        if (prng != (isHardnested ? MifareClassicPrngType.Hard :
                isStatic ? MifareClassicPrngType.Static : MifareClassicPrngType.Weak) &&
            !(isHardnested && session?.NestedUnreliable == true && prng == MifareClassicPrngType.Weak))
            throw new InvalidOperationException(Strings.Recovery_PrngUnsupported);
        if (FirstKnownKey(dump) is null)
            throw new InvalidOperationException(Strings.Recovery_NeedOneKey);
        if (isHardnested && !(await client.GetCapabilitiesAsync(ct)).Contains((ushort)Command.Mf1HardnestedAcquire))
            throw new NotSupportedException(Strings.Recovery_NoHardnestedFirmware);

        await SpreadKnownKeysAsync();
        for (var s = 0; s < dump.SectorCount; s++)
            foreach (var kt in new[] { MfKeyType.A, MfKeyType.B })
            {
                ct.ThrowIfCancellationRequested();
                if (Known(s, kt)) continue;
                var target = MifareClassic.SectorTrailerBlock(s);
                if (session?.Refused.Contains((target, kt)) == true) continue;
                var untestedKeys = reusable.Count(k => !rejected.Contains((target, kt, k.Key)));
                var knownFirst = timing?.ShouldCheckKnownKeys(untestedKeys) ?? true;
                if (knownFirst && await TryKnownKeysAsync(s, kt)) continue;
                if (!knownFirst && untestedKeys > 0 &&
                    timing?.EstimateKnownKeys(untestedKeys) is { } knownCost && timing.RecoveryTime is { } nestedCost)
                    progress?.Report(new KeyRecoveryProgress(Strings.Recovery_UsingNested, s, kt,
                        KeyMark.Checking, Detail: $"Known keys ({untestedKeys}): {knownCost.TotalMilliseconds:F0} ms · " +
                        $"nested: {nestedCost.TotalMilliseconds:F0} ms"));
                var seed = FirstKnownKey(dump)!.Value;
                Mark(s, kt, KeyMark.Checking,
                    isHardnested ? Strings.Recovery_CollectingHardnested :
                    isStatic ? Strings.Recovery_CollectingStaticNested : Strings.Recovery_CollectingNested);
                var attempts = session is not null && method == MifareRecoveryMethod.Nested ? 2 : 1;
                for (var attempt = 0; attempt < attempts; attempt++)
                {
                    try
                    {
                        var started = timing is null ? 0 : _timeProvider.GetTimestamp();
                        var candidates = isHardnested
                            ? await HardnestedSectorAsync(dump.Uid, authUid, seed, target, kt, progress, ct)
                            : isStatic
                            ? await StaticNestedSectorAsync(seed, target, kt, progress, ct)
                            : await NestedSectorAsync(seed, target, kt, progress, ct, session);
                        Mark(s, kt, KeyMark.Checking, string.Format(Strings.Recovery_CheckingCandidatesN, candidates.Count));
                        var hit = await FirstWorkingKeyAsync(target, kt, candidates, progress, ct, rejected: rejected, session: session);
                        if (hit is { } key)
                        {
                            timing?.RecordRecovery(_timeProvider.GetElapsedTime(started));
                            Store(s, kt, key);
                            break;
                        }
                        Mark(s, kt, KeyMark.None, Strings.Recovery_KeyNotRecovered);
                    }
                    catch (HardnestedStaticNonceException ex) when (session is not null)
                    {
                        // Auto resumes dictionary/SEN evidence after this method;
                        // a static target must not abort the whole recovery run.
                        Mark(s, kt, KeyMark.None, ex.Message);
                        return;
                    }
                    catch (ChameleonException ex)
                    {
                        if (session is not null && ex.Status == Status.HfTagNo) throw;
                        Mark(s, kt, KeyMark.None, Strings.Recovery_NonceFailed);
                        if (session is not null && ex.Status == Status.MfErrAuth)
                        { session.Refused.Add((target, kt)); break; }
                    }
                    if (session is not null && method == MifareRecoveryMethod.Nested)
                    {
                        session.Distances.Clear();
                        if (attempt == attempts - 1) session.NestedUnreliable = true;
                    }
                }
                // A fast attack is only the preferred first path. Preserve reuse
                // as a fallback, with rejected combinations filtered as usual.
                if (!knownFirst && !Known(s, kt)) await TryKnownKeysAsync(s, kt);
                await SpreadKnownKeysAsync();
                // Two inconclusive Nested attempts are enough to reconsider the
                // family, rather than repeat the same failure across the whole card.
                if (method == MifareRecoveryMethod.Nested && session?.NestedUnreliable == true) return;
            }
        // A later sector may reveal a key for an earlier failed attack. Only untested
        // combinations (and inconclusive RF failures) need another authentication.
        for (var s = 0; s < dump.SectorCount; s++)
            foreach (var kt in new[] { MfKeyType.A, MfKeyType.B })
            {
                ct.ThrowIfCancellationRequested();
                if (!Known(s, kt)) await TryKnownKeysAsync(s, kt);
            }
        progress?.Report(new KeyRecoveryProgress(Strings.Recovery_Finished));
    }

    // ---- attack helpers ---------------------------------------------------

    private async Task StaticEncryptedNestedAsync(MifareDump dump, Action<int, MfKeyType, byte[]> store,
        IProgress<KeyRecoveryProgress>? progress, CancellationToken ct)
    {
        var capabilities = await client.GetCapabilitiesAsync(ct);
        if (!capabilities.Contains((ushort)Command.Mf1EncNestedAcquire) ||
            !capabilities.Contains((ushort)Command.Mf1CheckKeysOnBlock))
            throw new NotSupportedException(Strings.Recovery_NoStaticEncFirmware);

        if (dump.KeyA.Concat(dump.KeyB).All(k => k is not null)) return;

        var session = MifareRecoverySession.For(dump, client);
        async Task Spread()
        {
            await ReadableKeysBAsync(dump, store, session, ct);
            await SpreadKeysAsync(dump, store, progress, session.Rejected, ct, session);
            await ReadableKeysBAsync(dump, store, session, ct);
        }
        await RecoverSenAsync(dump, [], session, store, progress, Spread, capabilities, ct, manual: true);
        progress?.Report(new KeyRecoveryProgress(Strings.Recovery_Finished));
    }

    private static ulong KeyValue(byte[] key)
    {
        ulong value = 0;
        foreach (var b in key) value = (value << 8) | b;
        return value;
    }

    private async Task<IReadOnlyList<ulong>> HardnestedSectorAsync(
        byte[] uid, uint authenticationUid, (int Block, MfKeyType Type, byte[] Key) seed,
        int target, MfKeyType targetType, IProgress<KeyRecoveryProgress>? progress, CancellationToken ct)
    {
        var sector = MifareClassic.SectorOfBlock(target);
        var updates = new RecoveryProgress(progress, sector, targetType);
        var source = new ChameleonHardnestedSource(client, uid, seed.Block, seed.Type, seed.Key, target, targetType);
        var capture = await HardnestedCollector.CollectAsync(source, authenticationUid, updates, ct);
        updates.Report(Strings.Recovery_CalcHardnested);
        var candidate = await _hardnested.RecoverAsync(capture, updates, ct);
        var tags = await client.Hf14aScanAsync(ct);
        if (tags.Count != 1 || !uid.AsSpan().SequenceEqual(tags[0].Uid))
            throw new InvalidOperationException(Strings.Recovery_CardRemoved);
        return candidate is { } key ? new[] { key } : Array.Empty<ulong>();
    }

    private sealed class RecoveryProgress(IProgress<KeyRecoveryProgress>? progress, int sector, MfKeyType type) : IProgress<string>
    {
        public void Report(string value) => progress?.Report(new KeyRecoveryProgress(value, sector, type, KeyMark.Checking));
    }

    private async Task TryDarksideAsync(
        MifareDump dump, uint authUid, Action<int, MfKeyType, byte[]> store,
        IProgress<KeyRecoveryProgress>? progress, CancellationToken ct, MifareRecoverySession? session = null, bool seedOnly = false)
    {
        for (var sector = 0; sector < dump.SectorCount; sector++)
            foreach (var kt in new[] { MfKeyType.B, MfKeyType.A })
            {
                if ((kt == MfKeyType.A ? dump.KeyA : dump.KeyB)[sector] is not null) continue;
                var target = MifareClassic.SectorTrailerBlock(sector);
                progress?.Report(new KeyRecoveryProgress(Strings.Recovery_CheckingDarkside, sector, kt, KeyMark.Checking));
                var vulnerability = session?.Darkside.GetValueOrDefault((target, kt));
                if (session is null || !session.Darkside.ContainsKey((target, kt)))
                {
                    vulnerability = await client.CheckDarksideAsync(ct, target, kt);
                    if (session is not null) session.Darkside[(target, kt)] = vulnerability.Value;
                }
                if (vulnerability != DarksideResult.Vulnerable)
                {
                    progress?.Report(new KeyRecoveryProgress(Strings.Recovery_NotDarksideVulnerable, sector, kt));
                    if (seedOnly) return;
                    continue;
                }
                progress?.Report(new KeyRecoveryProgress(Strings.Recovery_RunningDarkside, sector, kt, KeyMark.Checking));
                var items = new List<DarksideItem>();
                for (var tries = 0; tries < 5; tries++)
                {
                    ct.ThrowIfCancellationRequested();
                    var detail = string.Format(Strings.Recovery_AttemptOf5, tries + 1);
                    progress?.Report(new KeyRecoveryProgress(Strings.Recovery_CollectingDarkside, sector, kt, KeyMark.Checking, Detail: detail));
                    var (uid, item) = await client.GetDarksideAsync(target, kt, tries == 0, 15, ct);
                    items.Add(item);
                    if (uid != 0) authUid = uid;
                    progress?.Report(new KeyRecoveryProgress(Strings.Recovery_CalcDarkside, sector, kt, KeyMark.Checking, Detail: detail));
                    var candidates = await _keyRecovery.RecoverAsync(new(KeyRecoveryOperation.Darkside)
                        { Uid = authUid, Items = items.ToArray() }, ct);
                    var hit = await FirstWorkingKeyAsync(target, kt, candidates, progress, ct, detail);
                    if (hit is { } key) { store(sector, kt, key); return; }
                }
                progress?.Report(new KeyRecoveryProgress(Strings.Recovery_KeyNotRecovered, sector, kt));
                if (seedOnly) return;
            }
    }

    private async Task<IReadOnlyList<ulong>> NestedSectorAsync(
        (int Block, MfKeyType Type, byte[] Key) seed, int target, MfKeyType targetKeyType,
        IProgress<KeyRecoveryProgress>? progress, CancellationToken ct, MifareRecoverySession? session = null)
    {
        var updates = new RecoveryProgress(progress, MifareClassic.SectorOfBlock(target), targetKeyType);
        updates.Report(Strings.Recovery_MeasuringDistance);
        var seedId = (seed.Block, seed.Type, KeyValue(seed.Key));
        NtDistance dist;
        if (session is not null && session.Distances.TryGetValue(seedId, out var cached)) dist = cached;
        else
        {
            dist = await client.GetNtDistanceAsync(seed.Block, seed.Type, seed.Key, ct);
            if (session is not null) session.Distances[seedId] = dist;
        }
        updates.Report(Strings.Recovery_CollectingNestedNonces);
        var nonces = await client.GetNestedNoncesAsync(seed.Block, seed.Type, seed.Key, target, targetKeyType, isStatic: false, ct);
        if (nonces.Count < 2) return Array.Empty<ulong>();
        updates.Report(Strings.Recovery_CalcNested);
        return await _keyRecovery.RecoverAsync(new(KeyRecoveryOperation.Nested)
            { Uid = dist.Uid, Distance = dist.Distance, Nonces = [nonces[0], nonces[1]] }, ct);
    }

    private async Task<IReadOnlyList<ulong>> StaticNestedSectorAsync(
        (int Block, MfKeyType Type, byte[] Key) seed, int target, MfKeyType targetKeyType,
        IProgress<KeyRecoveryProgress>? progress, CancellationToken ct)
    {
        const int attempts = 3;
        var sector = MifareClassic.SectorOfBlock(target);
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var detail = string.Format(Strings.Recovery_Attempt, attempt + 1, attempts);
            progress?.Report(new KeyRecoveryProgress(
                attempt == 0 ? Strings.Recovery_CollectingStaticNestedNonces : Strings.Recovery_CollectingAgain,
                sector, targetKeyType, KeyMark.Checking, Detail: detail));
            var capture = await client.GetStaticNestedNoncesAsync(seed.Block, seed.Type, seed.Key, target, targetKeyType, ct);
            var nonces = capture.Nonces;
            progress?.Report(new KeyRecoveryProgress(Strings.Recovery_CalcStaticNested, sector, targetKeyType, KeyMark.Checking, Detail: detail));
            var candidates = await _keyRecovery.RecoverAsync(new(KeyRecoveryOperation.StaticNested)
                { Uid = capture.Uid, KeyType = seed.Type, Nonces = [nonces[0], nonces[1]] }, ct);
            if (candidates.Count > 0) return candidates;
        }
        return Array.Empty<ulong>();
    }

    private async Task<byte[]?> FirstWorkingKeyAsync(int block, MfKeyType keyType, IReadOnlyList<ulong> candidates,
        IProgress<KeyRecoveryProgress>? progress, CancellationToken ct, string? detail = null,
        MifareKeyRejections? rejected = null, MifareRecoverySession? session = null)
    {
        for (var i = 0; i < candidates.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var counter = string.Format(Strings.Recovery_CandidateN, i + 1, candidates.Count);
            progress?.Report(new KeyRecoveryProgress(Strings.Recovery_CheckingCandidates,
                MifareClassic.SectorOfBlock(block), keyType, KeyMark.Checking,
                Detail: detail is null ? counter : $"{detail} · {counter}"));
            var key = KeyBytes(candidates[i]);
            if (await TryAuthAsync(block, keyType, key, ct, rejected, session)) return key;
        }
        return null;
    }

    private async Task<bool> TryAuthAsync(int block, MfKeyType keyType, byte[] key, CancellationToken ct,
        MifareKeyRejections? rejected = null, MifareRecoverySession? session = null)
    {
        var combination = (block, keyType, KeyValue(key));
        if (rejected?.Contains(combination) == true) return false;
        var started = _timeProvider.GetTimestamp();
        for (var attempt = 0; attempt < 4; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var status = await client.Mf1AuthStatusAsync(block, keyType, key, ct, drain: session is not null);
            if (status == Status.HfTagOk)
            { session?.RecordAuthentication(_timeProvider.GetElapsedTime(started)); return true; }
            if (status == Status.MfErrAuth)
            {
                rejected?.Add(combination);
                session?.RecordAuthentication(_timeProvider.GetElapsedTime(started));
                return false;
            }
            if (status is not (Status.HfTagNo or Status.HfErrStat or Status.HfErrCrc
                or Status.HfCollision or Status.HfErrBcc or Status.HfErrParity or Status.HfErrAts))
                throw new ChameleonException(Strings.Recovery_AuthFailed, status);
            if (session is not null && status == Status.HfTagNo && attempt == 3)
                throw new ChameleonException(Strings.Recovery_CardRemoved, status);
        }
        return false;
    }

    // ---- small helpers ----------------------------------------------------

    private static (int Block, MfKeyType Type, byte[] Key)? FirstKnownKey(MifareDump r)
    {
        for (var s = 0; s < r.KeyA.Length; s++)
        {
            if (r.KeyA[s] is { } a) return (MifareClassic.SectorTrailerBlock(s), MfKeyType.A, a);
            if (r.KeyB[s] is { } b) return (MifareClassic.SectorTrailerBlock(s), MfKeyType.B, b);
        }
        return null;
    }

    private static uint ReadUid(byte[] uid)
    {
        // rc522.c get_4byte_tag_uid selects the last cascade level for 7/10-byte UIDs.
        if (uid.Length is not (4 or 7 or 10))
            throw new ArgumentException("Expected a 4-, 7- or 10-byte card UID.", nameof(uid));
        return BinaryPrimitives.ReadUInt32BigEndian(uid.AsSpan(uid.Length - 4));
    }

    private static byte[] KeyBytes(ulong key)
    {
        var b = new byte[6];
        for (var i = 0; i < 6; i++) b[5 - i] = (byte)(key >> (8 * i));
        return b;
    }
}
