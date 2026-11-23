using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChameLab.Cli;
using ChameLab.Protocol;
using ChameLab.Transport;

internal static class LfT5577Runner
{
    private const string Key = "20206666";
    private const string Original = "0001014A3E0101010101016BE38CD64A";
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };
    private static string Raw(int cmd, string data = "") => $"hw raw -n {cmd} -t 8" +
        (data.Length == 0 ? "" : " -d " + data);

    internal static async Task Run(ITransport transport, string schedule, string folder, string? phaseFilter, int limit)
    {
        using var document = JsonDocument.Parse(schedule);
        var selected = document.RootElement.EnumerateArray()
            .Where(x => phaseFilter is null || x.GetProperty("phase").GetString() == phaseFilter).ToArray();
        if (limit > 0) selected = selected.Take(limit).ToArray();
        using var output = new StringWriter();
        using var error = new StringWriter();
        await using var cli = new CliSession(output, error);
        using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await cli.AttachAsync(transport, connectTimeout.Token);
        var client = new ChameleonClient(cli.Host.Connection!);
        var previousMode = await client.GetDeviceModeAsync();
        var metadata = new { utc = DateTimeOffset.UtcNow, transport = "Android BLE / Nordic UART",
            firmware = await client.GetGitVersionAsync(), address = await client.GetAddressAsync(),
            previousMode = previousMode.ToString(), phaseFilter, plannedCycles = selected.Length,
            android = Android.OS.Build.Model, scheduleSeed = 20261123 };
        await File.WriteAllTextAsync(Path.Combine(folder, "metadata.json"), JsonSerializer.Serialize(metadata, Pretty));
        await File.WriteAllTextAsync(Path.Combine(folder, "cycles.jsonl"), "");
        await File.WriteAllTextAsync(Path.Combine(folder, "commands.jsonl"), "");
        var rows = new List<Cycle>();
        var restored = false;
        string? failure = null;
        CommandResult? slotsBefore = null, slotsAfter = null;
        try
        {
            slotsBefore = await Execute("hw slot list");
            if ((await Execute("hw mode --reader")).exit != 0) throw new IOException("Cannot select reader mode");
            foreach (var item in selected)
            {
                if (!cli.Connected) throw new IOException("BLE connection lost; stop rather than log RF failures for absent hardware");
                var test = item.GetProperty("case");
                var name = test.GetProperty("name").GetString()!;
                var family = test.GetProperty("family").GetString()!;
                var expected = test.GetProperty("uid").GetString()!;
                var watch = Stopwatch.StartNew();
                var commands = new List<CommandResult>();
                var write = test.GetProperty("write");
                commands.Add(await Execute(write.ValueKind == JsonValueKind.Null ?
                    $"lf pyramid write --raw {expected}" : Raw(write.GetInt32(), expected + Key + Key)));
                if (test.TryGetProperty("rate", out var rate) && rate.GetInt32() != 64)
                {
                    var config = rate.GetInt32() == 16 ? "00048050" : "00088050";
                    commands.Add(await Execute(Raw(3016, "00" + config + "01" + Key + "00")));
                }
                var scan = family == "IDTECK" ? 3022 : test.GetProperty("scan").GetInt32();
                var scanData = test.TryGetProperty("scan_data", out var s) ? s.GetString()! : "";
                var reads = new List<string>();
                for (var read = 0; read < 2; read++)
                {
                    var result = await Execute(Raw(scan, scanData));
                    commands.Add(result);
                    var hex = Regex.Matches(result.stdout, @"(?m)^([0-9A-Fa-f]{2}(?:[0-9A-Fa-f]{2})+)\s*$")
                        .Select(m => m.Groups[1].Value.ToUpperInvariant()).ToArray();
                    if (hex.Length != 1) { reads.Add("NO_FRAME"); continue; }
                    var actual = hex[0];
                    if (scan == 3000)
                        actual = Convert.ToInt32(actual[..4], 16) == test.GetProperty("tag").GetInt32()
                            ? actual[4..] : "WRONG_TYPE:" + actual;
                    else if (test.GetProperty("tag").GetInt32() == 200)
                        actual = actual.Length >= 26 ? actual[..26] : "SHORT_FRAME:" + actual;
                    reads.Add(actual);
                }
                var row = new Cycle(rows.Count + 1, item.GetProperty("phase").GetString()!, name, family,
                    expected, reads.ToArray(), commands.All(x => x.exit == 0) && reads.All(x => x == expected),
                    watch.ElapsedMilliseconds, commands.ToArray());
                rows.Add(row);
                await File.AppendAllTextAsync(Path.Combine(folder, "cycles.jsonl"), JsonSerializer.Serialize(row) + "\n");
                await Save();
                if (!row.ok || rows.Count % 10 == 0 || row.phase == "smoke")
                    Console.WriteLine($"LF_BLE {rows.Count}/{selected.Length} {row.phase} {name} " +
                        $"{(row.ok ? "PASS" : "FAIL")} failures={rows.Count(x => !x.ok)} reads={string.Join(',', reads)}");
                // Preserve evidence immediately, without retrying the failed read.
                if (!row.ok && rows.Count(x => !x.ok) <= 10 && cli.Connected)
                    await Execute($"lf sniff --timeout 100 --out {Path.Combine(folder, $"failed-{row.index}.bin")}");
            }
        }
        catch (Exception e) { failure = e.ToString(); throw; }
        finally
        {
            if (cli.Connected)
            {
                var write = await Execute($"lf pyramid write --raw {Original}");
                var first = await Execute(Raw(3021));
                var second = await Execute(Raw(3021));
                restored = write.exit == 0 && first.exit == 0 && second.exit == 0 &&
                    first.stdout.Contains(Original) && second.stdout.Contains(Original);
                slotsAfter = await Execute("hw slot list");
                await Execute(previousMode == DeviceMode.Reader ? "hw mode --reader" : "hw mode --emulator");
            }
            await Save();
            Console.WriteLine($"LF_BLE_RESULT {rows.Count(x => x.ok)}/{rows.Count}; originalPyramidRestored={restored}; failures={rows.Count(x => !x.ok)}");
        }

        async Task<CommandResult> Execute(string command)
        {
            output.GetStringBuilder().Clear();
            error.GetStringBuilder().Clear();
            var watch = Stopwatch.StartNew();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var code = await cli.ExecuteAsync(command, timeout.Token);
            var result = new CommandResult(command, code, watch.ElapsedMilliseconds, output.ToString(), error.ToString());
            await File.AppendAllTextAsync(Path.Combine(folder, "commands.jsonl"), JsonSerializer.Serialize(result) + "\n");
            return result;
        }

        Task Save() => File.WriteAllTextAsync(Path.Combine(folder, "results.json"), JsonSerializer.Serialize(new
        {
            metadata, complete = rows.Count == selected.Length && failure is null,
            cycles = rows.Count, passed = rows.Count(x => x.ok), failed = rows.Count(x => !x.ok),
            nativeReadCount = rows.Count * 2, wrongIds = rows.Sum(x => x.reads.Count(r => r != "NO_FRAME" && r != x.expected)),
            originalPyramidRestored = restored, slotsUnchanged = slotsBefore?.stdout == slotsAfter?.stdout,
            slotsBefore, slotsAfter, failure,
            byFamily = rows.GroupBy(x => x.family).Select(g => new { family = g.Key, cycles = g.Count(),
                passed = g.Count(x => x.ok), failed = g.Count(x => !x.ok) }),
            failures = rows.Where(x => !x.ok)
        }, Pretty));
    }

    private sealed record CommandResult(string command, int exit, long milliseconds, string stdout, string stderr);
    private sealed record Cycle(int index, string phase, string name, string family, string expected,
        string[] reads, bool ok, long milliseconds, CommandResult[] commands);
}
