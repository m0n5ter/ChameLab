using System;
using System.Buffers.Binary;
using System.Linq;
using ChameLab.Protocol;

namespace ChameLab.Models;

/// <summary>
/// Random identities for cards entered by hand: a UID an ISO 14443-A reader accepts, or an LF ID
/// the firmware can emulate as is.
/// </summary>
public static class RandomUid
{
    // NXP: the manufacturer byte of 7- and 10-byte UIDs.
    private const byte Nxp = 0x04;
    // Cascade tag: never a UID byte that opens a cascade level.
    private const byte CascadeTag = 0x88;
    // A 4-byte UID starting with 08 says it is random per session.
    private const byte RandomSingleSize = 0x08;

    /// <summary>
    /// Whether <see cref="Generate"/> can make an ID for <paramref name="tag"/>. ioProx and IDTECK
    /// frames carry check bits or fixed fields random bytes would break.
    /// </summary>
    public static bool Supports(TagSpecificType tag) =>
        tag.SenseType() == TagSenseType.Hf
        || tag is TagSpecificType.Em410x or TagSpecificType.Em410xElectra or TagSpecificType.Em410x16 or TagSpecificType.Em410x32 or TagSpecificType.Em410x64
            or TagSpecificType.Viking or TagSpecificType.Jablotron or TagSpecificType.Pac
            or TagSpecificType.HidProx or TagSpecificType.FarpointePyramid;

    /// <summary>
    /// A random UID / ID for <paramref name="tag"/>. An HF UID keeps <paramref name="currentLength"/>
    /// when it is a valid UID size for the type (Ultralight / NTAG are always 7 bytes), else 4 bytes.
    /// </summary>
    public static byte[] Generate(TagSpecificType tag, int currentLength = 0, Random? random = null)
    {
        random ??= Random.Shared;
        if (tag.SenseType() == TagSenseType.Hf)
        {
            var length = tag.IsUltralight() ? 7 : currentLength is 4 or 7 or 10 ? currentLength : 4;
            return Hf(length, random);
        }

        switch (tag)
        {
            case TagSpecificType.FarpointePyramid:
                return PyramidFrame.Encode26((byte)random.Next(256), (ushort)random.Next(65536));
            case TagSpecificType.HidProx:
                return HidProxH10301((byte)random.Next(256), (ushort)random.Next(65536));
            case TagSpecificType.Em410xElectra:
                return Electra(random);
            case TagSpecificType.Pac:
                // Eight ASCII hex digits, as PAC / Stanley cards carry them.
                return Enumerable.Range(0, 8).Select(_ => (byte)"0123456789ABCDEF"[random.Next(16)]).ToArray();
        }

        var len = tag.LfIdLength();
        if (len == 0 || !Supports(tag)) throw new NotSupportedException($"No random ID for {tag}.");
        var id = new byte[len];
        random.NextBytes(id);
        return id;
    }

    private static byte[] Hf(int length, Random random)
    {
        var uid = new byte[length];
        random.NextBytes(uid);
        if (length == 4)
        {
            while (uid[0] is CascadeTag or RandomSingleSize) uid[0] = (byte)random.Next(256);
        }
        else
        {
            uid[0] = Nxp;
            // Byte 3 opens cascade level 2 (and byte 6 level 3 of a 10-byte UID).
            while (uid[3] == CascadeTag) uid[3] = (byte)random.Next(256);
        }
        return uid;
    }

    /// <summary>
    /// EM4100 Electra: the 5-byte EM ID and the 64-bit extension sent raw after the EM frame. The
    /// extension never holds nine ones in a row, the EM frame header, so a reader can't sync on it.
    /// </summary>
    private static byte[] Electra(Random random)
    {
        var id = new byte[13];
        random.NextBytes(id.AsSpan(0, 5));
        ulong extension;
        do extension = (ulong)random.NextInt64() ^ ((ulong)random.Next(2) << 63);
        while (HasNineOnes(extension));
        BinaryPrimitives.WriteUInt64BigEndian(id.AsSpan(5), extension);
        return id;
    }

    private static bool HasNineOnes(ulong bits)
    {
        var run = bits;
        for (var i = 1; i < 9; i++) run &= bits >> i;
        return run != 0;
    }

    /// <summary>HID Prox emulator ID of an H10301 (26-bit) card: format, facility, card number, issue level, OEM.</summary>
    private static byte[] HidProxH10301(byte facility, ushort card)
    {
        var id = new byte[13];
        id[0] = 1; // H10301 in the firmware's Wiegand format list
        BinaryPrimitives.WriteUInt32BigEndian(id.AsSpan(1), facility);
        BinaryPrimitives.WriteUInt32BigEndian(id.AsSpan(6), card);
        return id;
    }
}
