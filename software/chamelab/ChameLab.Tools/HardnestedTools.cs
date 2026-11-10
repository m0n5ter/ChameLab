using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using K4os.Compression.LZ4.Streams;

namespace ChameLab.Tools;

internal static class HardnestedTools
{
    public static void GeneratePartialSums(string root, bool check = false)
    {
        var sums = ChameLab.Crypto.Hardnested.HardnestedTables.BuildPartialSums();
        var packed = new byte[sums[0].Length];
        for (int i = 0; i < packed.Length; i++) packed[i] = (byte)(sums[0][i] | (sums[1][i] << 4));
        var path = Path.Combine(Destination(root), "partial-sums.bin");
        if (check)
        {
            if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(packed))
                throw new InvalidDataException("The packed partial sums need regeneration.");
        }
        else File.WriteAllBytes(path, packed);
        Console.WriteLine($"{path}: {Convert.ToHexString(SHA256.HashData(packed))}");
    }

    private const string Commit = "1b71dfbbd4c156c34c02cfdd9140470d97d1336c";
    private static string Destination(string root) => Path.Combine(root, "software/chamelab/ChameLab.Core/Crypto/Hardnested");

    public static void Generate(string root, bool check)
    {
        GeneratePartialSums(root, check);
        var template = File.ReadAllText(Path.Combine(root, "software/chamelab/tools/hardnested/core.cs.txt")).Replace("\r\n", "\n");
        foreach (var values in new[]
        {
            new[] { "Scalar", "System.UInt64", "1", "0UL", "ulong.MaxValue", "packed[0]", "results" },
            new[] { "Vector", "System.Numerics.Vector<ulong>", "Vector<ulong>.Count", "Vector<ulong>.Zero", "new Vector<ulong>(ulong.MaxValue)", "new Vector<ulong>(packed)", "results[lane]" }
        })
        {
            var text = template;
            var names = new[] { "NAME", "BITS", "LANES", "ZERO", "ONES", "LOAD", "LANE" };
            for (var i = 0; i < names.Length; i++) text = text.Replace("@" + names[i] + "@", values[i]);
            var path = Path.Combine(Destination(root), $"Hardnested{values[0]}.g.cs");
            if (check)
            {
                if (!File.Exists(path) || File.ReadAllText(path).Replace("\r\n", "\n") != text) throw new InvalidDataException("Generated file is out of date: " + path);
            }
            else File.WriteAllText(path, text, new UTF8Encoding(false));
            Console.WriteLine((check ? "Verified " : "Generated ") + Path.GetFileName(path));
        }
    }

    public static void ImportTables(string root, string upstream)
    {
        // Hash every input against the checked-in manifest before replacing the packed resource.
        var destination = Destination(root);
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(destination, "tables.manifest.json")));
        if (manifest.RootElement.GetProperty("commit").GetString() != Commit) throw new InvalidDataException("Unexpected table revision.");
        var source = Path.Combine(Path.GetFullPath(upstream), "client/resources/hardnested_tables");
        var pages = new List<byte[]> { new byte[256] };
        var pageIds = new Dictionary<byte[], ushort>(new PageComparer()) { [pages[0]] = 0 };
        var tables = new List<(ushort Half, ushort Flip, uint Count, ushort[] Pages)>();
        var entries = manifest.RootElement.GetProperty("tables").EnumerateArray().OrderBy(t => t.GetProperty("file").GetString(), StringComparer.Ordinal).ToArray();
        if (entries.Length != 351) throw new InvalidDataException("Expected 351 upstream tables.");
        foreach (var table in entries)
        {
            var name = table.GetProperty("file").GetString()!;
            if (Path.GetFileName(name) != name) throw new InvalidDataException("Invalid manifest path.");
            var compressed = File.ReadAllBytes(Path.Combine(source, name));
            CheckHash(compressed, table.GetProperty("sha256").GetString()!, name);
            using var input = new MemoryStream(compressed); using var decoder = LZ4Stream.Decode(input);
            var raw = new byte[4 + (1 << 21)]; decoder.ReadExactly(raw);
            if (decoder.ReadByte() != -1) throw new InvalidDataException("Unexpected table size: " + name);
            CheckHash(raw, table.GetProperty("raw_sha256").GetString()!, name + " (decoded)");
            var count = BinaryPrimitives.ReadUInt32LittleEndian(raw);
            if (count != table.GetProperty("count").GetUInt32()) throw new InvalidDataException("Unexpected table count: " + name);
            if (count >= .9901 * (1 << 24)) continue;
            var ids = new ushort[8192];
            for (var i = 0; i < ids.Length; i++)
            {
                var page = raw.AsSpan(4 + i * 256, 256).ToArray();
                if (!pageIds.TryGetValue(page, out var id))
                { id = checked((ushort)pages.Count); pages.Add(page); pageIds.Add(page, id); }
                ids[i] = id;
            }
            var parts = name.Split('_');
            tables.Add((ushort.Parse(parts[1], CultureInfo.InvariantCulture), ushort.Parse(parts[2], NumberStyles.HexNumber), count, ids));
        }
        if (tables.Count != 241) throw new InvalidDataException("Expected 241 effective tables.");
        using var packed = new MemoryStream();
        using (var writer = new BinaryWriter(packed, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("HNT1"u8); writer.Write(pages.Count); writer.Write(tables.Count);
            foreach (var page in pages) writer.Write(page);
            foreach (var table in tables)
            {
                writer.Write(table.Half); writer.Write(table.Flip); writer.Write(table.Count);
                foreach (var id in table.Pages) writer.Write(id);
            }
        }
        var bytes = packed.ToArray(); CheckHash(bytes, manifest.RootElement.GetProperty("packed_sha256").GetString()!, "packed tables");
        File.WriteAllBytes(Path.Combine(destination, "tables.bin"), bytes);
        Console.WriteLine($"Verified and repacked {tables.Count} tables, {pages.Count} shared pages, {bytes.Length} bytes.");
    }

    private static void CheckHash(byte[] bytes, string expected, string name)
    {
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SHA256 mismatch: " + name);
    }

    private sealed class PageComparer : IEqualityComparer<byte[]>
    {
        public bool Equals(byte[]? a, byte[]? b) => a is not null && b is not null && a.AsSpan().SequenceEqual(b);
        public int GetHashCode(byte[] value) { var hash = new HashCode(); hash.AddBytes(value); return hash.ToHashCode(); }
    }

    public static async Task PrepareReference(string upstream)
    {
        upstream = Path.GetFullPath(upstream);
        if ((await Program.Run("git", ["rev-parse", "HEAD"], upstream)).Trim() != Commit) throw new InvalidDataException("Reference checkout must be at " + Commit);
        var path = Path.Combine(upstream, "client/src/cmdhfmf.c");
        var text = File.ReadAllText(path).Replace("\r\n", "\n");
        foreach (var (before, after) in new[]
        {
            ("if ((g_session.pm3_present == false) && (tests == false)) {", "if ((g_session.pm3_present == false) && (tests == false) && !nonce_file_read) {"),
            ("if (nonce_file_read) {\n        char *fptr = GenerateFilename", "if (nonce_file_read && fnlen == 0) {\n        char *fptr = GenerateFilename")
        })
        {
            if (text.Contains(after, StringComparison.Ordinal)) continue;
            if (text.Split(before).Length != 2) throw new InvalidDataException("Reference patch context is not unique: " + before);
            text = text.Replace(before, after);
        }
        File.WriteAllText(path, text, new UTF8Encoding(false));
        Console.WriteLine("Patched CLI device check and explicit filename preservation; rebuild the reference client.");
    }
}
