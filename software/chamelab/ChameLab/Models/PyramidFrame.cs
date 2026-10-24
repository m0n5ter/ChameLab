using System;

namespace ChameLab.Models;

/// <summary>
/// The raw 128-bit Farpointe / Pyramid frame the firmware scans and emulates:
/// a 24-bit preamble, then 15 groups of 7 data bits + odd parity, the last byte
/// holding a CRC-8/Maxim of bytes 2..14. Mirrors Proxmark's demodPyramid / getPyramidBits.
/// </summary>
public static class PyramidFrame
{
    public const int Length = 16;

    /// <summary>
    /// Format length, facility code (26- and 42-bit formats only) and card number of a frame.
    /// </summary>
    public static (int FormatLength, uint? Facility, ulong Card) Decode(ReadOnlySpan<byte> frame)
    {
        if (frame.Length != Length) throw new ArgumentException($"A Pyramid frame is {Length} bytes.");

        // Strip the parity bit of every group of bits 8..127: 15 groups -> 105 data bits.
        var data = new byte[105];
        for (var g = 0; g < 15; g++)
            for (var b = 0; b < 7; b++)
                data[g * 7 + b] = Bit(frame, 8 + g * 8 + b);

        // A leading 1 marks where the format field starts.
        var start = Array.IndexOf(data, (byte)1);
        if (start < 0) start = data.Length;
        var formatLength = data.Length - start - 8;

        return formatLength switch
        {
            26 => (26, (uint)Read(data, 73, 8), Read(data, 81, 16)),
            45 => (42, (uint)Read(data, 53, 10), Read(data, 63, 32)),
            _ => (formatLength, null, Read(data, 81, 16)),
        };
    }

    /// <summary>Builds the frame of a 26-bit card.</summary>
    public static byte[] Encode26(byte facility, ushort card)
    {
        var wiegand = new byte[24];
        for (var i = 0; i < 8; i++) wiegand[i] = (byte)((facility >> (7 - i)) & 1);
        for (var i = 0; i < 16; i++) wiegand[8 + i] = (byte)((card >> (15 - i)) & 1);

        var data = new byte[105];
        data[71] = 1; // format sentinel
        data[72] = (byte)(Ones(wiegand.AsSpan(0, 12)) & 1);        // even parity
        wiegand.CopyTo(data, 73);
        data[97] = (byte)((Ones(wiegand.AsSpan(12)) & 1) ^ 1);     // odd parity

        // Byte 0 stays 0; the empty leading groups (00000001) complete the preamble.
        var frame = new byte[Length];
        for (var g = 0; g < 15; g++)
        {
            var group = data.AsSpan(g * 7, 7);
            var value = 0;
            foreach (var bit in group) value = (value << 1) | bit;
            frame[1 + g] = (byte)((value << 1) | ((Ones(group) & 1) ^ 1)); // odd parity
        }
        frame[15] = Crc8Maxim(frame.AsSpan(2, 13));
        return frame;
    }

    private static byte Bit(ReadOnlySpan<byte> frame, int i) => (byte)((frame[i >> 3] >> (7 - (i & 7))) & 1);

    private static ulong Read(byte[] bits, int offset, int count)
    {
        var v = 0UL;
        for (var i = 0; i < count; i++) v = (v << 1) | bits[offset + i];
        return v;
    }

    private static int Ones(ReadOnlySpan<byte> bits)
    {
        var n = 0;
        foreach (var b in bits) n += b;
        return n;
    }

    private static byte Crc8Maxim(ReadOnlySpan<byte> data)
    {
        var crc = 0;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0x8C : crc >> 1;
        }
        return (byte)crc;
    }
}
