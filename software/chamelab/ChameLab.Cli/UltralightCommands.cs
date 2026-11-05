using System.Buffers.Binary;
using ChameLab.Models;
using ChameLab.Protocol;

namespace ChameLab.Cli;

public sealed partial class CliSession
{
    private partial void RegisterUltralight()
    {
        Add("hf mfu rdpg", async (a, ct) => Print(await ReadUltralightPage(a, a.Int("page"), ct)));
        Add("hf mfu wrpg", async (a, ct) =>
        {
            var key = UlKey(a); var page = B(a.Int("page")); var data = a.Hex("data", 4);
            if (key is { Length: 16 }) await Wire(2021, [.. key, page, .. data], ct);
            else
            {
                var r = await UltralightRaw([0xA2, page, .. data], key, ct, checkCrc: false);
                if (r.Length == 0 || r[0] != 0x0A) throw new IOException("Page write rejected.");
            }
            Print("Page written.");
        });
        Add("hf mfu setkey", async (a, ct) => { await Wire(2022, [.. a.Hex("key", 16), .. a.Hex("new_key", 16)], ct); Print("Key updated."); });
        Add("hf mfu version", async (_, ct) => Print(await RawHf([0x60], ct)));
        Add("hf mfu signature", async (_, ct) => Print(await RawHf([0x3C, 0], ct)));
        Add("hf mfu authnonce", async (_, ct) =>
        {
            var r = await RawHf([0x1A, 0], ct);
            if (r.Length < 9 || r[0] != 0xAF) throw new IOException("Unexpected Ultralight C authentication response.");
            Print(r[1..9]);
        });
        Add("hf mfu rcnt", async (a, ct) =>
        {
            var key = UlKey(a);
            if (key is { Length: 16 }) throw new ArgumentException("Ultralight C does not support rcnt.");
            var r = await UltralightRaw([0x39, B(Range(a.Int("counter"), 0, 2, "counter"))], key, ct);
            if (r.Length != 3) throw new IOException("Invalid counter response."); Print(r[0] | r[1] << 8 | r[2] << 16);
        });
        Add("hf mfu ercnt", async (a, ct) =>
        {
            var r = await Wire(4027, [B(Range(a.Int("counter"), 0, 2, "counter"))], ct);
            if (r.Length != 4) throw new InvalidDataException("Invalid emulator counter.");
            Print($"Value: {r[0] | r[1] << 8 | r[2] << 16}; tearing flag: {r[3]:X2}");
        });
        Add("hf mfu ewcnt", async (a, ct) => await Client.Mf0SetCounterAsync(Range(a.Int("counter"), 0, 2, "counter"), Range(a.Int("value"), 0, 0xFFFFFF, "value"), a.Flag("reset_tearing"), ct));
        Add("hf mfu eview", async (_, ct) => Dump(await ReadUltralightEmulator(ct), 4));
        Add("hf mfu esave", async (a, ct) => SaveDump(a.Text("file"), await ReadUltralightEmulator(ct), a.Get("type"), 4));
        Add("hf mfu eload", async (a, ct) =>
        {
            var data = LoadDump(a.Text("file"), a.Get("type"));
            var count = (await Wire(4030, null, ct))[0];
            if (data.Length == 0 || data.Length % 4 != 0 || data.Length > count * 4) throw new ArgumentException("Dump is not aligned to pages or exceeds slot capacity.");
            for (var i = 0; i < data.Length; i += 480) await Client.Mf0WriteEmuPagesAsync(i / 4, data[i..Math.Min(i + 480, data.Length)], ct);
            Print("Emulator data loaded; use hw slot store to persist it.");
        });
        Add("hf mfu dump", async (a, ct) =>
        {
            var start = Range(a.Int("page"), 0, 255, "page"); byte[] data; var incomplete = false;
            if (a.Has("qty"))
            {
                var count = Range(a.Int("qty"), 1, 256 - start, "qty"); var result = new List<byte>();
                for (var p = start; p < start + count; p++) result.AddRange(await ReadUltralightPage(a, p, ct));
                data = result.ToArray();
            }
            else
            {
                var dump = await Client.DumpUltralightAsync(UlKey(a), ct: ct);
                data = dump.Card.Data.Skip(start).SelectMany(s => s.Length == 0 ? new byte[4] : s).ToArray();
                incomplete = dump.PagesRead < dump.PageCount;
                if (incomplete) Error.WriteLine($"Partial dump: {dump.PagesRead}/{dump.PageCount} pages; unread pages contain zeros.");
            }
            Dump(data, 4);
            if (!string.IsNullOrEmpty(a.Get("file"))) SaveDump(a.Text("file"), data, a.Get("type"), 4);
            if (incomplete) throw new IOException("The Ultralight dump is incomplete.");
        });
        Add("hf mfu econfig", async (a, ct) =>
        {
            await UpdateIdentity(a, ct);
            if (a.Flag("enable_uid_magic") || a.Flag("disable_uid_magic")) await Client.SetMf0UidMagicModeAsync(a.Flag("enable_uid_magic"), ct);
            if (a.Has("write")) await Client.SetMf0WriteModeAsync((EmulatorWriteMode)CommandCatalog.EnumValue("MifareUltralightWriteMode", a.Text("write")), ct);
            if (a.Flag("enable_log") || a.Flag("disable_log")) await Client.SetMf0DetectionEnableAsync(a.Flag("enable_log"), ct);
            if (a.Has("set_version")) await Client.Mf0SetVersionDataAsync(a.Hex("set_version", 8), ct);
            if (a.Has("set_signature")) await Client.Mf0SetSignatureDataAsync(a.Hex("set_signature", 32), ct);
            if (a.Flag("reset_auth_cnt")) await Client.Mf0ResetAuthCountAsync(ct);
            Json(await Client.GetHf14aEmulatorIdentityAsync(ct));
            Print($"UID magic: {await Client.GetMf0UidMagicModeAsync(ct)}; write mode: {await Client.GetMf0WriteModeAsync(ct)}; detection: {await Client.GetMf0DetectionEnableAsync(ct)}");
        });
        Add("hf mfu edetect", async (a, ct) =>
        {
            var countBytes = await Wire(4034, null, ct);
            if (countBytes.Length != 4) throw new InvalidDataException("Invalid detection count.");
            var total = checked((int)BinaryPrimitives.ReadUInt32BigEndian(countBytes));
            var index = Range(a.Int("index"), 0, total, "index");
            var end = index + Math.Min(Range(a.Int("count", total - index), 0, int.MaxValue, "count"), total - index);
            while (index < end)
            {
                var data = await Wire(4035, U32((uint)index), ct);
                if (data.Length == 0 || data.Length % 4 != 0) throw new InvalidDataException("Invalid password detection log.");
                foreach (var pwd in data.Chunk(4)) { if (index >= end) break; Print($"{index++}: {Convert.ToHexString(pwd)}"); }
            }
        });
        Add("hf mfu ulcg", UlcgAsync);
    }

    private static byte[]? UlKey(CliArguments a)
    {
        if (!a.Has("key")) return null;
        var key = a.Hex("key");
        if (key.Length is not (4 or 16)) throw new ArgumentException("Key must contain four or sixteen bytes.");
        if (a.Flag("swap_endian")) Array.Reverse(key);
        return key;
    }
    private async Task<byte[]> ReadUltralightPage(CliArguments a, int page, CancellationToken ct)
    {
        var key = UlKey(a); B(page);
        var r = key is { Length: 16 } ? await Wire(2019, [.. key, B(page), 1], ct)
            : await UltralightRaw([0x30, B(page)], key, ct);
        if (r.Length < 4) throw new IOException("Page read rejected.");
        return r[..4];
    }
    private async Task<byte[]> UltralightRaw(byte[] command, byte[]? pwd, CancellationToken ct, bool checkCrc = true)
    {
        if (pwd is not null)
        {
            var pack = await RawHf([0x1B, .. pwd], ct, keep: true);
            if (pack.Length < 2) throw new IOException("Password authentication failed.");
        }
        return await Wire(2010, [B(0x60 | (pwd is null ? 0x10 : 0) | (checkCrc ? 4 : 0)), .. U16(1000), .. U16(command.Length * 8), .. command], ct);
    }
    private async Task<byte[]> ReadUltralightEmulator(CancellationToken ct)
    {
        var count = (await Wire(4030, null, ct))[0]; var result = new List<byte>();
        for (var page = 0; page < count; page += 120) result.AddRange(await Wire(4021, [B(page), B(Math.Min(120, count - page))], ct));
        return result.ToArray();
    }
}
