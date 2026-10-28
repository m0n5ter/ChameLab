using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Models;
using ChameLab.Services;

namespace ChameLab.Protocol;

/// <summary>
/// Typed, high-level command API over a <see cref="ChameleonConnection"/>.
/// Each method mirrors a reference-client call and returns decoded results.
/// </summary>
public sealed class ChameleonClient(ChameleonConnection conn)
{
    public ChameleonConnection Connection => conn;

    private readonly SemaphoreSlim _slotLock = new(1, 1);

    /// <summary>
    /// Runs <paramref name="work"/> alone among callers of this method. Commands act on the active
    /// slot, so a sequence that switches it and then reads or writes must not have another switch
    /// it midway. Not reentrant.
    /// </summary>
    public Task WithSlotLockAsync(Func<Task> work, CancellationToken ct = default) =>
        WithSlotLockAsync(async () => { await work(); return true; }, ct);

    public async Task<T> WithSlotLockAsync<T>(Func<Task<T>> work, CancellationToken ct = default)
    {
        await _slotLock.WaitAsync(ct);
        try { return await work(); }
        finally { _slotLock.Release(); }
    }

    // ---- Device info ---------------------------------------------------

    public async Task<string> GetAppVersionAsync(CancellationToken ct = default)
    {
        var f = await conn.SendExpectAsync(Command.GetAppVersion, Status.Success, ct: ct);
        return f.Data.Length >= 2 ? $"{f.Data[0]}.{f.Data[1]}" : "?";
    }

    public async Task<string> GetGitVersionAsync(CancellationToken ct = default)
    {
        var f = await conn.SendExpectAsync(Command.GetGitVersion, Status.Success, ct: ct);
        return Encoding.UTF8.GetString(f.Data);
    }

    public async Task<DeviceModel> GetDeviceModelAsync(CancellationToken ct = default)
    {
        var f = await conn.SendExpectAsync(Command.GetDeviceModel, Status.Success, ct: ct);
        return (DeviceModel)(f.Data.Length > 0 ? f.Data[0] : 0);
    }

    public async Task<string> GetChipIdAsync(CancellationToken ct = default)
    {
        var f = await conn.SendExpectAsync(Command.GetDeviceChipId, Status.Success, ct: ct);
        return Convert.ToHexString(f.Data);
    }

    public async Task<string> GetAddressAsync(CancellationToken ct = default)
    {
        var f = await conn.SendExpectAsync(Command.GetDeviceAddress, Status.Success, ct: ct);
        return Convert.ToHexString(f.Data);
    }

    public async Task<DeviceMode> GetDeviceModeAsync(CancellationToken ct = default)
    {
        var f = await conn.SendExpectAsync(Command.GetDeviceMode, Status.Success, ct: ct);
        return (f.Data.Length > 0 && f.Data[0] != 0) ? DeviceMode.Reader : DeviceMode.Tag;
    }

    public Task SetDeviceModeAsync(DeviceMode mode, CancellationToken ct = default) =>
        conn.SendExpectAsync(Command.ChangeDeviceMode, Status.Success,
            [(byte)mode], ct: ct);

    public async Task<BatteryInfo> GetBatteryInfoAsync(CancellationToken ct = default)
    {
        // voltage[2] | percent[1] | condition[1]; older firmware rejects the command.
        var ex = await conn.SendAsync(Command.GetBatteryInfoEx, ct: ct);
        if (ex.Status == Status.Success && ex.Data.Length >= 4 && ex.Data[3] <= (byte)BatteryCondition.Excellent)
        {
            var millivolts = BinaryPrimitives.ReadUInt16BigEndian(ex.Data);
            // 0 mV: not measured yet (the first seconds after a reset), not a critical battery.
            return new BatteryInfo(millivolts, ex.Data[2], millivolts > 0 ? (BatteryCondition)ex.Data[3] : null);
        }

        var f = await conn.SendExpectAsync(Command.GetBatteryInfo, Status.Success, ct: ct);
        int mv = BinaryPrimitives.ReadUInt16BigEndian(f.Data);
        var pct = f.Data.Length > 2 ? f.Data[2] : 0;
        return new BatteryInfo(mv, pct);
    }

    public async Task<IReadOnlyList<ushort>> GetCapabilitiesAsync(CancellationToken ct = default)
    {
        var f = await conn.SendAsync(Command.GetDeviceCapabilities, ct: ct);
        var list = new List<ushort>();
        if (f.Status == Status.Success)
            for (var i = 0; i + 1 < f.Data.Length; i += 2)
                list.Add(BinaryPrimitives.ReadUInt16BigEndian(f.Data.AsSpan(i)));
        return list;
    }

    /// <summary>Gathers everything needed for the dashboard in one call.</summary>
    public async Task<DeviceInfo> GetDeviceInfoAsync(CancellationToken ct = default)
    {
        var fw = await GetAppVersionAsync(ct);
        string git;
        try { git = await GetGitVersionAsync(ct); } catch { git = "-"; }
        var model = await GetDeviceModelAsync(ct);
        var chip = await GetChipIdAsync(ct);
        string addr;
        try { addr = await GetAddressAsync(ct); } catch { addr = "-"; }
        var mode = await GetDeviceModeAsync(ct);
        var colors = false;
        try { colors = (await GetCapabilitiesAsync(ct)).Contains((ushort)Command.SetSlotColor); }
        catch { /* older firmware */ }
        return new DeviceInfo
        {
            FirmwareVersion = fw,
            GitVersion = git,
            Model = model,
            ChipId = chip,
            Address = addr,
            Mode = mode,
            SupportsSlotColors = colors,
        };
    }

    // ---- Slots ---------------------------------------------------------

    public async Task<int> GetActiveSlotAsync(CancellationToken ct = default)
    {
        var f = await conn.SendExpectAsync(Command.GetActiveSlot, Status.Success, ct: ct);
        return (f.Data.Length > 0 ? f.Data[0] : 0) + 1; // fw 0-based → 1-based
    }

    public Task SetActiveSlotAsync(int slot1Based, CancellationToken ct = default) =>
        conn.SendExpectAsync(Command.SetActiveSlot, Status.Success,
            [(byte)(slot1Based - 1)], ct: ct);

    /// <summary>Reads type + enable + nick for all 8 slots and marks the active one.</summary>
    public async Task<IReadOnlyList<SlotInfo>> GetAllSlotsAsync(CancellationToken ct = default)
    {
        var slots = new List<SlotInfo>(8);
        for (var i = 1; i <= 8; i++)
            slots.Add(new SlotInfo { Index = i });

        var info = await conn.SendExpectAsync(Command.GetSlotInfo, Status.Success, ct: ct);
        for (var i = 0; i < 8 && (i * 4 + 3) < info.Data.Length; i++)
        {
            slots[i].HfType = (TagSpecificType)BinaryPrimitives.ReadUInt16BigEndian(info.Data.AsSpan(i * 4));
            slots[i].LfType = (TagSpecificType)BinaryPrimitives.ReadUInt16BigEndian(info.Data.AsSpan(i * 4 + 2));
        }

        try
        {
            var en = await conn.SendExpectAsync(Command.GetEnabledSlots, Status.Success, ct: ct);
            for (var i = 0; i < 8 && (i * 2 + 1) < en.Data.Length; i++)
            {
                slots[i].HfEnabled = en.Data[i * 2] != 0;
                slots[i].LfEnabled = en.Data[i * 2 + 1] != 0;
            }
        }
        catch { /* older firmware */ }

        try
        {
            var nicks = await conn.SendExpectAsync(Command.GetAllSlotNicks, Status.Success, ct: ct);
            ParseNicks(nicks.Data, slots);
        }
        catch { /* older firmware */ }

        try
        {
            var colors = await conn.SendExpectAsync(Command.GetAllSlotColors, Status.Success, ct: ct);
            for (var i = 0; i < 8 && (i * 6 + 5) < colors.Data.Length; i++)
            {
                slots[i].HfColor = SlotColorFromBytes(colors.Data.AsSpan(i * 6, 3));
                slots[i].LfColor = SlotColorFromBytes(colors.Data.AsSpan(i * 6 + 3, 3));
            }
        }
        catch { /* older firmware */ }

        try
        {
            var active = await GetActiveSlotAsync(ct);
            if (active >= 1 && active <= 8) slots[active - 1].IsActive = true;
        }
        catch { /* ignore */ }

        return slots;
    }

    private static void ParseNicks(byte[] data, List<SlotInfo> slots)
    {
        var i = 0;
        for (var s = 0; s < 8 && i < data.Length; s++)
        {
            if (i < data.Length)
            {
                int hfLen = data[i++];
                if (hfLen > 0 && i + hfLen <= data.Length)
                    slots[s].HfNick = Encoding.UTF8.GetString(data, i, hfLen);
                i += hfLen;
            }
            if (i < data.Length)
            {
                int lfLen = data[i++];
                if (lfLen > 0 && i + lfLen <= data.Length)
                    slots[s].LfNick = Encoding.UTF8.GetString(data, i, lfLen);
                i += lfLen;
            }
        }
    }

    public Task SetSlotTagTypeAsync(int slot1Based, TagSpecificType type, CancellationToken ct = default)
    {
        var data = new byte[3];
        data[0] = (byte)(slot1Based - 1);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(1), (ushort)type);
        return conn.SendExpectAsync(Command.SetSlotTagType, Status.Success, data, ct: ct);
    }

    public Task SetSlotEnableAsync(int slot1Based, TagSenseType sense, bool enabled, CancellationToken ct = default) =>
        conn.SendExpectAsync(Command.SetSlotEnable, Status.Success,
            [(byte)(slot1Based - 1), (byte)sense, (byte)(enabled ? 1 : 0)], ct: ct);

    public Task SetSlotDataDefaultAsync(int slot1Based, TagSpecificType type, CancellationToken ct = default)
    {
        var data = new byte[3];
        data[0] = (byte)(slot1Based - 1);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(1), (ushort)type);
        return conn.SendExpectAsync(Command.SetSlotDataDefault, Status.Success, data, ct: ct);
    }

    public Task SetSlotNickAsync(int slot1Based, TagSenseType sense, string name, CancellationToken ct = default)
    {
        var enc = Encoding.UTF8.GetBytes(name);
        if (enc.Length > 32) throw new ArgumentException("Nick name too long (max 32 bytes).");
        var data = new byte[2 + enc.Length];
        data[0] = (byte)(slot1Based - 1);
        data[1] = (byte)sense;
        enc.CopyTo(data, 2);
        return conn.SendExpectAsync(Command.SetSlotTagNick, Status.Success, data, ct: ct);
    }

    /// <summary>
    /// Sets the LED color the device shows for the slot's HF or LF card
    /// (#RRGGBB); null brings back the default HF/LF color. Not persisted until
    /// <see cref="SaveSlotConfigAsync"/>.
    /// </summary>
    public Task SetSlotColorAsync(int slot1Based, TagSenseType sense, string? color, CancellationToken ct = default)
    {
        var rgb = new byte[3];
        if (color is not null)
        {
            var hex = CardSave.NormalizeColor(color);
            rgb = Convert.FromHexString(hex.AsSpan(1));
        }
        return conn.SendExpectAsync(Command.SetSlotColor, Status.Success,
            [(byte)(slot1Based - 1), (byte)sense, rgb[0], rgb[1], rgb[2]], ct: ct);
    }

    private static string? SlotColorFromBytes(ReadOnlySpan<byte> rgb) =>
        rgb[0] == 0 && rgb[1] == 0 && rgb[2] == 0 ? null : "#" + Convert.ToHexString(rgb);

    public Task DeleteSlotNickAsync(int slot1Based, TagSenseType sense, CancellationToken ct = default) =>
        conn.SendExpectAsync(Command.DeleteSlotTagNick, Status.Success,
            [(byte)(slot1Based - 1), (byte)sense], ct: ct);

    public Task SaveSlotConfigAsync(CancellationToken ct = default) =>
        conn.SendExpectAsync(Command.SlotDataConfigSave, Status.Success, ct: ct);

    /// <summary>Clears one frequency of a slot (removes its HF or LF tag).</summary>
    public Task DeleteSlotSenseTypeAsync(int slot1Based, TagSenseType sense, CancellationToken ct = default) =>
        conn.SendExpectAsync(Command.DeleteSlotSenseType, Status.Success,
            [(byte)(slot1Based - 1), (byte)sense], ct: ct);

    // ---- Emulator configuration (active slot) -------------------------

    public async Task<Mf1EmulatorConfig> GetMf1EmulatorConfigAsync(CancellationToken ct = default)
    {
        var f = await conn.SendExpectAsync(Command.Mf1GetEmulatorConfig, Status.Success, ct: ct);
        var d = f.Data;
        return new Mf1EmulatorConfig(
            Detection: d.Length > 0 && d[0] != 0,
            Gen1a: d.Length > 1 && d[1] != 0,
            Gen2: d.Length > 2 && d[2] != 0,
            BlockAntiColl: d.Length > 3 && d[3] != 0,
            WriteMode: (EmulatorWriteMode)(d.Length > 4 ? d[4] : 0));
    }

    public Task SetMf1Gen1aModeAsync(bool enabled, CancellationToken ct = default) =>
        conn.SendExpectAsync(Command.Mf1SetGen1aMode, Status.Success, [(byte)(enabled ? 1 : 0)], ct: ct);

    public Task SetMf1Gen2ModeAsync(bool enabled, CancellationToken ct = default) =>
        conn.SendExpectAsync(Command.Mf1SetGen2Mode, Status.Success, [(byte)(enabled ? 1 : 0)], ct: ct);

    public Task SetMf1BlockAntiCollModeAsync(bool enabled, CancellationToken ct = default) =>
        conn.SendExpectAsync(Command.Mf1SetBlockAntiCollMode, Status.Success, [(byte)(enabled ? 1 : 0)], ct: ct);

    public Task SetMf1WriteModeAsync(EmulatorWriteMode mode, CancellationToken ct = default) =>
        conn.SendExpectAsync(Command.Mf1SetWriteMode, Status.Success, [(byte)mode], ct: ct);

    public async Task<bool> GetMf1DetectionEnableAsync(CancellationToken ct = default) =>
        (await GetMf1EmulatorConfigAsync(ct)).Detection;

    public Task SetMf1DetectionEnableAsync(bool enabled, CancellationToken ct = default) =>
        conn.SendExpectAsync(Command.Mf1SetDetectionEnable, Status.Success, [(byte)(enabled ? 1 : 0)], ct: ct);

    /// <summary>Number of MIFARE Classic auth nonces the emulator has captured (for mfkey32).</summary>
    public async Task<int> GetMf1DetectionCountAsync(CancellationToken ct = default)
    {
        var f = await conn.SendExpectAsync(Command.Mf1GetDetectionCount, Status.Success, ct: ct);
        return f.Data.Length >= 4 ? (int)BinaryPrimitives.ReadUInt32BigEndian(f.Data) : 0;
    }

    /// <summary>Reads captured auth nonce records starting at <paramref name="index"/>.</summary>
    public async Task<IReadOnlyList<DetectionLog>> GetMf1DetectionLogAsync(int index, CancellationToken ct = default)
    {
        var data = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(data, (uint)index);
        var f = await conn.SendExpectAsync(Command.Mf1GetDetectionLog, Status.Success, data, ct: ct);

        var logs = new List<DetectionLog>();
        const int entry = 18; // block + bitfield + uid(4) + nt(4) + nr(4) + ar(4)
        for (var o = 0; o + entry <= f.Data.Length; o += entry)
        {
            var block = f.Data[o];
            var bits = f.Data[o + 1];
            logs.Add(new DetectionLog(
                Block: block,
                KeyType: (bits & 0x01) == 0 ? MfKeyType.A : MfKeyType.B,
                IsNested: (bits & 0x02) != 0,
                Uid: f.Data[(o + 2)..(o + 6)],
                Nt: f.Data[(o + 6)..(o + 10)],
                Nr: f.Data[(o + 10)..(o + 14)],
                Ar: f.Data[(o + 14)..(o + 18)]));
        }
        return logs;
    }

    /// <summary>Reads all captured nonce records.</summary>
    public async Task<IReadOnlyList<DetectionLog>> GetAllMf1DetectionLogsAsync(CancellationToken ct = default)
    {
        var count = await GetMf1DetectionCountAsync(ct);
        var all = new List<DetectionLog>();
        while (all.Count < count)
        {
            var batch = await GetMf1DetectionLogAsync(all.Count, ct);
            if (batch.Count == 0) break;
            all.AddRange(batch);
        }
        return all;
    }

    public async Task<bool> GetMf1FieldOffDoResetAsync(CancellationToken ct = default)
    {
        var f = await conn.SendExpectAsync(Command.Mf1GetFieldOffDoReset, Status.Success, ct: ct);
        return f.Data.Length > 0 && f.Data[0] != 0;
    }

    public Task SetMf1FieldOffDoResetAsync(bool enabled, CancellationToken ct = default) =>
        conn.SendExpectAsync(Command.Mf1SetFieldOffDoReset, Status.Success, [(byte)(enabled ? 1 : 0)], ct: ct);

    public async Task<MifareClassicPrngType> GetMf1PrngTypeAsync(CancellationToken ct = default)
    {
        var f = await conn.SendExpectAsync(Command.Mf1GetPrngType, Status.Success, ct: ct);
        return (MifareClassicPrngType)(f.Data.Length > 0 ? f.Data[0] : 0);
    }

    public Task SetMf1PrngTypeAsync(MifareClassicPrngType prng, CancellationToken ct = default) =>
        conn.SendExpectAsync(Command.Mf1SetPrngType, Status.Success, [(byte)prng], ct: ct);

    // ---- MIFARE Classic key-recovery acquisition (reader side) --------
    // These talk to the card on the reader to collect the material the darkside / nested
    // attacks need; the solving is done in software (ChameLab.Services.*Recovery).

    /// <summary>
    /// Detects the card's nonce PRNG level: Static, Weak (nested) or Hard (hardnested).
    /// Requires an ISO14443-A tag on the reader.
    /// </summary>
    public async Task<MifareClassicPrngType> DetectNtLevelAsync(CancellationToken ct = default)
    {
        var f = await conn.SendAsync(Command.Mf1DetectPrng, ct: ct);
        if (f.Status != Status.HfTagOk || f.Data.Length == 0)
            throw new ChameleonException("Could not detect the card PRNG.", f.Status);
        return (MifareClassicPrngType)f.Data[0]; // 0 static, 1 weak, 2 hard
    }

    /// <summary>
    /// Measures the PRNG distance between the known key's nonce and the target, needed to seed
    /// the nested attack's nonce sweep. <paramref name="key"/> is a working 6-byte sector key.
    /// </summary>
    public async Task<NtDistance> GetNtDistanceAsync(int block, MfKeyType keyType, byte[] key, CancellationToken ct = default)
    {
        if (key.Length != 6) throw new ArgumentException("Key must be 6 bytes.");
        var data = new byte[8];
        data[0] = keyType.ToWire();
        data[1] = (byte)block;
        key.CopyTo(data, 2);
        var f = await conn.SendAsync(Command.Mf1DetectNtDist, data, timeoutMs: 10000, ct: ct);
        if (f.Status != Status.HfTagOk || f.Data.Length < 8)
            throw new ChameleonException("Could not measure the nonce distance.", f.Status);
        return new NtDistance(
            BinaryPrimitives.ReadUInt32BigEndian(f.Data.AsSpan(0, 4)),
            BinaryPrimitives.ReadUInt32BigEndian(f.Data.AsSpan(4, 4)));
    }

    /// <summary>
    /// Collects nested-attack nonce samples for <paramref name="targetBlock"/>/<paramref name="targetKeyType"/>,
    /// authenticating through the known key on <paramref name="block"/>. <paramref name="isStatic"/>
    /// selects the static-nonce variant (records carry no parity then).
    /// </summary>
    public async Task<IReadOnlyList<NestedNonce>> GetNestedNoncesAsync(
        int block, MfKeyType keyType, byte[] key, int targetBlock, MfKeyType targetKeyType,
        bool isStatic = false, CancellationToken ct = default)
    {
        if (isStatic)
            return (await GetStaticNestedNoncesAsync(block, keyType, key, targetBlock, targetKeyType, ct)).Nonces;
        var f = await AcquireNestedAsync(block, keyType, key, targetBlock, targetKeyType, false, ct);
        if (f.Data.Length < 18 || f.Data.Length % 9 != 0)
            throw new ChameleonException("Incomplete nested nonce response.", f.Status);
        var nonces = new List<NestedNonce>();
        for (var i = 0; i < f.Data.Length; i += 9)
            nonces.Add(new NestedNonce(
                BinaryPrimitives.ReadUInt32BigEndian(f.Data.AsSpan(i, 4)),
                BinaryPrimitives.ReadUInt32BigEndian(f.Data.AsSpan(i + 4, 4)), f.Data[i + 8]));
        return nonces;
    }

    /// <summary>Static-nested captures include the authentication UID chosen by the firmware's cascade handling.</summary>
    public async Task<(uint Uid, IReadOnlyList<NestedNonce> Nonces)> GetStaticNestedNoncesAsync(
        int block, MfKeyType keyType, byte[] key, int targetBlock, MfKeyType targetKeyType, CancellationToken ct = default)
    {
        var f = await AcquireNestedAsync(block, keyType, key, targetBlock, targetKeyType, true, ct);
        if (f.Data.Length < 20 || (f.Data.Length - 4) % 8 != 0)
            throw new ChameleonException("Incomplete static nested nonce response.", f.Status);
        var uid = BinaryPrimitives.ReadUInt32BigEndian(f.Data.AsSpan(0, 4));
        var nonces = new List<NestedNonce>();
        for (var i = 4; i < f.Data.Length; i += 8)
            nonces.Add(new NestedNonce(
                BinaryPrimitives.ReadUInt32BigEndian(f.Data.AsSpan(i, 4)),
                BinaryPrimitives.ReadUInt32BigEndian(f.Data.AsSpan(i + 4, 4)), 0));
        return (uid, nonces);
    }

    private async Task<ChameleonFrame> AcquireNestedAsync(
        int block, MfKeyType keyType, byte[] key, int targetBlock, MfKeyType targetKeyType,
        bool isStatic, CancellationToken ct)
    {
        if (key.Length != 6) throw new ArgumentException("Key must be 6 bytes.");
        var data = new byte[10];
        data[0] = keyType.ToWire();
        data[1] = (byte)block;
        key.CopyTo(data, 2);
        data[8] = targetKeyType.ToWire();
        data[9] = (byte)targetBlock;
        var cmd = isStatic ? Command.Mf1StaticNestedAcquire : Command.Mf1NestedAcquire;
        var f = await conn.SendAsync(cmd, data, timeoutMs: 30000, ct: ct);
        if (f.Status != Status.HfTagOk)
            throw new ChameleonException("Could not collect nested nonces.", f.Status);
        return f;
    }

    /// <summary>Probes whether a target key is vulnerable to darkside (defaults to sector 0, key B).</summary>
    public async Task<IReadOnlyList<HardnestedSample>> GetHardnestedNoncesAsync(
        bool slow, int knownBlock, MfKeyType knownType, byte[] knownKey,
        int targetBlock, MfKeyType targetType, CancellationToken ct = default)
    {
        if (knownBlock is < 0 or > 255) throw new ArgumentOutOfRangeException(nameof(knownBlock));
        if (targetBlock is < 0 or > 255) throw new ArgumentOutOfRangeException(nameof(targetBlock));
        if (knownKey.Length != 6) throw new ArgumentException("Key must be 6 bytes.", nameof(knownKey));
        var data = new byte[11];
        data[0] = slow ? (byte)1 : (byte)0;
        data[1] = (byte)knownType;
        data[2] = (byte)knownBlock;
        knownKey.CopyTo(data, 3);
        data[9] = (byte)targetType;
        data[10] = (byte)targetBlock;
        ct.ThrowIfCancellationRequested();
        var frame = await conn.SendExpectAsync(Command.Mf1HardnestedAcquire, Status.HfTagOk,
            data, timeoutMs: 30000);
        ct.ThrowIfCancellationRequested();
        if (frame.Data.Length == 0 || frame.Data.Length % 9 != 0)
            throw new ChameleonException("Invalid hardnested nonce response.", frame.Status);
        var samples = new List<HardnestedSample>(frame.Data.Length / 9 * 2);
        for (var offset = 0; offset < frame.Data.Length; offset += 9)
        {
            var parity = frame.Data[offset + 8];
            samples.Add(new HardnestedSample(
                BinaryPrimitives.ReadUInt32BigEndian(frame.Data.AsSpan(offset, 4)), (byte)(parity >> 4)));
            samples.Add(new HardnestedSample(
                BinaryPrimitives.ReadUInt32BigEndian(frame.Data.AsSpan(offset + 4, 4)), (byte)(parity & 15)));
        }
        return samples;
    }

    public async Task<DarksideResult> CheckDarksideAsync(CancellationToken ct = default, int block = 3, MfKeyType keyType = MfKeyType.B)
    {
        var f = await conn.SendAsync(Command.Mf1DarksideAcquire, [(byte)keyType, (byte)block, 1, 2], timeoutMs: 60000, ct: ct);
        if (f.Status != Status.HfTagOk || f.Data.Length == 0) return DarksideResult.Fixed;
        var status = f.Data[0];
        return status <= 4 ? (DarksideResult)status : DarksideResult.Fixed;
    }

    /// <summary>
    /// Collects one darkside round for <paramref name="targetBlock"/>/<paramref name="targetKeyType"/>.
    /// Throws when the card is no longer vulnerable; <paramref name="firstRecover"/> is false on
    /// retries that gather another round for the parity-zero case.
    /// </summary>
    public async Task<(uint Uid, DarksideItem Item)> GetDarksideAsync(
        int targetBlock, MfKeyType targetKeyType, bool firstRecover, int syncMax, CancellationToken ct = default)
    {
        var data = new byte[] { targetKeyType.ToWire(), (byte)targetBlock, (byte)(firstRecover ? 1 : 0), (byte)syncMax };
        var f = await conn.SendAsync(Command.Mf1DarksideAcquire, data, timeoutMs: 60000, ct: ct);
        if (f.Status != Status.HfTagOk || f.Data.Length != 33 || f.Data[0] != 0)
            throw new ChameleonException("Card is not vulnerable to the darkside attack.", f.Status);

        var d = f.Data.AsSpan(1);
        var uid = BinaryPrimitives.ReadUInt32BigEndian(d[..4]);
        var nt1 = BinaryPrimitives.ReadUInt32BigEndian(d.Slice(4, 4));
        var par = BinaryPrimitives.ReadUInt64BigEndian(d.Slice(8, 8));
        var ks1 = BinaryPrimitives.ReadUInt64BigEndian(d.Slice(16, 8));
        var nr = BinaryPrimitives.ReadUInt32BigEndian(d.Slice(24, 4));
        var ar = BinaryPrimitives.ReadUInt32BigEndian(d.Slice(28, 4));
        return (uid, new DarksideItem(nt1, ks1, par, nr, ar));
    }

    // ---- Ultralight / NTAG emulator settings --------------------------

    public async Task<bool> GetMf0UidMagicModeAsync(CancellationToken ct = default)
    {
        var f = await conn.SendExpectAsync(Command.Mf0NtagGetUidMagicMode, Status.Success, ct: ct);
        return f.Data.Length > 0 && f.Data[0] != 0;
    }

    public Task SetMf0UidMagicModeAsync(bool enabled, CancellationToken ct = default) =>
        conn.SendExpectAsync(Command.Mf0NtagSetUidMagicMode, Status.Success, [(byte)(enabled ? 1 : 0)], ct: ct);

    public async Task<EmulatorWriteMode> GetMf0WriteModeAsync(CancellationToken ct = default)
    {
        var f = await conn.SendExpectAsync(Command.Mf0NtagGetWriteMode, Status.Success, ct: ct);
        return (EmulatorWriteMode)(f.Data.Length > 0 ? f.Data[0] : 0);
    }

    public Task SetMf0WriteModeAsync(EmulatorWriteMode mode, CancellationToken ct = default) =>
        conn.SendExpectAsync(Command.Mf0NtagSetWriteMode, Status.Success, [(byte)mode], ct: ct);

    public async Task<bool> GetMf0DetectionEnableAsync(CancellationToken ct = default)
    {
        var f = await conn.SendExpectAsync(Command.Mf0NtagGetDetectionEnable, Status.Success, ct: ct);
        return f.Data.Length > 0 && f.Data[0] != 0;
    }

    public Task SetMf0DetectionEnableAsync(bool enabled, CancellationToken ct = default) =>
        conn.SendExpectAsync(Command.Mf0NtagSetDetectionEnable, Status.Success, [(byte)(enabled ? 1 : 0)], ct: ct);

    // ---- Scanning ------------------------------------------------------

    public async Task<IReadOnlyList<Hf14aTag>> Hf14aScanAsync(CancellationToken ct = default)
    {
        var f = await conn.SendAsync(Command.Hf14aScan, timeoutMs: 4000, ct: ct);
        if (f.Status != Status.HfTagOk)
            throw new ChameleonException($"No HF tag: {f.Status.Describe()}", f.Status);
        var tags = new List<Hf14aTag>();
        var o = 0;
        var d = f.Data;
        while (o < d.Length)
        {
            int uidLen = d[o++];
            if (o + uidLen + 4 > d.Length) break;
            var uid = d[o..(o + uidLen)]; o += uidLen;
            var atqa = d[o..(o + 2)]; o += 2;
            var sak = d[o++];
            int atsLen = d[o++];
            var ats = (atsLen > 0 && o + atsLen <= d.Length) ? d[o..(o + atsLen)] : [];
            o += atsLen;
            tags.Add(new Hf14aTag(uid, atqa, sak, ats));
        }
        return tags;
    }

    public async Task<Em410xTag> Em410xScanAsync(CancellationToken ct = default)
    {
        var f = await conn.SendAsync(Command.Em410xScan, timeoutMs: 4000, ct: ct);
        if (f.Status != Status.LfTagOk)
            throw new ChameleonException($"No EM410x tag: {f.Status.Describe()}", f.Status);
        var type = (TagSpecificType)BinaryPrimitives.ReadUInt16BigEndian(f.Data);
        var idLen = type == TagSpecificType.Em410xElectra ? 13 : 5;
        var id = f.Data.Length >= 2 + idLen ? f.Data[2..(2 + idLen)] : f.Data[2..];
        return new Em410xTag(type, id);
    }

    /// <summary>
    /// Scans one of the non-EM LF protocols. The returned <see cref="LfScanResult.Id"/>
    /// is the raw emulator-ID buffer the firmware reports, so it can be written
    /// straight back with <see cref="SetLfEmulatorIdAsync"/>.
    /// </summary>
    public async Task<LfScanResult> LfScanAsync(TagSpecificType type, CancellationToken ct = default)
    {
        var (cmd, data) = type switch
        {
            TagSpecificType.HidProx => (Command.HidProxScan, [0]), // format 0 = auto-detect
            TagSpecificType.IoProx => (Command.IoProxScan, []),
            TagSpecificType.Viking => (Command.VikingScan, []),
            TagSpecificType.Pac => (Command.PacScan, Array.Empty<byte>()),
            TagSpecificType.Jablotron => (Command.JablotronScan, Array.Empty<byte>()),
            TagSpecificType.FarpointePyramid => (Command.PyramidScan, Array.Empty<byte>()),
            _ => throw new NotSupportedException($"LF scan for {type.DisplayName()} is not supported."),
        };

        var f = await conn.SendAsync(cmd, data, timeoutMs: 4000, ct: ct);
        if (f.Status != Status.LfTagOk)
            throw new ChameleonException($"No {type.DisplayName()} tag: {f.Status.Describe()}", f.Status);

        var len = type.LfIdLength();
        return new LfScanResult(type, f.Data[..Math.Min(len, f.Data.Length)]);
    }

    // ---- LF write (T55xx) ---------------------------------------------

    /// <summary>Default T55xx write key used by the firmware (0x20206666).</summary>
    public static readonly byte[] DefaultT55xxKey = [0x20, 0x20, 0x66, 0x66];

    /// <summary>
    /// T55xx keys the firmware tries when a tag is password-protected. Includes
    /// <see cref="DefaultT55xxKey"/>, so a tag ChameLab locked earlier can be re-keyed.
    /// </summary>
    public static readonly byte[][] DefaultT55xxOldKeys =
    [
        [0x20, 0x20, 0x66, 0x66],
        [0x51, 0x24, 0x36, 0x48],
        [0x19, 0x92, 0x04, 0x27]
    ];

    /// <summary>
    /// Writes an LF card onto a blank T55xx tag. <paramref name="id"/> is the raw
    /// emulator-ID buffer for the protocol (as produced by a scan or stored on a card).
    /// </summary>
    public async Task WriteLfToT55xxAsync(
        TagSpecificType type, byte[] id, byte[]? newKey = null, IReadOnlyList<byte[]>? oldKeys = null,
        CancellationToken ct = default)
    {
        await SetDeviceModeAsync(DeviceMode.Reader, ct);
        newKey ??= DefaultT55xxKey;
        oldKeys ??= DefaultT55xxOldKeys;
        if (newKey.Length != 4) throw new ArgumentException("A T55xx key must be 4 bytes.");

        var cmd = type switch
        {
            TagSpecificType.Em410xElectra => Command.Em410xElectraWriteToT55xx,
            _ when type.IsEm410x() => Command.Em410xWriteToT55xx,
            TagSpecificType.HidProx => Command.HidProxWriteToT55xx,
            TagSpecificType.IoProx => Command.IoProxWriteToT55xx,
            TagSpecificType.Viking => Command.VikingWriteToT55xx,
            TagSpecificType.Pac => Command.PacWriteToT55xx,
            TagSpecificType.Jablotron => Command.JablotronWriteToT55xx,
            TagSpecificType.Idteck => Command.IdteckWriteToT55xx,
            _ => throw new NotSupportedException($"{type.DisplayName()} cannot be written to a T55xx tag."),
        };

        var expected = type.LfIdLength();
        if (expected > 0 && id.Length != expected)
            throw new ArgumentException($"{type.DisplayName()} ID must be {expected} bytes (got {id.Length}).");

        var buf = new List<byte>(id);
        buf.AddRange(newKey);
        foreach (var k in oldKeys) buf.AddRange(k);
        var f = await conn.SendAsync(cmd, [.. buf], timeoutMs: 5000, ct: ct);
        if (f.Status != Status.LfTagOk)
            throw new ChameleonException($"T55xx write failed: {f.Status.Describe()}", f.Status);

        // A T55xx tag never ACKs a write, so the firmware reports success blindly: a tag
        // locked with a password we don't have silently rejects every block. Read the ID
        // back and confirm it actually landed.
        byte[] readBack;
        try
        {
            readBack = type.IsEm410x() ? (await Em410xScanAsync(ct)).Id : (await LfScanAsync(type, ct)).Id;
        }
        catch (ChameleonException e)
        {
            throw new ChameleonException(
                "Wrote the tag but could not read it back to confirm. It may be locked with a different " +
                "password, or it is not a writable T55xx — enter the tag's current password and try again.",
                e.Status);
        }
        if (!readBack.AsSpan().SequenceEqual(id))
            throw new ChameleonException(
                "The tag read back a different ID, so the write did not take. It is most likely locked with a " +
                "different password — enter the tag's current password and try again.",
                Status.LfTagOk);
    }

    // ---- MIFARE Classic ------------------------------------------------

    public async Task<bool> Mf1AuthAsync(int block, MfKeyType keyType, byte[] key, CancellationToken ct = default) =>
        await Mf1AuthStatusAsync(block, keyType, key, ct) == Status.HfTagOk;

    /// <summary>
    /// Authenticates and returns the device status: <see cref="Status.HfTagOk"/>, <see cref="Status.MfErrAuth"/>
    /// for a wrong key, or an RF error (tag lost, CRC, parity…) that says nothing about the key.
    /// </summary>
    private async Task<Status> Mf1AuthStatusAsync(int block, MfKeyType keyType, byte[] key, CancellationToken ct)
    {
        if (key.Length != 6) throw new ArgumentException("Key must be 6 bytes.");
        var data = new byte[8];
        data[0] = (byte)keyType;
        data[1] = (byte)block;
        key.CopyTo(data, 2);
        var f = await conn.SendAsync(Command.Mf1AuthOneKeyBlock, data, ct: ct);
        return f.Status;
    }

    public async Task<byte[]> Mf1ReadBlockAsync(int block, MfKeyType keyType, byte[] key, CancellationToken ct = default)
    {
        if (key.Length != 6) throw new ArgumentException("Key must be 6 bytes.");
        var data = new byte[8];
        data[0] = (byte)keyType;
        data[1] = (byte)block;
        key.CopyTo(data, 2);
        var f = await conn.SendExpectAsync(Command.Mf1ReadOneBlock, Status.HfTagOk, data, ct: ct);
        return f.Data;
    }

    public async Task<bool> Mf1WriteBlockAsync(int block, MfKeyType keyType, byte[] key, byte[] blockData, CancellationToken ct = default)
    {
        if (key.Length != 6) throw new ArgumentException("Key must be 6 bytes.");
        if (blockData.Length != 16) throw new ArgumentException("Block data must be 16 bytes.");
        var data = new byte[24];
        data[0] = (byte)keyType;
        data[1] = (byte)block;
        key.CopyTo(data, 2);
        blockData.CopyTo(data, 8);
        var f = await conn.SendAsync(Command.Mf1WriteOneBlock, data, ct: ct);
        return f.Status == Status.HfTagOk;
    }

    public async Task<bool> Mf1IsSupportedAsync(CancellationToken ct = default)
    {
        var f = await conn.SendAsync(Command.Mf1DetectSupport, ct: ct);
        return f.Status == Status.HfTagOk;
    }

    /// <summary>Writes one or more consecutive 16-byte blocks into emulator memory.</summary>
    public Task Mf1WriteEmuBlockDataAsync(int startBlock, byte[] blockData, CancellationToken ct = default)
    {
        if (blockData.Length == 0 || blockData.Length % 16 != 0)
            throw new ArgumentException("Emulator data must be a whole number of 16-byte blocks.");
        var data = new byte[1 + blockData.Length];
        data[0] = (byte)startBlock;
        blockData.CopyTo(data, 1);
        return conn.SendExpectAsync(Command.Mf1WriteEmuBlockData, Status.Success, data, timeoutMs: 5000, ct: ct);
    }

    /// <summary>Reads <paramref name="count"/> consecutive blocks from emulator memory.</summary>
    public async Task<byte[]> Mf1ReadEmuBlockDataAsync(int startBlock, int count, CancellationToken ct = default)
    {
        var f = await conn.SendExpectAsync(Command.Mf1ReadEmuBlockData, Status.Success,
            [(byte)startBlock, (byte)count], timeoutMs: 5000, ct: ct);
        return f.Data;
    }

    // ---- MIFARE Classic dump / clone ----------------------------------

    /// <summary>
    /// Reads a complete MIFARE Classic card in reader mode, trying each key in
    /// <paramref name="keyDict"/> (key A then key B) per sector. Blocks whose
    /// sector key is not found are left unread. Reports one tick per sector, and
    /// per-block states to <paramref name="blocks"/> (first a Pending tick covering
    /// the whole card, so a listener can size itself). The card's size comes from
    /// <paramref name="type"/> when given (the user's choice), else from its SAK.
    /// With <paramref name="existing"/>, keeps known keys and blocks and only fills missing ones.
    /// </summary>
    public async Task<MifareDump> ReadMifareClassicAsync(
        IReadOnlyList<byte[]> keyDict,
        IProgress<MifareProgress>? progress = null,
        CancellationToken ct = default,
        IProgress<MifareBlockProgress>? blocks = null,
        IProgress<MifareKeyProgress>? keyProgress = null,
        MifareClassicType? type = null,
        MifareDump? existing = null)
    {
        await SetDeviceModeAsync(DeviceMode.Reader, ct);
        var tags = await Hf14aScanAsync(ct);
        if (tags.Count == 0)
            throw new ChameleonException("No HF tag detected on the reader.", Status.HfTagNo);

        var tag = tags[0];
        if (existing is not null && (!existing.Uid.AsSpan().SequenceEqual(tag.Uid)
            || existing.Type != (type ?? MifareClassic.FromSak(tag.Sak))))
            throw new InvalidOperationException("The card changed. Scan it again before continuing.");
        var dump = existing ?? new MifareDump(type ?? MifareClassic.FromSak(tag.Sak), tag.Uid, tag.Atqa, tag.Sak);
        var sectors = dump.SectorCount;
        var recovered = dump.RecoveredBlocks;
        // Keys that already opened a sector: cards usually reuse them, so each type's
        // working keys are tried first (with retries) before the dictionary pass.
        var foundA = dump.KeyA.OfType<byte[]>().ToList();
        var foundB = dump.KeyB.OfType<byte[]>().ToList();
        if (existing is null) blocks?.Report(new MifareBlockProgress(0, dump.BlockCount, MifareBlockState.Pending));

        try
        {
            for (var s = 0; s < sectors; s++)
            {
                ct.ThrowIfCancellationRequested();
                var first = MifareClassic.SectorFirstBlock(s);
                var count = MifareClassic.BlocksInSector(s);
                blocks?.Report(new MifareBlockProgress(first, count, MifareBlockState.Reading));

                // Both keys are searched independently: a sector may hand out data under
                // key A, key B or both, and the trailer only yields the key used to read it.
                // Key A joins the known keys before key B is searched, so the same sector's B tries it first.
                dump.KeyA[s] ??= await FindSectorKeyAsync(first, MfKeyType.A, s, keyDict, foundA, foundB, keyProgress, ct);
                if (dump.KeyA[s] is { } ka && !foundA.Any(k => k.AsSpan().SequenceEqual(ka))) foundA.Add(ka);
                dump.KeyB[s] ??= await FindSectorKeyAsync(first, MfKeyType.B, s, keyDict, foundB, foundA, keyProgress, ct);
                if (dump.KeyB[s] is { } kb && !foundB.Any(k => k.AsSpan().SequenceEqual(kb))) foundB.Add(kb);

                if (dump.KeyA[s] is null && dump.KeyB[s] is null)
                {
                    blocks?.Report(new MifareBlockProgress(first, count, MifareBlockState.NoKey));
                }
                else
                {
                    for (var b = 0; b < count; b++)
                    {
                        var block = first + b;
                        if (dump.GetBlock(block) is not null) continue;
                        if (await ReadBlockWithEitherKeyAsync(block, dump.KeyA[s], dump.KeyB[s], ct) is { } data)
                        {
                            dump.SetBlock(block, data);
                            recovered++;
                            blocks?.Report(new MifareBlockProgress(block, 1, MifareBlockState.Read));
                        }
                        else blocks?.Report(new MifareBlockProgress(block, 1, MifareBlockState.ReadError));
                    }
                }

                progress?.Report(new MifareProgress(s + 1, sectors, recovered));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopped by the user: keep what was read so far so a partial dump can still be saved.
        }

        dump.FillRecoveredKeys();
        return dump;
    }

    /// <summary>
    /// Searches <paramref name="keyDict"/> for a key of type <paramref name="kt"/> that opens
    /// the sector (authenticating against <paramref name="block"/>). Keys that already opened other
    /// sectors go first: this type's (<paramref name="known"/>), then the other type's
    /// (<paramref name="knownOther"/>), as cards often use one key for both. Reports its progress
    /// to <paramref name="keyProgress"/>. Null if none is found.
    /// </summary>
    private async Task<byte[]?> FindSectorKeyAsync(
        int block, MfKeyType kt, int sector, IReadOnlyList<byte[]> keyDict,
        IReadOnlyList<byte[]> known, IReadOnlyList<byte[]> knownOther,
        IProgress<MifareKeyProgress>? keyProgress, CancellationToken ct)
    {
        var total = keyDict.Count;
        keyProgress?.Report(new MifareKeyProgress(sector, kt, MifareKeyPhase.Searching, 0, total, null));

        // Keys already tried on this sector and type: the dictionary pass skips them.
        var tried = new HashSet<string>();
        foreach (var k in known.Concat(knownOther))
        {
            if (!tried.Add(Convert.ToHexString(k))) continue;
            if (await AuthKnownKeyAsync(block, kt, k, ct))
            {
                keyProgress?.Report(new MifareKeyProgress(sector, kt, MifareKeyPhase.Found, total, total, k));
                return k;
            }
        }

        // Throttle the "searching" ticks: a dictionary can hold thousands of keys.
        var step = Math.Max(1, total / 50);
        for (var i = 0; i < total; i++)
        {
            ct.ThrowIfCancellationRequested();
            var candidate = keyDict[i];
            if (tried.Contains(Convert.ToHexString(candidate))) continue;
            bool ok;
            try { ok = await Mf1AuthAsync(block, kt, candidate, ct); }
            catch (Exception ex) when (IsRfFailure(ex)) { ok = false; }
            if (ok)
            {
                keyProgress?.Report(new MifareKeyProgress(sector, kt, MifareKeyPhase.Found, i + 1, total, candidate));
                return candidate;
            }
            if (i % step == 0)
                keyProgress?.Report(new MifareKeyProgress(sector, kt, MifareKeyPhase.Searching, i + 1, total, null));
        }

        keyProgress?.Report(new MifareKeyProgress(sector, kt, MifareKeyPhase.NotFound, total, total, null));
        return null;
    }

    /// <summary>Reads one block with key A, falling back to key B; null if neither opens it.</summary>
    private async Task<byte[]?> ReadBlockWithEitherKeyAsync(int block, byte[]? keyA, byte[]? keyB, CancellationToken ct)
    {
        if (keyA is not null && await ReadBlockWithRetryAsync(block, MfKeyType.A, keyA, ct) is { } a) return a;
        if (keyB is not null && await ReadBlockWithRetryAsync(block, MfKeyType.B, keyB, ct) is { } b) return b;
        return null;
    }

    /// <summary>How many times a single RF exchange is attempted before it counts as failed.</summary>
    private const int RfAttempts = 4;

    /// <summary>
    /// A failed RF exchange worth retrying or skipping: not a cancellation and not a lost link,
    /// which would otherwise fail every remaining key and block one by one.
    /// </summary>
    private bool IsRfFailure(Exception ex) =>
        ex is not (OperationCanceledException or ChameleonDisconnectedException) && !conn.IsClosed;

    /// <summary>
    /// Authenticates with a key that opened another sector, retrying transient RF failures
    /// (a card slightly off the antenna drops individual exchanges). A plain "wrong key" answer
    /// is final: retrying it would only cost time for every known key that doesn't fit.
    /// </summary>
    private async Task<bool> AuthKnownKeyAsync(int block, MfKeyType keyType, byte[] key, CancellationToken ct)
    {
        for (var i = 0; i < RfAttempts; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                switch (await Mf1AuthStatusAsync(block, keyType, key, ct))
                {
                    case Status.HfTagOk: return true;
                    case Status.MfErrAuth: return false;
                }
            }
            catch (Exception ex) when (IsRfFailure(ex)) { /* retry */ }
        }
        return false;
    }

    /// <summary>Reads one block, retrying transient RF failures; null if it never comes back.</summary>
    private async Task<byte[]?> ReadBlockWithRetryAsync(int block, MfKeyType keyType, byte[] key, CancellationToken ct)
    {
        for (var i = 0; i < RfAttempts; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var data = await Mf1ReadBlockAsync(block, keyType, key, ct);
                if (data.Length == 16) return data;
            }
            catch (Exception ex) when (IsRfFailure(ex)) { /* retry */ }
        }
        return null;
    }

    /// <summary>
    /// Loads a dump into the device's currently active slot so it can be
    /// emulated: sets the slot tag type, writes every known block into
    /// emulator memory, then persists the slot to flash.
    /// </summary>
    public async Task LoadMifareDumpToActiveSlotAsync(
        MifareDump dump,
        IProgress<MifareProgress>? progress = null,
        CancellationToken ct = default)
    {
        await SetDeviceModeAsync(DeviceMode.Tag, ct);
        var slot = await GetActiveSlotAsync(ct);
        var tagType = MifareClassic.ToSlotTagType(dump.Type);
        await SetSlotTagTypeAsync(slot, tagType, ct);
        await SetSlotDataDefaultAsync(slot, tagType, ct);
        await SetSlotEnableAsync(slot, TagSenseType.Hf, enabled: true, ct);

        // Write the known blocks in contiguous runs (fewer, larger frames).
        var written = 0;
        var block = 0;
        while (block < dump.BlockCount)
        {
            if (dump.GetBlock(block) is null) { block++; continue; }
            var runStart = block;
            var run = new List<byte>();
            while (block < dump.BlockCount && dump.GetBlock(block) is { } bd)
            {
                run.AddRange(bd);
                block++;
                if (block - runStart >= 16) break; // cap frame size
            }
            await Mf1WriteEmuBlockDataAsync(runStart, [.. run], ct);
            written += (block - runStart);
            progress?.Report(new MifareProgress(block, dump.BlockCount, written));
        }

        await SaveSlotConfigAsync(ct);
    }

    // ---- Emulator data (active slot) ----------------------------------

    /// <summary>Sets UID / ATQA / SAK / ATS answered by the active HF slot.</summary>
    public Task Hf14aSetAntiCollDataAsync(byte[] uid, byte[] atqa, byte sak, byte[] ats, CancellationToken ct = default)
    {
        if (uid.Length is not (4 or 7 or 10)) throw new ArgumentException("UID must be 4, 7 or 10 bytes.");
        if (atqa.Length != 2) throw new ArgumentException("ATQA must be 2 bytes.");
        var data = new byte[1 + uid.Length + 2 + 1 + 1 + ats.Length];
        var o = 0;
        data[o++] = (byte)uid.Length;
        uid.CopyTo(data, o); o += uid.Length;
        atqa.CopyTo(data, o); o += 2;
        data[o++] = sak;
        data[o++] = (byte)ats.Length;
        ats.CopyTo(data, o);
        return conn.SendExpectAsync(Command.Hf14aSetAntiCollData, Status.Success, data, ct: ct);
    }

    /// <summary>Sets the ID emulated by the active LF slot for the given LF tag type.</summary>
    public Task SetLfEmulatorIdAsync(TagSpecificType type, byte[] id, CancellationToken ct = default)
    {
        var cmd = type switch
        {
            _ when type.IsEm410x() => Command.Em410xSetEmuId,
            TagSpecificType.HidProx => Command.HidProxSetEmuId,
            TagSpecificType.IoProx => Command.IoProxSetEmuId,
            TagSpecificType.Viking => Command.VikingSetEmuId,
            TagSpecificType.Pac => Command.PacSetEmuId,
            TagSpecificType.Jablotron => Command.JablotronSetEmuId,
            TagSpecificType.Idteck => Command.IdteckSetEmuId,
            TagSpecificType.FarpointePyramid => Command.PyramidSetEmuId,
            _ => throw new NotSupportedException($"{type.DisplayName()} cannot be emulated."),
        };
        var len = type.LfIdLength();
        if (len > 0 && id.Length != len)
            throw new ArgumentException($"{type.DisplayName()} ID must be {len} bytes (got {id.Length}).");
        return conn.SendExpectAsync(cmd, Status.Success, id, ct: ct);
    }

    /// <summary>Writes whole 4-byte pages into the active Ultralight / NTAG slot.</summary>
    public Task Mf0WriteEmuPagesAsync(int startPage, byte[] pages, CancellationToken ct = default)
    {
        if (pages.Length == 0 || pages.Length % 4 != 0)
            throw new ArgumentException("Page data must be a whole number of 4-byte pages.");
        var data = new byte[2 + pages.Length];
        data[0] = (byte)startPage;
        data[1] = (byte)(pages.Length / 4);
        pages.CopyTo(data, 2);
        return conn.SendExpectAsync(Command.Mf0NtagWriteEmuPageData, Status.Success, data, timeoutMs: 5000, ct: ct);
    }

    public Task Mf0SetVersionDataAsync(byte[] version, CancellationToken ct = default) =>
        version.Length == 8
            ? conn.SendExpectAsync(Command.Mf0NtagSetVersionData, Status.Success, version, ct: ct)
            : throw new ArgumentException("Ultralight version must be 8 bytes.");

    public Task Mf0SetSignatureDataAsync(byte[] signature, CancellationToken ct = default) =>
        signature.Length == 32
            ? conn.SendExpectAsync(Command.Mf0NtagSetSignatureData, Status.Success, signature, ct: ct)
            : throw new ArgumentException("Ultralight signature must be 32 bytes.");

    public Task Mf0SetCounterAsync(int index, int value, bool resetTearing, CancellationToken ct = default) =>
        conn.SendExpectAsync(Command.Mf0NtagSetCounterData, Status.Success, [
            (byte)(index | (resetTearing ? 0x80 : 0)),
            (byte)(value & 0xFF), (byte)((value >> 8) & 0xFF), (byte)((value >> 16) & 0xFF)
        ], ct: ct);

    public Task Mf0ResetAuthCountAsync(CancellationToken ct = default) =>
        conn.SendExpectAsync(Command.Mf0NtagResetAuthCnt, Status.Success, ct: ct);

    /// <summary>
    /// Emulates a saved card in <paramref name="slot1Based"/>: switches to tag
    /// mode, activates and enables the slot, sets its type and default data,
    /// uploads identity + memory, names it after the card and saves to flash.
    /// Progress reports written blocks/pages.
    /// </summary>
    public async Task LoadCardToSlotAsync(
        CardSave card, int slot1Based,
        IProgress<MifareProgress>? progress = null,
        CancellationToken ct = default)
    {
        var sense = card.Tag.SenseType();
        if (sense == TagSenseType.Undefined)
            throw new NotSupportedException("Pick a tag type for this card before emulating it.");
        var tag = SlotTypeFor(card.Tag);

        await SetDeviceModeAsync(DeviceMode.Tag, ct);
        await SetSlotEnableAsync(slot1Based, sense, enabled: true, ct);
        await SetActiveSlotAsync(slot1Based, ct);
        await SetSlotTagTypeAsync(slot1Based, tag, ct);
        await SetSlotDataDefaultAsync(slot1Based, tag, ct);

        if (sense == TagSenseType.Lf)
        {
            await SetLfEmulatorIdAsync(tag, card.Uid, ct);
        }
        else
        {
            if (card.Uid.Length > 0)
            {
                var atqa = card.Atqa.Length == 2 ? card.Atqa : [0x04, 0x00];
                await Hf14aSetAntiCollDataAsync(card.Uid, atqa, card.Sak, card.Ats, ct);
            }

            if (tag.IsMifareClassic())
                await WriteMf1BlocksAsync(card.Data, tag.MifareClassicBlocks(), progress, ct);
            else if (tag.IsUltralight())
                await WriteUltralightAsync(card, tag, progress, ct);
        }

        await SetSlotNickAsync(slot1Based, sense, SlotNickFor(card), ct);
        try { await SetSlotColorAsync(slot1Based, sense, card.Color, ct); }
        catch (ChameleonException) { /* older firmware: no per-card LED colors */ }
        await SaveSlotConfigAsync(ct);
    }

    /// <summary>The slot tag type a card is emulated with.</summary>
    /// <remarks>EM410x sub-variants (16/32/64) are emulated by the plain EM410x slot type.</remarks>
    public static TagSpecificType SlotTypeFor(TagSpecificType tag) =>
        tag.IsEm410x() && tag != TagSpecificType.Em410xElectra ? TagSpecificType.Em410x : tag;

    /// <summary>The slot nick <see cref="LoadCardToSlotAsync"/> gives a card (its name, cut to 32 UTF-8 bytes).</summary>
    public static string SlotNickFor(CardSave card)
    {
        var name = string.IsNullOrWhiteSpace(card.Name) ? SlotTypeFor(card.Tag).DisplayName() : card.Name;
        while (Encoding.UTF8.GetByteCount(name) > 32) name = name[..^1];
        return name;
    }

    /// <summary>The slot still holds what <see cref="LoadCardToSlotAsync"/> put there for this card (same type and nick).</summary>
    public static bool SlotHoldsCard(SlotInfo slot, CardSave card)
    {
        var sense = card.Tag.SenseType();
        if (sense == TagSenseType.Undefined) return false;
        var (type, enabled, nick) = sense == TagSenseType.Lf
            ? (slot.LfType, slot.LfEnabled, slot.LfNick)
            : (slot.HfType, slot.HfEnabled, slot.HfNick);
        return enabled && type == SlotTypeFor(card.Tag) && nick == SlotNickFor(card);
    }

    /// <summary>Uploads known 16-byte blocks in contiguous runs; empty entries are skipped.</summary>
    private async Task WriteMf1BlocksAsync(
        IReadOnlyList<byte[]> blocks, int maxBlocks, IProgress<MifareProgress>? progress, CancellationToken ct)
    {
        var total = Math.Min(blocks.Count, maxBlocks);
        var written = 0;
        var block = 0;
        while (block < total)
        {
            if (blocks[block].Length != 16) { block++; continue; }
            var runStart = block;
            var run = new List<byte>();
            while (block < total && blocks[block].Length == 16 && block - runStart < 16)
                run.AddRange(blocks[block++]);
            await Mf1WriteEmuBlockDataAsync(runStart, [.. run], ct);
            written += block - runStart;
            progress?.Report(new MifareProgress(block, total, written));
        }
    }

    private async Task WriteUltralightAsync(
        CardSave card, TagSpecificType tag, IProgress<MifareProgress>? progress, CancellationToken ct)
    {
        var total = Math.Min(card.Data.Count, tag.UltralightPages());
        const int pagesPerFrame = 16;
        for (var page = 0; page < total; page += pagesPerFrame)
        {
            var chunk = new List<byte>();
            for (var p = page; p < Math.Min(total, page + pagesPerFrame); p++)
            {
                var d = card.Data[p];
                chunk.AddRange(d.Length == 4 ? d : new byte[4]);
            }
            await Mf0WriteEmuPagesAsync(page, [.. chunk], ct);
            var done = Math.Min(total, page + pagesPerFrame);
            progress?.Report(new MifareProgress(done, total, done));
        }

        if (card.Extra.UltralightVersion.Length == 8)
            await Mf0SetVersionDataAsync(card.Extra.UltralightVersion, ct);
        if (card.Extra.UltralightSignature.Length == 32)
            await Mf0SetSignatureDataAsync(card.Extra.UltralightSignature, ct);
        for (var i = 0; i < card.Extra.UltralightCounters.Count; i++)
            await Mf0SetCounterAsync(i, card.Extra.UltralightCounters[i], resetTearing: true, ct);
        if (tag.HasUltralightCounters())
            await Mf0ResetAuthCountAsync(ct);
    }

    // ---- Save a slot back to the library -------------------------------

    /// <summary>
    /// Reads what <paramref name="slot"/> emulates on <paramref name="sense"/>
    /// into a <see cref="CardSave"/>: identity + memory for HF, the emulator
    /// ID for LF. Emulator data commands act on the active slot, so the slot
    /// is activated for the read and the previously active slot restored.
    /// </summary>
    public async Task<CardSave> ReadCardFromSlotAsync(
        SlotInfo slot, TagSenseType sense,
        IProgress<MifareProgress>? progress = null,
        CancellationToken ct = default)
    {
        var tag = sense == TagSenseType.Hf ? slot.HfType : slot.LfType;
        if (tag == TagSpecificType.Undefined)
            throw new InvalidOperationException($"Slot {slot.Index} has no {sense} card.");
        var nick = sense == TagSenseType.Hf ? slot.HfNick : slot.LfNick;

        var previous = await GetActiveSlotAsync(ct);
        if (previous != slot.Index) await SetActiveSlotAsync(slot.Index, ct);
        try
        {
            var card = new CardSave { Tag = tag };
            if (sense == TagSenseType.Lf)
            {
                card.Uid = await GetLfEmulatorIdAsync(tag, ct);
            }
            else
            {
                await ReadHf14aAntiCollAsync(card, ct);
                if (tag.IsMifareClassic())
                    card.Data = await ReadMf1BlocksAsync(tag.MifareClassicBlocks(), progress, ct);
                else if (tag.IsUltralight())
                    await ReadUltralightAsync(card, tag, progress, ct);
            }
            card.Name = !string.IsNullOrWhiteSpace(nick)
                ? nick
                : card.Uid.Length > 0 ? $"{tag.DisplayName()} {Hex.Compact(card.Uid)}" : tag.DisplayName();
            if ((sense == TagSenseType.Hf ? slot.HfColor : slot.LfColor) is { } color)
                card.Color = SlotColors.SnapToPalette(color);
            return card;
        }
        finally
        {
            if (previous != slot.Index && previous is >= 1 and <= 8)
                await SetActiveSlotAsync(previous, ct);
        }
    }

    /// <summary>The UID (HF) or ID (LF) the active slot emulates for <paramref name="type"/>; empty when it can't be read.</summary>
    public async Task<byte[]> ReadSlotUidAsync(TagSpecificType type, CancellationToken ct = default)
    {
        switch (type.SenseType())
        {
            case TagSenseType.Hf:
                var card = new CardSave { Tag = type };
                await ReadHf14aAntiCollAsync(card, ct);
                return card.Uid;
            case TagSenseType.Lf:
                try { return await GetLfEmulatorIdAsync(type, ct); }
                catch (NotSupportedException) { return Array.Empty<byte>(); }
            default:
                return Array.Empty<byte>();
        }
    }

    // ---- Whole slots (moving content between slots) ---------------------

    /// <summary>
    /// Reads the emulator settings of the slot's HF card (MIFARE Classic or Ultralight / NTAG);
    /// none for other types or an empty / disabled HF side. The slot is activated for the
    /// read and the previously active slot restored.
    /// </summary>
    public async Task<SlotEmulatorSettings> ReadEmulatorSettingsAsync(SlotInfo slot, CancellationToken ct = default)
    {
        var mfc = slot.HfEnabled && slot.HfType.IsMifareClassic();
        var ul = slot.HfEnabled && slot.HfType.IsUltralight();
        if (!mfc && !ul) return SlotEmulatorSettings.None;

        var previous = await GetActiveSlotAsync(ct);
        if (previous != slot.Index) await SetActiveSlotAsync(slot.Index, ct);
        try
        {
            if (mfc)
            {
                var cfg = await GetMf1EmulatorConfigAsync(ct);
                MifareClassicPrngType? prng = null;
                bool? fieldOff = null;
                try { prng = await GetMf1PrngTypeAsync(ct); } catch (ChameleonException) { /* older firmware */ }
                try { fieldOff = await GetMf1FieldOffDoResetAsync(ct); } catch (ChameleonException) { /* older firmware */ }
                return new SlotEmulatorSettings(
                    new Mf1EmulatorSettings(cfg.Gen1a, cfg.Gen2, cfg.BlockAntiColl, cfg.Detection, cfg.WriteMode, prng, fieldOff),
                    null);
            }

            var uid = await GetMf0UidMagicModeAsync(ct);
            var mode = await GetMf0WriteModeAsync(ct);
            bool? detection = null;
            try { detection = await GetMf0DetectionEnableAsync(ct); } catch (ChameleonException) { /* older firmware */ }
            return new SlotEmulatorSettings(null, new Mf0EmulatorSettings(uid, mode, detection));
        }
        finally
        {
            if (previous != slot.Index && previous is >= 1 and <= 8)
                await SetActiveSlotAsync(previous, ct);
        }
    }

    /// <summary>
    /// Applies emulator settings to the active slot (the one its HF card is in); settings the
    /// firmware couldn't report are left alone. Not persisted until <see cref="SaveSlotConfigAsync"/>.
    /// </summary>
    public async Task ApplyEmulatorSettingsAsync(SlotEmulatorSettings settings, CancellationToken ct = default)
    {
        if (settings.Mf1 is { } m)
        {
            await SetMf1Gen1aModeAsync(m.Gen1a, ct);
            await SetMf1Gen2ModeAsync(m.Gen2, ct);
            await SetMf1BlockAntiCollModeAsync(m.BlockAntiColl, ct);
            await SetMf1DetectionEnableAsync(m.Detection, ct);
            await SetMf1WriteModeAsync(m.WriteMode, ct);
            if (m.FieldOffReset is { } reset)
                try { await SetMf1FieldOffDoResetAsync(reset, ct); } catch (ChameleonException) { }
            if (m.Prng is { } prng)
                try { await SetMf1PrngTypeAsync(prng, ct); } catch (ChameleonException) { }
        }
        if (settings.Mf0 is { } u)
        {
            await SetMf0UidMagicModeAsync(u.UidMagic, ct);
            await SetMf0WriteModeAsync(u.WriteMode, ct);
            if (u.Detection is { } detection)
                try { await SetMf0DetectionEnableAsync(detection, ct); } catch (ChameleonException) { }
        }
    }

    /// <summary>Reads everything a slot holds (both cards with their nick, LED color and enable flag, and the emulator settings).</summary>
    public async Task<SlotContent> ReadSlotContentAsync(
        SlotInfo slot, IProgress<MifareProgress>? progress = null, CancellationToken ct = default)
    {
        async Task<SlotSide?> Side(TagSenseType sense)
        {
            var (type, nick, color, enabled) = sense == TagSenseType.Hf
                ? (slot.HfType, slot.HfNick, slot.HfColor, slot.HfEnabled)
                : (slot.LfType, slot.LfNick, slot.LfColor, slot.LfEnabled);
            if (type == TagSpecificType.Undefined) return null;
            var card = await ReadCardFromSlotAsync(slot, sense, progress, ct);
            return new SlotSide(card, nick, color, enabled);
        }

        var hf = await Side(TagSenseType.Hf);
        var lf = await Side(TagSenseType.Lf);
        var emulator = await ReadEmulatorSettingsAsync(slot, ct);
        return new SlotContent(slot.Index, hf, lf, emulator);
    }

    /// <summary>
    /// Replaces what <paramref name="slot1Based"/> holds with <paramref name="content"/> (read from
    /// another slot): each card is uploaded with its own nick, LED color and enable flag, an empty
    /// side is cleared, and the HF emulator settings follow the card. Saves to flash; the slot is
    /// left active.
    /// </summary>
    public async Task WriteSlotContentAsync(
        int slot1Based, SlotContent content, IProgress<MifareProgress>? progress = null, CancellationToken ct = default)
    {
        foreach (var (sense, side) in new[] { (TagSenseType.Hf, content.Hf), (TagSenseType.Lf, content.Lf) })
        {
            if (side is null)
            {
                try { await DeleteSlotSenseTypeAsync(slot1Based, sense, ct); }
                catch (ChameleonException) { /* already empty */ }
                continue;
            }

            await LoadCardToSlotAsync(side.Card, slot1Based, progress, ct);
            // The upload names and colors the slot after the card; put back what the slot had.
            if (string.IsNullOrEmpty(side.Nick))
                try { await DeleteSlotNickAsync(slot1Based, sense, ct); } catch (ChameleonException) { }
            else
                await SetSlotNickAsync(slot1Based, sense, side.Nick, ct);
            try { await SetSlotColorAsync(slot1Based, sense, side.Color, ct); }
            catch (ChameleonException) { /* older firmware: no per-card LED colors */ }
            if (!side.Enabled) await SetSlotEnableAsync(slot1Based, sense, enabled: false, ct);
        }

        if (content.Hf is not null && content.Emulator != SlotEmulatorSettings.None)
        {
            await SetActiveSlotAsync(slot1Based, ct);
            await ApplyEmulatorSettingsAsync(content.Emulator, ct);
        }
        await SaveSlotConfigAsync(ct);
    }

    /// <summary>Reads the ID emulated by the active LF slot.</summary>
    public async Task<byte[]> GetLfEmulatorIdAsync(TagSpecificType type, CancellationToken ct = default)
    {
        var cmd = type switch
        {
            _ when type.IsEm410x() => Command.Em410xGetEmuId,
            TagSpecificType.HidProx => Command.HidProxGetEmuId,
            TagSpecificType.IoProx => Command.IoProxGetEmuId,
            TagSpecificType.Viking => Command.VikingGetEmuId,
            TagSpecificType.Pac => Command.PacGetEmuId,
            TagSpecificType.Jablotron => Command.JablotronGetEmuId,
            TagSpecificType.Idteck => Command.IdteckGetEmuId,
            TagSpecificType.FarpointePyramid => Command.PyramidGetEmuId,
            _ => throw new NotSupportedException($"{type.DisplayName()} cannot be read from a slot."),
        };
        var f = await conn.SendExpectAsync(cmd, Status.Success, ct: ct);
        // EM410x answers type[2] | id, the other LF types the bare id.
        return type.IsEm410x() && f.Data.Length > 2 ? f.Data[2..] : f.Data;
    }

    private async Task ReadHf14aAntiCollAsync(CardSave card, CancellationToken ct)
    {
        // uidlen[1]|uid[uidlen]|atqa[2]|sak[1]|atslen[1]|ats[atslen]
        var d = (await conn.SendExpectAsync(Command.Hf14aGetAntiCollData, Status.Success, ct: ct)).Data;
        if (d.Length == 0) return;
        var o = 0;
        int uidLen = d[o++];
        if (d.Length < 1 + uidLen + 2 + 1) return;
        card.Uid = d[o..(o + uidLen)]; o += uidLen;
        card.Atqa = d[o..(o + 2)]; o += 2;
        card.Sak = d[o++];
        if (o < d.Length)
        {
            int atsLen = d[o++];
            card.Ats = d[o..Math.Min(d.Length, o + atsLen)];
        }
    }

    private async Task<List<byte[]>> ReadMf1BlocksAsync(
        int total, IProgress<MifareProgress>? progress, CancellationToken ct)
    {
        const int blocksPerFrame = 16;
        var blocks = new List<byte[]>(total);
        for (var start = 0; start < total; start += blocksPerFrame)
        {
            var count = Math.Min(blocksPerFrame, total - start);
            var data = await Mf1ReadEmuBlockDataAsync(start, count, ct);
            for (var i = 0; i < count; i++)
                blocks.Add(data.Length >= (i + 1) * 16 ? data[(i * 16)..((i + 1) * 16)] : new byte[16]);
            progress?.Report(new MifareProgress(start + count, total, start + count));
        }
        return blocks;
    }

    private async Task ReadUltralightAsync(
        CardSave card, TagSpecificType tag, IProgress<MifareProgress>? progress, CancellationToken ct)
    {
        var total = tag.UltralightPages();
        try
        {
            var count = await conn.SendExpectAsync(Command.Mf0NtagGetPageCount, Status.Success, ct: ct);
            if (count.Data.Length > 0 && count.Data[0] > 0) total = count.Data[0];
        }
        catch (ChameleonException) { /* fall back to the nominal page count */ }

        const int pagesPerFrame = 16;
        for (var page = 0; page < total; page += pagesPerFrame)
        {
            var n = Math.Min(pagesPerFrame, total - page);
            var data = (await conn.SendExpectAsync(Command.Mf0NtagReadEmuPageData, Status.Success,
                [(byte)page, (byte)n], timeoutMs: 5000, ct: ct)).Data;
            for (var i = 0; i < n; i++)
                card.Data.Add(data.Length >= (i + 1) * 4 ? data[(i * 4)..((i + 1) * 4)] : new byte[4]);
            progress?.Report(new MifareProgress(page + n, total, page + n));
        }

        // Version / signature / counters exist only on some types; the firmware
        // answers INVALID_SLOT_TYPE for the rest.
        var version = await conn.SendAsync(Command.Mf0NtagGetVersionData, ct: ct);
        if (version.Status == Status.Success && version.Data.Length == 8)
            card.Extra.UltralightVersion = version.Data;
        var signature = await conn.SendAsync(Command.Mf0NtagGetSignatureData, ct: ct);
        if (signature.Status == Status.Success && signature.Data.Length == 32)
            card.Extra.UltralightSignature = signature.Data;
        if (tag.HasUltralightCounters())
        {
            for (var i = 0; i < 3; i++)
            {
                var ctr = await conn.SendAsync(Command.Mf0NtagGetCounterData, [(byte)i], ct: ct);
                if (ctr.Status != Status.Success || ctr.Data.Length < 3) break;
                card.Extra.UltralightCounters.Add(ctr.Data[0] | (ctr.Data[1] << 8) | (ctr.Data[2] << 16));
            }
        }
    }

    // ---- Ultralight / NTAG dump ---------------------------------------

    /// <summary>
    /// Reads an Ultralight / NTAG card on the reader into a <see cref="CardSave"/>: the model
    /// from GET_VERSION (or the Ultralight C AUTHENTICATE probe), every page it shows, the
    /// originality signature and the one-way counters. Mirrors the reference client's
    /// <c>hf mfu dump</c>. Per-page states go to <paramref name="pages"/> (first a Pending tick
    /// covering the whole card, so a listener can size itself).
    /// </summary>
    /// <param name="key">
    /// Optional key for protected pages: the 4-byte PWD of Ultralight EV1 / NTAG21x, or the
    /// 16-byte Ultralight C 3DES key (cipher order). A rejected key is not tried again, since
    /// failed attempts count towards the card's AUTHLIM. An accepted PWD and its PACK (or the
    /// Ultralight C key) are put into the dump, where the card itself only shows zeros.
    /// </param>
    public async Task<UltralightDumpResult> DumpUltralightAsync(
        byte[]? key = null,
        IProgress<MifareProgress>? progress = null,
        CancellationToken ct = default,
        IProgress<MifareBlockProgress>? pages = null)
    {
        if (key is { Length: 0 }) key = null;
        if (key is { Length: not (4 or 16) })
            throw new ArgumentException("The key must be 4 bytes (NTAG / Ultralight EV1 password) or 16 bytes (Ultralight C).");

        await SetDeviceModeAsync(DeviceMode.Reader, ct);
        var tags = await Hf14aScanAsync(ct);
        if (tags.Count == 0)
            throw new ChameleonException("No HF tag detected on the reader.", Status.HfTagNo);
        var tag = tags[0];
        if (tag.Sak != 0x00)
            throw new ChameleonException($"Not an Ultralight / NTAG card (SAK {tag.Sak:X2}).", Status.HfTagNo);

        try
        {
            var (type, version) = await IdentifyUltralightCoreAsync(ct);

            // An unknown model is read until the card stops answering.
            var total = type?.UltralightPages() ?? 256;
            var data = new byte[total][];
            for (var i = 0; i < total; i++) data[i] = [];
            pages?.Report(new MifareBlockProgress(0, total, MifareBlockState.Pending));

            bool? keyAccepted = key is null ? null : false;
            byte[]? pwd = null, pack = null;
            var readDone = false;

            if (key is { Length: 16 } && type == TagSpecificType.Mf0Icu2)
            {
                // The firmware authenticates and reads in one command; it stops before the key pages.
                pages?.Report(new MifareBlockProgress(0, total, MifareBlockState.Reading));
                await FieldOffAsync(ct);
                var req = new byte[18];
                key.CopyTo(req, 0);
                req[17] = (byte)total;
                var f = await conn.SendAsync(Command.Mf0UlcRead, req, timeoutMs: 4000, ct: ct);
                if (f.Status == Status.HfTagOk && f.Data.Length >= 4)
                {
                    keyAccepted = readDone = true;
                    for (var p = 0; p < f.Data.Length / 4 && p < total; p++)
                        data[p] = f.Data[(p * 4)..((p + 1) * 4)];
                    // The key pages hold each 8-byte half of the key reversed.
                    for (var i = 0; i < 16; i++)
                    {
                        var p = CardTags.UlcFirstKeyPage + i / 4;
                        if (data[p].Length == 0) data[p] = new byte[4];
                        data[p][i % 4] = key[(i < 8 ? 7 : 23) - i];
                    }
                }
                else pages?.Report(new MifareBlockProgress(0, total, MifareBlockState.Pending));
            }
            else if (key is { Length: 4 } && version.Length == 8)
            {
                // PWD_AUTH answers the 2-byte PACK; it is presented again after every reselect.
                var r = await UltralightCommandAsync(PwdAuth(key), ct);
                if (r.Length == 2) { keyAccepted = true; pwd = key; pack = r; }
            }

            int? lockedFrom = null;
            var live = false; // the card answered the last READ, so it is still selected (and authenticated)
            for (var page = 0; page < total && !readDone;)
            {
                ct.ThrowIfCancellationRequested();
                var n = Math.Min(4, total - page);
                pages?.Report(new MifareBlockProgress(page, n, MifareBlockState.Reading));

                // READ returns four pages (wrapping at the end of memory). The card refuses it from
                // the first password-protected page on, and Ultralight C never shows its key pages.
                var r = await UltralightReadAsync(page, live, pwd, ct);
                live = r is not null;
                if (r is null) { lockedFrom = page; break; }
                for (var i = 0; i < n; i++) data[page + i] = r[(i * 4)..((i + 1) * 4)];
                pages?.Report(new MifareBlockProgress(page, n, MifareBlockState.Read));
                page += n;
                progress?.Report(new MifareProgress(Math.Min(page, total), total, page));
            }

            if (type is null)
            {
                // Unknown model: the first refused page is the end of memory. Keep what was read,
                // typed by the smallest NTAG that holds it.
                total = lockedFrom ?? total;
                data = data[..total];
                type = total <= 45 ? TagSpecificType.Ntag213 : total <= 135 ? TagSpecificType.Ntag215 : TagSpecificType.Ntag216;
            }

            // PWD and PACK always read back as zeros; put the accepted ones in so a clone answers alike.
            if (pwd is not null && pack is not null && type.Value.UltralightCfg0Page() is > 0 and var cfg0
                && data[cfg0 + 3].Length == 4)
            {
                data[cfg0 + 2] = (byte[])pwd.Clone();
                data[cfg0 + 3] = [pack[0], pack[1], data[cfg0 + 3][2], data[cfg0 + 3][3]];
            }

            var read = Array.FindAll(data, d => d.Length > 0).Length;
            var firstMissing = Array.FindIndex(data, d => d.Length == 0);
            lockedFrom = firstMissing >= 0 ? firstMissing : null;
            pages?.Report(new MifareBlockProgress(0, total, MifareBlockState.Pending));
            if (read > 0) pages?.Report(new MifareBlockProgress(0, firstMissing >= 0 ? firstMissing : total, MifareBlockState.Read));
            if (firstMissing >= 0) pages?.Report(new MifareBlockProgress(firstMissing, total - firstMissing, MifareBlockState.NoKey));
            progress?.Report(new MifareProgress(total, total, read));

            var card = new CardSave
            {
                Name = $"{type.Value.DisplayName()} {tag.UidHex}",
                Tag = type.Value,
                Uid = tag.Uid,
                Sak = tag.Sak,
                Atqa = tag.Atqa,
                Ats = tag.Ats,
                Data = [.. data],
            };

            if (version.Length == 8)
            {
                card.Extra.UltralightVersion = version;
                var sig = await UltralightCommandAsync([0x3C, 0x00], ct);
                if (sig.Length == 32) card.Extra.UltralightSignature = sig;

                // READ_CNT: EV1 has counters 0–2; NTAG213/215/216 one NFC counter at address 2,
                // which the card may keep behind the password.
                var counters = type.Value switch
                {
                    TagSpecificType.Mf0Ul11 or TagSpecificType.Mf0Ul21 => new byte[] { 0, 1, 2 },
                    TagSpecificType.Ntag213 or TagSpecificType.Ntag215 or TagSpecificType.Ntag216 => new byte[] { 2 },
                    _ => Array.Empty<byte>(),
                };
                foreach (var index in counters)
                {
                    var c = await UltralightCommandAsync([0x39, index], ct, pwd: pwd);
                    if (c.Length < 3) break;
                    card.Extra.UltralightCounters.Add(c[0] | (c[1] << 8) | (c[2] << 16));
                }
            }

            return new UltralightDumpResult(card, read, total, lockedFrom, keyAccepted, pack);
        }
        finally
        {
            await FieldOffAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Exact Ultralight / NTAG model of the card on the reader (the SAK alone can't tell them
    /// apart); null when the card answers GET_VERSION with an unknown model.
    /// </summary>
    public async Task<TagSpecificType?> IdentifyUltralightAsync(CancellationToken ct = default)
    {
        await SetDeviceModeAsync(DeviceMode.Reader, ct);
        try { return (await IdentifyUltralightCoreAsync(ct)).Type; }
        finally { await FieldOffAsync(CancellationToken.None); }
    }

    /// <summary>The model from GET_VERSION or, without it, the Ultralight C AUTHENTICATE probe; plus the version bytes (empty without).</summary>
    private async Task<(TagSpecificType? Type, byte[] Version)> IdentifyUltralightCoreAsync(CancellationToken ct)
    {
        // GET_VERSION is answered by Ultralight EV1 and NTAG21x; storage size is byte 6.
        var version = await UltralightCommandAsync([0x60], ct);
        if (version.Length == 8)
        {
            TagSpecificType? type = (version[2], version[6]) switch
            {
                (0x04, 0x0B) => TagSpecificType.Ntag210,
                (0x04, 0x0E) => TagSpecificType.Ntag212,
                (0x04, 0x0F) => TagSpecificType.Ntag213,
                (0x04, 0x11) => TagSpecificType.Ntag215,
                (0x04, 0x13) => TagSpecificType.Ntag216,
                (0x03, 0x0B) => TagSpecificType.Mf0Ul11,
                (0x03, 0x0E) => TagSpecificType.Mf0Ul21,
                _ => null,
            };
            return (type, version);
        }
        // No GET_VERSION: Ultralight C answers the first 3DES AUTHENTICATE step, Ultralight does not.
        var auth = await UltralightCommandAsync([0x1A, 0x00], ct);
        return (auth.Length > 0 ? TagSpecificType.Mf0Icu2 : TagSpecificType.Mf0Icu1, []);
    }

    // ---- Ultralight / NTAG write --------------------------------------

    /// <summary>
    /// Writes the user pages of a saved Ultralight / NTAG card onto the card on the reader with
    /// the standard WRITE command. The UID pages, lock bytes, capability container / OTP and the
    /// configuration pages are left untouched, so a write can never lock or misconfigure the card.
    /// Returns the pages the card refused. Per-page states go to <paramref name="pages"/>
    /// (first a Pending tick covering every page of the card, so a listener can size itself).
    /// </summary>
    /// <param name="pwd">Optional 4-byte PWD of the target card when its pages are write-protected.</param>
    public async Task<IReadOnlyList<int>> WriteUltralightUserPagesAsync(
        CardSave card, byte[]? pwd = null,
        IProgress<MifareProgress>? progress = null, CancellationToken ct = default,
        IProgress<MifareBlockProgress>? pages = null)
    {
        if (pwd is { Length: 0 }) pwd = null;
        if (pwd is { Length: not 4 })
            throw new ArgumentException("The password must be 4 bytes.");

        await SetDeviceModeAsync(DeviceMode.Reader, ct);
        var tags = await Hf14aScanAsync(ct);
        if (tags.Count == 0)
            throw new ChameleonException("No HF tag detected on the reader.", Status.HfTagNo);
        if (tags[0].Sak != 0x00)
            throw new ChameleonException($"Not an Ultralight / NTAG card (SAK {tags[0].Sak:X2}).", Status.HfTagNo);

        var (first, end) = UltralightUserPages(card.Tag);
        var targets = Enumerable.Range(first, Math.Max(0, Math.Min(end, card.Data.Count) - first))
            .Where(p => card.Data[p].Length == 4)
            .ToList();
        pages?.Report(new MifareBlockProgress(0, Math.Max(card.Tag.UltralightPages(), card.Data.Count), MifareBlockState.Pending));

        var failed = new List<int>();
        var written = 0;
        try
        {
            if (pwd is not null && (await UltralightCommandAsync(PwdAuth(pwd), ct)).Length != 2)
                throw new ChameleonException("The card did not accept the password.", Status.HfTagNo);

            bool live = pwd is not null; // still selected (and authenticated) after PWD_AUTH
            for (var i = 0; i < targets.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var page = targets[i];
                pages?.Report(new MifareBlockProgress(page, 1, MifareBlockState.Reading));
                var ok = await UltralightWritePageAsync(page, card.Data[page], live, pwd, ct);
                live = ok;
                if (ok) written++; else failed.Add(page);
                pages?.Report(new MifareBlockProgress(page, 1, ok ? MifareBlockState.Read : MifareBlockState.ReadError));
                progress?.Report(new MifareProgress(i + 1, targets.Count, written));
            }
        }
        finally
        {
            if (!conn.IsClosed) await FieldOffAsync(CancellationToken.None);
        }
        return failed;
    }

    /// <summary>
    /// User memory of an Ultralight type as [first, end): from page 4 up to the dynamic lock
    /// bytes, the configuration pages or (Ultralight C) the counter / AUTH pages.
    /// </summary>
    private static (int First, int End) UltralightUserPages(TagSpecificType t)
    {
        var end = t.UltralightPages();
        if (t.UltralightDynamicLockPage() is > 0 and var dyn) end = Math.Min(end, dyn);
        if (t.UltralightCfg0Page() is > 0 and var cfg0) end = Math.Min(end, cfg0);
        return (4, end);
    }

    /// <summary>WRITE (0xA2) of one page, retrying transient RF failures; true when the card ACKs.</summary>
    private async Task<bool> UltralightWritePageAsync(int page, byte[] data, bool live, byte[]? pwd, CancellationToken ct)
    {
        const RawOpt opt = RawOpt.WaitResponse | RawOpt.AppendCrc | RawOpt.KeepRfField;
        var cmd = new byte[] { 0xA2, (byte)page, data[0], data[1], data[2], data[3] };
        for (var i = 0; i < RfAttempts; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!(live && i == 0))
                {
                    // Reselect (and present the password again) before retrying.
                    await FieldOffAsync(ct);
                    if (pwd is null)
                        await Hf14aRawAsync(opt | RawOpt.AutoSelect | RawOpt.CheckResponseCrc, [0x30, 0x00], ct: ct);
                    else if ((await Hf14aRawAsync(opt | RawOpt.AutoSelect | RawOpt.CheckResponseCrc, PwdAuth(pwd), ct: ct)).Length != 2)
                        continue;
                }
                // The 4-bit ACK comes without CRC.
                var r = await Hf14aRawAsync(opt, cmd, ct: ct);
                if (r.Length > 0 && (r[0] & 0x0f) == 0x0a) return true;
            }
            catch (Exception ex) when (IsRfFailure(ex)) { /* retry */ }
        }
        return false;
    }

    private static byte[] PwdAuth(byte[] pwd) => [0x1B, pwd[0], pwd[1], pwd[2], pwd[3]];

    /// <summary>
    /// Sends one Ultralight command and returns the reply without CRC; empty on NAK, CRC error
    /// or no answer. With <paramref name="reselect"/> the field is reset and the card selected
    /// first, since a NAK leaves it idle; otherwise the command goes to the card still selected.
    /// A <paramref name="pwd"/> is presented (PWD_AUTH) right after the reselect.
    /// </summary>
    private async Task<byte[]> UltralightCommandAsync(
        byte[] command, CancellationToken ct, bool reselect = true, byte[]? pwd = null)
    {
        const RawOpt opt = RawOpt.WaitResponse | RawOpt.AppendCrc | RawOpt.CheckResponseCrc | RawOpt.KeepRfField;
        try
        {
            if (!reselect) return await Hf14aRawAsync(opt, command, ct: ct);
            await FieldOffAsync(ct);
            if (pwd is null) return await Hf14aRawAsync(opt | RawOpt.AutoSelect, command, ct: ct);
            var pack = await Hf14aRawAsync(opt | RawOpt.AutoSelect, PwdAuth(pwd), ct: ct);
            return pack.Length == 2 ? await Hf14aRawAsync(opt, command, ct: ct) : [];
        }
        catch (Exception ex) when (IsRfFailure(ex)) { return []; }
    }

    /// <summary>READ of four pages from <paramref name="page"/>, retrying transient RF failures; null if refused.</summary>
    /// <remarks>With <paramref name="live"/> the first attempt reuses the selected card.</remarks>
    private async Task<byte[]?> UltralightReadAsync(int page, bool live, byte[]? pwd, CancellationToken ct)
    {
        for (var i = 0; i < RfAttempts; i++)
        {
            ct.ThrowIfCancellationRequested();
            var r = await UltralightCommandAsync([0x30, (byte)page], ct, reselect: !(live && i == 0), pwd);
            if (r.Length == 16) return r;
        }
        return null;
    }

    /// <summary>Switches the reader field off (an empty raw frame without keep-field).</summary>
    private async Task FieldOffAsync(CancellationToken ct)
    {
        try { await Hf14aRawAsync(RawOpt.None, [], ct: ct); }
        catch (Exception ex) when (IsRfFailure(ex)) { /* best effort */ }
    }

    // ---- Raw ISO14443-A exchange (Hf14aRaw) ---------------------------

    /// <summary>Option bits for <see cref="Command.Hf14aRaw"/> (big-endian bitfield, MSB first).</summary>
    [Flags]
    private enum RawOpt : byte
    {
        None = 0,
        CheckResponseCrc = 1 << 2,
        KeepRfField = 1 << 3,
        AutoSelect = 1 << 4,
        AppendCrc = 1 << 5,
        WaitResponse = 1 << 6,
        ActivateRfField = 1 << 7,
    }

    /// <summary>
    /// Sends a raw ISO14443-A frame and returns the tag's reply. Mirrors the
    /// reference client's <c>hf14a_raw</c>: a 1-byte option field, a 16-bit
    /// response timeout and a 16-bit bit length precede the data.
    /// </summary>
    private async Task<byte[]> Hf14aRawAsync(
        RawOpt opt, byte[] data, int? bitLen = null, int timeoutMs = 200, CancellationToken ct = default)
    {
        var bits = bitLen ?? data.Length * 8;
        var buf = new byte[5 + data.Length];
        buf[0] = (byte)opt;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(1, 2), (ushort)timeoutMs);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(3, 2), (ushort)bits);
        data.CopyTo(buf, 5);
        var f = await conn.SendAsync(Command.Hf14aRaw, buf, timeoutMs: timeoutMs + 1500, ct: ct);
        return f.Data;
    }

    // ---- Magic-card detection -----------------------------------------

    /// <summary>
    /// Probes the card on the reader and reports which magic generation it is,
    /// or <c>null</c> if no tag answers. Gen1a is identified by its magic
    /// wakeup and Gen3 by its backdoor command; anything else that selects
    /// normally is reported as Gen2 (the generic / CUID case).
    /// </summary>
    public async Task<MagicGeneration?> DetectMagicAsync(CancellationToken ct = default)
    {
        await SetDeviceModeAsync(DeviceMode.Reader, ct);

        // Gen1a: the magic wakeup (0x40 at 7 bits, then 0x43) answers with an ACK (0x0a).
        try
        {
            var r = await Hf14aRawAsync(RawOpt.ActivateRfField | RawOpt.WaitResponse | RawOpt.KeepRfField,
                [0x40], bitLen: 7, ct: ct);
            if (r.Length > 0 && (r[0] & 0x0f) == 0x0a)
            {
                await DropFieldAsync(ct);
                return MagicGeneration.Gen1a;
            }
        }
        catch { /* not a Gen1a card */ }
        await DropFieldAsync(ct);

        // Any ordinary 14443-A tag must select for the remaining checks.
        var tags = await Hf14aScanAsync(ct);
        if (tags.Count == 0) return null;

        // Gen3: reading block 0 (0x30 0x00) without authentication answers with 16 data bytes + 2 CRC.
        try
        {
            var r = await Hf14aRawAsync(
                RawOpt.ActivateRfField | RawOpt.WaitResponse | RawOpt.AppendCrc | RawOpt.AutoSelect,
                [0x30, 0x00], ct: ct);
            if (r.Length == 18)
                return MagicGeneration.Gen3;
        }
        catch { /* not a Gen3 card */ }

        return MagicGeneration.Gen2;
    }

    /// <summary>Drops the RF field so a probe does not leave a card half-selected.</summary>
    private async Task DropFieldAsync(CancellationToken ct)
    {
        try
        {
            // HALT (0x50 0x00) with the field released.
            await Hf14aRawAsync(RawOpt.AppendCrc, [0x50, 0x00], ct: ct);
        }
        catch { /* best effort */ }
    }

    // ---- Write a dump to a magic card (Gen1a / Gen2 / Gen3) -----------

    /// <summary>
    /// Writes a MIFARE Classic dump onto a magic card physically on the reader.
    /// <list type="bullet">
    /// <item>Gen1a — magic wakeup, then every block (block 0 included) is written raw with no keys.</item>
    /// <item>Gen2 / CUID — each sector is authenticated with the dump's own keys, then the dictionary; every block including block 0 is written normally.</item>
    /// <item>Gen3 — data blocks are written like Gen2, and block 0 is set with the backdoor command.</item>
    /// </list>
    /// Returns the blocks that could not be written. Per-block states go to <paramref name="blocks"/>
    /// (first a Pending tick covering the whole card, so a listener can size itself).
    /// </summary>
    public Task<IReadOnlyList<int>> WriteMifareDumpToCardAsync(
        MifareDump dump, IReadOnlyList<byte[]> keyDict, MagicGeneration gen = MagicGeneration.Gen2,
        IProgress<MifareProgress>? progress = null, CancellationToken ct = default,
        IProgress<MifareBlockProgress>? blocks = null)
        => gen switch
        {
            MagicGeneration.Gen1a => WriteGen1aAsync(dump, progress, blocks, ct),
            MagicGeneration.Gen3 => WriteGen2Or3Async(dump, keyDict, backdoorBlock0: true, progress, blocks, ct),
            _ => WriteGen2Or3Async(dump, keyDict, backdoorBlock0: false, progress, blocks, ct),
        };

    /// <summary>Gen2 / Gen3 shared path: auth + write each block, with Gen3's backdoor used for block 0.</summary>
    private async Task<IReadOnlyList<int>> WriteGen2Or3Async(
        MifareDump dump, IReadOnlyList<byte[]> keyDict, bool backdoorBlock0,
        IProgress<MifareProgress>? progress, IProgress<MifareBlockProgress>? blocks, CancellationToken ct)
    {
        await SetDeviceModeAsync(DeviceMode.Reader, ct);
        var tags = await Hf14aScanAsync(ct);
        if (tags.Count == 0)
            throw new ChameleonException("No HF tag on the reader.", Status.HfTagNo);

        var failed = new List<int>();
        var total = dump.BlockCount;
        var written = 0;
        blocks?.Report(new MifareBlockProgress(0, total, MifareBlockState.Pending));

        for (var s = 0; s < dump.SectorCount; s++)
        {
            ct.ThrowIfCancellationRequested();
            var first = MifareClassic.SectorFirstBlock(s);
            var count = MifareClassic.BlocksInSector(s);

            // Candidate write keys for this sector: the dump's own keys, then the dictionary.
            var candidates = new List<(MfKeyType type, byte[] key)>();
            if (dump.KeyA[s] is { } ka) candidates.Add((MfKeyType.A, ka));
            if (dump.KeyB[s] is { } kb) candidates.Add((MfKeyType.B, kb));
            foreach (var k in keyDict) { candidates.Add((MfKeyType.A, k)); candidates.Add((MfKeyType.B, k)); }

            for (var b = 0; b < count; b++)
            {
                var block = first + b;
                var data = dump.GetBlock(block);
                if (data is null || data.Length != 16) continue;
                blocks?.Report(new MifareBlockProgress(block, 1, MifareBlockState.Reading));

                bool ok;
                if (block == 0 && backdoorBlock0)
                {
                    ok = await WriteGen3Block0Async(data, tags[0].Uid, ct);
                }
                else
                {
                    ok = false;
                    foreach (var (kt, key) in candidates)
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            if (await Mf1WriteBlockAsync(block, kt, key, data, ct)) { ok = true; break; }
                        }
                        catch (Exception ex) when (IsRfFailure(ex)) { /* try the next key */ }
                    }
                }

                if (ok) written++; else failed.Add(block);
                blocks?.Report(new MifareBlockProgress(block, 1, ok ? MifareBlockState.Read : MifareBlockState.ReadError));
                progress?.Report(new MifareProgress(block + 1, total, written));
            }
        }

        return failed;
    }

    /// <summary>
    /// Sets block 0 of a Gen3 card with the backdoor command (0x90 0xFB 0xCC 0xCC …): first the
    /// whole block, then a UID-only fallback. The card does not answer, so success is confirmed by
    /// rescanning and comparing the UID, as the reference client does.
    /// </summary>
    private async Task<bool> WriteGen3Block0Async(byte[] block0, byte[] currentUid, CancellationToken ct)
    {
        const RawOpt opt = RawOpt.ActivateRfField | RawOpt.WaitResponse | RawOpt.AppendCrc | RawOpt.AutoSelect;
        // UID length is taken from the current tag (4, 7 or 10 bytes); block 0 starts with the UID.
        var uidLen = currentUid.Length is 4 or 7 or 10 ? currentUid.Length : 4;
        var newUid = block0[..Math.Min(uidLen, block0.Length)];

        try
        {
            // Whole block 0.
            var whole = new byte[5 + 16];
            whole[0] = 0x90; whole[1] = 0xFB; whole[2] = 0xCC; whole[3] = 0xCC; whole[4] = 0x10;
            block0.CopyTo(whole, 5);
            await Hf14aRawAsync(opt, whole, timeoutMs: 1000, ct: ct);

            // UID only.
            var uidCmd = new byte[5 + newUid.Length];
            uidCmd[0] = 0x90; uidCmd[1] = 0xFB; uidCmd[2] = 0xCC; uidCmd[3] = 0xCC; uidCmd[4] = (byte)newUid.Length;
            newUid.CopyTo(uidCmd, 5);
            await Hf14aRawAsync(opt, uidCmd, timeoutMs: 1000, ct: ct);
        }
        catch (Exception ex) when (IsRfFailure(ex)) { /* the card reboots without answering */ }

        // The card reboots after the backdoor write; give it a moment, then confirm by UID.
        await Task.Delay(500, ct);
        try
        {
            var tags = await Hf14aScanAsync(ct);
            return tags.Count > 0 && tags[0].Uid.AsSpan().SequenceEqual(newUid);
        }
        catch (Exception ex) when (IsRfFailure(ex)) { return false; }
    }

    /// <summary>Gen1a path: write every block raw with no authentication (block 0 included).</summary>
    private async Task<IReadOnlyList<int>> WriteGen1aAsync(
        MifareDump dump, IProgress<MifareProgress>? progress, IProgress<MifareBlockProgress>? blocks, CancellationToken ct)
    {
        await SetDeviceModeAsync(DeviceMode.Reader, ct);

        var failed = new List<int>();
        var total = dump.BlockCount;
        var written = 0;
        blocks?.Report(new MifareBlockProgress(0, total, MifareBlockState.Pending));

        try
        {
            for (var block = 0; block < dump.BlockCount; block++)
            {
                ct.ThrowIfCancellationRequested();
                var data = dump.GetBlock(block);
                if (data is null || data.Length != 16) continue;

                blocks?.Report(new MifareBlockProgress(block, 1, MifareBlockState.Reading));
                var ok = await Gen1aWriteBlockAsync(block, data, ct);
                if (ok) written++; else failed.Add(block);
                blocks?.Report(new MifareBlockProgress(block, 1, ok ? MifareBlockState.Read : MifareBlockState.ReadError));
                progress?.Report(new MifareProgress(block + 1, total, written));
            }
        }
        finally
        {
            // Also after a cancel: the field must not stay on.
            if (!conn.IsClosed) await DropFieldAsync(CancellationToken.None);
        }

        return failed;
    }

    /// <summary>
    /// Writes one block on a Gen1a card. Each attempt resets the field, replays the
    /// magic wakeup (0x40 at 7 bits, then 0x43), then sends 0xA0 + block and the 16
    /// data bytes — the same per-block unlock the reference client uses, retried a few times.
    /// </summary>
    private async Task<bool> Gen1aWriteBlockAsync(int block, byte[] data, CancellationToken ct)
    {
        const RawOpt baseOpt = RawOpt.ActivateRfField | RawOpt.WaitResponse;
        for (var retry = 0; retry < 5; retry++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // Reset / wake the card (default options: field on, auto-select, CRC).
                await Hf14aRawAsync(baseOpt | RawOpt.AppendCrc | RawOpt.AutoSelect | RawOpt.CheckResponseCrc,
                    [0x00], ct: ct);
                // Magic wakeup, keeping the field between frames; no CRC, no auto-select.
                await Hf14aRawAsync(baseOpt | RawOpt.KeepRfField, [0x40], bitLen: 7, ct: ct);
                await Hf14aRawAsync(baseOpt | RawOpt.KeepRfField, [0x43], ct: ct);
                // Write command and payload (CRC appended, field kept).
                await Hf14aRawAsync(baseOpt | RawOpt.AppendCrc | RawOpt.KeepRfField,
                    [0xA0, (byte)block], ct: ct);
                var output = await Hf14aRawAsync(baseOpt | RawOpt.AppendCrc | RawOpt.KeepRfField, data, ct: ct);
                if (output.Length > 0 && (output[0] & 0x0f) == 0x0a) return true;
                await Task.Delay(100, ct);
            }
            catch (Exception ex) when (IsRfFailure(ex)) { /* retry */ }
        }
        return false;
    }

    // ---- Settings ------------------------------------------------------

    public async Task<AnimationMode> GetAnimationModeAsync(CancellationToken ct = default)
    {
        var f = await conn.SendExpectAsync(Command.GetAnimationMode, Status.Success, ct: ct);
        return (AnimationMode)(f.Data.Length > 0 ? f.Data[0] : 0);
    }

    public Task SetAnimationModeAsync(AnimationMode mode, CancellationToken ct = default) =>
        conn.SendExpectAsync(Command.SetAnimationMode, Status.Success, [(byte)mode], ct: ct);

    public async Task<int> GetSleepTimeoutAsync(CancellationToken ct = default)
    {
        var f = await conn.SendExpectAsync(Command.GetSleepTimeout, Status.Success, ct: ct);
        return f.Data.Length > 0 ? f.Data[0] : 0;
    }

    public Task SetSleepTimeoutAsync(int seconds, CancellationToken ct = default) =>
        conn.SendExpectAsync(Command.SetSleepTimeout, Status.Success, [(byte)seconds], ct: ct);

    public async Task<ButtonPressFunction> GetButtonConfigAsync(ButtonType button, bool longPress, CancellationToken ct = default)
    {
        var cmd = longPress ? Command.GetLongButtonPressConfig : Command.GetButtonPressConfig;
        var f = await conn.SendExpectAsync(cmd, Status.Success, [(byte)button], ct: ct);
        return (ButtonPressFunction)(f.Data.Length > 0 ? f.Data[0] : 0);
    }

    public Task SetButtonConfigAsync(ButtonType button, bool longPress, ButtonPressFunction fn, CancellationToken ct = default)
    {
        var cmd = longPress ? Command.SetLongButtonPressConfig : Command.SetButtonPressConfig;
        return conn.SendExpectAsync(cmd, Status.Success, [(byte)button, (byte)fn], ct: ct);
    }

    public Task SaveSettingsAsync(CancellationToken ct = default) =>
        conn.SendExpectAsync(Command.SaveSettings, Status.Success, ct: ct);

    public Task ResetSettingsAsync(CancellationToken ct = default) =>
        conn.SendExpectAsync(Command.ResetSettings, Status.Success, ct: ct);

    public async Task<int> GetLongPressThresholdAsync(CancellationToken ct = default)
    {
        var f = await conn.SendExpectAsync(Command.GetLongPressThreshold, Status.Success, ct: ct);
        return f.Data.Length >= 2 ? BinaryPrimitives.ReadUInt16BigEndian(f.Data) : 0;
    }

    public Task SetLongPressThresholdAsync(int ms, CancellationToken ct = default)
    {
        var data = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(data, (ushort)ms);
        return conn.SendExpectAsync(Command.SetLongPressThreshold, Status.Success, data, ct: ct);
    }

    // ---- BLE -----------------------------------------------------------

    public async Task<bool> GetBlePairingEnableAsync(CancellationToken ct = default)
    {
        var f = await conn.SendExpectAsync(Command.GetBlePairingEnable, Status.Success, ct: ct);
        return f.Data.Length > 0 && f.Data[0] != 0;
    }

    public Task SetBlePairingEnableAsync(bool enabled, CancellationToken ct = default) =>
        conn.SendExpectAsync(Command.SetBlePairingEnable, Status.Success, [(byte)(enabled ? 1 : 0)], ct: ct);

    public async Task<string> GetBlePairingKeyAsync(CancellationToken ct = default)
    {
        var f = await conn.SendExpectAsync(Command.GetBlePairingKey, Status.Success, ct: ct);
        return Encoding.ASCII.GetString(f.Data);
    }

    public Task SetBlePairingKeyAsync(string pin, CancellationToken ct = default)
    {
        var bytes = Encoding.ASCII.GetBytes(pin);
        if (bytes.Length != 6) throw new ArgumentException("The BLE PIN must be exactly 6 digits.");
        return conn.SendExpectAsync(Command.SetBlePairingKey, Status.Success, bytes, ct: ct);
    }

    public Task DeleteAllBleBondsAsync(CancellationToken ct = default) =>
        conn.SendExpectAsync(Command.DeleteAllBleBonds, Status.Success, ct: ct);

    /// <summary>Factory reset. The device closes the connection afterwards.</summary>
    public Task WipeFdsAsync(CancellationToken ct = default) =>
        conn.SendExpectAsync(Command.WipeFds, Status.Success, timeoutMs: 5000, ct: ct);

    public void EnterBootloader() =>
        _ = conn.SendAsync(Command.EnterBootloader, timeoutMs: 500);
}
