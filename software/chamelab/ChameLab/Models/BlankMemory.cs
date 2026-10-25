using System;
using System.Collections.Generic;
using System.Linq;
using ChameLab.Protocol;

namespace ChameLab.Models;

/// <summary>
/// Memory of a factory-blank card of a given type, with the manufacturer area
/// built from the card's identity: what a card entered by hand starts from.
/// </summary>
public static class BlankMemory
{
    /// <summary>Tag types that have editable memory.</summary>
    public static bool Supports(TagSpecificType tag) => tag.IsMifareClassic() || tag.UltralightPages() > 0;

    /// <summary>Blank blocks / pages for <paramref name="tag"/>; empty when it has no memory.</summary>
    public static List<byte[]> Create(TagSpecificType tag, byte[] uid, byte sak, byte[] atqa)
    {
        if (tag.IsMifareClassic()) return MifareClassicBlocks(tag, uid, sak, atqa);
        if (tag.UltralightPages() > 0) return UltralightPages(tag, uid);
        return new List<byte[]>();
    }

    private static List<byte[]> MifareClassicBlocks(TagSpecificType tag, byte[] uid, byte sak, byte[] atqa)
    {
        var blocks = Enumerable.Range(0, tag.MifareClassicBlocks()).Select(MifareClassic.BlankBlock).ToList();
        var b0 = blocks[0];
        if (uid.Length == 4)
        {
            // UID, BCC, SAK, ATQA (device order, low byte first), then manufacturer data.
            uid.CopyTo(b0, 0);
            b0[4] = (byte)(uid[0] ^ uid[1] ^ uid[2] ^ uid[3]);
            b0[5] = sak;
            if (atqa.Length == 2) atqa.CopyTo(b0, 6);
        }
        else
        {
            uid.AsSpan(0, Math.Min(uid.Length, 16)).CopyTo(b0);
        }
        return blocks;
    }

    private static List<byte[]> UltralightPages(TagSpecificType tag, byte[] uid)
    {
        var count = tag.UltralightPages();
        var pages = Enumerable.Range(0, count).Select(_ => new byte[4]).ToList();

        // Pages 0–2: UID0-2 BCC0 | UID3-6 | BCC1 internal lock lock (cascade tag 0x88 in BCC0).
        var u = new byte[7];
        uid.AsSpan(0, Math.Min(uid.Length, 7)).CopyTo(u);
        pages[0] = new[] { u[0], u[1], u[2], (byte)(0x88 ^ u[0] ^ u[1] ^ u[2]) };
        pages[1] = new[] { u[3], u[4], u[5], u[6] };
        pages[2] = new[] { (byte)(u[3] ^ u[4] ^ u[5] ^ u[6]), (byte)0x48, (byte)0, (byte)0 };

        // NTAG: capability container announcing the NDEF area size.
        byte? ndefSize = tag switch
        {
            TagSpecificType.Ntag210 => 0x06,
            TagSpecificType.Ntag212 => 0x10,
            TagSpecificType.Ntag213 => 0x12,
            TagSpecificType.Ntag215 => 0x3E,
            TagSpecificType.Ntag216 => 0x6D,
            _ => null,
        };
        if (ndefSize is { } size) pages[3] = new byte[] { 0xE1, 0x10, size, 0x00 };

        // Configuration pages at the end. AUTH0 = FF (Ultralight C: 30) leaves the
        // memory unprotected; all-zero pages there would password-protect it from page 0.
        var ntag = ndefSize is not null;
        var ev1 = tag is TagSpecificType.Mf0Ul11 or TagSpecificType.Mf0Ul21;
        if (ntag || ev1)
        {
            var cfg0 = count - 4;
            pages[cfg0] = new byte[] { (byte)(ntag ? 0x04 : 0x00), 0x00, 0x00, 0xFF };
            pages[cfg0 + 1] = new byte[] { 0x00, (byte)(ev1 ? 0x05 : 0x00), 0x00, 0x00 };
            pages[cfg0 + 2] = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }; // PWD
            // Dynamic lock bytes sit right before the configuration on the larger types.
            if (ntag && tag != TagSpecificType.Ntag210) pages[cfg0 - 1] = new byte[] { 0x00, 0x00, 0x00, 0xBD };
        }
        else if (tag == TagSpecificType.Mf0Icu2)
        {
            pages[42] = new byte[] { 0x30, 0x00, 0x00, 0x00 }; // AUTH0 past the last page: no authentication
        }
        return pages;
    }
}
