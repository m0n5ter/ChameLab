using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Models;

namespace ChameLab.Protocol;

/// <summary>
/// Typed, high-level command API over a <see cref="ChameleonConnection"/>.
/// Each method mirrors a reference-client call and returns decoded results.
/// </summary>
public sealed class ChameleonClient
{
    private readonly ChameleonConnection _conn;

    public ChameleonClient(ChameleonConnection conn) => _conn = conn;

    public ChameleonConnection Connection => _conn;

    // ---- Device info ---------------------------------------------------

    public async Task<string> GetAppVersionAsync(CancellationToken ct = default)
    {
        var f = await _conn.SendExpectAsync(Command.GetAppVersion, Status.Success, ct: ct);
        return f.Data.Length >= 2 ? $"{f.Data[0]}.{f.Data[1]}" : "?";
    }

    public async Task<string> GetGitVersionAsync(CancellationToken ct = default)
    {
        var f = await _conn.SendExpectAsync(Command.GetGitVersion, Status.Success, ct: ct);
        return Encoding.UTF8.GetString(f.Data);
    }

    public async Task<DeviceModel> GetDeviceModelAsync(CancellationToken ct = default)
    {
        var f = await _conn.SendExpectAsync(Command.GetDeviceModel, Status.Success, ct: ct);
        return (DeviceModel)(f.Data.Length > 0 ? f.Data[0] : 0);
    }

    public async Task<string> GetChipIdAsync(CancellationToken ct = default)
    {
        var f = await _conn.SendExpectAsync(Command.GetDeviceChipId, Status.Success, ct: ct);
        return Convert.ToHexString(f.Data);
    }

    public async Task<string> GetAddressAsync(CancellationToken ct = default)
    {
        var f = await _conn.SendExpectAsync(Command.GetDeviceAddress, Status.Success, ct: ct);
        return Convert.ToHexString(f.Data);
    }

    public async Task<DeviceMode> GetDeviceModeAsync(CancellationToken ct = default)
    {
        var f = await _conn.SendExpectAsync(Command.GetDeviceMode, Status.Success, ct: ct);
        return (f.Data.Length > 0 && f.Data[0] != 0) ? DeviceMode.Reader : DeviceMode.Tag;
    }

    public Task SetDeviceModeAsync(DeviceMode mode, CancellationToken ct = default) =>
        _conn.SendExpectAsync(Command.ChangeDeviceMode, Status.Success,
            new[] { (byte)mode }, ct: ct);

    public async Task<BatteryInfo> GetBatteryInfoAsync(CancellationToken ct = default)
    {
        var f = await _conn.SendExpectAsync(Command.GetBatteryInfo, Status.Success, ct: ct);
        int mv = BinaryPrimitives.ReadUInt16BigEndian(f.Data);
        var pct = f.Data.Length > 2 ? f.Data[2] : 0;
        return new BatteryInfo(mv, pct);
    }

    public async Task<IReadOnlyList<ushort>> GetCapabilitiesAsync(CancellationToken ct = default)
    {
        var f = await _conn.SendAsync(Command.GetDeviceCapabilities, ct: ct);
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
        return new DeviceInfo
        {
            FirmwareVersion = fw,
            GitVersion = git,
            Model = model,
            ChipId = chip,
            Address = addr,
            Mode = mode,
        };
    }

    // ---- Slots ---------------------------------------------------------

    public async Task<int> GetActiveSlotAsync(CancellationToken ct = default)
    {
        var f = await _conn.SendExpectAsync(Command.GetActiveSlot, Status.Success, ct: ct);
        return (f.Data.Length > 0 ? f.Data[0] : 0) + 1; // fw 0-based → 1-based
    }

    public Task SetActiveSlotAsync(int slot1Based, CancellationToken ct = default) =>
        _conn.SendExpectAsync(Command.SetActiveSlot, Status.Success,
            new[] { (byte)(slot1Based - 1) }, ct: ct);

    /// <summary>Reads type + enable + nick for all 8 slots and marks the active one.</summary>
    public async Task<IReadOnlyList<SlotInfo>> GetAllSlotsAsync(CancellationToken ct = default)
    {
        var slots = new List<SlotInfo>(8);
        for (var i = 1; i <= 8; i++)
            slots.Add(new SlotInfo { Index = i });

        var info = await _conn.SendExpectAsync(Command.GetSlotInfo, Status.Success, ct: ct);
        for (var i = 0; i < 8 && (i * 4 + 3) < info.Data.Length; i++)
        {
            slots[i].HfType = (TagSpecificType)BinaryPrimitives.ReadUInt16BigEndian(info.Data.AsSpan(i * 4));
            slots[i].LfType = (TagSpecificType)BinaryPrimitives.ReadUInt16BigEndian(info.Data.AsSpan(i * 4 + 2));
        }

        try
        {
            var en = await _conn.SendExpectAsync(Command.GetEnabledSlots, Status.Success, ct: ct);
            for (var i = 0; i < 8 && (i * 2 + 1) < en.Data.Length; i++)
            {
                slots[i].HfEnabled = en.Data[i * 2] != 0;
                slots[i].LfEnabled = en.Data[i * 2 + 1] != 0;
            }
        }
        catch { /* older firmware */ }

        try
        {
            var nicks = await _conn.SendExpectAsync(Command.GetAllSlotNicks, Status.Success, ct: ct);
            ParseNicks(nicks.Data, slots);
        }
        catch { /* older firmware */ }

        try
        {
            var colors = await _conn.SendExpectAsync(Command.GetAllSlotColors, Status.Success, ct: ct);
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
        return _conn.SendExpectAsync(Command.SetSlotTagType, Status.Success, data, ct: ct);
    }

    public Task SetSlotEnableAsync(int slot1Based, TagSenseType sense, bool enabled, CancellationToken ct = default) =>
        _conn.SendExpectAsync(Command.SetSlotEnable, Status.Success,
            new[] { (byte)(slot1Based - 1), (byte)sense, (byte)(enabled ? 1 : 0) }, ct: ct);

    public Task SetSlotDataDefaultAsync(int slot1Based, TagSpecificType type, CancellationToken ct = default)
    {
        var data = new byte[3];
        data[0] = (byte)(slot1Based - 1);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(1), (ushort)type);
        return _conn.SendExpectAsync(Command.SetSlotDataDefault, Status.Success, data, ct: ct);
    }

    public Task SetSlotNickAsync(int slot1Based, TagSenseType sense, string name, CancellationToken ct = default)
    {
        var enc = Encoding.UTF8.GetBytes(name);
        if (enc.Length > 32) throw new ArgumentException("Nick name too long (max 32 bytes).");
        var data = new byte[2 + enc.Length];
        data[0] = (byte)(slot1Based - 1);
        data[1] = (byte)sense;
        enc.CopyTo(data, 2);
        return _conn.SendExpectAsync(Command.SetSlotTagNick, Status.Success, data, ct: ct);
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
        return _conn.SendExpectAsync(Command.SetSlotColor, Status.Success,
            new[] { (byte)(slot1Based - 1), (byte)sense, rgb[0], rgb[1], rgb[2] }, ct: ct);
    }

    private static string? SlotColorFromBytes(ReadOnlySpan<byte> rgb) =>
        rgb[0] == 0 && rgb[1] == 0 && rgb[2] == 0 ? null : "#" + Convert.ToHexString(rgb);

    public Task DeleteSlotNickAsync(int slot1Based, TagSenseType sense, CancellationToken ct = default) =>
        _conn.SendExpectAsync(Command.DeleteSlotTagNick, Status.Success,
            new[] { (byte)(slot1Based - 1), (byte)sense }, ct: ct);

    public Task SaveSlotConfigAsync(CancellationToken ct = default) =>
        _conn.SendExpectAsync(Command.SlotDataConfigSave, Status.Success, ct: ct);

    /// <summary>Clears one frequency of a slot (removes its HF or LF tag).</summary>
    public Task DeleteSlotSenseTypeAsync(int slot1Based, TagSenseType sense, CancellationToken ct = default) =>
        _conn.SendExpectAsync(Command.DeleteSlotSenseType, Status.Success,
            new[] { (byte)(slot1Based - 1), (byte)sense }, ct: ct);

    // ---- Emulator configuration (active slot) -------------------------

    public async Task<Mf1EmulatorConfig> GetMf1EmulatorConfigAsync(CancellationToken ct = default)
    {
        var f = await _conn.SendExpectAsync(Command.Mf1GetEmulatorConfig, Status.Success, ct: ct);
        var d = f.Data;
        return new Mf1EmulatorConfig(
            Detection: d.Length > 0 && d[0] != 0,
            Gen1a: d.Length > 1 && d[1] != 0,
            Gen2: d.Length > 2 && d[2] != 0,
            BlockAntiColl: d.Length > 3 && d[3] != 0,
            WriteMode: (EmulatorWriteMode)(d.Length > 4 ? d[4] : 0));
    }

    public Task SetMf1Gen1aModeAsync(bool enabled, CancellationToken ct = default) =>
        _conn.SendExpectAsync(Command.Mf1SetGen1aMode, Status.Success, new[] { (byte)(enabled ? 1 : 0) }, ct: ct);

    public Task SetMf1Gen2ModeAsync(bool enabled, CancellationToken ct = default) =>
        _conn.SendExpectAsync(Command.Mf1SetGen2Mode, Status.Success, new[] { (byte)(enabled ? 1 : 0) }, ct: ct);

    public Task SetMf1BlockAntiCollModeAsync(bool enabled, CancellationToken ct = default) =>
        _conn.SendExpectAsync(Command.Mf1SetBlockAntiCollMode, Status.Success, new[] { (byte)(enabled ? 1 : 0) }, ct: ct);

    public Task SetMf1WriteModeAsync(EmulatorWriteMode mode, CancellationToken ct = default) =>
        _conn.SendExpectAsync(Command.Mf1SetWriteMode, Status.Success, new[] { (byte)mode }, ct: ct);

    public async Task<bool> GetMf1DetectionEnableAsync(CancellationToken ct = default) =>
        (await GetMf1EmulatorConfigAsync(ct)).Detection;

    public Task SetMf1DetectionEnableAsync(bool enabled, CancellationToken ct = default) =>
        _conn.SendExpectAsync(Command.Mf1SetDetectionEnable, Status.Success, new[] { (byte)(enabled ? 1 : 0) }, ct: ct);

    /// <summary>Number of MIFARE Classic auth nonces the emulator has captured (for mfkey32).</summary>
    public async Task<int> GetMf1DetectionCountAsync(CancellationToken ct = default)
    {
        var f = await _conn.SendExpectAsync(Command.Mf1GetDetectionCount, Status.Success, ct: ct);
        return f.Data.Length >= 4 ? (int)BinaryPrimitives.ReadUInt32BigEndian(f.Data) : 0;
    }

    /// <summary>Reads captured auth nonce records starting at <paramref name="index"/>.</summary>
    public async Task<IReadOnlyList<DetectionLog>> GetMf1DetectionLogAsync(int index, CancellationToken ct = default)
    {
        var data = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(data, (uint)index);
        var f = await _conn.SendExpectAsync(Command.Mf1GetDetectionLog, Status.Success, data, ct: ct);

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
        var f = await _conn.SendExpectAsync(Command.Mf1GetFieldOffDoReset, Status.Success, ct: ct);
        return f.Data.Length > 0 && f.Data[0] != 0;
    }

    public Task SetMf1FieldOffDoResetAsync(bool enabled, CancellationToken ct = default) =>
        _conn.SendExpectAsync(Command.Mf1SetFieldOffDoReset, Status.Success, new[] { (byte)(enabled ? 1 : 0) }, ct: ct);

    public async Task<MifareClassicPrngType> GetMf1PrngTypeAsync(CancellationToken ct = default)
    {
        var f = await _conn.SendExpectAsync(Command.Mf1GetPrngType, Status.Success, ct: ct);
        return (MifareClassicPrngType)(f.Data.Length > 0 ? f.Data[0] : 0);
    }

    public Task SetMf1PrngTypeAsync(MifareClassicPrngType prng, CancellationToken ct = default) =>
        _conn.SendExpectAsync(Command.Mf1SetPrngType, Status.Success, new[] { (byte)prng }, ct: ct);

    // ---- Ultralight / NTAG emulator settings --------------------------

    public async Task<bool> GetMf0UidMagicModeAsync(CancellationToken ct = default)
    {
        var f = await _conn.SendExpectAsync(Command.Mf0NtagGetUidMagicMode, Status.Success, ct: ct);
        return f.Data.Length > 0 && f.Data[0] != 0;
    }

    public Task SetMf0UidMagicModeAsync(bool enabled, CancellationToken ct = default) =>
        _conn.SendExpectAsync(Command.Mf0NtagSetUidMagicMode, Status.Success, new[] { (byte)(enabled ? 1 : 0) }, ct: ct);

    public async Task<EmulatorWriteMode> GetMf0WriteModeAsync(CancellationToken ct = default)
    {
        var f = await _conn.SendExpectAsync(Command.Mf0NtagGetWriteMode, Status.Success, ct: ct);
        return (EmulatorWriteMode)(f.Data.Length > 0 ? f.Data[0] : 0);
    }

    public Task SetMf0WriteModeAsync(EmulatorWriteMode mode, CancellationToken ct = default) =>
        _conn.SendExpectAsync(Command.Mf0NtagSetWriteMode, Status.Success, new[] { (byte)mode }, ct: ct);

    public async Task<bool> GetMf0DetectionEnableAsync(CancellationToken ct = default)
    {
        var f = await _conn.SendExpectAsync(Command.Mf0NtagGetDetectionEnable, Status.Success, ct: ct);
        return f.Data.Length > 0 && f.Data[0] != 0;
    }

    public Task SetMf0DetectionEnableAsync(bool enabled, CancellationToken ct = default) =>
        _conn.SendExpectAsync(Command.Mf0NtagSetDetectionEnable, Status.Success, new[] { (byte)(enabled ? 1 : 0) }, ct: ct);

    // ---- Scanning ------------------------------------------------------

    public async Task<IReadOnlyList<Hf14aTag>> Hf14aScanAsync(CancellationToken ct = default)
    {
        var f = await _conn.SendAsync(Command.Hf14aScan, timeoutMs: 4000, ct: ct);
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
            var ats = (atsLen > 0 && o + atsLen <= d.Length) ? d[o..(o + atsLen)] : Array.Empty<byte>();
            o += atsLen;
            tags.Add(new Hf14aTag(uid, atqa, sak, ats));
        }
        return tags;
    }

    public async Task<Em410xTag> Em410xScanAsync(CancellationToken ct = default)
    {
        var f = await _conn.SendAsync(Command.Em410xScan, timeoutMs: 4000, ct: ct);
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
            TagSpecificType.HidProx => (Command.HidProxScan, new byte[] { 0 }), // format 0 = auto-detect
            TagSpecificType.IoProx => (Command.IoProxScan, Array.Empty<byte>()),
            TagSpecificType.Viking => (Command.VikingScan, Array.Empty<byte>()),
            TagSpecificType.Pac => (Command.PacScan, Array.Empty<byte>()),
            TagSpecificType.Jablotron => (Command.JablotronScan, Array.Empty<byte>()),
            _ => throw new NotSupportedException($"LF scan for {type.DisplayName()} is not supported."),
        };

        var f = await _conn.SendAsync(cmd, data, timeoutMs: 4000, ct: ct);
        if (f.Status != Status.LfTagOk)
            throw new ChameleonException($"No {type.DisplayName()} tag: {f.Status.Describe()}", f.Status);

        var len = type.LfIdLength();
        return new LfScanResult(type, f.Data[..Math.Min(len, f.Data.Length)]);
    }

    // ---- LF write (T55xx) ---------------------------------------------

    /// <summary>Default T55xx write key used by the firmware (0x20206666).</summary>
    public static readonly byte[] DefaultT55xxKey = { 0x20, 0x20, 0x66, 0x66 };

    /// <summary>T55xx keys the firmware tries when a tag is password-protected.</summary>
    public static readonly byte[][] DefaultT55xxOldKeys =
    {
        new byte[] { 0x51, 0x24, 0x36, 0x48 },
        new byte[] { 0x19, 0x92, 0x04, 0x27 },
    };

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
        var f = await _conn.SendAsync(cmd, buf.ToArray(), timeoutMs: 5000, ct: ct);
        if (f.Status != Status.LfTagOk)
            throw new ChameleonException($"T55xx write failed: {f.Status.Describe()}", f.Status);
    }

    // ---- MIFARE Classic ------------------------------------------------

    public async Task<bool> Mf1AuthAsync(int block, MfKeyType keyType, byte[] key, CancellationToken ct = default)
    {
        if (key.Length != 6) throw new ArgumentException("Key must be 6 bytes.");
        var data = new byte[8];
        data[0] = (byte)keyType;
        data[1] = (byte)block;
        key.CopyTo(data, 2);
        var f = await _conn.SendAsync(Command.Mf1AuthOneKeyBlock, data, ct: ct);
        return f.Status == Status.HfTagOk;
    }

    public async Task<byte[]> Mf1ReadBlockAsync(int block, MfKeyType keyType, byte[] key, CancellationToken ct = default)
    {
        if (key.Length != 6) throw new ArgumentException("Key must be 6 bytes.");
        var data = new byte[8];
        data[0] = (byte)keyType;
        data[1] = (byte)block;
        key.CopyTo(data, 2);
        var f = await _conn.SendExpectAsync(Command.Mf1ReadOneBlock, Status.HfTagOk, data, ct: ct);
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
        var f = await _conn.SendAsync(Command.Mf1WriteOneBlock, data, ct: ct);
        return f.Status == Status.HfTagOk;
    }

    public async Task<bool> Mf1IsSupportedAsync(CancellationToken ct = default)
    {
        var f = await _conn.SendAsync(Command.Mf1DetectSupport, ct: ct);
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
        return _conn.SendExpectAsync(Command.Mf1WriteEmuBlockData, Status.Success, data, timeoutMs: 5000, ct: ct);
    }

    /// <summary>Reads <paramref name="count"/> consecutive blocks from emulator memory.</summary>
    public async Task<byte[]> Mf1ReadEmuBlockDataAsync(int startBlock, int count, CancellationToken ct = default)
    {
        var f = await _conn.SendExpectAsync(Command.Mf1ReadEmuBlockData, Status.Success,
            new[] { (byte)startBlock, (byte)count }, timeoutMs: 5000, ct: ct);
        return f.Data;
    }

    // ---- MIFARE Classic dump / clone ----------------------------------

    /// <summary>
    /// Reads a complete MIFARE Classic card in reader mode, trying each key in
    /// <paramref name="keyDict"/> (key A then key B) per sector. Blocks whose
    /// sector key is not found are left unread. Reports one tick per sector, and
    /// per-block states to <paramref name="blocks"/> (first a Pending tick covering
    /// the whole card, so a listener can size itself).
    /// </summary>
    public async Task<MifareDump> ReadMifareClassicAsync(
        IReadOnlyList<byte[]> keyDict,
        IProgress<MifareProgress>? progress = null,
        CancellationToken ct = default,
        IProgress<MifareBlockProgress>? blocks = null)
    {
        await SetDeviceModeAsync(DeviceMode.Reader, ct);
        var tags = await Hf14aScanAsync(ct);
        if (tags.Count == 0)
            throw new ChameleonException("No HF tag detected on the reader.", Status.HfTagNo);

        var tag = tags[0];
        var type = MifareClassic.FromSak(tag.Sak);
        var dump = new MifareDump(type, tag.Uid, tag.Atqa, tag.Sak);
        var sectors = dump.SectorCount;
        var recovered = 0;
        // Keys that already opened a sector: cards usually reuse them, so they are tried
        // first (with retries) before falling back to a single pass over the dictionary.
        var found = new List<(MfKeyType Type, byte[] Key)>();
        blocks?.Report(new MifareBlockProgress(0, dump.BlockCount, MifareBlockState.Pending));

        for (var s = 0; s < sectors; s++)
        {
            ct.ThrowIfCancellationRequested();
            var first = MifareClassic.SectorFirstBlock(s);
            var count = MifareClassic.BlocksInSector(s);
            blocks?.Report(new MifareBlockProgress(first, count, MifareBlockState.Reading));

            // Find a working key for this sector (authenticate against its first block).
            (MfKeyType Type, byte[] Key)? hit = null;
            foreach (var known in found)
                if (await AuthWithRetryAsync(first, known.Type, known.Key, ct)) { hit = known; break; }
            if (hit is null)
            {
                foreach (var kt in new[] { MfKeyType.A, MfKeyType.B })
                {
                    foreach (var candidate in keyDict)
                    {
                        ct.ThrowIfCancellationRequested();
                        bool ok;
                        try { ok = await Mf1AuthAsync(first, kt, candidate, ct); }
                        catch { ok = false; }
                        if (ok) { hit = (kt, candidate); break; }
                    }
                    if (hit is not null) break;
                }
                if (hit is { } h) found.Add(h);
            }

            if (hit is { } use)
            {
                if (use.Type == MfKeyType.A) dump.KeyA[s] = use.Key; else dump.KeyB[s] = use.Key;
                for (var b = 0; b < count; b++)
                {
                    var block = first + b;
                    if (await ReadBlockWithRetryAsync(block, use.Type, use.Key, ct) is { } data)
                    {
                        dump.SetBlock(block, data);
                        recovered++;
                        blocks?.Report(new MifareBlockProgress(block, 1, MifareBlockState.Read));
                    }
                    else blocks?.Report(new MifareBlockProgress(block, 1, MifareBlockState.ReadError));
                }
            }
            else blocks?.Report(new MifareBlockProgress(first, count, MifareBlockState.NoKey));

            progress?.Report(new MifareProgress(s + 1, sectors, recovered));
        }

        return dump;
    }

    /// <summary>How many times a single RF exchange is attempted before it counts as failed.</summary>
    private const int RfAttempts = 4;

    /// <summary>
    /// Authenticates with a key that is expected to work, retrying transient RF failures
    /// (a card slightly off the antenna drops individual exchanges).
    /// </summary>
    private async Task<bool> AuthWithRetryAsync(int block, MfKeyType keyType, byte[] key, CancellationToken ct)
    {
        for (var i = 0; i < RfAttempts; i++)
        {
            ct.ThrowIfCancellationRequested();
            try { if (await Mf1AuthAsync(block, keyType, key, ct)) return true; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* retry */ }
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
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* retry */ }
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
            await Mf1WriteEmuBlockDataAsync(runStart, run.ToArray(), ct);
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
        return _conn.SendExpectAsync(Command.Hf14aSetAntiCollData, Status.Success, data, ct: ct);
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
            _ => throw new NotSupportedException($"{type.DisplayName()} cannot be emulated."),
        };
        var len = type.LfIdLength();
        if (len > 0 && id.Length != len)
            throw new ArgumentException($"{type.DisplayName()} ID must be {len} bytes (got {id.Length}).");
        return _conn.SendExpectAsync(cmd, Status.Success, id, ct: ct);
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
        return _conn.SendExpectAsync(Command.Mf0NtagWriteEmuPageData, Status.Success, data, timeoutMs: 5000, ct: ct);
    }

    public Task Mf0SetVersionDataAsync(byte[] version, CancellationToken ct = default) =>
        version.Length == 8
            ? _conn.SendExpectAsync(Command.Mf0NtagSetVersionData, Status.Success, version, ct: ct)
            : throw new ArgumentException("Ultralight version must be 8 bytes.");

    public Task Mf0SetSignatureDataAsync(byte[] signature, CancellationToken ct = default) =>
        signature.Length == 32
            ? _conn.SendExpectAsync(Command.Mf0NtagSetSignatureData, Status.Success, signature, ct: ct)
            : throw new ArgumentException("Ultralight signature must be 32 bytes.");

    public Task Mf0SetCounterAsync(int index, int value, bool resetTearing, CancellationToken ct = default) =>
        _conn.SendExpectAsync(Command.Mf0NtagSetCounterData, Status.Success, new[]
        {
            (byte)(index | (resetTearing ? 0x80 : 0)),
            (byte)(value & 0xFF), (byte)((value >> 8) & 0xFF), (byte)((value >> 16) & 0xFF),
        }, ct: ct);

    public Task Mf0ResetAuthCountAsync(CancellationToken ct = default) =>
        _conn.SendExpectAsync(Command.Mf0NtagResetAuthCnt, Status.Success, ct: ct);

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
                var atqa = card.Atqa.Length == 2 ? card.Atqa : new byte[] { 0x00, 0x04 };
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
            await Mf1WriteEmuBlockDataAsync(runStart, run.ToArray(), ct);
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
            await Mf0WriteEmuPagesAsync(page, chunk.ToArray(), ct);
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

    /// <summary>Reads everything a slot holds (both cards with their nick, LED colour and enable flag, and the emulator settings).</summary>
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
    /// another slot): each card is uploaded with its own nick, LED colour and enable flag, an empty
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
            // The upload names and colours the slot after the card; put back what the slot had.
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
            _ => throw new NotSupportedException($"{type.DisplayName()} cannot be read from a slot."),
        };
        var f = await _conn.SendExpectAsync(cmd, Status.Success, ct: ct);
        // EM410x answers type[2] | id, the other LF types the bare id.
        return type.IsEm410x() && f.Data.Length > 2 ? f.Data[2..] : f.Data;
    }

    private async Task ReadHf14aAntiCollAsync(CardSave card, CancellationToken ct)
    {
        // uidlen[1]|uid[uidlen]|atqa[2]|sak[1]|atslen[1]|ats[atslen]
        var d = (await _conn.SendExpectAsync(Command.Hf14aGetAntiCollData, Status.Success, ct: ct)).Data;
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
            var count = await _conn.SendExpectAsync(Command.Mf0NtagGetPageCount, Status.Success, ct: ct);
            if (count.Data.Length > 0 && count.Data[0] > 0) total = count.Data[0];
        }
        catch (ChameleonException) { /* fall back to the nominal page count */ }

        const int pagesPerFrame = 16;
        for (var page = 0; page < total; page += pagesPerFrame)
        {
            var n = Math.Min(pagesPerFrame, total - page);
            var data = (await _conn.SendExpectAsync(Command.Mf0NtagReadEmuPageData, Status.Success,
                new[] { (byte)page, (byte)n }, timeoutMs: 5000, ct: ct)).Data;
            for (var i = 0; i < n; i++)
                card.Data.Add(data.Length >= (i + 1) * 4 ? data[(i * 4)..((i + 1) * 4)] : new byte[4]);
            progress?.Report(new MifareProgress(page + n, total, page + n));
        }

        // Version / signature / counters exist only on some types; the firmware
        // answers INVALID_SLOT_TYPE for the rest.
        var version = await _conn.SendAsync(Command.Mf0NtagGetVersionData, ct: ct);
        if (version.Status == Status.Success && version.Data.Length == 8)
            card.Extra.UltralightVersion = version.Data;
        var signature = await _conn.SendAsync(Command.Mf0NtagGetSignatureData, ct: ct);
        if (signature.Status == Status.Success && signature.Data.Length == 32)
            card.Extra.UltralightSignature = signature.Data;
        if (tag.HasUltralightCounters())
        {
            for (var i = 0; i < 3; i++)
            {
                var ctr = await _conn.SendAsync(Command.Mf0NtagGetCounterData, new[] { (byte)i }, ct: ct);
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
            for (var i = 0; i < total; i++) data[i] = Array.Empty<byte>();
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
                var f = await _conn.SendAsync(Command.Mf0UlcRead, req, timeoutMs: 4000, ct: ct);
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
                data[cfg0 + 3] = new byte[] { pack[0], pack[1], data[cfg0 + 3][2], data[cfg0 + 3][3] };
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
                Data = new List<byte[]>(data),
            };

            if (version.Length == 8)
            {
                card.Extra.UltralightVersion = version;
                var sig = await UltralightCommandAsync(new byte[] { 0x3C, 0x00 }, ct);
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
                    var c = await UltralightCommandAsync(new byte[] { 0x39, index }, ct, pwd: pwd);
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
        var version = await UltralightCommandAsync(new byte[] { 0x60 }, ct);
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
        var auth = await UltralightCommandAsync(new byte[] { 0x1A, 0x00 }, ct);
        return (auth.Length > 0 ? TagSpecificType.Mf0Icu2 : TagSpecificType.Mf0Icu1, Array.Empty<byte>());
    }

    // ---- Ultralight / NTAG write --------------------------------------

    /// <summary>
    /// Writes the user pages of a saved Ultralight / NTAG card onto the card on the reader with
    /// the standard WRITE command. The UID pages, lock bytes, capability container / OTP and the
    /// configuration pages are left untouched, so a write can never lock or misconfigure the card.
    /// Returns the pages the card refused.
    /// </summary>
    /// <param name="pwd">Optional 4-byte PWD of the target card when its pages are write-protected.</param>
    public async Task<IReadOnlyList<int>> WriteUltralightUserPagesAsync(
        CardSave card, byte[]? pwd = null,
        IProgress<MifareProgress>? progress = null, CancellationToken ct = default)
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
        var pages = Enumerable.Range(first, Math.Max(0, Math.Min(end, card.Data.Count) - first))
            .Where(p => card.Data[p].Length == 4)
            .ToList();

        var failed = new List<int>();
        var written = 0;
        try
        {
            if (pwd is not null && (await UltralightCommandAsync(PwdAuth(pwd), ct)).Length != 2)
                throw new ChameleonException("The card did not accept the password.", Status.HfTagNo);

            bool live = pwd is not null; // still selected (and authenticated) after PWD_AUTH
            for (var i = 0; i < pages.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var page = pages[i];
                var ok = await UltralightWritePageAsync(page, card.Data[page], live, pwd, ct);
                live = ok;
                if (ok) written++; else failed.Add(page);
                progress?.Report(new MifareProgress(i + 1, pages.Count, written));
            }
        }
        finally
        {
            await FieldOffAsync(CancellationToken.None);
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
                        await Hf14aRawAsync(opt | RawOpt.AutoSelect | RawOpt.CheckResponseCrc, new byte[] { 0x30, 0x00 }, ct: ct);
                    else if ((await Hf14aRawAsync(opt | RawOpt.AutoSelect | RawOpt.CheckResponseCrc, PwdAuth(pwd), ct: ct)).Length != 2)
                        continue;
                }
                // The 4-bit ACK comes without CRC.
                var r = await Hf14aRawAsync(opt, cmd, ct: ct);
                if (r.Length > 0 && (r[0] & 0x0f) == 0x0a) return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* retry */ }
        }
        return false;
    }

    private static byte[] PwdAuth(byte[] pwd) => new byte[] { 0x1B, pwd[0], pwd[1], pwd[2], pwd[3] };

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
            return pack.Length == 2 ? await Hf14aRawAsync(opt, command, ct: ct) : Array.Empty<byte>();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return Array.Empty<byte>(); }
    }

    /// <summary>READ of four pages from <paramref name="page"/>, retrying transient RF failures; null if refused.</summary>
    /// <remarks>With <paramref name="live"/> the first attempt reuses the selected card.</remarks>
    private async Task<byte[]?> UltralightReadAsync(int page, bool live, byte[]? pwd, CancellationToken ct)
    {
        for (var i = 0; i < RfAttempts; i++)
        {
            ct.ThrowIfCancellationRequested();
            var r = await UltralightCommandAsync(new byte[] { 0x30, (byte)page }, ct, reselect: !(live && i == 0), pwd);
            if (r.Length == 16) return r;
        }
        return null;
    }

    /// <summary>Switches the reader field off (an empty raw frame without keep-field).</summary>
    private async Task FieldOffAsync(CancellationToken ct)
    {
        try { await Hf14aRawAsync(RawOpt.None, Array.Empty<byte>(), ct: ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { /* best effort */ }
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
        var f = await _conn.SendAsync(Command.Hf14aRaw, buf, timeoutMs: timeoutMs + 1500, ct: ct);
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
                new byte[] { 0x40 }, bitLen: 7, ct: ct);
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
                new byte[] { 0x30, 0x00 }, ct: ct);
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
            await Hf14aRawAsync(RawOpt.AppendCrc, new byte[] { 0x50, 0x00 }, ct: ct);
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
    /// Returns the blocks that could not be written.
    /// </summary>
    public Task<IReadOnlyList<int>> WriteMifareDumpToCardAsync(
        MifareDump dump, IReadOnlyList<byte[]> keyDict, MagicGeneration gen = MagicGeneration.Gen2,
        IProgress<MifareProgress>? progress = null, CancellationToken ct = default)
        => gen switch
        {
            MagicGeneration.Gen1a => WriteGen1aAsync(dump, progress, ct),
            MagicGeneration.Gen3 => WriteGen2Or3Async(dump, keyDict, backdoorBlock0: true, progress, ct),
            _ => WriteGen2Or3Async(dump, keyDict, backdoorBlock0: false, progress, ct),
        };

    /// <summary>Gen2 / Gen3 shared path: auth + write each block, with Gen3's backdoor used for block 0.</summary>
    private async Task<IReadOnlyList<int>> WriteGen2Or3Async(
        MifareDump dump, IReadOnlyList<byte[]> keyDict, bool backdoorBlock0,
        IProgress<MifareProgress>? progress, CancellationToken ct)
    {
        await SetDeviceModeAsync(DeviceMode.Reader, ct);
        var tags = await Hf14aScanAsync(ct);
        if (tags.Count == 0)
            throw new ChameleonException("No HF tag on the reader.", Status.HfTagNo);

        var failed = new List<int>();
        var total = dump.BlockCount;
        var written = 0;

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
                        catch { /* try the next key */ }
                    }
                }

                if (ok) written++; else failed.Add(block);
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
        catch { /* the card reboots without answering */ }

        // The card reboots after the backdoor write; give it a moment, then confirm by UID.
        await Task.Delay(500, ct);
        try
        {
            var tags = await Hf14aScanAsync(ct);
            return tags.Count > 0 && tags[0].Uid.AsSpan().SequenceEqual(newUid);
        }
        catch { return false; }
    }

    /// <summary>Gen1a path: write every block raw with no authentication (block 0 included).</summary>
    private async Task<IReadOnlyList<int>> WriteGen1aAsync(
        MifareDump dump, IProgress<MifareProgress>? progress, CancellationToken ct)
    {
        await SetDeviceModeAsync(DeviceMode.Reader, ct);

        var failed = new List<int>();
        var total = dump.BlockCount;
        var written = 0;

        try
        {
            for (var block = 0; block < dump.BlockCount; block++)
            {
                ct.ThrowIfCancellationRequested();
                var data = dump.GetBlock(block);
                if (data is null || data.Length != 16) continue;

                var ok = await Gen1aWriteBlockAsync(block, data, ct);
                if (ok) written++; else failed.Add(block);
                progress?.Report(new MifareProgress(block + 1, total, written));
            }
        }
        finally
        {
            await DropFieldAsync(ct);
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
                    new byte[] { 0x00 }, ct: ct);
                // Magic wakeup, keeping the field between frames; no CRC, no auto-select.
                await Hf14aRawAsync(baseOpt | RawOpt.KeepRfField, new byte[] { 0x40 }, bitLen: 7, ct: ct);
                await Hf14aRawAsync(baseOpt | RawOpt.KeepRfField, new byte[] { 0x43 }, ct: ct);
                // Write command and payload (CRC appended, field kept).
                await Hf14aRawAsync(baseOpt | RawOpt.AppendCrc | RawOpt.KeepRfField,
                    new byte[] { 0xA0, (byte)block }, ct: ct);
                var output = await Hf14aRawAsync(baseOpt | RawOpt.AppendCrc | RawOpt.KeepRfField, data, ct: ct);
                if (output.Length > 0 && (output[0] & 0x0f) == 0x0a) return true;
                await Task.Delay(100, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch { /* retry */ }
        }
        return false;
    }

    // ---- Settings ------------------------------------------------------

    public async Task<AnimationMode> GetAnimationModeAsync(CancellationToken ct = default)
    {
        var f = await _conn.SendExpectAsync(Command.GetAnimationMode, Status.Success, ct: ct);
        return (AnimationMode)(f.Data.Length > 0 ? f.Data[0] : 0);
    }

    public Task SetAnimationModeAsync(AnimationMode mode, CancellationToken ct = default) =>
        _conn.SendExpectAsync(Command.SetAnimationMode, Status.Success, new[] { (byte)mode }, ct: ct);

    public async Task<int> GetSleepTimeoutAsync(CancellationToken ct = default)
    {
        var f = await _conn.SendExpectAsync(Command.GetSleepTimeout, Status.Success, ct: ct);
        return f.Data.Length > 0 ? f.Data[0] : 0;
    }

    public Task SetSleepTimeoutAsync(int seconds, CancellationToken ct = default) =>
        _conn.SendExpectAsync(Command.SetSleepTimeout, Status.Success, new[] { (byte)seconds }, ct: ct);

    public async Task<ButtonPressFunction> GetButtonConfigAsync(ButtonType button, bool longPress, CancellationToken ct = default)
    {
        var cmd = longPress ? Command.GetLongButtonPressConfig : Command.GetButtonPressConfig;
        var f = await _conn.SendExpectAsync(cmd, Status.Success, new[] { (byte)button }, ct: ct);
        return (ButtonPressFunction)(f.Data.Length > 0 ? f.Data[0] : 0);
    }

    public Task SetButtonConfigAsync(ButtonType button, bool longPress, ButtonPressFunction fn, CancellationToken ct = default)
    {
        var cmd = longPress ? Command.SetLongButtonPressConfig : Command.SetButtonPressConfig;
        return _conn.SendExpectAsync(cmd, Status.Success, new[] { (byte)button, (byte)fn }, ct: ct);
    }

    public Task SaveSettingsAsync(CancellationToken ct = default) =>
        _conn.SendExpectAsync(Command.SaveSettings, Status.Success, ct: ct);

    public Task ResetSettingsAsync(CancellationToken ct = default) =>
        _conn.SendExpectAsync(Command.ResetSettings, Status.Success, ct: ct);

    public async Task<int> GetLongPressThresholdAsync(CancellationToken ct = default)
    {
        var f = await _conn.SendExpectAsync(Command.GetLongPressThreshold, Status.Success, ct: ct);
        return f.Data.Length >= 2 ? BinaryPrimitives.ReadUInt16BigEndian(f.Data) : 0;
    }

    public Task SetLongPressThresholdAsync(int ms, CancellationToken ct = default)
    {
        var data = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(data, (ushort)ms);
        return _conn.SendExpectAsync(Command.SetLongPressThreshold, Status.Success, data, ct: ct);
    }

    // ---- BLE -----------------------------------------------------------

    public async Task<bool> GetBlePairingEnableAsync(CancellationToken ct = default)
    {
        var f = await _conn.SendExpectAsync(Command.GetBlePairingEnable, Status.Success, ct: ct);
        return f.Data.Length > 0 && f.Data[0] != 0;
    }

    public Task SetBlePairingEnableAsync(bool enabled, CancellationToken ct = default) =>
        _conn.SendExpectAsync(Command.SetBlePairingEnable, Status.Success, new[] { (byte)(enabled ? 1 : 0) }, ct: ct);

    public async Task<string> GetBlePairingKeyAsync(CancellationToken ct = default)
    {
        var f = await _conn.SendExpectAsync(Command.GetBlePairingKey, Status.Success, ct: ct);
        return Encoding.ASCII.GetString(f.Data);
    }

    public Task SetBlePairingKeyAsync(string pin, CancellationToken ct = default)
    {
        var bytes = Encoding.ASCII.GetBytes(pin);
        if (bytes.Length != 6) throw new ArgumentException("The BLE PIN must be exactly 6 digits.");
        return _conn.SendExpectAsync(Command.SetBlePairingKey, Status.Success, bytes, ct: ct);
    }

    public Task DeleteAllBleBondsAsync(CancellationToken ct = default) =>
        _conn.SendExpectAsync(Command.DeleteAllBleBonds, Status.Success, ct: ct);

    /// <summary>Factory reset. The device closes the connection afterwards.</summary>
    public Task WipeFdsAsync(CancellationToken ct = default) =>
        _conn.SendExpectAsync(Command.WipeFds, Status.Success, timeoutMs: 5000, ct: ct);

    public void EnterBootloader() =>
        _ = _conn.SendAsync(Command.EnterBootloader, timeoutMs: 500);
}
