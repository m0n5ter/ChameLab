using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Models;
using ChameLab.Protocol;

namespace ChameLab.Services;

/// <summary>State of one sector key during recovery, surfaced to the per-sector grid.</summary>
public enum KeyMark { None, Checking, Found }

/// <summary>A live recovery update: a status line and, when it concerns one key, which sector/key changed.</summary>
public sealed record KeyRecoveryProgress(
    string Status, int Sector = -1, MfKeyType KeyType = MfKeyType.A, KeyMark Mark = KeyMark.None, byte[]? Key = null);

/// <summary>The keys recovered for a card, ready to feed into a dump.</summary>
public sealed class MifareKeyRecoveryResult
{
    public MifareKeyRecoveryResult(MifareClassicType type, byte[] uid)
    {
        Type = type;
        Uid = uid;
        var sectors = MifareClassic.SectorCount(type);
        KeyA = new byte[]?[sectors];
        KeyB = new byte[]?[sectors];
    }

    public MifareClassicType Type { get; }
    public byte[] Uid { get; }
    public byte[]?[] KeyA { get; }
    public byte[]?[] KeyB { get; }

    /// <summary>True when recovery stopped because the card needs the (unsupported) hardnested attack.</summary>
    public bool HardnestedRequired { get; set; }

    /// <summary>Every known key as a flat dictionary, for the dump pass.</summary>
    public IReadOnlyList<byte[]> AllKeys()
    {
        var keys = new List<byte[]>();
        foreach (var k in KeyA) if (k is not null) keys.Add(k);
        foreach (var k in KeyB) if (k is not null) keys.Add(k);
        return keys;
    }
}

/// <summary>
/// Orchestrates MIFARE Classic key recovery on the reader: dictionary check, then darkside to
/// get a first key, then the nested / static-nested attacks for the rest — mirroring the
/// ChameleonUltraGUI recovery flow, minus hardnested (reported as unsupported). The solving
/// runs in <see cref="DarksideRecovery"/> / <see cref="NestedRecovery"/> / <see cref="StaticNestedRecovery"/>.
/// </summary>
public sealed class MifareKeyRecovery
{
    private readonly ChameleonClient _client;

    public MifareKeyRecovery(ChameleonClient client) => _client = client;

    public async Task<MifareKeyRecoveryResult> RecoverAsync(
        IReadOnlyList<byte[]> dictionary,
        IProgress<KeyRecoveryProgress>? progress = null,
        CancellationToken ct = default)
    {
        await _client.SetDeviceModeAsync(DeviceMode.Reader, ct);
        var tags = await _client.Hf14aScanAsync(ct);
        if (tags.Count == 0)
            throw new ChameleonException("No HF tag detected on the reader.", Status.HfTagNo);

        var tag = tags[0];
        var type = MifareClassic.FromSak(tag.Sak);
        var uidBytes = tag.Uid.Length >= 4 ? tag.Uid[..4] : tag.Uid;
        var authUid = ReadUid(tag.Uid);
        var result = new MifareKeyRecoveryResult(type, uidBytes);
        var sectors = MifareClassic.SectorCount(type);

        // The keys the dictionary pass already proved, used to seed nested acquisition.
        void Mark(int s, MfKeyType kt, KeyMark m, string status) =>
            progress?.Report(new KeyRecoveryProgress(status, s, kt, m));

        bool Known(int s, MfKeyType kt) => (kt == MfKeyType.A ? result.KeyA : result.KeyB)[s] is not null;
        void Store(int s, MfKeyType kt, byte[] key)
        {
            (kt == MfKeyType.A ? result.KeyA : result.KeyB)[s] = key;
            progress?.Report(new KeyRecoveryProgress("Found a key.", s, kt, KeyMark.Found, key));
        }

        // 1) Dictionary check -------------------------------------------------
        progress?.Report(new KeyRecoveryProgress("Checking keys from the dictionary…"));
        foreach (var kt in new[] { MfKeyType.A, MfKeyType.B })
            for (var s = 0; s < sectors; s++)
            {
                ct.ThrowIfCancellationRequested();
                if (Known(s, kt)) continue;
                var trailer = MifareClassic.SectorTrailerBlock(s);
                Mark(s, kt, KeyMark.Checking, "Checking keys from the dictionary…");
                foreach (var key in dictionary)
                {
                    ct.ThrowIfCancellationRequested();
                    if (await TryAuthAsync(trailer, kt, key, ct)) { Store(s, kt, key); break; }
                }
                if (!Known(s, kt)) Mark(s, kt, KeyMark.None, "Checking keys from the dictionary…");
            }

        // 2) Darkside for a first key, if the dictionary found nothing ---------
        var prng = await _client.DetectNtLevelAsync(ct);
        if (!AnyKnown(result) && prng != MifareClassicPrngType.Static)
        {
            progress?.Report(new KeyRecoveryProgress("Running the darkside attack…", 0, MfKeyType.B, KeyMark.Checking));
            await TryDarksideAsync(result, authUid, Store, ct);
        }

        // Nothing to pivot from and not static → the remaining attacks can't start.
        if (!AnyKnown(result) && prng != MifareClassicPrngType.Static)
            return result;

        if (prng == MifareClassicPrngType.Hard)
        {
            // Hardnested is not implemented; report and stop with whatever keys we have.
            result.HardnestedRequired = true;
            progress?.Report(new KeyRecoveryProgress("This card needs the hardnested attack, which is not supported yet."));
            return result;
        }

        // 3) Nested / static nested for the rest ------------------------------
        for (var s = 0; s < sectors; s++)
            foreach (var kt in new[] { MfKeyType.A, MfKeyType.B })
            {
                ct.ThrowIfCancellationRequested();
                if (Known(s, kt)) continue;

                // Both attacks authenticate through an already-known key to reach the target.
                if (FirstKnownKey(result) is not { } seed) continue;

                var target = MifareClassic.SectorTrailerBlock(s);
                Mark(s, kt, KeyMark.Checking,
                    prng == MifareClassicPrngType.Static ? "Collecting nonces (static nested)…" : "Collecting nonces (nested)…");

                try
                {
                    var candidates = prng == MifareClassicPrngType.Static
                        ? await StaticNestedSectorAsync(authUid, seed, target, kt, ct)
                        : await NestedSectorAsync(seed, target, kt, ct);

                    var hit = await FirstWorkingKeyAsync(target, kt, candidates, ct);
                    if (hit is { } key) Store(s, kt, key);
                    else Mark(s, kt, KeyMark.None, "Key not recovered for this sector.");
                }
                catch (ChameleonException)
                {
                    Mark(s, kt, KeyMark.None, "Nonce collection failed for this sector.");
                }
            }

        progress?.Report(new KeyRecoveryProgress("Recovery finished."));
        return result;
    }

    // ---- attack helpers ---------------------------------------------------

    private async Task TryDarksideAsync(
        MifareKeyRecoveryResult result, uint authUid, Action<int, MfKeyType, byte[]> store, CancellationToken ct)
    {
        if (await _client.CheckDarksideAsync(ct) != DarksideResult.Vulnerable) return;

        const int trailer0 = 3; // sector 0 trailer
        var items = new List<DarksideItem>();
        var first = true;
        for (var tries = 0; tries < 5; tries++)
        {
            ct.ThrowIfCancellationRequested();
            var (uid, item) = await _client.GetDarksideAsync(trailer0, MfKeyType.B, first, 15, ct);
            first = false;
            items.Add(item);
            if (uid != 0) authUid = uid;

            var candidates = DarksideRecovery.Recover(authUid, items);
            var hit = await FirstWorkingKeyAsync(trailer0, MfKeyType.B, candidates, ct);
            if (hit is { } key) { store(0, MfKeyType.B, key); return; }
        }
    }

    private async Task<IReadOnlyList<ulong>> NestedSectorAsync(
        (int Block, MfKeyType Type, byte[] Key) seed, int target, MfKeyType targetKeyType, CancellationToken ct)
    {
        var dist = await _client.GetNtDistanceAsync(seed.Block, seed.Type, seed.Key, ct);
        var nonces = await _client.GetNestedNoncesAsync(seed.Block, seed.Type, seed.Key, target, targetKeyType, isStatic: false, ct);
        if (nonces.Count < 2) return Array.Empty<ulong>();
        return NestedRecovery.Recover(
            dist.Uid, dist.Distance,
            nonces[0].Nt, nonces[0].NtEnc, nonces[0].Parity,
            nonces[1].Nt, nonces[1].NtEnc, nonces[1].Parity);
    }

    private async Task<IReadOnlyList<ulong>> StaticNestedSectorAsync(
        uint authUid, (int Block, MfKeyType Type, byte[] Key) seed, int target, MfKeyType targetKeyType, CancellationToken ct)
    {
        var nonces = await _client.GetNestedNoncesAsync(seed.Block, seed.Type, seed.Key, target, targetKeyType, isStatic: true, ct);
        if (nonces.Count < 2) return Array.Empty<ulong>();
        return StaticNestedRecovery.Recover(
            authUid, targetKeyType,
            nonces[0].Nt, nonces[0].NtEnc,
            nonces[1].Nt, nonces[1].NtEnc);
    }

    private async Task<byte[]?> FirstWorkingKeyAsync(int block, MfKeyType keyType, IReadOnlyList<ulong> candidates, CancellationToken ct)
    {
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var key = KeyBytes(candidate);
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

    private static (int Block, MfKeyType Type, byte[] Key)? FirstKnownKey(MifareKeyRecoveryResult r)
    {
        for (var s = 0; s < r.KeyA.Length; s++)
        {
            if (r.KeyA[s] is { } a) return (MifareClassic.SectorTrailerBlock(s), MfKeyType.A, a);
            if (r.KeyB[s] is { } b) return (MifareClassic.SectorTrailerBlock(s), MfKeyType.B, b);
        }
        return null;
    }

    private static bool AnyKnown(MifareKeyRecoveryResult r)
    {
        foreach (var k in r.KeyA) if (k is not null) return true;
        foreach (var k in r.KeyB) if (k is not null) return true;
        return false;
    }

    private static uint ReadUid(byte[] uid)
    {
        Span<byte> buf = stackalloc byte[4];
        var n = Math.Min(uid.Length, 4);
        uid.AsSpan(0, n).CopyTo(buf);
        return BinaryPrimitives.ReadUInt32BigEndian(buf);
    }

    private static byte[] KeyBytes(ulong key)
    {
        var b = new byte[6];
        for (var i = 0; i < 6; i++) b[5 - i] = (byte)(key >> (8 * i));
        return b;
    }
}
