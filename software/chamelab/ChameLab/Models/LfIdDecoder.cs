using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using ChameLab.Protocol;

namespace ChameLab.Models;

/// <summary>
/// Human-readable fields packed in an LF emulator ID (the raw buffer the firmware
/// scans and emulates, kept in <see cref="CardSave.Uid"/>).
/// </summary>
public static class LfIdDecoder
{
    /// <summary>
    /// Decoded fields for protocols whose ID is not readable as is (HID Prox, ioProx, PAC, Pyramid);
    /// empty for the others or when <paramref name="id"/> is not the protocol's full length.
    /// </summary>
    public static IReadOnlyList<(string Field, string Value)> Decode(TagSpecificType type, ReadOnlySpan<byte> id)
    {
        if (id.Length == 0 || id.Length != type.LfIdLength()) return Array.Empty<(string, string)>();
        return type switch
        {
            TagSpecificType.HidProx => DecodeHidProx(id),
            TagSpecificType.IoProx => DecodeIoProx(id),
            TagSpecificType.Pac => new[] { ("Card ID (text)", System.Text.Encoding.ASCII.GetString(id)) },
            TagSpecificType.FarpointePyramid => DecodePyramid(id),
            _ => Array.Empty<(string, string)>(),
        };
    }

    private static List<(string, string)> DecodeHidProx(ReadOnlySpan<byte> id)
    {
        // >B I B I B H : format, facility code, cn-high, cn-low, issue level, OEM (13 bytes).
        var format = id[0];
        var fc = BinaryPrimitives.ReadUInt32BigEndian(id[1..]);
        var cn = ((ulong)id[5] << 32) | BinaryPrimitives.ReadUInt32BigEndian(id[6..]);
        var il = id[10];
        var oem = BinaryPrimitives.ReadUInt16BigEndian(id[11..]);
        var fields = new List<(string, string)> { ("Card number", cn.ToString()), ("Facility code", fc.ToString()) };
        if (il != 0) fields.Add(("Issue level", il.ToString()));
        if (oem != 0) fields.Add(("OEM", oem.ToString()));
        fields.Add(("Format", format.ToString()));
        return fields;
    }

    private static (string, string)[] DecodeIoProx(ReadOnlySpan<byte> id)
    {
        // >B B H 8s B B B B : version, facility, card number, raw8, padding (16 bytes).
        return new[]
        {
            ("Card number", BinaryPrimitives.ReadUInt16BigEndian(id[2..]).ToString()),
            ("Facility code", id[1].ToString()),
            ("Version", id[0].ToString()),
        };
    }

    private static List<(string, string)> DecodePyramid(ReadOnlySpan<byte> id)
    {
        var (format, fc, cn) = PyramidFrame.Decode(id);
        var fields = new List<(string, string)> { ("Card number", cn.ToString()) };
        if (fc is { } facility) fields.Add(("Facility code", facility.ToString()));
        fields.Add(("Format", $"{format}-bit"));
        return fields;
    }
}
