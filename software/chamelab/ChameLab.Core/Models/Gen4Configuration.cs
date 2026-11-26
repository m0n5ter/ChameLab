using System;
using ChameLab.Localization;

namespace ChameLab.Models;

/// <summary>
/// Gen4 Ultimate / GTU magic card 30-byte configuration layout, as documented by Proxmark3
/// (<c>magic_cards_notes.md#g4top</c>). GDM/USCUID cards use a different protocol and are not
/// handled here.
/// </summary>
public sealed class Gen4Configuration
{
    public const int ByteLength = 30;
    public const int MaxAtsLength = 16;

    private readonly byte[] _bytes;

    public Gen4Configuration(ReadOnlySpan<byte> data)
    {
        if (data.Length != ByteLength || data[7] > MaxAtsLength)
            throw new FormatException(Strings.Tools_Gen4InvalidConfig);
        _bytes = data.ToArray();
    }

    /// <summary>
    /// Decodes a <c>0xC6</c> configuration response, stripping optional trailing internal checksum
    /// and/or RF CRC bytes (30, 32, or 34 bytes).
    /// </summary>
    public static Gen4Configuration FromResponse(ReadOnlySpan<byte> data)
    {
        if (data.Length is not (30 or 32 or 34))
            throw new FormatException(Strings.Tools_Gen4UnexpectedConfigLength);
        return new Gen4Configuration(data[..ByteLength]);
    }

    public byte[] Bytes => (byte[])_bytes.Clone();
    public ReadOnlySpan<byte> RawBytes => _bytes;

    public byte Protocol => _bytes[0];
    public byte UidSizeCode => _bytes[1];

    public int UidLength => _bytes[1] switch
    {
        0 => 4,
        1 => 7,
        2 => 10,
        _ => 0,
    };

    public byte[] Password => _bytes[2..6];
    public byte ShadowMode => _bytes[6];
    public byte[] Ats => _bytes[8..(8 + _bytes[7])];

    /// <summary>ATQA is stored least-significant byte first on the card; returned in display order.</summary>
    public byte[] Atqa => [_bytes[25], _bytes[24]];

    public byte Sak => _bytes[26];
    public byte UltralightMode => _bytes[27];
    public byte MaxBlock => _bytes[28];
    public byte Block0Mode => _bytes[29];

    /// <summary>
    /// Returns a new configuration with the editable fields updated while retaining unused ATS bytes
    /// from the original configuration for a lossless round trip.
    /// </summary>
    public Gen4Configuration WithEdits(
        byte protocol,
        byte uidSizeCode,
        ReadOnlySpan<byte> password,
        byte shadowMode,
        ReadOnlySpan<byte> ats,
        ReadOnlySpan<byte> atqa,
        byte sak,
        byte ultralightMode,
        byte maxBlock,
        byte block0Mode)
    {
        if (password.Length != 4) throw new ArgumentException("Password must be 4 bytes.", nameof(password));
        if (ats.Length > MaxAtsLength) throw new ArgumentException("ATS must be at most 16 bytes.", nameof(ats));
        if (atqa.Length != 2) throw new ArgumentException("ATQA must be 2 bytes.", nameof(atqa));

        var edited = (byte[])_bytes.Clone();
        edited[0] = protocol;
        edited[1] = uidSizeCode;
        password.CopyTo(edited.AsSpan(2, 4));
        edited[6] = shadowMode;
        edited[7] = (byte)ats.Length;
        ats.CopyTo(edited.AsSpan(8, ats.Length));
        edited[24] = atqa[1];
        edited[25] = atqa[0];
        edited[26] = sak;
        edited[27] = ultralightMode;
        edited[28] = maxBlock;
        edited[29] = block0Mode;
        return new Gen4Configuration(edited);
    }
}
