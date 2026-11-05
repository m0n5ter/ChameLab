using System.Buffers.Binary;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;

namespace ChameLab.Cli;

public sealed partial class CliSession
{
    private partial void RegisterMifare()
    {
        Add("hf mf rdbl", async (a, ct) => Print(await Client.Mf1ReadBlockAsync(B(a.Int("blk")), KeyType(a), a.Hex("key", 6), ct)));
        Add("hf mf wrbl", async (a, ct) =>
        {
            if (!await Client.Mf1WriteBlockAsync(B(a.Int("blk")), KeyType(a), a.Hex("key", 6), a.Hex("data", 16), ct)) throw new IOException("Block write failed.");
            Print("Block written.");
        });
        Add("hf mf nested", NestedAsync);
        Add("hf mf hardnested", HardnestedAsync);
        Add("hf mf darkside", async (_, ct) => Print(Convert.ToHexString(await DarksideAsync(ct))));
        Add("hf mf senested", StaticEncryptedAsync);
        Add("hf mf autopwn", AutopwnAsync);
        Add("hf mf fchk", CheckKeysAsync);
        Add("hf mf value", ValueBlockAsync);
        Add("hf mf dump", async (a, ct) =>
        {
            var dump = await Client.ReadMifareClassicAsync(ReadDictionary(a.Text("dic")), new DirectProgress<MifareProgress>(p => Print($"Sectors {p.Done}/{p.Total}; blocks {p.Recovered}")), ct);
            SaveDump(a.Text("dump_file"), dump.ToBin(), a.Get("dump_file_type"), 16);
            Print($"Saved {dump.RecoveredBlocks}/{dump.BlockCount} blocks to {a.Text("dump_file")}");
            if (dump.RecoveredBlocks != dump.BlockCount) throw new IOException("The saved dump is incomplete; unread blocks contain zeros.");
        });
        Add("hf mf clone", CloneMifareAsync);
        Add("hf mf view", async (a, ct) =>
        {
            if (a.Has("dump_file")) { Dump(LoadDump(a.Text("dump_file")), 16); return; }
            if (!a.Has("key_file")) throw new ArgumentException("Specify --dump-file or --key-file.");
            var sectors = a.Int("maxSectors", 16);
            var keys = ReadSectorKeys(File.ReadAllBytes(a.Text("key_file")), sectors);
            var blocks = MifareClassic.SectorFirstBlock(sectors);
            var bytes = new List<byte>();
            for (var block = 0; block < blocks; block++)
            {
                var s = MifareClassic.SectorOfBlock(block);
                try { bytes.AddRange(await Client.Mf1ReadBlockAsync(block, MfKeyType.B, keys[s].B, ct)); }
                catch (ChameleonException) { bytes.AddRange(await Client.Mf1ReadBlockAsync(block, MfKeyType.A, keys[s].A, ct)); }
            }
            Dump(bytes.ToArray(), 16);
        });
        Add("hf mf eload", async (a, ct) =>
        {
            var data = LoadDump(a.Text("file"), a.Get("type"));
            if (data.Length == 0 || data.Length % 16 != 0 || data.Length > 4096) throw new ArgumentException("MIFARE dump must contain 1–256 complete blocks.");
            for (var i = 0; i < data.Length; i += 16 * 30) await Client.Mf1WriteEmuBlockDataAsync(i / 16, data[i..Math.Min(i + 16 * 30, data.Length)], ct);
            Print("Emulator data loaded; use hw slot store to persist it.");
        });
        Add("hf mf esave", async (a, ct) => SaveDump(a.Text("file"), await ReadMifareEmulator(ct), a.Get("type"), 16));
        Add("hf mf eview", async (_, ct) => Dump(await ReadMifareEmulator(ct), 16));
        Add("hf mf econfig", async (a, ct) =>
        {
            await UpdateIdentity(a, ct);
            async Task Set(string on, string off, Func<bool, CancellationToken, Task> set)
            { if (a.Flag(on) || a.Flag(off)) await set(a.Flag(on), ct); }
            await Set("enable_gen1a", "disable_gen1a", Client.SetMf1Gen1aModeAsync);
            await Set("enable_gen2", "disable_gen2", Client.SetMf1Gen2ModeAsync);
            await Set("enable_block0", "disable_block0", Client.SetMf1BlockAntiCollModeAsync);
            await Set("enable_log", "disable_log", Client.SetMf1DetectionEnableAsync);
            await Set("enable_field_off_do_reset", "disable_field_off_do_reset", Client.SetMf1FieldOffDoResetAsync);
            if (a.Has("write")) await Client.SetMf1WriteModeAsync((EmulatorWriteMode)CommandCatalog.EnumValue("MifareClassicWriteMode", a.Text("write")), ct);
            Json(await Client.GetMf1EmulatorConfigAsync(ct)); Json(await Client.GetHf14aEmulatorIdentityAsync(ct));
        });
        Add("hf mf elog", async (a, ct) =>
        {
            var logs = await Client.GetAllMf1DetectionLogsAsync(ct);
            foreach (var log in logs) Print($"Block {log.Block} {log.KeyChar} UID={log.UidHex} NT={log.NtHex} NR={log.NrHex} AR={log.ArHex}");
            if (a.Flag("decrypt"))
                foreach (var group in logs.GroupBy(l => (l.UidHex, l.Block, l.KeyType)))
                {
                    var entries = group.ToArray();
                    for (var i = 0; i < entries.Length; i++) for (var j = i + 1; j < entries.Length; j++)
                        if (Mfkey32Recovery.Recover(entries[i], entries[j]) is { } key) { Print($"{group.Key}: {key:X12}"); goto next; }
                    next: ;
                }
        });
    }

    private async Task<byte[]> ReadMifareEmulator(CancellationToken ct)
    {
        var active = await Client.GetActiveSlotAsync(ct);
        var slot = (await Client.GetAllSlotsAsync(ct)).Single(s => s.Index == active);
        var blocks = slot.HfType.MifareClassicBlocks();
        if (blocks == 0) throw new InvalidOperationException("The active slot is not MIFARE Classic.");
        var data = new List<byte>();
        for (var b = 0; b < blocks; b += 30) data.AddRange(await Client.Mf1ReadEmuBlockDataAsync(b, Math.Min(30, blocks - b), ct));
        return data.ToArray();
    }

    private async Task NestedAsync(CliArguments a, CancellationToken ct)
    {
        var block = B(a.Int("blk")); var target = B(a.Int("tblk")); var key = a.Hex("key", 6);
        var kind = await Client.DetectNtLevelAsync(ct);
        IReadOnlyList<ulong> keys;
        if (kind == MifareClassicPrngType.Hard) throw new InvalidOperationException("Use hf mf hardnested for this card.");
        if (kind == MifareClassicPrngType.Static)
        {
            var capture = await Client.GetStaticNestedNoncesAsync(block, KeyType(a), key, target, KeyType(a, "t"), ct);
            keys = await Task.Run(() => StaticNestedRecovery.Recover(capture.Uid, KeyType(a), capture.Nonces[0].Nt, capture.Nonces[0].NtEnc, capture.Nonces[1].Nt, capture.Nonces[1].NtEnc, ct), ct);
        }
        else
        {
            var d = await Client.GetNtDistanceAsync(block, KeyType(a), key, ct);
            var n = await Client.GetNestedNoncesAsync(block, KeyType(a), key, target, KeyType(a, "t"), ct: ct);
            keys = await Task.Run(() => NestedRecovery.Recover(d.Uid, d.Distance, n[0].Nt, n[0].NtEnc, n[0].Parity, n[1].Nt, n[1].NtEnc, n[1].Parity, ct), ct);
        }
        var found = await Client.CheckMifareKeysOnBlockAsync(target, KeyType(a, "t"), keys.Select(KeyBytes).ToArray(), ct);
        Print(found ?? throw new InvalidOperationException("No candidate authenticated against the card."));
    }

    private static byte[] KeyBytes(ulong key) => Convert.FromHexString(key.ToString("X12"));
    private async Task<byte[]> DarksideAsync(CancellationToken ct)
    {
        var captures = new List<DarksideItem>(); uint? uid = null;
        for (var i = 0; i < 32; i++)
        {
            var c = await Client.GetDarksideAsync(3, MfKeyType.B, i == 0, 30, ct);
            if (uid is { } previous && c.Uid != previous) throw new InvalidOperationException("Card changed during recovery.");
            uid = c.Uid; captures.Add(c.Item);
            var keys = await Task.Run(() => DarksideRecovery.Recover(c.Uid, captures, ct), ct);
            if (keys.Count > 0 && await Client.CheckMifareKeysOnBlockAsync(3, MfKeyType.B, keys.Select(KeyBytes).ToArray(), ct) is { } found) return found;
        }
        throw new InvalidOperationException("Darkside recovery did not find a key.");
    }

    private async Task HardnestedAsync(CliArguments a, CancellationToken ct)
    {
        var block = B(a.Int("blk")); var target = B(a.Int("tblk")); var key = a.Hex("key", 6);
        var tag = (await Client.Hf14aScanAsync(ct)).Single();
        var uid = BinaryPrimitives.ReadUInt32BigEndian(tag.Uid.AsSpan(tag.Uid.Length - 4));
        IHardnestedNonceSource source = new ChameleonHardnestedSource(Client, tag.Uid, block, KeyType(a), key, target, KeyType(a, "t"));
        if (a.Flag("slow")) source = new SlowNonceSource(source);
        var attempts = Range(a.Int("max_attempts"), 1, 100, "max-attempts");
        HardnestedCapture? capture = null;
        for (var i = 0; i < attempts; i++)
        {
            try { capture = await HardnestedCollector.CollectAsync(source, uid, new DirectProgress<string>(Print), ct, maximumBatches: Range(a.Int("max_runs"), 1, 10000, "max-runs"), maximumAttempts: 1); break; }
            catch (InvalidOperationException) when (i + 1 < attempts) { Print("Retrying acquisition."); }
        }
        if (capture is null) throw new InvalidOperationException("No usable nonces.");
        if (a.Flag("keep_nonce_file")) await File.WriteAllBytesAsync("nonces.bin", HardnestedCaptureCodec.EncodeBinary(capture, target, KeyType(a, "t") == MfKeyType.B), ct);
        var found = await new ManagedHardnestedEngine().RecoverAsync(capture, new DirectProgress<string>(Print), ct);
        if (found is null || !await Client.Mf1AuthAsync(target, KeyType(a, "t"), KeyBytes(found.Value), ct)) throw new InvalidOperationException("Recovered key could not be verified.");
        Print($"Key: {found:X12}");
    }
    private sealed class SlowNonceSource(IHardnestedNonceSource source) : IHardnestedNonceSource
    { public Task<IReadOnlyList<HardnestedSample>> ReadBatchAsync(bool slow, CancellationToken ct) => source.ReadBatchAsync(true, ct); }

    private async Task StaticEncryptedAsync(CliArguments a, CancellationToken ct)
    {
        var count = Range(a.Int("sectors"), 1, 40, "sectors"); var start = Range(a.Int("starting_sector"), 0, count - 1, "starting-sector");
        var capture = await Client.GetStaticEncryptedNestedNoncesAsync(a.Hex("key", 6), count, ct);
        for (var sector = start; sector < count; sector++)
        {
            var n = capture.Sectors[sector];
            var keysA = await Task.Run(() => StaticEncryptedNestedRecovery.Recover(capture.Uid, n.A, ct), ct);
            var keysB = await Task.Run(() => StaticEncryptedNestedRecovery.Recover(capture.Uid, n.B, ct), ct);
            foreach (var (type, nonce, keys, otherNonce, otherKeys) in new[] { (MfKeyType.A, n.A, keysA, n.B, keysB), (MfKeyType.B, n.B, keysB, n.A, keysA) })
            {
                var filtered = StaticEncryptedNestedRecovery.Filter(nonce.Nt, keys, otherNonce.Nt, otherKeys, ct);
                var ordered = filtered.Concat(keys.Except(filtered)).Select(KeyBytes).ToArray();
                var key = await Client.CheckMifareKeysOnBlockAsync(MifareClassic.SectorTrailerBlock(sector), type, ordered, ct);
                Print($"Sector {sector} {type}: {(key is null ? "not found" : Convert.ToHexString(key))}");
            }
        }
    }

    private async Task AutopwnAsync(CliArguments a, CancellationToken ct)
    {
        var tag = (await Client.Hf14aScanAsync(ct)).Single();
        var dump = new MifareDump(MifareClassic.FromSak(tag.Sak), tag.Uid, tag.Atqa, tag.Sak);
        var keys = MifareClassic.DefaultKeys.ToList(); if (a.Has("key")) keys.Insert(0, a.Hex("key", 6));
        await Client.FindMifareKeysAsync(dump, keys, ct: ct);
        var recovery = new MifareKeyRecovery(Client, new ManagedHardnestedEngine(), keyRecovery: new ManagedKeyRecoveryEngine());
        var progress = new DirectProgress<KeyRecoveryProgress>(p => Print(p.Status));
        if (dump.KeyA.All(k => k is null) && dump.KeyB.All(k => k is null))
        {
            try { await recovery.RecoverAsync(dump, MifareRecoveryMethod.Darkside, progress, ct); }
            catch (Exception ex) when (ex is InvalidOperationException or ChameleonException or NotSupportedException)
            { Print("Darkside did not recover a key: " + ex.Message); }
        }
        if (dump.KeyA.All(k => k is null) && dump.KeyB.All(k => k is null))
            await recovery.RecoverAsync(dump, MifareRecoveryMethod.StaticEncryptedNested, progress, ct);
        if (dump.KeyA.Any(k => k is null) || dump.KeyB.Any(k => k is null))
        {
            var type = await Client.DetectNtLevelAsync(ct);
            await recovery.RecoverAsync(dump, type switch { MifareClassicPrngType.Static => MifareRecoveryMethod.StaticNested, MifareClassicPrngType.Weak => MifareRecoveryMethod.Nested, _ => MifareRecoveryMethod.Hardnested }, progress, ct);
        }
        await File.WriteAllBytesAsync(dump.UidHex + ".key", Enumerable.Range(0, dump.SectorCount)
            .SelectMany(i => (dump.KeyA[i] ?? new byte[6]).Concat(dump.KeyB[i] ?? new byte[6])).ToArray(), ct);
        await File.WriteAllLinesAsync(dump.UidHex + ".dic", dump.KeyA.Concat(dump.KeyB).OfType<byte[]>().Select(Convert.ToHexString).Distinct(), ct);
        dump = await Client.ReadMifareClassicAsync(keys, ct: ct, existing: dump);
        var name = dump.UidHex + ".bin"; SaveDump(name, dump.ToBin(), "bin", 16); Print("Saved " + name);
        for (var i = 0; i < dump.SectorCount; i++) Print($"{i}: A={HexKey(dump.KeyA[i])} B={HexKey(dump.KeyB[i])}");
        if (dump.RecoveredBlocks != dump.BlockCount) throw new IOException("Recovery is incomplete; saved the partial dump.");
    }
    private static string HexKey(byte[]? key) => key is null ? "------------" : Convert.ToHexString(key);

    private async Task CheckKeysAsync(CliArguments a, CancellationToken ct)
    {
        var keys = a.Many("keys").Select(s => ParseKey(s)).ToList();
        if (a.Has("import_dic")) keys.AddRange(ReadDictionary(a.Text("import_dic")));
        if (a.Has("import_key"))
        {
            var bytes = File.ReadAllBytes(a.Text("import_key"));
            if (bytes.Length % 6 != 0) throw new ArgumentException("Key file must contain complete 6-byte keys.");
            keys.AddRange(bytes.Chunk(6));
        }
        keys = keys.DistinctBy(Convert.ToHexString).ToList();
        if (keys.Count == 0) throw new ArgumentException("Supply keys, --key FILE or --dic FILE.");
        var maskText = a.Text("mask"); if (maskText.Length is < 1 or > 20) throw new ArgumentException("Mask must contain 1–20 hex digits.");
        var mask = Convert.FromHexString(maskText.PadRight(20, '0')); var sectors = a.Int("maxSectors", 16);
        for (var s = sectors; s < 40; s++) mask[s / 4] |= B(3 << (6 - s % 4 * 2));
        var found = new Dictionary<int, byte[]>();
        foreach (var batch in keys.Chunk(83))
        {
            if (mask.All(b => b == 255)) break;
            ct.ThrowIfCancellationRequested();
            var unmasked = 80 - mask.Sum(b => System.Numerics.BitOperations.PopCount(b));
            var frame = await Client.Connection.SendAsync(Command.Mf1CheckKeysOfSectors, [.. mask, .. batch.SelectMany(k => k)], timeoutMs: 1000 + (unmasked + 1) * batch.Length * 100);
            ct.ThrowIfCancellationRequested();
            if (frame.Status != Status.HfTagOk || frame.Data.Length != 490) throw new ChameleonException("Key checking interrupted: " + frame.Status.Describe(), frame.Status);
            for (var bit = 0; bit < 80; bit++) if ((frame.Data[bit / 8] & (1 << (7 - bit % 8))) != 0)
                found[bit] = frame.Data[(10 + 6 * bit)..(16 + 6 * bit)];
            for (var i = 0; i < 10; i++) mask[i] |= frame.Data[i];
        }
        for (var s = 0; s < sectors; s++) Print($"{s}: A={HexKey(found.GetValueOrDefault(2 * s))} B={HexKey(found.GetValueOrDefault(2 * s + 1))}");
        if (a.Has("export_key")) await File.WriteAllBytesAsync(a.Text("export_key"), Enumerable.Range(0, sectors * 2).SelectMany(i => found.GetValueOrDefault(i) ?? new byte[6]).ToArray(), ct);
        if (a.Has("export_dic")) await File.WriteAllLinesAsync(a.Text("export_dic"), found.Values.Select(Convert.ToHexString).Distinct(), ct);
    }

    private async Task ValueBlockAsync(CliArguments a, CancellationToken ct)
    {
        var block = B(a.Int("blk")); var key = a.Hex("src_key", 6);
        if (a.Flag("get") || !new[] { "set", "inc", "dec", "res" }.Any(a.Has))
        {
            var bytes = await Client.Mf1ReadBlockAsync(block, KeyType(a), key, ct);
            if (!bytes.AsSpan(0, 4).SequenceEqual(bytes.AsSpan(8, 4)) || Enumerable.Range(0, 4).Any(i => (bytes[i] ^ bytes[i + 4]) != 255)) throw new InvalidDataException("Invalid value block.");
            Print(BinaryPrimitives.ReadInt32LittleEndian(bytes)); return;
        }
        if (a.Has("set"))
        {
            var bytes = new byte[16]; BinaryPrimitives.WriteInt32LittleEndian(bytes, a.Int("set"));
            for (var i = 0; i < 4; i++) { bytes[i + 4] = (byte)~bytes[i]; bytes[i + 8] = bytes[i]; }
            bytes[12] = bytes[14] = block; bytes[13] = bytes[15] = (byte)~block;
            if (!await Client.Mf1WriteBlockAsync(block, KeyType(a), key, bytes, ct)) throw new IOException("Value block write failed."); return;
        }
        var operand = a.Has("inc") ? a.Int("inc") : a.Int("dec"); if (operand < 0) throw new ArgumentException("Operand must be nonnegative.");
        var op = a.Has("inc") ? 0xC1 : a.Has("dec") ? 0xC0 : 0xC2;
        await Wire(2011, [(byte)KeyType(a), block, .. key, B(op), .. U32((uint)operand), (byte)(a.Has("ta") || a.Has("tb") ? KeyType(a, "t") : KeyType(a)), B(a.Int("tblk", block)), .. (a.Has("tkey") ? a.Hex("tkey", 6) : key)], ct);
    }

    private async Task CloneMifareAsync(CliArguments a, CancellationToken ct)
    {
        var bytes = LoadDump(a.Text("dump_file"), a.Get("dump_file_type"));
        if (bytes.Length is not (320 or 1024 or 2048 or 4096)) throw new ArgumentException("Expected a complete Mini, 1K, 2K or 4K dump.");
        var keys = ReadDictionary(a.Text("dic"));
        // Validate every trailer before the first card write, including trailers in later sectors.
        for (var block = 0; block < bytes.Length / 16; block++)
        {
            if (!MifareClassic.IsTrailer(block)) continue;
            var trailer = bytes.AsSpan(block * 16, 16);
            if (!a.Flag("clone_access")) { trailer[6] = 255; trailer[7] = 7; trailer[8] = 128; }
            if (!MifareClassic.AccessBitsValid(trailer)) throw new ArgumentException($"Invalid access bits for sector {MifareClassic.SectorOfBlock(block)}.");
        }
        for (var block = 0; block < bytes.Length / 16;)
        {
            var sector = MifareClassic.SectorOfBlock(block); var trailer = MifareClassic.SectorTrailerBlock(sector);
            var keyB = await Client.CheckMifareKeysOnBlockAsync(block, MfKeyType.B, keys, ct);
            var keyA = await Client.CheckMifareKeysOnBlockAsync(block, MfKeyType.A, keys, ct);
            if (keyA is null && keyB is null) throw new InvalidOperationException($"No key found for sector {sector}.");
            for (; block <= trailer; block++)
            {
                var data = bytes[(block * 16)..(block * 16 + 16)];
                if (keyB is not null && await Client.Mf1WriteBlockAsync(block, MfKeyType.B, keyB, data, ct)) continue;
                if (keyA is not null && await Client.Mf1WriteBlockAsync(block, MfKeyType.A, keyA, data, ct)) continue;
                throw new IOException($"Write failed at block {block}.");
            }
        }
        Print("Dump written.");
    }

    private static byte[] ParseKey(string value)
    { var bytes = Convert.FromHexString(value); return bytes.Length == 6 ? bytes : throw new ArgumentException("MIFARE keys must contain six bytes."); }
    internal static (byte[] A, byte[] B)[] ReadSectorKeys(byte[] bytes, int sectors)
    {
        if (bytes.Length == sectors * 12) return bytes.Chunk(12).Select(pair => (pair[..6], pair[6..])).ToArray();
        var lines = System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF').Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length != sectors) throw new ArgumentException($"Expected {sectors} pairs of sector keys.");
        return lines.Select(line =>
        {
            var pair = line.Trim().Split(':');
            if (pair.Length != 2) throw new ArgumentException("Text key files require A:B on each line.");
            return (ParseKey(pair[0]), ParseKey(pair[1]));
        }).ToArray();
    }
    private static byte[][] ReadDictionary(string path) => File.ReadLines(path).Select(s => s.Split('#')[0].Trim()).Where(s => s.Length != 0).Select(ParseKey).DistinctBy(Convert.ToHexString).ToArray();
    private static byte[] LoadDump(string path, string? type = null)
        => type == "hex" || type is null && Path.GetExtension(path).ToLowerInvariant() is ".eml" or ".hex"
            ? Convert.FromHexString(string.Concat(File.ReadAllText(path).Where(c => !char.IsWhiteSpace(c)))) : File.ReadAllBytes(path);
    private static void SaveDump(string path, byte[] bytes, string? type, int width)
    {
        if (string.IsNullOrEmpty(path)) throw new ArgumentException("An output file is required.");
        if (type == "hex" || type is null && Path.GetExtension(path).ToLowerInvariant() is ".eml" or ".hex") File.WriteAllLines(path, bytes.Chunk(width).Select(Convert.ToHexString));
        else File.WriteAllBytes(path, bytes);
    }
    private sealed class DirectProgress<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }
}
