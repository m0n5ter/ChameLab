using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Models;
using ChameLab.Protocol;

namespace ChameLab.Services;

public sealed record Mfkey32Progress(string Status, int? CapturedCount = null, string? KeyFound = null);

/// <summary>Collects reader authentications using the card's existing slot, then reads the physical card.</summary>
public sealed class Mfkey32Wizard(ChameleonClient client, KnownCardKeys knownKeys,
    IKeyRecoveryEngine? keyRecovery = null)
{
    private readonly IKeyRecoveryEngine _keyRecovery = keyRecovery ?? AppServices.KeyRecovery;
    public static SlotInfo? FindSlot(IEnumerable<SlotInfo> slots, CardSave card) =>
        slots.Where(s => ChameleonClient.SlotHoldsCard(s, card))
            .OrderByDescending(s => s.IsActive).ThenBy(s => s.Index).FirstOrDefault();

    public async Task RunAsync(CardSave card, IProgress<Mfkey32Progress> progress,
        Func<IReadOnlyList<byte[]>, Task> readCard, CancellationToken ct) =>
        await client.WithSlotLockAsync(async () =>
        {
            if (!card.Tag.IsMifareClassic() || card.Uid.Length is not (4 or 7 or 10))
                throw new ArgumentException("mfkey32 requires a MIFARE Classic card with a valid UID.", nameof(card));
            progress.Report(new("Preparing emulation…"));
            var slot = FindSlot(await client.GetAllSlotsAsync(ct), card)
                ?? throw new InvalidOperationException("Load this card into a slot before starting mfkey32.");
            var previousMode = await client.GetDeviceModeAsync(ct);
            var previousSlot = await client.GetActiveSlotAsync(ct);
            Mf1EmulatorConfig? config = null;
            try
            {
                await client.SetDeviceModeAsync(DeviceMode.Tag, ct);
                await client.SetActiveSlotAsync(slot.Index, ct);
                var identity = await client.GetHf14aEmulatorIdentityAsync(ct);
                if (!identity.Uid.AsSpan().SequenceEqual(card.Uid))
                    throw new InvalidOperationException("The slot UID differs from this card. Load the card into the slot again.");
                config = await client.GetMf1EmulatorConfigAsync(ct);
                // Keep the emulated dump intact even if the reader tries to write to it.
                await client.SetMf1WriteModeAsync(EmulatorWriteMode.Denied, ct);
                await client.SetMf1DetectionEnableAsync(false, ct);
                await client.SetMf1DetectionEnableAsync(true, ct);
                var keys = await CaptureAsync(card, progress, ct);
                ct.ThrowIfCancellationRequested();
                // Detection logs contain only the last four UID bytes. Save against the full card UID.
                knownKeys.Remember(card.Uid, keys);
                await client.SetMf1DetectionEnableAsync(false, ct);
                await client.SetDeviceModeAsync(DeviceMode.Reader, ct);
                var keyText = string.Join(" · ", keys.Select(Hex.Spaced));
                progress.Report(new("Place original card on Chameleon.", KeyFound: $"Key found: {keyText}"));
                await WaitForCardAsync(card.Uid, ct);
                await readCard(keys);
            }
            finally
            {
                // Cleanup must run after cancellation and after a partially completed setup too.
                // Try every restoration even if one command fails; report the failure to the caller.
                var errors = new List<Exception>();
                async Task Restore(Func<Task> action)
                {
                    try { await action(); } catch (Exception ex) { errors.Add(ex); }
                }
                await Restore(() => client.SetActiveSlotAsync(slot.Index));
                if (config is not null)
                {
                    await Restore(() => client.SetMf1DetectionEnableAsync(config.Detection));
                    await Restore(() => client.SetMf1WriteModeAsync(config.WriteMode));
                }
                await Restore(() => client.SetActiveSlotAsync(previousSlot));
                await Restore(() => client.SetDeviceModeAsync(previousMode));
                if (errors.Count > 0)
                    throw new InvalidOperationException("Could not restore the device state after mfkey32.", new AggregateException(errors));
            }
        }, ct);

    private async Task<IReadOnlyList<byte[]>> CaptureAsync(CardSave card, IProgress<Mfkey32Progress> progress, CancellationToken ct)
    {
        var attempted = new HashSet<string>();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var logs = (await client.GetAllMf1DetectionLogsAsync(ct))
                .Where(l => !l.IsNested && l.Block < card.Tag.MifareClassicBlocks()
                    && l.Uid.AsSpan().SequenceEqual(card.Uid.AsSpan(card.Uid.Length - 4))).ToList();
            progress.Report(new("Hold Chameleon to the reader.", logs.Count));
            if (logs.Count >= 2) progress.Report(new("Recovering keys…", logs.Count));
            var keys = await RecoverAsync(logs, attempted, ct);
            if (keys.Count > 0) return keys;
            await Task.Delay(1200, ct);
        }
    }

    private async Task WaitForCardAsync(byte[] uid, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var tags = await client.Hf14aScanAsync(ct);
                if (tags.Count > 0 && tags[0].Uid.AsSpan().SequenceEqual(uid)) return;
            }
            catch (ChameleonException ex) when (ex.Status == Status.HfTagNo) { }
            await Task.Delay(1000, ct);
        }
    }

    private async Task<IReadOnlyList<byte[]>> RecoverAsync(List<DetectionLog> logs, HashSet<string> attempted, CancellationToken ct)
    {
        var keys = new Dictionary<string, byte[]>();
        foreach (var group in logs.GroupBy(l => (l.Block, l.KeyType)))
        {
            var distinct = group.DistinctBy(l => l.NtHex + l.NrHex + l.ArHex).ToList();
            for (var i = 0; i < distinct.Count; i++)
                for (var j = i + 1; j < distinct.Count; j++)
                {
                    ct.ThrowIfCancellationRequested();
                    var a = distinct[i];
                    var b = distinct[j];
                    if (!attempted.Add($"{a.Block}:{a.KeyChar}:{a.NtHex}{a.NrHex}{a.ArHex}:{b.NtHex}{b.NrHex}{b.ArHex}")) continue;
                    var recovered = await _keyRecovery.RecoverAsync(new(KeyRecoveryOperation.Mfkey32)
                        { Logs = [a, b] }, ct);
                    if (recovered.Count == 0) continue;
                    var key = recovered[0];
                    var hex = key.ToString("X12");
                    keys[hex] = Convert.FromHexString(hex);
                    break;
                }
        }
        return keys.Values.ToArray();
    }
}
