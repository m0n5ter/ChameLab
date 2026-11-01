using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using ChameLab.Services;
using ChameLab.Crypto;

int iterations = args.Length > 0 ? int.Parse(args[0]) : 7;
int warmups = args.Length > 1 ? int.Parse(args[1]) : 2;
if (iterations < 1 || warmups < 1) throw new ArgumentException("Positive iterations and warmups required.");
var vectorsPath = args.Length > 2 ? args[2] : "../../chamelab/ChameLab.Tests/Reference/crypto_vectors.json";
using var document = JsonDocument.Parse(File.ReadAllText(vectorsPath));
var rows = new List<object>();
Console.WriteLine($"{RuntimeInformation.FrameworkDescription}; {RuntimeInformation.OSDescription}; {RuntimeInformation.ProcessArchitecture}; logical CPUs={Environment.ProcessorCount}");
Console.WriteLine($"Warmups={warmups}, measured iterations={iterations}; elapsed time excludes validation and process startup.");
foreach (var v in document.RootElement.EnumerateArray().Where(v => v.GetProperty("kind").GetString() == "nested"))
{
    string key = v.GetProperty("key").GetString()!;
    uint[] input = new[] { "uid", "distance", "nt0", "enc0", "par0", "nt1", "enc1", "par1" }
        .Select(p => v.GetProperty(p).GetUInt32()).ToArray();
    ulong expected = ulong.Parse(key, NumberStyles.HexNumber);
    int nonceCandidates = 0;
    for (var sample = 2; sample < 8; sample += 3)
    {
        byte[] parity = [ (byte)(input[sample + 2] & 1), (byte)((input[sample + 2] >> 1) & 1), (byte)((input[sample + 2] >> 2) & 1) ];
        var nt = Crypto1.PrngSuccessor(input[sample], (int)input[1] - 14);
        for (var step = 0; step < 29; step++)
        {
            if (NestedRecovery.ValidNonce(nt, input[sample + 1], nt ^ input[sample + 1], parity)) nonceCandidates++;
            nt = Crypto1.PrngSuccessor(nt, 1);
        }
    }
    Console.WriteLine($"{key}: nonce candidates={nonceCandidates}; available logical CPUs={Environment.ProcessorCount}");
    var reference = v.GetProperty("cKeys").EnumerateArray()
        .Select(k => ulong.Parse(k.GetString()!, NumberStyles.HexNumber)).Order().ToArray();
    string[] modes = args.Length > 3 ? args[3].Split(',') : ["C/1", "C/4", "C#/1", "C#/4", "C#/all"];
    if (modes.Any(m => m is not ("C/1" or "C/4" or "C#/1" or "C#/4" or "C#/all"))) throw new ArgumentException("Unknown mode");
    var timings = modes.ToDictionary(m => m, _ => new List<double>());
    var results = new Dictionary<string, ulong[]>();
    ulong[] nativeOutput = new ulong[50];
    // Rotate execution order to spread thermal/frequency drift across modes.
    for (int round = -warmups; round < iterations; round++)
    {
        for (int j = 0; j < modes.Length; j++)
        {
            string mode = modes[(j + round + warmups) % modes.Length];
            long start = Stopwatch.GetTimestamp();
            IReadOnlyList<ulong> keys;
            if (mode == "C#/all")
                keys = NestedRecovery.Recover(input[0], input[1], input[2], input[3], (byte)input[4], input[5], input[6], (byte)input[7]);
            else if (mode is "C#/1" or "C#/4")
                keys = NestedRecovery.RecoverCore(input[0], input[1], input[2], input[3], (byte)input[4], input[5], input[6], (byte)input[7], default, mode == "C#/1" ? 1 : 4);
            else
            {
                int count = Native.bench_nested(input, mode == "C/1" ? 1 : 0, nativeOutput);
                keys = new ArraySegment<ulong>(nativeOutput, 0, count);
            }
            double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (!keys.Contains(expected)) throw new Exception($"{key} {mode}: known key missing");
            var sorted = keys.Order().ToArray();
            if (!sorted.SequenceEqual(reference))
                throw new Exception($"{key} {mode}: differs from saved C reference");
            if (results.TryGetValue(mode, out var previous) && !previous.SequenceEqual(sorted))
                throw new Exception($"{key} {mode}: unstable candidates");
            results[mode] = sorted;
            if (round >= 0) timings[mode].Add(elapsed);
        }
    }
    foreach (var mode in modes)
    {
        var samples = timings[mode].Order().ToArray();
        double median = samples.Length % 2 == 1 ? samples[samples.Length / 2] : (samples[samples.Length / 2 - 1] + samples[samples.Length / 2]) / 2;
        Console.WriteLine($"{key} {mode,-3}: median {median:F2} ms; min {samples[0]:F2}; max {samples[^1]:F2}; candidates {results[mode].Length}");
        rows.Add(new { key, mode, nonceCandidates, medianMs = median, minMs = samples[0], maxMs = samples[^1], samplesMs = timings[mode], candidates = results[mode].Select(k => k.ToString("X12")), sameCandidateSetAsC = results[mode].SequenceEqual(reference) });
    }
}
File.WriteAllText("results.json", JsonSerializer.Serialize(new { runtime = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(), logicalCpus = Environment.ProcessorCount, iterations, warmups, rows }, new JsonSerializerOptions { WriteIndented = true }));

internal static class Native
{
    [DllImport("nested_bench", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int bench_nested([In] uint[] input, int serial, [Out] ulong[] output);
}
