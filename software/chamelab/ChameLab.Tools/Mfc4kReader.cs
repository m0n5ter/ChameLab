using System.Diagnostics;
using System.Text.Json;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;
using ChameLab.Transport.Serial;

namespace ChameLab.Tools;

internal static class Mfc4kReader
{
    public static async Task Run(string[] args)
    {
        if (args is ["--help"])
        {
            Console.WriteLine("read-mfc4k [--port COM8] [--key FFFFFFFFFFFF] [--dictionary FILE] [--output DIRECTORY] [--attempts 3]"); return;
        }
        var options = new Dictionary<string, string>();
        if (args.Length % 2 != 0) throw new ArgumentException("Expected --name VALUE pairs. Use read-mfc4k --help.");
        for (var i = 0; i < args.Length; i += 2)
        {
            if (args[i] is not ("--port" or "--key" or "--dictionary" or "--output" or "--attempts")) throw new ArgumentException("Unknown option: " + args[i]);
            options.Add(args[i], args[i + 1]);
        }
        byte[] Key(string hex) { var bytes = Convert.FromHexString(hex); return bytes.Length == 6 ? bytes : throw new ArgumentException("Keys must contain six bytes."); }
        var keys = new List<byte[]> { Key(options.GetValueOrDefault("--key", "FFFFFFFFFFFF")) };
        keys.AddRange(MifareClassic.DefaultKeys);
        if (options.TryGetValue("--dictionary", out var dictionary)) keys.AddRange(File.ReadLines(dictionary)
            .Select(l => l.Split('#', ';')[0].Trim()).Where(l => l.Length != 0).Select(Key));
        var attempts = int.Parse(options.GetValueOrDefault("--attempts", "3"));
        if (attempts is < 1 or > 10) throw new ArgumentException("Attempts must be between 1 and 10.");
        var directory = Path.Combine(Path.GetFullPath(options.GetValueOrDefault("--output", "mfc4k-results")), DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        var watch = Stopwatch.StartNew(); var report = new Dictionary<string, object?>();
        MifareDump? dump = null; double? readingStarted = null;
        var port = options.GetValueOrDefault("--port", "COM8"); report["port"] = port;
        async Task Save()
        {
            report["total_seconds"] = watch.Elapsed.TotalSeconds;
            if (readingStarted is { } started) report["reading_seconds"] = watch.Elapsed.TotalSeconds - started;
            if (dump is not null)
            {
                var missing = Enumerable.Range(0, dump.BlockCount).Where(i => dump.GetBlock(i) is null).ToArray();
                report["uid"] = dump.UidHex; report["unreadable_blocks"] = missing; report["dump_complete"] = missing.Length == 0;
                report["known_keys"] = dump.KeyA.Concat(dump.KeyB).Count(k => k is not null);
                report["missing_keys"] = Enumerable.Range(0, 80).Where(i => (i % 2 == 0 ? dump.KeyA[i / 2] : dump.KeyB[i / 2]) is null).Select(i => $"{i / 2}{(i % 2 == 0 ? 'A' : 'B')}").ToArray();
                await File.WriteAllBytesAsync(Path.Combine(directory, missing.Length == 0 ? "card-raw.bin" : "card-raw.partial.bin"), dump.ToBin());
            }
            await File.WriteAllTextAsync(Path.Combine(directory, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        Console.WriteLine("Results: " + directory);
        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cts.Cancel(); };
        Console.CancelKeyPress += cancel;
        await using var connection = new ChameleonConnection(new SerialTransport(port));
        DeviceMode? previous = null;
        try
        {
            await connection.OpenAsync(cts.Token); var client = new ChameleonClient(connection);
            previous = await client.GetDeviceModeAsync(cts.Token); await client.SetDeviceModeAsync(DeviceMode.Reader, cts.Token);
            var tag = (await client.Hf14aScanAsync(cts.Token)).Single();
            if (tag.Sak != 0x18) throw new InvalidOperationException($"Expected MFC4K SAK 18; received {tag.Sak:X2}.");
            dump = new MifareDump(MifareClassicType.Classic4K, tag.Uid, tag.Atqa, tag.Sak);
            await client.FindMifareKeysAsync(dump, keys.DistinctBy(Convert.ToHexString).ToArray(), ct: cts.Token); await Save();
            if (dump.KeyA.Concat(dump.KeyB).All(k => k is null)) throw new InvalidOperationException("No working sector key found. Nested requires one authenticated key.");
            var prng = await client.DetectNtLevelAsync(cts.Token); report["prng_level"] = (int)prng;
            var recovery = new MifareKeyRecovery(client, keyRecovery: new ManagedKeyRecoveryEngine());
            for (var attempt = 0; attempt < attempts && prng != MifareClassicPrngType.Hard && dump.KeyA.Concat(dump.KeyB).Any(k => k is null); attempt++)
            {
                await recovery.RecoverAsync(dump, prng == MifareClassicPrngType.Static ? MifareRecoveryMethod.StaticNested : MifareRecoveryMethod.Nested,
                    new Progress<KeyRecoveryProgress>(p => Console.WriteLine(p.Status)), cts.Token); await Save();
            }
            report["preparation_and_recovery_seconds"] = watch.Elapsed.TotalSeconds; readingStarted = watch.Elapsed.TotalSeconds;
            await client.ReadMifareClassicAsync(keys, ct: cts.Token, existing: dump);
            await Save();
            if (dump.RecoveredBlocks != 256 || dump.KeyA.Concat(dump.KeyB).Any(k => k is null)) throw new IOException("Recovery/read is incomplete; see report.json and the partial dump.");
            Console.WriteLine("All 256 blocks read and 80 keys recovered.");
        }
        catch (Exception ex) { report["error"] = ex.Message; throw; }
        finally
        {
            Console.CancelKeyPress -= cancel;
            if (previous is { } mode && connection.IsConnected && !connection.IsClosed)
            {
                using var restore = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await new ChameleonClient(connection).SetDeviceModeAsync(mode, restore.Token); }
                catch (Exception ex) { report["restore_error"] = ex.Message; }
            }
            await Save();
        }
    }
}
