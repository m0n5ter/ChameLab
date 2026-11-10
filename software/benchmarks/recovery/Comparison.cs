using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class Comparison
{
    internal static async Task Run(string[] args)
    {
        if (args.Length is < 5 or > 7)
            throw new ArgumentException("--compare VECTORS BASELINE_EXE OPTIMIZED_EXE GUI_LIBRARY OUTPUT_DIRECTORY [ITERATIONS WARMUPS]");
        var vectors = Path.GetFullPath(args[0]); var baseline = Path.GetFullPath(args[1]);
        var optimized = Path.GetFullPath(args[2]); var library = Path.GetFullPath(args[3]);
        var output = Path.GetFullPath(args[4]); Directory.CreateDirectory(output);
        var iterations = args.Length > 5 ? int.Parse(args[5]) : 7;
        var warmups = args.Length > 6 ? int.Parse(args[6]) : 2;
        if (iterations is < 1 or > 100 || warmups is < 0 or > 20) throw new ArgumentOutOfRangeException(nameof(args));
        using var document = JsonDocument.Parse(File.ReadAllText(vectors));
        var cases = document.RootElement.EnumerateArray()
            .Where(v => v.GetProperty("kind").GetString() is "nested" or "darkside" or "darkside-zero")
            .Select(v => v.GetProperty("kind").GetString() + "-" + v.GetProperty("key").GetString()).ToList();
        cases.AddRange(new[] { "000000000000", "FFFFFFFFFFFF", "112233445566", "A0A1A2A3A4A5" }.Select(k => "static-encrypted-" + k));
        var rows = new JsonArray();
        var modes = new[] { ("before", baseline, "managed"), ("optimized", optimized, "managed"), ("gui", optimized, "gui") };
        for (var c = 0; c < cases.Count; c++)
        {
            var results = new Dictionary<string, JsonObject>();
            for (var j = 0; j < modes.Length; j++)
            {
                var (label, executable, engine) = modes[(c + j) % modes.Length];
                var file = Path.Combine(output, cases[c] + "-" + label + ".json");
                var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                foreach (var arg in new[] { vectors, cases[c], engine, library, iterations.ToString(), warmups.ToString(), file }) start.ArgumentList.Add(arg);
                start.Environment["DOTNET_TieredCompilation"] = "0";
                start.Environment["DOTNET_ReadyToRun"] = "0";
                using var process = Process.Start(start) ?? throw new IOException("Cannot start " + executable);
                var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                try { await process.WaitForExitAsync(stop.Token); }
                catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
                var message = await stdout + await stderr;
                if (process.ExitCode != 0) throw new IOException($"{cases[c]} {label}: {message}");
                Console.WriteLine(label + ": " + message.Trim());
                results.Add(label, JsonNode.Parse(await File.ReadAllTextAsync(file))!.AsObject());
            }
            if (results["before"]["fingerprint"]!.GetValue<string>() != results["optimized"]["fingerprint"]!.GetValue<string>() ||
                results["before"]["candidates"]!.GetValue<int>() != results["optimized"]["candidates"]!.GetValue<int>())
                throw new InvalidDataException("Candidate multiset changed: " + cases[c]);
            var sameAsGui = results["optimized"]["uniqueFingerprint"]!.GetValue<string>() == results["gui"]["uniqueFingerprint"]!.GetValue<string>() &&
                results["optimized"]["uniqueCandidates"]!.GetValue<int>() == results["gui"]["uniqueCandidates"]!.GetValue<int>();
            if (!sameAsGui && !cases[c].StartsWith("nested-")) throw new InvalidDataException("GUI candidate set differs: " + cases[c]);
            rows.Add(new JsonObject { ["case"] = cases[c], ["sameUniqueSetAsGui"] = sameAsGui,
                ["before"] = results["before"], ["optimized"] = results["optimized"], ["gui"] = results["gui"] });
            await File.WriteAllTextAsync(Path.Combine(output, "results.json"), new JsonObject
            {
                ["dateUtc"] = DateTime.UtcNow.ToString("O"), ["iterations"] = iterations, ["warmups"] = warmups,
                ["guiSha256"] = Hash(library), ["baselineCoreSha256"] = Hash(Path.Combine(Path.GetDirectoryName(baseline)!, "ChameLab.Core.dll")),
                ["optimizedCoreSha256"] = Hash(Path.Combine(Path.GetDirectoryName(optimized)!, "ChameLab.Core.dll")),
                ["vectorsSha256"] = Hash(vectors), ["rows"] = rows.DeepClone()
            }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        }
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
