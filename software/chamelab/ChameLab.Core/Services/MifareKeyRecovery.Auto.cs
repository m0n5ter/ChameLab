using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Localization;
using ChameLab.Models;
using ChameLab.Protocol;

namespace ChameLab.Services;

public sealed record MifareAutoRecoveryResult(int MissingKeys, string? Message)
{
    public bool Complete => MissingKeys == 0;
}

public sealed partial class MifareKeyRecovery
{
    /// <summary>Spread recovered keys first, then choose an applicable recovery family.
    /// Only the supplied dictionary is searched. Every confirmed key is retained immediately.</summary>
    public async Task<MifareAutoRecoveryResult> RecoverAutomaticallyAsync(
        MifareDump dump, IReadOnlyList<byte[]> dictionary,
        IProgress<KeyRecoveryProgress>? progress = null, CancellationToken ct = default,
        MifareKeySearchOptions? options = null, bool useKnownUidKeys = true)
    {
        var session = MifareRecoverySession.For(dump, client);
        var search = (options ?? new()) with { StopAfterFirstKey = false, Session = session };
        var keys = dictionary.Where(k => k.Length == 6).DistinctBy(Convert.ToHexString).ToArray();
        var rejected = session.Rejected;
        bool HasSeed() => FirstKnownKey(dump) is not null;
        int Missing() => dump.KeyA.Concat(dump.KeyB).Count(k => k is null);
        void Store(int sector, MfKeyType type, byte[] key)
        {
            (type == MfKeyType.A ? dump.KeyA : dump.KeyB)[sector] = key;
            knownKeys?.RememberPosition(dump.Uid, sector, type, key, MifareClassic.ToSlotTagType(dump.Type));
            progress?.Report(new(Strings.Recovery_FoundKey, sector, type, KeyMark.Found, key));
        }
        var dictionaryProgress = new AutoDictionaryProgress(p =>
        {
            if (p.Phase == MifareKeyPhase.Found && p.Key is { } key)
                knownKeys?.RememberPosition(dump.Uid, p.Sector, p.KeyType, key, MifareClassic.ToSlotTagType(dump.Type));
            progress?.Report(new(p.Phase == MifareKeyPhase.Found ? Strings.Common_DictKeyFound
                    : Strings.Common_CheckingDictKeys, p.Sector, p.KeyType,
                p.Phase == MifareKeyPhase.Found ? KeyMark.Found
                    : p.Phase == MifareKeyPhase.Searching ? KeyMark.Checking : KeyMark.None, p.Key,
                string.Format(Strings.Common_KeysTriedOf, p.Tried, p.Total))
                { Tried = p.Tried, Total = p.Total });
        });
        Task Search(IReadOnlyList<byte[]> candidates, bool seedOnly) =>
            candidates.Count == 0 ? Task.CompletedTask : client.FindMifareKeysAsync(dump, candidates, dictionaryProgress, ct,
                search with { StopAfterFirstKey = seedOnly });
        async Task Spread()
        {
            await ReadableKeysBAsync(dump, Store, session, ct);
            await SpreadKeysAsync(dump, Store, progress, rejected, ct, session);
            await ReadableKeysBAsync(dump, Store, session, ct);
        }
        void Choosing(string method) => progress?.Report(new(Strings.AutoRecovery_Choosing,
            Detail: string.Format(Strings.AutoRecovery_NextMethod, method)));
        MifareAutoRecoveryResult Finish(string? reason = null)
        {
            ct.ThrowIfCancellationRequested();
            var missing = Missing();
            var message = missing == 0 ? null : string.Format(Strings.AutoRecovery_Incomplete, missing)
                + " " + (reason ?? Strings.AutoRecovery_TryReader);
            progress?.Report(new(missing == 0 ? Strings.Recovery_Finished : message!));
            return new(missing, message);
        }

        await client.SetDeviceModeAsync(DeviceMode.Reader, ct);
        var tags = await client.Hf14aScanAsync(ct);
        if (tags.Count != 1 || !dump.Uid.AsSpan().SequenceEqual(tags[0].Uid))
            throw new InvalidOperationException(Strings.Recovery_CardChanged);
        if (Missing() == 0) return Finish();
        if (useKnownUidKeys) await KnownUidKeysAsync(dump, session, Store, progress, ct);
        await Spread();
        if (Missing() == 0) return Finish();
        var capabilities = await client.GetCapabilitiesAsync(ct);
        // Decide whether a confirmed seed opens another recovery family. SEN
        // gets the first chance without a seed, then yields after key reuse.
        async Task<MifareClassicPrngType> Prng()
        {
            if (session.Prng is not { } cached)
            {
                progress?.Report(new(Strings.Common_CheckingRecoveryMethods));
                cached = await client.DetectNtLevelAsync(ct);
                session.Prng = cached;
                progress?.Report(new(Strings.Common_CheckingRecoveryMethods) { Prng = cached });
            }
            return cached;
        }
        MifareRecoveryMethod? Method(MifareClassicPrngType type) => type switch
        {
            MifareClassicPrngType.Weak => MifareRecoveryMethod.Nested,
            MifareClassicPrngType.Static => MifareRecoveryMethod.StaticNested,
            MifareClassicPrngType.Hard => MifareRecoveryMethod.Hardnested,
            _ => null,
        };
        bool SupportsLegacy(Command command) => capabilities.Count == 0 || capabilities.Contains((ushort)command);
        bool Supports(MifareRecoveryMethod? method) => method switch
        {
            MifareRecoveryMethod.Nested => SupportsLegacy(Command.Mf1NestedAcquire) && SupportsLegacy(Command.Mf1DetectNtDist),
            MifareRecoveryMethod.StaticNested => SupportsLegacy(Command.Mf1StaticNestedAcquire),
            MifareRecoveryMethod.Hardnested => _hardnested.UnavailableReason is null &&
                capabilities.Contains((ushort)Command.Mf1HardnestedAcquire),
            _ => false,
        };
        bool CanUseSeededFamily(MifareClassicPrngType type) => Supports(Method(type)) &&
            (session.SenCapture is null || type == MifareClassicPrngType.Static);
        async Task<bool> ReplanAfterSeed()
        {
            if (Missing() == 0 || !HasSeed()) return false;
            var type = await Prng();
            // Stable encrypted nonce evidence takes precedence over a changing
            // first-auth PRNG. Nested/HN need varying nested nonces on the target.
            return CanUseSeededFamily(type);
        }

        // SEN backdoors and local dictionary evidence precede dictionary guessing.
        // A newly confirmed key is spread before returning to the family selector.
        var hadSeedBeforeSen = HasSeed();
        if (capabilities.Contains((ushort)Command.Mf1CheckKeysOnBlock))
        {
            Choosing("Static encrypted nested");
            await RecoverSenAsync(dump, keys, session, Store, progress, Spread, capabilities, ct,
                replanAfterSeed: ReplanAfterSeed);
            if (Missing() == 0) return Finish();
        }
        var prng = await Prng();
        var seededMethod = Method(prng);
        var canAttack = CanUseSeededFamily(prng);

        if (!HasSeed() && prng == MifareClassicPrngType.Weak &&
            capabilities.Contains((ushort)Command.Mf1DarksideAcquire))
        {
            Choosing("Darkside");
            // Darkside is a seed attempt, not a slow vulnerability scan of every
            // sector. If this target does not work, continue to the selected keys.
            try { await TryDarksideAsync(dump, ReadUid(dump.Uid), Store, progress, ct, session, seedOnly: true); }
            catch (ChameleonException ex) when (ex.Status != Status.HfTagNo)
            {
                progress?.Report(new(Strings.Recovery_NonceFailed));
            }
            await Spread();
            if (Missing() == 0) return Finish();
        }

        // A seed discovered by any preceding stage changes the plan immediately.
        // If no applicable seeded attack exists, exhaust the selected dictionary normally.
        if (!HasSeed() || !canAttack)
        {
            Choosing(Strings.Common_TryKeys);
            await Search(keys, seedOnly: canAttack);
            await Spread();
            if (Missing() == 0) return Finish();
        }
        if (HasSeed() && canAttack)
        {
            Choosing(seededMethod switch
            {
                MifareRecoveryMethod.StaticNested => "Static nested",
                MifareRecoveryMethod.Hardnested => "Hardnested",
                _ => "Nested",
            });
            await RecoverCoreAsync(dump, seededMethod!.Value, progress, ct, spreadKeys: true, rejected, session);
            if (Missing() == 0) return Finish();
            if (seededMethod == MifareRecoveryMethod.Nested && session.NestedUnreliable &&
                _hardnested.UnavailableReason is null && capabilities.Contains((ushort)Command.Mf1HardnestedAcquire))
            {
                Choosing("Hardnested");
                await RecoverCoreAsync(dump, MifareRecoveryMethod.Hardnested, progress, ct, spreadKeys: true, rejected, session);
                if (Missing() == 0) return Finish();
            }
            await Search(keys, seedOnly: false);
            await Spread();
            if (Missing() == 0) return Finish();
        }
        // Preserve SEN evidence/candidates when yielding. If the preferred seeded
        // method did not finish, resume SEN rather than repeating its preparation.
        // Without a new seed or a seeded attempt, the first SEN pass has already
        // exhausted the available path; do not repeat failed capture calibration.
        if (HasSeed() && (canAttack || !hadSeedBeforeSen) &&
            capabilities.Contains((ushort)Command.Mf1CheckKeysOnBlock))
        {
            Choosing("Static encrypted nested");
            await RecoverSenAsync(dump, keys, session, Store, progress, Spread, capabilities, ct);
            if (Missing() == 0) return Finish();
        }

        return Finish(prng == MifareClassicPrngType.Hard && HasSeed() && !Supports(seededMethod)
            ? _hardnested.UnavailableReason ?? Strings.Recovery_NoHardnestedFirmware : null);
    }

    private async Task SpreadKeysAsync(MifareDump dump, Action<int, MfKeyType, byte[]> store,
        IProgress<KeyRecoveryProgress>? progress, MifareKeyRejections rejected,
        CancellationToken ct, MifareRecoverySession? session = null)
    {
        bool Known(int sector, MfKeyType type) =>
            (type == MfKeyType.A ? dump.KeyA : dump.KeyB)[sector] is not null;
        var checkedThisPass = new HashSet<ulong>();
        var batch = await client.SupportsBatchKeyCheckAsync(ct);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            // A successful A authentication can disclose a new trailer B; spread that too.
            var key = dump.KeyA.Concat(dump.KeyB).OfType<byte[]>()
                .FirstOrDefault(k => !checkedThisPass.Contains(KeyValue(k)));
            if (key is null) return;
            var value = KeyValue(key);
            checkedThisPass.Add(value);
            var pending = Enumerable.Range(0, dump.SectorCount)
                .SelectMany(s => new[] { (Sector: s, Type: MfKeyType.A), (Sector: s, Type: MfKeyType.B) })
                .Where(p => !Known(p.Sector, p.Type) &&
                    !rejected.Contains((MifareClassic.SectorTrailerBlock(p.Sector), p.Type, value))).ToArray();
            if (pending.Length == 0) continue;
            foreach (var p in pending)
                progress?.Report(new(Strings.AutoRecovery_Reusing, p.Sector, p.Type, KeyMark.Checking));
            if (batch)
            {
                try
                {
                    await client.CheckMifareKeysOfSectorsAsync(pending, [key], onFound: (p, found) =>
                    {
                        if (!Known(p.Sector, p.Type)) store(p.Sector, p.Type, found);
                    }, ct: ct, session: session);
                    foreach (var p in pending.Where(p => !Known(p.Sector, p.Type)))
                        rejected.Add((MifareClassic.SectorTrailerBlock(p.Sector), p.Type, value));
                    continue;
                }
                catch (NotSupportedException) { batch = false; }
                catch (ChameleonException ex) when (ex.Status != Status.HfTagNo) { batch = false; }
            }
            foreach (var p in pending)
            {
                if (Known(p.Sector, p.Type)) continue;
                if (await TryAuthAsync(MifareClassic.SectorTrailerBlock(p.Sector), p.Type, key, ct, rejected, session))
                    store(p.Sector, p.Type, key);
            }
        }
    }

    private sealed class AutoDictionaryProgress(Action<MifareKeyProgress> report) : IProgress<MifareKeyProgress>
    {
        public void Report(MifareKeyProgress value) => report(value);
    }
}
