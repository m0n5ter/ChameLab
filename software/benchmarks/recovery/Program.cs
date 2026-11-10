using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using ChameLab.Crypto;
using ChameLab.Models;
using ChameLab.Services;

if (args.Length > 0 && args[0] == "--compare")
{
    await Comparison.Run(args[1..]);
    return;
}

// Run one fixture per process: the upstream Darkside wrapper retains allocations.
// Arguments: VECTORS CASE managed|gui LIBRARY ITERATIONS WARMUPS OUTPUT
if (args.Length != 7) throw new ArgumentException("VECTORS CASE managed|gui LIBRARY ITERATIONS WARMUPS OUTPUT");
var iterations = int.Parse(args[4]); var warmups = int.Parse(args[5]);
if (iterations < 1 || warmups < 0) throw new ArgumentOutOfRangeException(nameof(args));
var engine = args[2];
if (engine is not ("managed" or "gui")) throw new ArgumentException("Unknown engine");
if (engine == "gui") NativeLibrary.SetDllImportResolver(typeof(Native).Assembly,
    (name, _, _) => name == "gui" ? NativeLibrary.Load(Path.GetFullPath(args[3])) : IntPtr.Zero);
using var document = JsonDocument.Parse(File.ReadAllText(args[0]));
var fixtures = new Dictionary<string, (Func<ulong[]> Managed, Func<ulong[]> Gui, ulong? Expected)>();
foreach (var v in document.RootElement.EnumerateArray())
{
    uint U(string name) => v.GetProperty(name).GetUInt32();
    ulong H(string name) => ulong.Parse(v.GetProperty(name).GetString()!, NumberStyles.HexNumber);
    var kind = v.GetProperty("kind").GetString();
    if (kind is not ("nested" or "darkside" or "darkside-zero")) continue;
    var key = H("key"); var uid = U("uid");
    if (kind == "nested")
    {
        var input = new Native.Nested { Uid = uid, Distance = U("distance"), Nt0 = U("nt0"), Enc0 = U("enc0"),
            Par0 = U("par0"), Nt1 = U("nt1"), Enc1 = U("enc1"), Par1 = U("par1") };
        fixtures.Add($"nested-{key:X12}", (() => NestedRecovery.Recover(input.Uid, input.Distance,
            input.Nt0, input.Enc0, (byte)input.Par0, input.Nt1, input.Enc1, (byte)input.Par1).ToArray(),
            () => Native.Read(Native.nested(ref input, out var count), count), key));
    }
    else
    {
        DarksideItem Item(int i) => kind == "darkside-zero" ? new(U("nt"), H($"ks{i}"), 0, U($"nr{i}"), U("ar"))
            : new(U("nt"), H("ks"), H("par"), U("nr"), U("ar"));
        var items = kind == "darkside-zero" ? new[] { Item(0), Item(1) } : new[] { Item(0) };
        fixtures.Add($"{kind}-{key:X12}", (() => DarksideRecovery.Recover(uid, items).ToArray(),
            () => Native.DarksideKeys(uid, items), kind == "darkside-zero" || v.GetProperty("cContains").GetBoolean() ? key : null));
    }
}
const uint staticUid = 0xA13E4902, nt = 0xD14191B3;
foreach (var key in new[] { 0UL, 0xFFFFFFFFFFFFUL, 0x112233445566UL, 0xA0A1A2A3A4A5UL })
{
    var state = new Crypto1State(key);
    var enc = nt ^ state.Crypto1Word(staticUid ^ nt, false);
    var parity = (byte)(Crypto1.OddParity8(unchecked((byte)nt)) ^ Crypto1.OddParity8((byte)enc) ^ Crypto1.Filter(state.Odd));
    // GUI's FFI represents parity-error bits as a decimal digit string in a uint.
    var input = new Native.StaticEncrypted { Uid = staticUid, Nt = nt, Enc = enc, Parity = parity };
    fixtures.Add($"static-encrypted-{key:X12}", (() => StaticEncryptedNestedRecovery.Recover(staticUid, new(nt, enc, parity)).ToArray(),
        () => Native.Read(Native.static_encrypted_nested(ref input, out var count), count), key));
}
if (!fixtures.TryGetValue(args[1], out var fixture)) throw new ArgumentException("Unknown case: " + args[1]);
var timings = new List<double>(); var allocated = new List<long>(); ulong[]? expectedSet = null;
double coldMs = 0;
for (var round = -warmups; round < iterations; round++)
{
    var before = GC.GetTotalAllocatedBytes(true); var started = Stopwatch.GetTimestamp();
    var keys = engine == "managed" ? fixture.Managed() : fixture.Gui();
    var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    var bytes = GC.GetTotalAllocatedBytes(true) - before;
    if (round == -warmups) coldMs = elapsed;
    // Preserve duplicates when validating stability. Also save a unique-set fingerprint.
    Array.Sort(keys);
    if (fixture.Expected is { } key && Array.BinarySearch(keys, key) < 0) throw new Exception("Known key missing");
    if (fixture.Expected is null && keys.Length != 0) throw new Exception("Negative reference produced keys");
    if (expectedSet is not null && !keys.SequenceEqual(expectedSet)) throw new Exception("Unstable candidate multiset");
    expectedSet = keys;
    if (round >= 0) { timings.Add(elapsed); allocated.Add(bytes); }
}
var sorted = timings.Order().ToArray();
var result = new { name = args[1], engine, runtime = RuntimeInformation.FrameworkDescription,
    os = RuntimeInformation.OSDescription, cpus = Environment.ProcessorCount, iterations, warmups, coldMs,
    medianMs = (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2, samplesMs = timings,
    allocatedBytes = allocated, candidates = expectedSet!.Length, fingerprint = Fingerprint(expectedSet),
    uniqueCandidates = expectedSet.Distinct().Count(), uniqueFingerprint = Fingerprint(expectedSet.Distinct()),
    peakMiB = Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0 };
File.WriteAllText(args[6], JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }) + "\n");
Console.WriteLine($"{result.name} {engine}: {result.medianMs:F2} ms; cold {coldMs:F2} ms; {result.candidates} candidates");

static string Fingerprint(IEnumerable<ulong> keys)
{
    ulong hash = 14695981039346656037;
    foreach (var key in keys) for (var b = 0; b < 6; b++) hash = unchecked((hash ^ (byte)(key >> (8 * b))) * 1099511628211);
    return hash.ToString("X16");
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] internal struct Nested
    { public uint Uid, Distance, Nt0, Enc0, Par0, Nt1, Enc1, Par1; }
    [StructLayout(LayoutKind.Sequential)] internal struct StaticEncrypted
    { public uint Uid, Nt, Enc, Parity; }
    [StructLayout(LayoutKind.Sequential)] private struct Item
    { public uint Nt; public ulong Ks, Parity; public uint Nr, Ar; }
    [StructLayout(LayoutKind.Sequential)] private struct Darkside
    { public uint Uid; public IntPtr Items; public uint Count; }
    [DllImport("gui", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr nested(ref Nested input, out uint count);
    [DllImport("gui", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr static_encrypted_nested(ref StaticEncrypted input, out uint count);
    [DllImport("gui", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr darkside(ref Darkside input, out uint count);
    [DllImport("libc", CallingConvention = CallingConvention.Cdecl)] private static extern void free(IntPtr memory);
    internal static unsafe ulong[] Read(IntPtr memory, uint count)
    {
        try
        {
            if (count > 1 << 24 || (count != 0 && memory == IntPtr.Zero)) throw new InvalidDataException("Invalid native output");
            return new ReadOnlySpan<ulong>((void*)memory, checked((int)count)).ToArray();
        }
        finally { free(memory); }
    }
    internal static unsafe ulong[] DarksideKeys(uint uid, DarksideItem[] items)
    {
        var nativeItems = items.Select(x => new Item { Nt = x.Nt, Ks = x.Ks1, Parity = x.Par, Nr = x.Nr, Ar = x.Ar }).ToArray();
        fixed (Item* p = nativeItems)
        {
            var input = new Darkside { Uid = uid, Count = (uint)items.Length, Items = (IntPtr)p };
            return Read(darkside(ref input, out var count), count);
        }
    }
}
