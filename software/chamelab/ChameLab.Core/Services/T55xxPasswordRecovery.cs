using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Localization;
using ChameLab.Models;
using ChameLab.Protocol;

namespace ChameLab.Services;

public sealed record T55xxPasswordResult(byte[]? Key, byte[] NewKey);

/// <summary>
/// Proves write access by writing a different EM410x ID and reading it back. Probes do not
/// change the password. Then restores the original ID (Tools) or writes the requested card
/// (normal write), verifying that final ID as well.
/// </summary>
public static class T55xxPasswordRecovery
{
    private static readonly TagSpecificType[] ReadableTypes =
        [TagSpecificType.Em410x, TagSpecificType.HidProx, TagSpecificType.IoProx,
         TagSpecificType.Viking, TagSpecificType.Pac, TagSpecificType.Jablotron];

    public static Task<T55xxPasswordResult?> RecoverAsync(ChameleonClient client, IEnumerable<byte[]> passwords,
        byte[]? newKey = null, Action<int, int>? progress = null, Action<LfScanResult>? originalRead = null,
        Action<byte[]?>? keyFound = null, CancellationToken ct = default) =>
        RunAsync(client, null, passwords, newKey, progress, originalRead, keyFound, ct);

    public static Task<T55xxPasswordResult?> WriteAsync(ChameleonClient client, TagSpecificType type, byte[] id,
        IEnumerable<byte[]> passwords, byte[]? newKey = null, Action<int, int>? progress = null,
        Action<byte[]?>? keyFound = null, CancellationToken ct = default) =>
        RunAsync(client, new(type, (byte[])id.Clone()), passwords, newKey, progress, null, keyFound, ct);

    private static async Task<T55xxPasswordResult?> RunAsync(ChameleonClient client, LfScanResult? requested,
        IEnumerable<byte[]> passwords, byte[]? newKey, Action<int, int>? progress,
        Action<LfScanResult>? originalRead, Action<byte[]?>? keyFound, CancellationToken ct)
    {
        var target = (byte[])(newKey ?? ChameleonClient.DefaultT55xxKey).Clone();
        var keys = passwords.Select(k => (byte[])k.Clone()).ToArray();
        if (target.Length != 4 || keys.Any(k => k.Length != 4))
            throw new ArgumentException(Strings.Device_T55xxKeyLength);
        keys = keys.DistinctBy(Convert.ToHexString).ToArray();
        if (requested is not null) ValidateCard(requested);
        ct.ThrowIfCancellationRequested();
        progress?.Invoke(0, keys.Length);
        // Drain in-flight replies. Honor Cancel between attempts, never between a probe
        // and its verification/restoration; late replies must not leak into another run.
        var mode = await client.GetDeviceModeAsync();
        Exception? failure = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (mode != DeviceMode.Reader) await client.SetDeviceModeAsync(DeviceMode.Reader);
            LfScanResult? original = null;
            foreach (var type in ReadableTypes)
            {
                ct.ThrowIfCancellationRequested();
                original = await ReadAsync(client, type);
                if (original is not null) break;
            }
            if (original is null && requested is null)
                throw new InvalidOperationException(Strings.Tools_T55xxReadOriginalFailed);
            if (original is not null)
            {
                ValidateCard(original);
                originalRead?.Invoke(original);
            }
            var final = requested ?? original!;
            // Different from both IDs: ignored writes cannot pass when cloning the same ID.
            byte[] probeId = [0x43, 0x4C, 0x41, 0x42, 0x01];
            while (SameId(original, probeId) || SameId(final, probeId)) probeId[4]++;

            // Open writes identify unprotected tags without claiming a dictionary key matched.
            if (await AttemptAsync(null)) return new(null, target);
            if (original is null) throw new InvalidOperationException(Strings.Tools_T55xxReadOriginalFailed);
            for (var i = 0; i < keys.Length; i++)
            {
                var matched = await AttemptAsync(keys[i]);
                progress?.Invoke(i + 1, keys.Length);
                if (matched) return new(keys[i], target);
            }
            ct.ThrowIfCancellationRequested();
            return null;

            async Task<bool> AttemptAsync(byte[]? candidate)
            {
                ct.ThrowIfCancellationRequested();
                var finalStarted = false;
                var restored = false;
                try
                {
                    await WriteProbeAsync(client, probeId, candidate);
                    LfScanResult? read = null;
                    for (var retry = 0; retry < 3; retry++)
                    {
                        read = await ReadAsync(client, TagSpecificType.Em410x);
                        if (SameId(read, probeId)) break;
                        if (original is not null && read is not null && read.Id.AsSpan().SequenceEqual(original.Id)) break;
                    }
                    if (!SameId(read, probeId))
                    {
                        if (original is null) return false;
                        if (await OriginalIntactAsync())
                        {
                            ct.ThrowIfCancellationRequested();
                            return false;
                        }
                        throw new InvalidOperationException(Strings.Tools_T55xxProbeFailed);
                    }
                    keyFound?.Invoke(candidate);
                    if (ct.IsCancellationRequested && original is not null)
                    {
                        await RestoreAsync(candidate ?? target, candidate is null ? [target] : [candidate]);
                        restored = true;
                        ct.ThrowIfCancellationRequested();
                    }
                    finalStarted = true;
                    // Firmware write payloads require at least one old-key slot, even for an open tag.
                    await client.WriteLfToT55xxAsync(final.Type, final.Id, target, candidate is null ? [target] : [candidate]);
                    restored = true;
                    // Without a readable original, finish the requested card instead of leaving
                    // an initially blank tag broadcasting our temporary probe ID.
                    ct.ThrowIfCancellationRequested();
                    return true;
                }
                catch (Exception error)
                {
                    if (!restored && original is not null)
                    {
                        try
                        {
                            if (!finalStarted && await OriginalIntactAsync()) restored = true;
                            else
                            {
                                await RestoreAsync(finalStarted ? target : candidate ?? target,
                                    candidate is null ? [target] : [target, candidate]);
                                restored = true;
                            }
                        }
                        catch (Exception restoreError)
                        {
                            throw new InvalidOperationException(string.Format(Strings.Tools_T55xxRestoreFailed,
                                original.Type.DisplayName(), Convert.ToHexString(original.Id),
                                candidate is null ? "—" : Convert.ToHexString(candidate), Convert.ToHexString(target),
                                restoreError.Message), error);
                        }
                    }
                    throw;
                }

                async Task<bool> OriginalIntactAsync()
                {
                    try
                    {
                        var current = await ReadAsync(client, original!.Type);
                        return current is not null && current.Id.AsSpan().SequenceEqual(original.Id);
                    }
                    catch { return false; } // A failed check must not skip the restoration attempt.
                }

                Task RestoreAsync(byte[] password, byte[][] oldKeys) =>
                    client.WriteLfToT55xxAsync(original!.Type, original.Id, password, oldKeys);
            }
        }
        catch (Exception error)
        {
            failure = error;
            throw;
        }
        finally
        {
            if (mode != DeviceMode.Reader && !client.Connection.IsClosed)
                try { await client.SetDeviceModeAsync(mode); }
                catch when (failure is not null) { /* Preserve the recovery/rollback error. */ }
        }
    }

    private static bool SameId(LfScanResult? card, byte[] id) =>
        card is not null && card.Type.IsEm410x() && card.Id.AsSpan().SequenceEqual(id);

    private static void ValidateCard(LfScanResult card)
    {
        if ((!card.Type.IsEm410x() && !ReadableTypes.Contains(card.Type)) || card.Id.Length != card.Type.LfIdLength())
            throw new NotSupportedException(string.Format(Strings.Device_CannotWriteT55xx, card.Type.DisplayName()));
    }

    private static async Task<LfScanResult?> ReadAsync(ChameleonClient client, TagSpecificType type)
    {
        try
        {
            if (!type.IsEm410x()) return await client.LfScanAsync(type);
            var tag = await client.Em410xScanAsync();
            return new(tag.Type, tag.Id);
        }
        catch (ChameleonException ex) when (ex.Status is Status.LfTagNoFound or Status.InvalidCmd or Status.NotImplemented)
        {
            return null;
        }
    }

    private static async Task WriteProbeAsync(ChameleonClient client, byte[] id, byte[]? password)
    {
        // Same encoding as firmware em410x_raw_data: 9 header bits, 10 even-parity
        // nibbles, 4 column parity bits, and a stop bit. Configuration is written last.
        ulong raw = 0x1FF;
        uint columns = 0;
        foreach (var value in id)
            foreach (var nibble in new uint[] { (uint)(value >> 4), (uint)(value & 15) })
            {
                columns ^= nibble;
                raw = (raw << 5) | (nibble << 1) | (uint)(BitOperations.PopCount(nibble) & 1);
            }
        raw = (raw << 5) | (columns << 1);
        await client.WriteT55xxBlockAsync(1, (uint)(raw >> 32), password);
        await client.WriteT55xxBlockAsync(2, (uint)raw, password);
        // RF/64, Manchester, max block 2; keep the probe's open/password mode.
        await client.WriteT55xxBlockAsync(0, password is null ? 0x00148040u : 0x00148050u, password);
    }
}
