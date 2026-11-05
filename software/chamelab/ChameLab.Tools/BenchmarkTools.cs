using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ChameLab.Tools;

internal static class BenchmarkTools
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static async Task<(int Code, string Output)> Run(string file, IEnumerable<string> args, int timeoutSeconds = 1200,
        string? directory = null, bool optimizingJit = false)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        if (directory is not null) start.WorkingDirectory = directory;
        start.Environment["LC_ALL"] = "C";
        if (optimizingJit) { start.Environment["DOTNET_TieredCompilation"] = "0"; start.Environment["DOTNET_ReadyToRun"] = "0"; }
        using var process = Process.Start(start) ?? throw new IOException("Could not start " + file);
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        return (process.ExitCode, await stdout + await stderr);
    }

    private static void RequireSuccess((int Code, string Output) result)
    { if (result.Code != 0) throw new IOException($"Process failed ({result.Code}): {result.Output}"); }

    public static async Task Managed(string name, string[] args)
    {
        if (name is not ("nested" or "static-encrypted")) throw new ArgumentException("Choose nested or static-encrypted.");
        var iterations = args.Length > 0 ? int.Parse(args[0]) : 7; var warmups = args.Length > 1 ? int.Parse(args[1]) : 2;
        if (args.Length > 2 || iterations is < 1 or > 1000 || warmups is < 1 or > 100) throw new ArgumentException("Invalid iterations/warmups.");
        var root = Program.Root(); var directory = Path.Combine(root, "software/benchmarks", name);
        RequireSuccess(await Run("dotnet", ["build", directory, "-c", "Release", "--nologo"]));
        var assembly = name == "nested" ? "NestedBench" : "StaticEncryptedBench";
        var arguments = new List<string> { Path.Combine(directory, $"bin/Release/net10.0/{assembly}.dll"), iterations.ToString(), warmups.ToString() };
        if (name == "nested") arguments.AddRange([Path.Combine(root, "software/chamelab/ChameLab.Tests/Reference/crypto_vectors.json"), "C#/1,C#/4,C#/all"]);
        var result = await Run("dotnet", arguments, directory: directory, optimizingJit: true);
        Console.Write(result.Output); RequireSuccess(result);
    }

    public static async Task ReferenceVectors(string[] args)
    {
        if (args.Length % 2 != 0) throw new ArgumentException("Reference arguments require --name VALUE pairs.");
        var options = new Dictionary<string, string>();
        for (var i = 0; i < args.Length; i += 2)
        {
            if (args[i] is not ("--generator" or "--nested" or "--staticnested" or "--mfkey32v2" or "--output")) throw new ArgumentException("Unknown argument: " + args[i]);
            options.Add(args[i], Path.GetFullPath(args[i + 1]));
        }
        foreach (var name in new[] { "--generator", "--nested", "--staticnested", "--mfkey32v2" })
            if (!options.ContainsKey(name)) throw new ArgumentException("Required: " + name);
        async Task<string> Invoke(string name, IEnumerable<string>? values = null)
        { var result = await Run(options[name], values ?? []); RequireSuccess(result); return result.Output; }
        var output = options.GetValueOrDefault("--output") ?? Path.Combine(Program.Root(), "software/chamelab/ChameLab.Tests/Reference/crypto_vectors.json");
        var vectors = (await Invoke("--generator")).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => JsonNode.Parse(s)!.AsObject()).ToArray();
        foreach (var vector in vectors)
        {
            var kind = vector["kind"]!.GetValue<string>(); var key = vector["key"]!.GetValue<string>();
            string[] Values(string[] fields, bool hex = false) => fields.Select(f => vector[f]!.GetValue<uint>().ToString(hex ? "x" : "d", CultureInfo.InvariantCulture)).ToArray();
            if (kind == "nested")
            {
                var text = await Invoke("--nested", Values(["uid", "distance", "nt0", "enc0", "par0", "nt1", "enc1", "par1"]));
                var keys = Regex.Matches(text, @"\.\.\.\s*([a-fA-F0-9]{12})").Select(m => m.Groups[1].Value).ToArray();
                if (!keys.Contains(key, StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("Nested reference did not recover " + key);
                vector["cKeys"] = new JsonArray(keys.Select(k => (JsonNode)JsonValue.Create(k)!).ToArray());
            }
            else if (kind == "mfkey")
            {
                if (!(await Invoke("--mfkey32v2", Values(["uid", "nt0", "nr0", "ar0", "nt1", "nr1", "ar1"], true))).Contains("[" + key + "]", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Mfkey32 reference did not recover " + key);
            }
            else if (kind == "static" && !(await Invoke("--staticnested", Values(["uid", "knownType", "nonce", "enc0", "nonce", "enc1"]))).Contains(key, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Static nested reference did not recover " + key);
        }
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(vectors, JsonOptions) + "\n");
        Console.WriteLine($"Wrote {vectors.Length} independent C reference vectors: {output}");
    }

    public static async Task Compare(string[] args)
    {
        if (args.Length < 3) throw new ArgumentException("Expected MANAGED NATIVE OUTPUT [REPETITIONS] [--cases FILE] [--only CASE...].");
        if (!File.Exists("/usr/bin/time")) throw new PlatformNotSupportedException("Native comparison requires Linux /usr/bin/time, as in the published baseline.");
        var managed = Path.GetFullPath(args[0]); var native = Path.GetFullPath(args[1]); var output = Path.GetFullPath(args[2]);
        var repetitions = 3; var casesPath = Path.Combine(Program.Root(), "software/benchmarks/hardnested/CASES.json"); var only = new List<string>();
        var n = 3;
        if (n < args.Length && !args[n].StartsWith('-')) repetitions = int.Parse(args[n++]);
        while (n < args.Length)
        {
            if (args[n] == "--cases" && ++n < args.Length) casesPath = args[n++];
            else if (args[n] == "--only") { n++; while (n < args.Length && !args[n].StartsWith('-')) only.Add(args[n++]); }
            else throw new ArgumentException("Unknown or incomplete comparison option.");
        }
        if (repetitions < 1) throw new ArgumentException("Repetitions must be positive.");
        var cases = JsonNode.Parse(await File.ReadAllTextAsync(casesPath))!.AsArray().Select(v => v!.AsObject()).ToArray();
        if (only.Except(cases.Select(c => c["id"]!.GetValue<string>())).Any()) throw new ArgumentException("Unknown case ID.");
        if (only.Count > 0) cases = cases.Where(c => only.Contains(c["id"]!.GetValue<string>())).ToArray();
        Directory.CreateDirectory(output); var fixtures = new JsonArray(); var rows = new List<JsonObject>();
        foreach (var c in cases)
        {
            var id = c["id"]!.GetValue<string>();
            if (!Regex.IsMatch(id, @"\A[a-zA-Z0-9_-]+\z")) throw new InvalidDataException("Invalid case ID.");
            var count = c["samples"]!.GetValue<int>(); var uid = c["uid"]!.GetValue<string>();
            if (count <= 0 || count % 2 != 0) throw new InvalidDataException("Expected positive paired sample count.");
            var capture = Path.Combine(output, id + ".txt"); var binary = Path.ChangeExtension(capture, ".bin");
            RequireSuccess(await Run(managed, [count.ToString(), c["key"]!.GetValue<string>(), capture, uid, c["seed"]!.GetValue<string>(), "--generate-only"], 60));
            var lines = await File.ReadAllLinesAsync(capture);
            var samples = lines.Select(l => l.Split('|')).Select(p => (Nonce: uint.Parse(p[0], CultureInfo.InvariantCulture), Parity: byte.Parse(p[1], CultureInfo.InvariantCulture))).ToArray();
            if (samples.Length != count || samples.Select(s => s.Nonce >> 24).Distinct().Count() != 256 || samples.Any(s => s.Parity > 15)) throw new InvalidDataException("Incomplete capture: " + id);
            if (id == "reference")
            {
                var expectedLines = await File.ReadAllLinesAsync(Path.Combine(Program.Root(), "software/chamelab/ChameLab.Tests/Reference/hardnested_capture.txt"));
                if (!lines.SequenceEqual(expectedLines)) throw new InvalidDataException("Reference capture differs from the independent C fixture.");
            }
            var bytes = new byte[6 + count / 2 * 9]; BinaryPrimitives.WriteUInt32BigEndian(bytes, uint.Parse(uid, NumberStyles.HexNumber)); bytes[4] = 3;
            for (var i = 0; i < count; i += 2)
            {
                var offset = 6 + i / 2 * 9;
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), samples[i].Nonce);
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset + 4), samples[i + 1].Nonce);
                bytes[offset + 8] = (byte)(samples[i].Parity * 16 + samples[i + 1].Parity);
            }
            await File.WriteAllBytesAsync(binary, bytes);
            var fixture = c.DeepClone().AsObject(); fixture["capture_sha256"] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(); fixtures.Add(fixture);
        }
        await File.WriteAllTextAsync(Path.Combine(output, "captures.json"), fixtures.ToJsonString(JsonOptions) + "\n");
        for (var c = 0; c < cases.Length; c++)
        {
            var v = cases[c]; var id = v["id"]!.GetValue<string>(); var key = v["key"]!.GetValue<string>();
            for (var iteration = 0; iteration < repetitions; iteration++)
            {
                var engines = (c + iteration) % 2 == 0 ? new[] { "managed", "native" } : ["native", "managed"];
                for (var order = 0; order < engines.Length; order++)
                {
                    var engine = engines[order]; var name = $"{id}-{iteration}-{engine}";
                    var command = engine == "managed"
                        ? new[] { managed, "--capture", Path.Combine(output, id + ".txt"), v["uid"]!.GetValue<string>(), key }
                        : [native, "-c", $"hf mf hardnested -r -f \"{Path.Combine(output, id + ".bin").Replace("\"", "\\\"")}\""];
                    var result = await Run("/usr/bin/time", ["-f", "BENCH elapsed=%e peak_kib=%M", .. command]);
                    await File.WriteAllTextAsync(Path.Combine(output, name + ".log"), result.Output);
                    RequireSuccess(result);
                    if (!Regex.IsMatch(result.Output, (engine == "managed" ? "Key=" : @"Key found:\s*") + Regex.Escape(key) + @"\b", RegexOptions.IgnoreCase)) throw new InvalidDataException("Failed recovery: " + name);
                    var match = Regex.Match(result.Output, @"BENCH elapsed=([\d.]+) peak_kib=(\d+)");
                    if (!match.Success) throw new InvalidDataException("Missing measurements: " + name);
                    var row = v.DeepClone().AsObject(); row["engine"] = engine; row["iteration"] = iteration; row["order"] = order;
                    row["seconds"] = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture); row["peak_mib"] = int.Parse(match.Groups[2].Value) / 1024d;
                    rows.Add(row); Console.WriteLine(row.ToJsonString());
                    await File.WriteAllTextAsync(Path.Combine(output, "results.json"), JsonSerializer.Serialize(rows, JsonOptions) + "\n");
                }
            }
        }
        foreach (var c in cases)
        {
            var id = c["id"]!.GetValue<string>();
            double Median(string engine)
            {
                var values = rows.Where(r => r["id"]!.GetValue<string>() == id && r["engine"]!.GetValue<string>() == engine).Select(r => r["seconds"]!.GetValue<double>()).Order().ToArray();
                return values.Length % 2 == 0 ? (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2 : values[values.Length / 2];
            }
            Console.WriteLine($"{id}: managed={Median("managed"):F3}s native={Median("native"):F3}s speedup={Median("native") / Median("managed"):F2}");
        }
    }
}
