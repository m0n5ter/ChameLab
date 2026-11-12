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
        HashSet<(int Block, MfKeyType Type, ulong Key)> rejected)
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
            knownKeys?.Remember(dump.Uid, [key], MifareClassic.ToSlotTagType(dump.Type));
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
                var confirmed = await TryAuthAsync(block, kt, keys[i].Value, ct, rejected);
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
            await SpreadKeysAsync(dump, Store, progress, rejected, ct);
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

        var prng = await client.DetectNtLevelAsync(ct);
        var isStatic = method == MifareRecoveryMethod.StaticNested;
        var isHardnested = method == MifareRecoveryMethod.Hardnested;
        if (prng != (isHardnested ? MifareClassicPrngType.Hard :
                isStatic ? MifareClassicPrngType.Static : MifareClassicPrngType.Weak))
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
                try
                {
                    var started = timing is null ? 0 : _timeProvider.GetTimestamp();
                    var candidates = isHardnested
                        ? await HardnestedSectorAsync(dump.Uid, authUid, seed, target, kt, progress, ct)
                        : isStatic
                        ? await StaticNestedSectorAsync(seed, target, kt, progress, ct)
                        : await NestedSectorAsync(seed, target, kt, progress, ct);
                    Mark(s, kt, KeyMark.Checking, string.Format(Strings.Recovery_CheckingCandidatesN, candidates.Count));
                    var hit = await FirstWorkingKeyAsync(target, kt, candidates, progress, ct, rejected: rejected);
                    if (hit is { } key)
                    {
                        timing?.RecordRecovery(_timeProvider.GetElapsedTime(started));
                        Store(s, kt, key);
                    }
                    else Mark(s, kt, KeyMark.None, Strings.Recovery_KeyNotRecovered);
                }
                catch (ChameleonException)
                {
                    Mark(s, kt, KeyMark.None, Strings.Recovery_NonceFailed);
                }
                // A fast attack is only the preferred first path. Preserve reuse
                // as a fallback, with rejected combinations filtered as usual.
                if (!knownFirst && !Known(s, kt)) await TryKnownKeysAsync(s, kt);
                await SpreadKnownKeysAsync();
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
        IProgress<KeyRecoveryProgress>? progress, CancellationToken ct, bool automatic = false,
        Func<Task>? afterKeyFound = null)
    {
        var capabilities = await client.GetCapabilitiesAsync(ct);
        if (!capabilities.Contains((ushort)Command.Mf1EncNestedAcquire) ||
            !capabilities.Contains((ushort)Command.Mf1CheckKeysOnBlock))
            throw new NotSupportedException(Strings.Recovery_NoStaticEncFirmware);

        bool Known(int sector, MfKeyType type) =>
            (type == MfKeyType.A ? dump.KeyA : dump.KeyB)[sector] is not null;
        async Task<byte[]?> Check(int sector, MfKeyType type, IReadOnlyList<ulong> candidates, string status)
        {
            for (var i = 0; i < candidates.Count; i += 64)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new KeyRecoveryProgress(status, sector, type, KeyMark.Checking,
                    Detail: string.Format(Strings.Recovery_CandidatesRange, i + 1, Math.Min(i + 64, candidates.Count), candidates.Count)));
                var batch = candidates.Skip(i).Take(64).Select(KeyBytes).ToArray();
                var hit = await client.CheckMifareKeysOnBlockAsync(MifareClassic.SectorTrailerBlock(sector), type, batch, ct);
                if (hit is not null) return hit;
            }
            return null;
        }

        // SEN is also selectable on ordinary backdoored cards. Do not start an expensive
        // cryptanalytic search when a retained key or a factory key already opens them.
        var preferred = dump.KeyA.Concat(dump.KeyB).OfType<byte[]>()
            .Concat(knownKeys?.ForUid(dump.Uid) ?? Array.Empty<byte[]>())
            .Concat(MifareClassic.DefaultKeys).Select(KeyValue).Distinct().ToArray();
        for (var s = 0; !automatic && s < dump.SectorCount; s++)
            foreach (var type in new[] { MfKeyType.B, MfKeyType.A })
            {
                ct.ThrowIfCancellationRequested();
                if (Known(s, type)) continue;
                var hit = await Check(s, type, preferred, Strings.Recovery_CheckingPrevious);
                if (hit is not null) store(s, type, hit);
            }
        if (Enumerable.Range(0, dump.SectorCount).All(s => Known(s, MfKeyType.A) && Known(s, MfKeyType.B)))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new KeyRecoveryProgress(Strings.Recovery_Finished));
            return;
        }

        (uint Uid, IReadOnlyList<(NestedNonce A, NestedNonce B)> Sectors)? capture = null;
        byte[]? acceptedBackdoor = null;
        foreach (var backdoor in new[] { "A396EFA4E24F", "A31667A8CEC1", "518B3354E760" })
        {
            progress?.Report(new KeyRecoveryProgress(Strings.Recovery_CollectingStaticEnc, Detail: string.Format(Strings.Recovery_BackdoorKey, backdoor)));
            try
            {
                var key = Convert.FromHexString(backdoor);
                capture = await client.GetStaticEncryptedNestedNoncesAsync(key, dump.SectorCount, ct);
                acceptedBackdoor = key;
                break;
            }
            catch (ChameleonException ex) when (ex.Status == Status.MfErrAuth) { }
        }
        if (capture is not { } data)
        {
            if (automatic) return;
            throw new InvalidOperationException(Strings.Recovery_NoBackdoorAccepted);
        }
        var tags = await client.Hf14aScanAsync(ct);
        if (tags.Count != 1 || !dump.Uid.AsSpan().SequenceEqual(tags[0].Uid))
            throw new InvalidOperationException(Strings.Recovery_CardRemoved);

        // Accepting a backdoor key does not establish the static-encrypted variant.
        // With changing nonces, the backdoor plaintext and the target ciphertext belong
        // to different authentications: even the real key is absent from the candidates.
        progress?.Report(new KeyRecoveryProgress(Strings.Recovery_CollectingStaticEnc));
        var confirmation = await client.GetStaticEncryptedNestedNoncesAsync(acceptedBackdoor!, dump.SectorCount, ct);
        tags = await client.Hf14aScanAsync(ct);
        if (confirmation.Uid != data.Uid || tags.Count != 1 || !dump.Uid.AsSpan().SequenceEqual(tags[0].Uid))
            throw new InvalidOperationException(Strings.Recovery_CardRemoved);
        for (var s = 0; s < dump.SectorCount; s++)
            if ((!Known(s, MfKeyType.A) && data.Sectors[s].A != confirmation.Sectors[s].A) ||
                (!Known(s, MfKeyType.B) && data.Sectors[s].B != confirmation.Sectors[s].B))
            {
                if (automatic) return;
                throw new InvalidOperationException(Strings.Recovery_StaticEncNotStatic);
            }

        for (var s = 0; s < dump.SectorCount; s++)
        {
            ct.ThrowIfCancellationRequested();
            if (dump.KeyA[s] is not null && dump.KeyB[s] is not null) continue;
            var (a, b) = data.Sectors[s];
            async Task<IReadOnlyList<ulong>> Candidates(NestedNonce nonce, MfKeyType type, byte[]? known)
            {
                if (known is not null) return new[] { KeyValue(known) };
                progress?.Report(new KeyRecoveryProgress(Strings.Recovery_CalcStaticEnc, s, type, KeyMark.Checking));
                return await _keyRecovery.RecoverAsync(new(KeyRecoveryOperation.StaticEncryptedNested)
                    { Uid = data.Uid, Nonces = [nonce] }, ct);
            }
            var aKeys = await Candidates(a, MfKeyType.A, dump.KeyA[s]);
            var bKeys = await Candidates(b, MfKeyType.B, dump.KeyB[s]);
            foreach (var type in new[] { MfKeyType.B, MfKeyType.A })
            {
                if ((type == MfKeyType.A ? dump.KeyA : dump.KeyB)[s] is not null) continue;
                var nonce = type == MfKeyType.A ? a : b;
                var other = type == MfKeyType.A ? b : a;
                var keys = type == MfKeyType.A ? aKeys : bKeys;
                var otherKeys = type == MfKeyType.A ? bKeys : aKeys;
                var filtered = await _keyRecovery.RecoverAsync(new(KeyRecoveryOperation.FilterStaticEncrypted)
                    { Nonces = [nonce, other], Keys = keys.ToArray(), OtherKeys = otherKeys.ToArray() }, ct);
                var hit = await Check(s, type, filtered, Strings.Recovery_CheckStaticEnc);
                // Other backdoored chips need not share the RF08S A/B seed relationship.
                if (hit is null && filtered.Count != keys.Count)
                    hit = await Check(s, type, keys.Except(filtered).ToArray(), Strings.Recovery_CheckStaticEnc);
                if (hit is not null)
                {
                    store(s, type, hit);
                    if (afterKeyFound is not null) await afterKeyFound();
                    if (type == MfKeyType.B) bKeys = new[] { KeyValue(hit) };
                    else aKeys = new[] { KeyValue(hit) };
                }
                else progress?.Report(new KeyRecoveryProgress(Strings.Recovery_KeyNotRecovered, s, type));
            }
        }
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
        IProgress<KeyRecoveryProgress>? progress, CancellationToken ct)
    {
        for (var sector = 0; sector < dump.SectorCount; sector++)
            foreach (var kt in new[] { MfKeyType.B, MfKeyType.A })
            {
                if ((kt == MfKeyType.A ? dump.KeyA : dump.KeyB)[sector] is not null) continue;
                var target = MifareClassic.SectorTrailerBlock(sector);
                progress?.Report(new KeyRecoveryProgress(Strings.Recovery_CheckingDarkside, sector, kt, KeyMark.Checking));
                if (await client.CheckDarksideAsync(ct, target, kt) != DarksideResult.Vulnerable)
                {
                    progress?.Report(new KeyRecoveryProgress(Strings.Recovery_NotDarksideVulnerable, sector, kt));
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
            }
    }

    private async Task<IReadOnlyList<ulong>> NestedSectorAsync(
        (int Block, MfKeyType Type, byte[] Key) seed, int target, MfKeyType targetKeyType,
        IProgress<KeyRecoveryProgress>? progress, CancellationToken ct)
    {
        var updates = new RecoveryProgress(progress, MifareClassic.SectorOfBlock(target), targetKeyType);
        updates.Report(Strings.Recovery_MeasuringDistance);
        var dist = await client.GetNtDistanceAsync(seed.Block, seed.Type, seed.Key, ct);
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
        HashSet<(int Block, MfKeyType Type, ulong Key)>? rejected = null)
    {
        for (var i = 0; i < candidates.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var counter = string.Format(Strings.Recovery_CandidateN, i + 1, candidates.Count);
            progress?.Report(new KeyRecoveryProgress(Strings.Recovery_CheckingCandidates,
                MifareClassic.SectorOfBlock(block), keyType, KeyMark.Checking,
                Detail: detail is null ? counter : $"{detail} · {counter}"));
            var key = KeyBytes(candidates[i]);
            if (await TryAuthAsync(block, keyType, key, ct, rejected)) return key;
        }
        return null;
    }

    private async Task<bool> TryAuthAsync(int block, MfKeyType keyType, byte[] key, CancellationToken ct,
        HashSet<(int Block, MfKeyType Type, ulong Key)>? rejected = null)
    {
        var combination = (block, keyType, KeyValue(key));
        if (rejected?.Contains(combination) == true) return false;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var status = await client.Mf1AuthStatusAsync(block, keyType, key, ct);
            if (status == Status.HfTagOk) return true;
            if (status == Status.MfErrAuth)
            {
                rejected?.Add(combination);
                return false;
            }
            if (status is not (Status.HfTagNo or Status.HfErrStat or Status.HfErrCrc
                or Status.HfCollision or Status.HfErrBcc or Status.HfErrParity or Status.HfErrAts))
                throw new ChameleonException(Strings.Recovery_AuthFailed, status);
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
