using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ChameLab.Protocol;

namespace ChameLab.Models;

/// <summary>Family of MIFARE Classic cards, distinguished by capacity.</summary>
public enum MifareClassicType
{
    Mini,
    Classic1K,
    Classic2K,
    Classic4K,
}

/// <summary>
/// Geometry helpers for MIFARE Classic cards. Sectors 0..31 hold four blocks
/// each; on a 4K card sectors 32..39 hold sixteen blocks each.
/// </summary>
public static class MifareClassic
{
    public static MifareClassicType FromSak(byte sak) => sak switch
    {
        0x09 => MifareClassicType.Mini,
        0x18 => MifareClassicType.Classic4K,
        _ => MifareClassicType.Classic1K, // 0x08/0x88/0x28 and unknown → treat as 1K
    };

    public static int SectorCount(MifareClassicType t) => t switch
    {
        MifareClassicType.Mini => 5,
        MifareClassicType.Classic2K => 32,
        MifareClassicType.Classic4K => 40,
        _ => 16,
    };

    public static int BlockCount(MifareClassicType t) => t switch
    {
        MifareClassicType.Mini => 20,
        MifareClassicType.Classic2K => 128,
        MifareClassicType.Classic4K => 256,
        _ => 64,
    };

    public static int BlocksInSector(int sector) => sector < 32 ? 4 : 16;

    /// <summary>First (absolute) block number of a sector.</summary>
    public static int SectorFirstBlock(int sector) =>
        sector < 32 ? sector * 4 : 128 + (sector - 32) * 16;

    /// <summary>Sector trailer (last block of the sector), which holds the keys.</summary>
    public static int SectorTrailerBlock(int sector) =>
        SectorFirstBlock(sector) + BlocksInSector(sector) - 1;

    public static int SectorOfBlock(int block) =>
        block < 128 ? block / 4 : 32 + (block - 128) / 16;

    public static bool IsTrailer(int block) =>
        block == SectorTrailerBlock(SectorOfBlock(block));

    /// <summary>
    /// The access bits (trailer bytes 6–8) are stored twice, once inverted; a
    /// card refuses a trailer where the copies disagree and the sector is lost.
    /// </summary>
    public static bool AccessBitsValid(ReadOnlySpan<byte> trailer)
    {
        if (trailer.Length < 9) return false;
        int b6 = trailer[6], b7 = trailer[7], b8 = trailer[8];
        return (b7 >> 4) == (~b6 & 0x0F)          // C1 vs ~C1
            && (b8 & 0x0F) == ((~b6 >> 4) & 0x0F) // C2 vs ~C2
            && (b8 >> 4) == (~b7 & 0x0F);         // C3 vs ~C3
    }

    /// <summary>
    /// Whether a card hands out key B when the trailer is read: only when the trailer's access
    /// condition (C1 C2 C3) is 000, 010 or 001, where key B serves as data. Otherwise it reads as zeros.
    /// </summary>
    public static bool KeyBReadable(ReadOnlySpan<byte> trailer)
    {
        if (!AccessBitsValid(trailer)) return false;
        var c1 = (trailer[7] >> 7) & 1;
        var c2 = (trailer[8] >> 3) & 1;
        var c3 = (trailer[8] >> 7) & 1;
        return c1 == 0 && (c2 == 0 || c3 == 0);
    }

    /// <summary>Transport trailer of a blank card: default keys, access bits FF 07 80, user byte 69.</summary>
    private static readonly byte[] BlankTrailer = Convert.FromHexString("FFFFFFFFFFFFFF078069FFFFFFFFFFFF");

    /// <summary>Content of a block on a blank card: zeros, or the transport trailer.</summary>
    public static byte[] BlankBlock(int block) =>
        IsTrailer(block) ? (byte[])BlankTrailer.Clone() : new byte[16];

    /// <summary>The block holds what a blank card has there.</summary>
    public static bool IsBlankBlock(int block, ReadOnlySpan<byte> data) =>
        IsTrailer(block) ? data.SequenceEqual(BlankTrailer) : data.Length == 16 && !data.ContainsAnyExcept((byte)0);

    public static string DisplayName(MifareClassicType t) => t switch
    {
        MifareClassicType.Mini => "MIFARE Classic Mini (320 B)",
        MifareClassicType.Classic2K => "MIFARE Classic 2K",
        MifareClassicType.Classic4K => "MIFARE Classic 4K",
        _ => "MIFARE Classic 1K",
    };

    public static TagSpecificType ToSlotTagType(MifareClassicType t) => t switch
    {
        MifareClassicType.Mini => TagSpecificType.MifareMini,
        MifareClassicType.Classic2K => TagSpecificType.Mifare2048,
        MifareClassicType.Classic4K => TagSpecificType.Mifare4096,
        _ => TagSpecificType.Mifare1024,
    };

    public static MifareClassicType FromSlotTagType(TagSpecificType t) => t switch
    {
        TagSpecificType.MifareMini => MifareClassicType.Mini,
        TagSpecificType.Mifare2048 => MifareClassicType.Classic2K,
        TagSpecificType.Mifare4096 => MifareClassicType.Classic4K,
        _ => MifareClassicType.Classic1K,
    };

    /// <summary>
    /// A small dictionary of well-known factory/default MIFARE Classic keys,
    /// tried in order when dumping an unknown card.
    /// </summary>
    public static IReadOnlyList<byte[]> DefaultKeys { get; } = new[]
    {
        "FFFFFFFFFFFF", // factory default
        "A0A1A2A3A4A5", // MAD / NDEF key A
        "D3F7D3F7D3F7", // NDEF key B
        "000000000000",
        "B0B1B2B3B4B5",
        "4D3A99C351DD",
        "1A982C7E459A",
        "AABBCCDDEEFF",
        "714C5C886E97",
        "587EE5F9350F",
        "A0478CC39091",
        "533CB6C723F6",
        "8FD0A4F256E9",
    }.Select(Hex).ToArray();

    public static byte[] Hex(string s) => Convert.FromHexString(s);
}

/// <summary>Progress tick while reading or writing a dump, one per sector.</summary>
public readonly record struct MifareProgress(int Done, int Total, int Recovered);

/// <summary>Where one block stands during a dump.</summary>
public enum MifareBlockState { Pending, Reading, Read, ReadError, NoKey }

/// <summary>Per-block tick while dumping: <see cref="First"/>..First+Count-1 moved to <see cref="State"/>.</summary>
public readonly record struct MifareBlockProgress(int First, int Count, MifareBlockState State);

/// <summary>Phase of the dictionary search for one sector key.</summary>
public enum MifareKeyPhase { Searching, Found, NotFound }

/// <summary>
/// Progress of the key search for one sector and key type: <see cref="Tried"/> of
/// <see cref="Total"/> keys attempted, and the recovered key once <see cref="Phase"/> is Found.
/// </summary>
public readonly record struct MifareKeyProgress(
    int Sector, MfKeyType KeyType, MifareKeyPhase Phase, int Tried, int Total, byte[]? Key);

/// <summary>
/// An in-memory MIFARE Classic dump: block data plus the keys recovered for
/// each sector. Supports import/export of raw <c>.bin</c> and Proxmark
/// <c>.eml</c> files.
/// </summary>
public sealed class MifareDump
{
    private readonly byte[]?[] _blocks;

    public MifareDump(MifareClassicType type, byte[]? uid = null, byte[]? atqa = null, byte sak = 0)
    {
        Type = type;
        Uid = uid ?? Array.Empty<byte>();
        Atqa = atqa ?? Array.Empty<byte>();
        Sak = sak;
        _blocks = new byte[]?[MifareClassic.BlockCount(type)];
        KeyA = new byte[]?[MifareClassic.SectorCount(type)];
        KeyB = new byte[]?[MifareClassic.SectorCount(type)];
        KeyAUnknown = new bool[MifareClassic.SectorCount(type)];
        KeyBUnknown = new bool[MifareClassic.SectorCount(type)];
    }

    public MifareClassicType Type { get; }
    public byte[] Uid { get; private set; }
    public byte[] Atqa { get; }
    public byte Sak { get; }

    public int BlockCount => _blocks.Length;
    public int SectorCount => MifareClassic.SectorCount(Type);

    /// <summary>Recovered key A / key B per sector (null = unknown).</summary>
    public byte[]?[] KeyA { get; }
    public byte[]?[] KeyB { get; }

    /// <summary>
    /// The trailer's key A / key B bytes are not the key (the card hides it and it was not
    /// recovered), so they must not be shown or tried as one.
    /// </summary>
    public bool[] KeyAUnknown { get; }
    public bool[] KeyBUnknown { get; }

    /// <summary>
    /// A sector trailer as read from a card: key A always comes back as zeros, key B too unless
    /// the access bits make it readable. <see cref="FillRecoveredKeys"/> puts known keys in.
    /// </summary>
    public void SetTrailerFromCard(int sector, byte[] trailer)
    {
        SetBlock(MifareClassic.SectorTrailerBlock(sector), trailer);
        KeyAUnknown[sector] = true;
        KeyBUnknown[sector] = !MifareClassic.KeyBReadable(trailer);
    }

    /// <summary>The key bytes a read trailer really holds (not the zeros of an unknown key).</summary>
    public IEnumerable<byte[]> TrailerKeys(int sector)
    {
        if (_blocks[MifareClassic.SectorTrailerBlock(sector)] is not { Length: 16 } trailer) yield break;
        if (!KeyAUnknown[sector]) yield return trailer[..6];
        if (!KeyBUnknown[sector]) yield return trailer[10..];
    }

    public byte[]? GetBlock(int block) => _blocks[block];
    public void SetBlock(int block, byte[]? data) => _blocks[block] = data;

    public int RecoveredBlocks => _blocks.Count(b => b is not null);

    /// <summary>Sectors for which a key A or key B is known.</summary>
    public bool SectorHasKey(int sector) => KeyA[sector] is not null || KeyB[sector] is not null;

    /// <summary>Sectors that no tried key opened.</summary>
    public IReadOnlyList<int> SectorsWithoutKey() =>
        Enumerable.Range(0, SectorCount).Where(s => !SectorHasKey(s)).ToList();

    /// <summary>Blocks in opened sectors that still could not be read (access bits or RF errors).</summary>
    public IReadOnlyList<int> UnreadBlocksWithKey() =>
        Enumerable.Range(0, SectorCount).Where(SectorHasKey)
            .SelectMany(s => Enumerable.Range(MifareClassic.SectorFirstBlock(s), MifareClassic.BlocksInSector(s)))
            .Where(b => _blocks[b] is null).ToList();

    /// <summary>
    /// Writes each recovered key A / key B into the matching sector trailer that
    /// was read, so the dump carries the keys (a card returns the key bytes as
    /// zeros when the trailer is read), and marks them known. Trailers that were not read are left alone.
    /// </summary>
    public void FillRecoveredKeys()
    {
        for (var s = 0; s < SectorCount; s++)
        {
            var trailer = _blocks[MifareClassic.SectorTrailerBlock(s)];
            if (trailer is not { Length: 16 }) continue;
            if (KeyA[s] is { Length: 6 } a) { a.CopyTo(trailer, 0); KeyAUnknown[s] = false; }
            if (KeyB[s] is { Length: 6 } b) { b.CopyTo(trailer, 10); KeyBUnknown[s] = false; }
        }
    }

    public string UidHex => Uid.Length == 0 ? "-" : Convert.ToHexString(Uid);

    // ---- Export --------------------------------------------------------

    /// <summary>Raw binary: every block concatenated, unread blocks zero-filled.</summary>
    public byte[] ToBin()
    {
        var buf = new byte[_blocks.Length * 16];
        for (var i = 0; i < _blocks.Length; i++)
            (_blocks[i] ?? new byte[16]).CopyTo(buf, i * 16);
        return buf;
    }

    /// <summary>Proxmark <c>.eml</c>: one 32-hex-char line per block.</summary>
    public string ToEml()
    {
        var sb = new StringBuilder();
        foreach (var b in _blocks)
            sb.AppendLine(Convert.ToHexString(b ?? new byte[16]));
        return sb.ToString();
    }

    // ---- Import --------------------------------------------------------

    public static MifareDump FromBin(byte[] data)
    {
        var type = TypeForBlocks(data.Length / 16);
        var dump = new MifareDump(type);
        var blocks = Math.Min(dump.BlockCount, data.Length / 16);
        for (var i = 0; i < blocks; i++)
            dump.SetBlock(i, data[(i * 16)..(i * 16 + 16)]);
        dump.CaptureIdentityFromBlock0();
        return dump;
    }

    public static MifareDump FromEml(string text)
    {
        var lines = text.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length >= 32)
            .ToArray();
        var type = TypeForBlocks(lines.Length);
        var dump = new MifareDump(type);
        for (var i = 0; i < Math.Min(dump.BlockCount, lines.Length); i++)
        {
            try { dump.SetBlock(i, Convert.FromHexString(lines[i][..32])); }
            catch (FormatException) { /* skip malformed line */ }
        }
        dump.CaptureIdentityFromBlock0();
        return dump;
    }

    private static MifareClassicType TypeForBlocks(int blocks) => blocks switch
    {
        <= 20 => MifareClassicType.Mini,
        <= 64 => MifareClassicType.Classic1K,
        <= 128 => MifareClassicType.Classic2K,
        _ => MifareClassicType.Classic4K,
    };

    private void CaptureIdentityFromBlock0()
    {
        var b0 = _blocks[0];
        if (b0 is null || b0.Length < 16) return;
        // Block 0: [UID(4)][BCC(1)][SAK(1)][ATQA(2)][manufacturer…] for a 4-byte UID.
        Uid = b0[..4];
    }
}
