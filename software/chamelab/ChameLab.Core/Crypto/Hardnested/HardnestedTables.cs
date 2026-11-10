// Derived from Proxmark3, GPL-3.0-or-later. See NOTICE.md.
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace ChameLab.Crypto.Hardnested;

internal sealed class HardnestedTables
{
    internal static readonly int[] Sums = [0, 32, 56, 64, 80, 96, 104, 112, 120, 128, 136, 144, 152, 160, 176, 192, 200, 224, 256];
    internal static readonly Lazy<HardnestedTables> Shared = new(() => new());
    internal readonly record struct Table(int Half, int Flip, int Count, ushort[] Pages);
    internal readonly uint[] Words;
    internal readonly int FullPage = -1;
    internal readonly Table[][] Tables;
    internal readonly byte[][] PartialSums;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private HardnestedTables()
    {
        using var stream = typeof(HardnestedTables).Assembly.GetManifestResourceStream("ChameLab.Hardnested.Tables")
            ?? throw new InvalidOperationException("The managed hardnested tables are missing.");
        using var reader = new BinaryReader(stream);
        if (reader.ReadUInt32() != 0x31544E48) throw new InvalidDataException("Invalid hardnested tables.");
        int pages = reader.ReadInt32(), tables = reader.ReadInt32();
        if (pages is < 1 or > 65535 || tables != 241) throw new InvalidDataException("Invalid hardnested table sizes.");
        Words = new uint[pages * 64];
        stream.ReadExactly(MemoryMarshal.AsBytes(Words.AsSpan()));
        if (!BitConverter.IsLittleEndian) for (int i = 0; i < Words.Length; i++) Words[i] = BinaryPrimitives.ReverseEndianness(Words[i]);
        for (int page = 0; page < pages; page++)
            if (Words.AsSpan(page * 64, 64).IndexOfAnyExcept(uint.MaxValue) < 0) { FullPage = page; break; }
        if (FullPage < 0 || Words.AsSpan(0, 64).IndexOfAnyExcept(0u) >= 0)
            throw new InvalidDataException("Missing hardnested identity pages.");
        var all = new Table[tables];
        for (int t = 0; t < tables; t++)
        {
            int half = reader.ReadUInt16(), flip = reader.ReadUInt16(), count = reader.ReadInt32();
            if (half > 1 || flip >= 1024 || count is < 0 or > (1 << 24)) throw new InvalidDataException();
            var indices = new ushort[8192];
            stream.ReadExactly(MemoryMarshal.AsBytes(indices.AsSpan()));
            for (int i = 0; i < indices.Length; i++)
            {
                if (!BitConverter.IsLittleEndian) indices[i] = BinaryPrimitives.ReverseEndianness(indices[i]);
                if (indices[i] >= pages) throw new InvalidDataException();
            }
            all[t] = new(half, flip, count, indices);
        }
        Tables = [all.Where(t => t.Half == 0).OrderBy(t => t.Count).ToArray(),
                  all.Where(t => t.Half == 1).OrderBy(t => t.Count).ToArray()];
        using var partialStream = typeof(HardnestedTables).Assembly.GetManifestResourceStream("ChameLab.Hardnested.PartialSums")
            ?? throw new InvalidOperationException("The managed partial sums are missing.");
        var packed = new byte[1 << 20];
        partialStream.ReadExactly(packed);
        PartialSums = [new byte[packed.Length], new byte[packed.Length]];
        for (int i = 0; i < packed.Length; i++)
        {
            PartialSums[0][i] = (byte)(packed[i] & 15);
            PartialSums[1][i] = (byte)(packed[i] >> 4);
        }
    }

    // The upstream 16-path enumeration is a depth-four binary tree. Evaluate
    // its shared subtrees once, rather than evaluating 80 filters per state.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static byte[][] BuildPartialSums()
    {
        const int size = 1 << 20;
        var filter = new byte[size];
        for (uint i = 0; i < size; i++) filter[i] = Crypto1.Filter(i);
        var current = new byte[size];
        var next = new byte[size];
        for (int depth = 0; depth < 4; depth++)
        {
            int paths = 1 << depth;
            for (int i = 0; i < size; i++)
            {
                int left = (i << 1) & (size - 1), right = left | 1;
                next[i] = (byte)((filter[left] == 0 ? current[left] : paths - current[left])
                              + (filter[right] == 0 ? current[right] : paths - current[right]));
            }
            (current, next) = (next, current);
        }
        for (int i = 0; i < size; i++)
        {
            next[i] = (byte)((filter[i] == 0 ? 16 - current[i] : current[i]) / 2);
            current[i] /= 2;
        }
        return [current, next];
    }

    internal static int Sum(int odd, int even) => 2 * odd * (16 - 2 * even) + (16 - 2 * odd) * 2 * even;
}
