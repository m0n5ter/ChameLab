using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Models;
using ChameLab.Protocol;

namespace ChameLab.Services;

/// <summary>Independently selectable MIFARE Classic recovery methods.</summary>
public enum MifareRecoveryMethod { Darkside, Nested, StaticNested, Hardnested }

/// <summary>State of one sector key during recovery, surfaced to the per-sector grid.</summary>
public enum KeyMark { None, Checking, Found }

/// <summary>A live recovery update: a status line and, when it concerns one key, which sector/key changed.</summary>
public sealed record KeyRecoveryProgress(
    string Status, int Sector = -1, MfKeyType KeyType = MfKeyType.A, KeyMark Mark = KeyMark.None, byte[]? Key = null, string? Detail = null);

/// <summary>Runs one selected recovery method, preserving keys already found in the shared dump.</summary>
public sealed class MifareKeyRecovery
{
    private readonly ChameleonClient _client;
    private readonly IHardnestedEngine _hardnested;

    public MifareKeyRecovery(ChameleonClient client, IHardnestedEngine? hardnested = null)
    {
        _client = client;
        _hardnested = hardnested ?? new UnavailableHardnestedEngine();
    }

    public async Task RecoverAsync(
        MifareDump dump, MifareRecoveryMethod method,
        IProgress<KeyRecoveryProgress>? progress = null, CancellationToken ct = default)
    {
        if (method == MifareRecoveryMethod.Hardnested && _hardnested.UnavailableReason is { } unavailable)
            throw new NotSupportedException(unavailable);
        await _client.SetDeviceModeAsync(DeviceMode.Reader, ct);
        var tags = await _client.Hf14aScanAsync(ct);
        if (tags.Count == 0)
            throw new ChameleonException("No HF tag detected on the reader.", Status.HfTagNo);
        if (!dump.Uid.AsSpan().SequenceEqual(tags[0].Uid))
            throw new InvalidOperationException("The card changed. Scan it again before continuing.");
        var authUid = ReadUid(tags[0].Uid);

        bool Known(int s, MfKeyType kt) => (kt == MfKeyType.A ? dump.KeyA : dump.KeyB)[s] is not null;
        void Mark(int s, MfKeyType kt, KeyMark mark, string status) =>
            progress?.Report(new KeyRecoveryProgress(status, s, kt, mark));
        void Store(int s, MfKeyType kt, byte[] key)
        {
            (kt == MfKeyType.A ? dump.KeyA : dump.KeyB)[s] = key;
            progress?.Report(new KeyRecoveryProgress("Found a key.", s, kt, KeyMark.Found, key));
        }

        if (method == MifareRecoveryMethod.Darkside)
        {
            await TryDarksideAsync(dump, authUid, Store, progress, ct);
            return;
        }

        var prng = await _client.DetectNtLevelAsync(ct);
        var isStatic = method == MifareRecoveryMethod.StaticNested;
        var isHardnested = method == MifareRecoveryMethod.Hardnested;
        if (prng != (isHardnested ? MifareClassicPrngType.Hard :
                isStatic ? MifareClassicPrngType.Static : MifareClassicPrngType.Weak))
            throw new InvalidOperationException("This recovery method is not supported by the card's PRNG.");
        if (FirstKnownKey(dump) is null)
            throw new InvalidOperationException("Recover at least one key before starting nested recovery.");
        if (isHardnested && !(await _client.GetCapabilitiesAsync(ct)).Contains((ushort)Command.Mf1HardnestedAcquire))
            throw new NotSupportedException("This device firmware does not support hardnested nonce collection.");

        for (var s = 0; s < dump.SectorCount; s++)
            foreach (var kt in new[] { MfKeyType.A, MfKeyType.B })
            {
                ct.ThrowIfCancellationRequested();
                if (Known(s, kt)) continue;
                var seed = FirstKnownKey(dump)!.Value;
                var target = MifareClassic.SectorTrailerBlock(s);
                Mark(s, kt, KeyMark.Checking,
                    isHardnested ? "Collecting nonces (hardnested)…" :
                    isStatic ? "Collecting nonces (static nested)…" : "Collecting nonces (nested)…");
                try
                {
                    var candidates = isHardnested
                        ? await HardnestedSectorAsync(dump.Uid, authUid, seed, target, kt, progress, ct)
                        : isStatic
                        ? await StaticNestedSectorAsync(seed, target, kt, progress, ct)
                        : await NestedSectorAsync(seed, target, kt, progress, ct);
                    Mark(s, kt, KeyMark.Checking, $"Checking {candidates.Count} recovered key candidates…");
                    var hit = await FirstWorkingKeyAsync(target, kt, candidates, progress, ct);
                    if (hit is { } key) Store(s, kt, key);
                    else Mark(s, kt, KeyMark.None, "Key not recovered for this sector.");
                }
                catch (ChameleonException)
                {
                    Mark(s, kt, KeyMark.None, "Nonce collection failed for this sector.");
                }
            }
        progress?.Report(new KeyRecoveryProgress("Recovery finished."));
    }

    // ---- attack helpers ---------------------------------------------------

    private async Task<IReadOnlyList<ulong>> HardnestedSectorAsync(
        byte[] uid, uint authenticationUid, (int Block, MfKeyType Type, byte[] Key) seed,
        int target, MfKeyType targetType, IProgress<KeyRecoveryProgress>? progress, CancellationToken ct)
    {
        var sector = MifareClassic.SectorOfBlock(target);
        var updates = new RecoveryProgress(progress, sector, targetType);
        var source = new ChameleonHardnestedSource(_client, uid, seed.Block, seed.Type, seed.Key, target, targetType);
        var capture = await HardnestedCollector.CollectAsync(source, authenticationUid, updates, ct);
        updates.Report("Calculating hardnested key candidates…");
        var candidate = await _hardnested.RecoverAsync(capture, updates, ct);
        var tags = await _client.Hf14aScanAsync(ct);
        if (tags.Count != 1 || !uid.AsSpan().SequenceEqual(tags[0].Uid))
            throw new InvalidOperationException("The card was removed or changed. Scan it again before continuing.");
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
                progress?.Report(new KeyRecoveryProgress("Checking darkside vulnerability…", sector, kt, KeyMark.Checking));
                if (await _client.CheckDarksideAsync(ct, target, kt) != DarksideResult.Vulnerable)
                {
                    progress?.Report(new KeyRecoveryProgress("Key is not vulnerable to darkside.", sector, kt));
                    continue;
                }
                progress?.Report(new KeyRecoveryProgress("Running darkside…", sector, kt, KeyMark.Checking));
                var items = new List<DarksideItem>();
                for (var tries = 0; tries < 5; tries++)
                {
                    ct.ThrowIfCancellationRequested();
                    var detail = $"Attempt {tries + 1}/5";
                    progress?.Report(new KeyRecoveryProgress("Collecting darkside nonces…", sector, kt, KeyMark.Checking, Detail: detail));
                    var (uid, item) = await _client.GetDarksideAsync(target, kt, tries == 0, 15, ct);
                    items.Add(item);
                    if (uid != 0) authUid = uid;
                    progress?.Report(new KeyRecoveryProgress("Calculating darkside candidates…", sector, kt, KeyMark.Checking, Detail: detail));
                    var hit = await FirstWorkingKeyAsync(target, kt, DarksideRecovery.Recover(authUid, items), progress, ct, detail);
                    if (hit is { } key) { store(sector, kt, key); return; }
                }
                progress?.Report(new KeyRecoveryProgress("Key not recovered for this sector.", sector, kt));
            }
    }

    private async Task<IReadOnlyList<ulong>> NestedSectorAsync(
        (int Block, MfKeyType Type, byte[] Key) seed, int target, MfKeyType targetKeyType,
        IProgress<KeyRecoveryProgress>? progress, CancellationToken ct)
    {
        var updates = new RecoveryProgress(progress, MifareClassic.SectorOfBlock(target), targetKeyType);
        updates.Report("Measuring nested nonce distance…");
        var dist = await _client.GetNtDistanceAsync(seed.Block, seed.Type, seed.Key, ct);
        updates.Report("Collecting nested nonces…");
        var nonces = await _client.GetNestedNoncesAsync(seed.Block, seed.Type, seed.Key, target, targetKeyType, isStatic: false, ct);
        if (nonces.Count < 2) return Array.Empty<ulong>();
        updates.Report("Calculating nested candidates…");
        return NestedRecovery.Recover(
            dist.Uid, dist.Distance,
            nonces[0].Nt, nonces[0].NtEnc, nonces[0].Parity,
            nonces[1].Nt, nonces[1].NtEnc, nonces[1].Parity);
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
            var detail = $"Attempt {attempt + 1}/{attempts}";
            progress?.Report(new KeyRecoveryProgress(
                attempt == 0 ? "Collecting static nested nonces…" : "No consistent candidates; collecting again…",
                sector, targetKeyType, KeyMark.Checking, Detail: detail));
            var capture = await _client.GetStaticNestedNoncesAsync(seed.Block, seed.Type, seed.Key, target, targetKeyType, ct);
            var nonces = capture.Nonces;
            progress?.Report(new KeyRecoveryProgress("Calculating static nested candidates…", sector, targetKeyType, KeyMark.Checking, Detail: detail));
            var candidates = await Task.Run(() => StaticNestedRecovery.Recover(
                capture.Uid, seed.Type,
                nonces[0].Nt, nonces[0].NtEnc,
                nonces[1].Nt, nonces[1].NtEnc, ct), ct);
            if (candidates.Count > 0) return candidates;
        }
        return Array.Empty<ulong>();
    }

    private async Task<byte[]?> FirstWorkingKeyAsync(int block, MfKeyType keyType, IReadOnlyList<ulong> candidates,
        IProgress<KeyRecoveryProgress>? progress, CancellationToken ct, string? detail = null)
    {
        for (var i = 0; i < candidates.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var counter = $"Candidate {i + 1}/{candidates.Count}";
            progress?.Report(new KeyRecoveryProgress("Checking recovered key candidates…",
                MifareClassic.SectorOfBlock(block), keyType, KeyMark.Checking,
                Detail: detail is null ? counter : $"{detail} · {counter}"));
            var key = KeyBytes(candidates[i]);
            if (await TryAuthAsync(block, keyType, key, ct)) return key;
        }
        return null;
    }

    private async Task<bool> TryAuthAsync(int block, MfKeyType keyType, byte[] key, CancellationToken ct)
    {
        try { return await _client.Mf1AuthAsync(block, keyType, key, ct); }
        catch (ChameleonException) { return false; }
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
