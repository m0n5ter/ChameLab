using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ChameLab.Protocol;

namespace ChameLab.Models;

/// <summary>Tag-family helpers used when saving and emulating cards.</summary>
public static class CardTags
{
    public static bool IsMifareClassic(this TagSpecificType t) =>
        t is TagSpecificType.MifareMini or TagSpecificType.Mifare1024
          or TagSpecificType.Mifare2048 or TagSpecificType.Mifare4096;

    public static bool IsUltralight(this TagSpecificType t) =>
        t is >= TagSpecificType.Ntag213 and <= TagSpecificType.Ntag212;

    public static bool IsEm410x(this TagSpecificType t) =>
        t is >= TagSpecificType.Em410x and <= TagSpecificType.Em410xElectra;

    public static int MifareClassicBlocks(this TagSpecificType t) => t switch
    {
        TagSpecificType.MifareMini => 20,
        TagSpecificType.Mifare1024 => 64,
        TagSpecificType.Mifare2048 => 128,
        TagSpecificType.Mifare4096 => 256,
        _ => 0,
    };

    public static int UltralightPages(this TagSpecificType t) => t switch
    {
        TagSpecificType.Mf0Icu1 => 16,
        TagSpecificType.Mf0Icu2 => 48,
        TagSpecificType.Mf0Ul11 => 20,
        TagSpecificType.Mf0Ul21 => 41,
        TagSpecificType.Ntag210 => 20,
        TagSpecificType.Ntag212 => 41,
        TagSpecificType.Ntag213 => 45,
        TagSpecificType.Ntag215 => 135,
        TagSpecificType.Ntag216 => 231,
        _ => 0,
    };

    /// <summary>
    /// First configuration page (CFG0) of Ultralight EV1 / NTAG21x, where CFG0, CFG1, PWD and
    /// PACK close the memory; -1 for the other types.
    /// </summary>
    public static int UltralightCfg0Page(this TagSpecificType t) =>
        t.HasUltralightCounters() ? t.UltralightPages() - 4 : -1;

    /// <summary>Page of the dynamic lock bytes (larger EV1 / NTAG21x, Ultralight C); -1 when there are none.</summary>
    public static int UltralightDynamicLockPage(this TagSpecificType t) => t switch
    {
        TagSpecificType.Mf0Icu2 => 0x28,
        TagSpecificType.Mf0Ul21 or TagSpecificType.Ntag212 or TagSpecificType.Ntag213
            or TagSpecificType.Ntag215 or TagSpecificType.Ntag216 => t.UltralightPages() - 5,
        _ => -1,
    };

    /// <summary>Ultralight C: the 3DES key occupies pages 0x2C–0x2F; AUTH0 / AUTH1 are 0x2A / 0x2B.</summary>
    public const int UlcFirstKeyPage = 0x2C;

    /// <summary>Ultralight EV1 and NTAG21x carry one-way counters.</summary>
    public static bool HasUltralightCounters(this TagSpecificType t) =>
        t is TagSpecificType.Mf0Ul11 or TagSpecificType.Mf0Ul21
          or TagSpecificType.Ntag210 or TagSpecificType.Ntag212
          or TagSpecificType.Ntag213 or TagSpecificType.Ntag215 or TagSpecificType.Ntag216;

    public static TagSpecificType MifareClassicForBlocks(int blocks) => blocks switch
    {
        <= 20 => TagSpecificType.MifareMini,
        <= 64 => TagSpecificType.Mifare1024,
        <= 128 => TagSpecificType.Mifare2048,
        _ => TagSpecificType.Mifare4096,
    };

    /// <summary>
    /// Types a saved card of this type can be switched to without its data losing meaning:
    /// the other MIFARE Classic sizes, the other Ultralight / NTAG variants, the other
    /// EM410x variants with the same ID length; otherwise just the type itself.
    /// </summary>
    public static IReadOnlyList<TagSpecificType> CompatibleTypes(this TagSpecificType t)
    {
        Func<TagSpecificType, bool>? same =
            t.IsMifareClassic() ? x => x.IsMifareClassic()
            : t.IsUltralight() ? x => x.IsUltralight()
            : t.IsEm410x() ? x => x.IsEm410x() && x.LfIdLength() == t.LfIdLength()
            : null;
        return same is null ? new[] { t } : TagTypeExtensions.All().Where(same).ToArray();
    }

    /// <summary>
    /// Usual SAK and ATQA of a MIFARE Classic or Ultralight type, for a 4- or 7-byte UID;
    /// null for other types. ATQA is in device order (as sent, low byte first), like a scan.
    /// </summary>
    public static (byte Sak, byte[] Atqa)? DefaultSakAtqa(this TagSpecificType t, bool sevenByteUid)
    {
        var uidBit = sevenByteUid ? (byte)0x40 : (byte)0;
        return t switch
        {
            TagSpecificType.MifareMini => (0x09, new byte[] { (byte)(0x04 | uidBit), 0x00 }),
            TagSpecificType.Mifare1024 => (0x08, new byte[] { (byte)(0x04 | uidBit), 0x00 }),
            TagSpecificType.Mifare2048 => (0x19, new byte[] { (byte)(0x04 | uidBit), 0x00 }),
            TagSpecificType.Mifare4096 => (0x18, new byte[] { (byte)(0x02 | uidBit), 0x00 }),
            _ when t.IsUltralight() => (0x00, new byte[] { 0x44, 0x00 }),
            _ => null,
        };
    }

    /// <summary>
    /// Puts a MIFARE Classic / Ultralight ATQA saved high byte first (00 04, as older cards
    /// entered by hand and some imports have it) back in device order. A valid ATQA of these
    /// types never has a zero low byte, so the swap cannot hit a correct one.
    /// </summary>
    public static byte[] FixSwappedAtqa(TagSpecificType t, byte[] atqa) =>
        atqa is [0x00, not 0x00] && t.DefaultSakAtqa(false) is not null ? new[] { atqa[1], atqa[0] } : atqa;

    /// <summary>
    /// Swaps the two ATQA bytes between device order (low byte first, 04 00, how it is stored,
    /// sent and written into block 0) and the high-byte-first form shown to the user (00 04, as in
    /// ChameleonUltraGUI). A value that is not 2 bytes is returned unchanged.
    /// </summary>
    public static byte[] SwapAtqa(byte[] atqa) =>
        atqa.Length == 2 ? new[] { atqa[1], atqa[0] } : atqa;

    /// <summary>Whether this GUI can write the card type onto a physical tag (T55xx, magic MFC or Ultralight / NTAG user pages).</summary>
    public static bool CanWriteToTag(this TagSpecificType t) =>
        t.IsMifareClassic()
        || t.UltralightPages() > 0
        || t is TagSpecificType.HidProx or TagSpecificType.IoProx or TagSpecificType.Viking
             or TagSpecificType.Pac or TagSpecificType.Jablotron or TagSpecificType.Idteck
        || t.IsEm410x();

    /// <summary>Expected emulator-ID length for an LF tag type (0 = unknown / any).</summary>
    public static int LfIdLength(this TagSpecificType t) => t switch
    {
        TagSpecificType.Em410x or TagSpecificType.Em410x16
            or TagSpecificType.Em410x32 or TagSpecificType.Em410x64 => 5,
        TagSpecificType.Em410xElectra => 13,
        TagSpecificType.HidProx => 13,
        TagSpecificType.IoProx => 16,
        TagSpecificType.Viking => 4,
        TagSpecificType.Pac => 8,
        TagSpecificType.Jablotron => 5,
        TagSpecificType.Idteck => 8,
        TagSpecificType.FarpointePyramid => PyramidFrame.Length,
        _ => 0,
    };
}

/// <summary>
/// Builds <see cref="CardSave"/>s from scans, dumps and the file formats the
/// Flutter app imports: its own JSON, Proxmark3 JSON, Flipper NFC / RFID,
/// MIFARE Classic Tool, plus raw <c>.bin</c> / <c>.eml</c> dumps.
/// </summary>
public static class CardFormats
{
    public static readonly string[] ImportExtensions = { "json", "nfc", "rfid", "mct", "txt", "bin", "eml", "dump", "mfd" };

    // ---- From device data ---------------------------------------------

    public static CardSave FromHf14a(Hf14aTag tag, string? name = null) => new()
    {
        Name = name ?? $"HF {tag.UidHex}",
        Tag = GuessHfType(tag.Sak, tag.Atqa),
        Uid = tag.Uid,
        Sak = tag.Sak,
        Atqa = tag.Atqa,
        Ats = tag.Ats,
    };

    public static CardSave FromEm410x(Em410xTag tag, string? name = null) => new()
    {
        Name = name ?? $"{tag.Type.DisplayName()} {tag.IdHex}",
        Tag = tag.Type,
        Uid = tag.Id,
    };

    public static CardSave FromMifareDump(MifareDump dump, string? name = null)
    {
        var card = new CardSave
        {
            Name = name ?? $"MFC {dump.UidHex}",
            Tag = MifareClassic.ToSlotTagType(dump.Type),
            Uid = dump.Uid,
            Sak = dump.Sak,
            Atqa = dump.Atqa,
        };
        for (var b = 0; b < dump.BlockCount; b++)
            card.Data.Add(dump.GetBlock(b) ?? Array.Empty<byte>());
        FillIdentityFromBlock0(card);
        return card;
    }

    /// <summary>Rebuilds a MIFARE Classic dump from a saved card; empty rows stay unread.</summary>
    public static MifareDump ToMifareDump(this CardSave card)
    {
        var dump = new MifareDump(MifareClassic.FromSlotTagType(card.Tag), card.Uid, card.Atqa, card.Sak);
        for (var b = 0; b < Math.Min(card.Data.Count, dump.BlockCount); b++)
            if (card.Data[b].Length == 16) dump.SetBlock(b, card.Data[b]);
        return dump;
    }

    /// <summary>Best guess of the slot tag type from SAK (and ATQA for Ultralight).</summary>
    public static TagSpecificType GuessHfType(byte sak, byte[] atqa) => sak switch
    {
        0x09 => TagSpecificType.MifareMini,
        0x08 or 0x88 or 0x28 => TagSpecificType.Mifare1024,
        0x10 => TagSpecificType.Mifare2048,
        0x18 or 0x38 => TagSpecificType.Mifare4096,
        0x00 => TagSpecificType.Ntag215, // Ultralight family; exact model needs GET_VERSION
        0x20 or 0x24 => TagSpecificType.Hf14a4,
        _ => TagSpecificType.Hf14a4,
    };

    // ---- File import ---------------------------------------------------

    /// <summary>Parses one or more cards from a file, sniffing its format.</summary>
    public static IReadOnlyList<CardSave> Import(string fileName, byte[] bytes)
    {
        var baseName = System.IO.Path.GetFileNameWithoutExtension(fileName);
        var ext = System.IO.Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();

        if (ext is "bin" or "dump" or "mfd")
            return new[] { Named(FromMifareDump(MifareDump.FromBin(bytes)), baseName) };

        var text = Encoding.UTF8.GetString(bytes).TrimStart('﻿');

        if (ext == "eml")
            return new[] { Named(FromMifareDump(MifareDump.FromEml(text)), baseName) };

        if (text.Contains("Filetype: Flipper NFC device"))
            return new[] { Named(FromFlipperNfc(text), baseName) };
        if (text.Contains("Filetype: Flipper RFID key"))
            return new[] { Named(FromFlipperRfid(text), baseName) };
        if (text.Contains("+Sector:"))
            return new[] { Named(FromMct(text), baseName) };

        var node = JsonNode.Parse(text) ?? throw new FormatException("Empty file.");
        if (node is JsonArray list)
            return list.Where(n => n is not null).Select(n => CardSave.FromJson(n!)).ToList();
        if (node["Created"]?.ToString() == "proxmark3" || node["Card"] is not null && node["blocks"] is not null)
            return new[] { Named(FromProxmarkJson(node), baseName) };
        if (node["cards"] is JsonArray wrapped)
            return wrapped.Where(n => n is not null).Select(n => CardSave.FromJson(n!)).ToList();

        var card = CardSave.FromJson(node);
        if (string.IsNullOrWhiteSpace(card.Name)) card.Name = baseName;
        return new[] { card };
    }

    private static CardSave Named(CardSave card, string name)
    {
        if (!string.IsNullOrWhiteSpace(name)) card.Name = name;
        return card;
    }

    public static CardSave FromProxmarkJson(JsonNode root)
    {
        var info = root["Card"] ?? throw new FormatException("Proxmark JSON has no Card section.");
        var card = new CardSave
        {
            Uid = Hex.Parse(info["UID"]?.ToString() ?? string.Empty),
            Sak = FirstByte(info["SAK"]?.ToString()),
            Atqa = SwapAtqa(info["ATQA"]?.ToString()),
            Color = CardSave.DefaultColor,
        };

        var blocks = new List<byte[]>();
        if (root["blocks"] is JsonObject b)
            for (var i = 0; b[i.ToString()] is { } v; i++)
                blocks.Add(Hex.Parse(v.ToString()));

        card.Data = blocks;
        card.Tag = blocks.Count > 0 && blocks[0].Length > 16
            ? TagSpecificType.Undefined
            : CardTags.MifareClassicForBlocks(blocks.Count);
        card.Name = card.UidHex;
        return card;
    }

    public static CardSave FromFlipperNfc(string text)
    {
        var card = new CardSave
        {
            Uid = Hex.Parse(Match(text, @"UID:\s*([0-9A-Fa-f ]+)") ?? throw new FormatException("Flipper file has no UID.")),
            Sak = FirstByte(Match(text, @"SAK:\s*([0-9A-Fa-f ]+)")),
            Atqa = Hex.Parse(Match(text, @"ATQA:\s*([0-9A-Fa-f ]+)") ?? string.Empty),
        };

        var device = Match(text, @"Device type:\s*(.+)")?.Trim() ?? string.Empty;
        var lines = text.Split('\n').Select(l => l.Trim()).ToArray();

        var pages = lines.Where(l => l.StartsWith("Page ", StringComparison.Ordinal)).ToList();
        if (pages.Count > 0)
        {
            card.Tag = FlipperUltralightType(device);
            card.Data = pages.Select(l => Hex.Parse(l[(l.IndexOf(':') + 1)..].Replace("??", "00"))).ToList();
            if (Match(text, @"Signature:\s*([0-9A-Fa-f ]+)") is { } sig) card.Extra.UltralightSignature = Hex.Parse(sig);
            if (Match(text, @"Mifare version:\s*([0-9A-Fa-f ]+)") is { } ver) card.Extra.UltralightVersion = Hex.Parse(ver);
            foreach (Match m in Regex.Matches(text, @"Counter \d+:\s*(\d+)"))
                card.Extra.UltralightCounters.Add(int.Parse(m.Groups[1].Value));
        }
        else
        {
            var blocks = lines.Where(l => l.StartsWith("Block ", StringComparison.Ordinal))
                .Select(l => Hex.Parse(l[(l.IndexOf(':') + 1)..].Replace("??", "00")))
                .ToList();
            card.Data = blocks;
            card.Tag = blocks.Count > 0
                ? CardTags.MifareClassicForBlocks(blocks.Count)
                : GuessHfType(card.Sak, card.Atqa);
        }

        // Older Flipper files write ATQA high byte first, newer ones as sent.
        card.Atqa = CardTags.FixSwappedAtqa(card.Tag, card.Atqa);
        card.Name = card.UidHex;
        return card;
    }

    private static TagSpecificType FlipperUltralightType(string device) => device.ToUpperInvariant() switch
    {
        "NTAG210" => TagSpecificType.Ntag210,
        "NTAG212" => TagSpecificType.Ntag212,
        "NTAG213" => TagSpecificType.Ntag213,
        "NTAG215" => TagSpecificType.Ntag215,
        "NTAG216" => TagSpecificType.Ntag216,
        "MIFARE ULTRALIGHT C" => TagSpecificType.Mf0Icu2,
        "MIFARE ULTRALIGHT 11" => TagSpecificType.Mf0Ul11,
        "MIFARE ULTRALIGHT 21" => TagSpecificType.Mf0Ul21,
        _ => TagSpecificType.Mf0Icu1,
    };

    public static CardSave FromFlipperRfid(string text)
    {
        var type = Match(text, @"Key type:\s*(.+)")?.Trim() ?? string.Empty;
        var data = Hex.Parse(Match(text, @"Data:\s*([0-9A-Fa-f ]+)") ?? throw new FormatException("Flipper file has no Data."));

        var card = new CardSave();
        switch (type)
        {
            case "EM4100": card.Tag = TagSpecificType.Em410x64; card.Uid = data; break;
            case "EM4100/32": card.Tag = TagSpecificType.Em410x32; card.Uid = data; break;
            case "EM4100/16": card.Tag = TagSpecificType.Em410x16; card.Uid = data; break;
            case "H10301":
                // [hidType=1][FC u32][card number 5 bytes][issue level][OEM u16]
                if (data.Length < 3) throw new FormatException("H10301 data must be 3 bytes.");
                card.Tag = TagSpecificType.HidProx;
                card.Uid = new byte[] { 1, 0, 0, 0, data[0], 0, 0, 0, data[1], data[2], 0, 0, 0 };
                break;
            default:
                throw new FormatException($"Flipper RFID key type '{type}' is not supported yet.");
        }
        card.Name = card.UidHex;
        return card;
    }

    public static CardSave FromMct(string text)
    {
        var blocks = text.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("+Sector", StringComparison.Ordinal))
            .Select(l => Hex.Parse(l.Replace('-', '0')))
            .ToList();
        if (blocks.Count == 0 || blocks[0].Length < 16)
            throw new FormatException("MIFARE Classic Tool dump has no block 0.");

        var b0 = blocks[0];
        var card = new CardSave
        {
            Uid = b0[..4],
            Sak = b0[5],
            Atqa = b0[6..8],
            Data = blocks,
            Tag = CardTags.MifareClassicForBlocks(blocks.Count),
        };
        card.Name = card.UidHex;
        return card;
    }

    /// <summary>Takes UID/SAK/ATQA from block 0 when the dump came without them.</summary>
    private static void FillIdentityFromBlock0(CardSave card)
    {
        if (card.Data.Count == 0 || card.Data[0].Length < 16) return;
        var b0 = card.Data[0];
        if (card.Uid.Length == 0) card.Uid = b0[..4];
        if (card.Sak == 0) card.Sak = b0[5];
        if (card.Atqa.Length == 0) card.Atqa = b0[6..8];
    }

    private static string? Match(string text, string pattern)
    {
        var m = Regex.Match(text, pattern);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    private static byte FirstByte(string? hex) =>
        Hex.TryParse(hex, out var b) && b.Length > 0 ? b[0] : (byte)0;

    /// <summary>Proxmark / MCT write ATQA as a big-endian word; the device sends it LSB first.</summary>
    private static byte[] SwapAtqa(string? hex) =>
        Hex.TryParse(hex, out var b) && b.Length == 2 ? new[] { b[1], b[0] } : b;

    // ---- File export ---------------------------------------------------

    public static byte[] ExportJson(CardSave card) =>
        Encoding.UTF8.GetBytes(card.ToJson().ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
}
