using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using ChameLab.Benchmarks;
using ChameLab.Crypto;
using ChameLab.Models;
using ChameLab.Services;

int iterations = args.Length > 0 ? int.Parse(args[0]) : 7;
int warmups = args.Length > 1 ? int.Parse(args[1]) : 2;
if (iterations < 1 || warmups < 1) throw new ArgumentException("Positive iterations and warmups required.");
var rows = new List<object>();
Console.WriteLine($"{RuntimeInformation.FrameworkDescription}; {RuntimeInformation.OSDescription}; {RuntimeInformation.ProcessArchitecture}");
Console.WriteLine($"Warmups={warmups}, measured iterations={iterations}; timings include allocations, exclude validation and startup.");

const uint uid = 0xA13E4902, nt = 0xD14191B3;
var fixtures = new List<(string Name, NestedNonce Nonce, ulong? Key)>
{
    ("C reference", new(nt, 0x2E9E49FC, 15), null)
};
foreach (ulong key in new[] { 0UL, 0xFFFFFFFFFFFFUL, 0x112233445566UL })
{
    var state = new Crypto1State(key);
    var enc = nt ^ state.Crypto1Word(uid ^ nt, false);
    var stream = (state.Crypto1Word(0, false) >> 24) & 1;
    var parity = (byte)(Crypto1.OddParity8((byte)(nt & 255)) ^ Crypto1.OddParity8((byte)enc) ^ stream);
    fixtures.Add(($"key {key:X12}", new(nt, enc, parity), key));
}
foreach (var fixture in fixtures)
{
    var expected = BaselineRecovery.Recover(uid, fixture.Nonce).Order().ToArray();
    if (fixture.Key is { } key && !expected.Contains(key)) throw new Exception("Missing known key in baseline.");
    if (fixture.Key is null && (expected.Length != 44075 || Fingerprint(expected) != 0x79007A0A206AF947UL))
        throw new Exception("Baseline differs from the saved C reference.");
    Measure(fixture.Name, "recovery", () => BaselineRecovery.Recover(uid, fixture.Nonce),
        () => StaticEncryptedNestedRecovery.Recover(uid, fixture.Nonce), expected, sort: true);
}

// A complete low-word key range exercises all seeds, plus the known-other-key path.
var keys = Enumerable.Range(0, 65536).Select(i => (ulong)i).ToArray();
var otherKeys = keys.Select(k => k << 16 | 0x5566UL).ToArray();
foreach (var others in new[] { otherKeys, new[] { 0x112233445566UL } })
{
    var expected = BaselineRecovery.Filter(nt, keys, 0x12345678, others).ToArray();
    Measure($"other keys {others.Length}", "filter", () => BaselineRecovery.Filter(nt, keys, 0x12345678, others),
        () => StaticEncryptedNestedRecovery.Filter(nt, keys, 0x12345678, others), expected, sort: false);
}
File.WriteAllText("results.json", JsonSerializer.Serialize(new
{
    runtime = RuntimeInformation.FrameworkDescription,
    os = RuntimeInformation.OSDescription,
    architecture = RuntimeInformation.ProcessArchitecture.ToString(),
    logicalCpus = Environment.ProcessorCount,
    iterations, warmups, rows
}, new JsonSerializerOptions { WriteIndented = true }));

void Measure(string name, string operation, Func<IReadOnlyList<ulong>> baseline,
    Func<IReadOnlyList<ulong>> optimized, ulong[] expected, bool sort)
{
    Func<IReadOnlyList<ulong>>[] modes = [baseline, optimized];
    string[] names = ["baseline", "optimized"];
    var timings = new[] { new List<double>(), new List<double>() };
    var allocations = new[] { new List<long>(), new List<long>() };
    for (var round = -warmups; round < iterations; round++)
    for (var j = 0; j < 2; j++)
    {
        var mode = (j + round + warmups) % 2;
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var started = Stopwatch.GetTimestamp();
        var result = modes[mode]();
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        var actual = sort ? result.Order().ToArray() : result.ToArray();
        if (!actual.SequenceEqual(expected)) throw new Exception($"{name} {names[mode]}: candidate mismatch");
        if (round < 0) continue;
        timings[mode].Add(elapsed);
        allocations[mode].Add(allocated);
    }
    for (var mode = 0; mode < 2; mode++)
    {
        var samples = timings[mode].Order().ToArray();
        var median = (samples[(samples.Length - 1) / 2] + samples[samples.Length / 2]) / 2;
        Console.WriteLine($"{operation} {name} {names[mode]}: {median:F2} ms; allocated {allocations[mode].Average() / 1024:F0} KiB; candidates {expected.Length}");
        rows.Add(new { name, operation, mode = names[mode], medianMs = median,
            samplesMs = timings[mode], allocatedBytes = allocations[mode],
            candidates = expected.Length, fingerprint = Fingerprint(expected).ToString("X16") });
    }
}

static ulong Fingerprint(IEnumerable<ulong> keys)
{
    ulong hash = 14695981039346656037UL;
    foreach (var key in keys)
        for (var j = 0; j < 6; j++)
            hash = unchecked((hash ^ ((key >> (8 * j)) & 255)) * 1099511628211UL);
    return hash;
}
