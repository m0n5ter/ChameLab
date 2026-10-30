using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using ChameLab.Protocol;

namespace ChameLab.Models;

/// <summary>Extras that are not part of the block / page data.</summary>
public sealed class CardSaveExtra
{
    public byte[] UltralightVersion { get; set; } = Array.Empty<byte>();
    public byte[] UltralightSignature { get; set; } = Array.Empty<byte>();
    public List<int> UltralightCounters { get; set; } = new();

    /// <summary>
    /// MIFARE Classic sectors whose trailer was read but whose key A / key B is not known: the
    /// trailer holds zeros there, not the key. Stored as "unknown" so older cards (without the
    /// lists) keep treating every trailer key as real. ChameLab only (the Flutter app ignores them).
    /// </summary>
    public List<int> UnknownKeyA { get; set; } = new();
    public List<int> UnknownKeyB { get; set; } = new();
}

/// <summary>
/// A card saved in the local library: identity (UID/SAK/ATQA/ATS), tag type and
/// optional memory contents (16-byte MIFARE Classic blocks or 4-byte
/// Ultralight pages). For LF tags <see cref="Uid"/> holds the raw emulator ID.
/// The JSON shape matches the Flutter ChameleonUltraGUI <c>CardSave</c>, so
/// exports move freely between the two apps.
/// </summary>
public sealed class CardSave
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public TagSpecificType Tag { get; set; } = TagSpecificType.Undefined;
    public byte[] Uid { get; set; } = Array.Empty<byte>();
    public byte Sak { get; set; }
    public byte[] Atqa { get; set; } = Array.Empty<byte>();
    public byte[] Ats { get; set; } = Array.Empty<byte>();
    public List<byte[]> Data { get; set; } = new();
    public CardSaveExtra Extra { get; set; } = new();

    /// <summary>Accent color as <c>#RRGGBB</c>.</summary>
    public string Color { get; set; } = DefaultColor;

    public const string DefaultColor = "#FF4000"; // the palette color nearest Flutter's Colors.deepOrange

    /// <summary>The folder the card is filed in; empty for none. ChameLab only (the Flutter app ignores it).</summary>
    public string Folder { get; set; } = string.Empty;

    public TagSenseType Sense => Tag.SenseType();

    public string UidHex => Uid.Length == 0 ? "-" : Hex.Spaced(Uid);

    public CardSave Clone()
    {
        var json = ToJson();
        var copy = FromJson(json);
        copy.Id = Guid.NewGuid().ToString();
        return copy;
    }

    // ---- JSON ----------------------------------------------------------

    public JsonObject ToJson()
    {
        var o = new JsonObject
        {
            ["id"] = Id,
            ["uid"] = Hex.Spaced(Uid),
            ["sak"] = (int)Sak,
            ["atqa"] = ToArray(Atqa),
            ["ats"] = ToArray(Ats),
            ["name"] = Name,
            ["tag"] = (int)Tag,
            ["color"] = Color,
            ["data"] = new JsonArray(Data.Select(d => (JsonNode)ToArray(d)).ToArray()),
        };

        var extra = new JsonObject();
        if (Extra.UltralightSignature.Length > 0) extra["ultralightSignature"] = ToArray(Extra.UltralightSignature);
        if (Extra.UltralightVersion.Length > 0) extra["ultralightVersion"] = ToArray(Extra.UltralightVersion);
        if (Extra.UltralightCounters.Count > 0)
            extra["ultralightCounters"] = new JsonArray(Extra.UltralightCounters.Select(c => (JsonNode)c).ToArray());
        if (Extra.UnknownKeyA.Count > 0) extra["unknownKeyA"] = ToSectors(Extra.UnknownKeyA);
        if (Extra.UnknownKeyB.Count > 0) extra["unknownKeyB"] = ToSectors(Extra.UnknownKeyB);
        o["extra"] = extra;
        if (Folder.Length > 0) o["folder"] = Folder;
        return o;
    }

    public static CardSave FromJson(JsonNode node)
    {
        if (node is not JsonObject o)
            throw new FormatException("A card must be a JSON object.");

        var card = new CardSave
        {
            Id = (string?)o["id"] ?? Guid.NewGuid().ToString(),
            Name = (string?)o["name"] ?? string.Empty,
            Uid = Hex.Parse((string?)o["uid"] ?? string.Empty),
            Sak = (byte)((int?)o["sak"] ?? 0),
            Atqa = FromArray(o["atqa"]),
            Ats = FromArray(o["ats"]),
            Tag = ParseTag(o["tag"]),
            Color = NormalizeColor((string?)o["color"]),
            Folder = NormalizeFolder((string?)o["folder"]),
        };
        // Older versions saved the ATQA of cards entered by hand high byte first.
        card.Atqa = CardTags.FixSwappedAtqa(card.Tag, card.Atqa);

        if (o["data"] is JsonArray data)
            card.Data = data.Select(FromArray).ToList();

        if (o["extra"] is JsonObject extra)
        {
            card.Extra.UltralightSignature = FromArray(extra["ultralightSignature"]);
            card.Extra.UltralightVersion = FromArray(extra["ultralightVersion"]);
            if (extra["ultralightCounters"] is JsonArray counters)
                card.Extra.UltralightCounters = counters.Select(c => (int?)c ?? 0).ToList();
            card.Extra.UnknownKeyA = FromSectors(extra["unknownKeyA"]);
            card.Extra.UnknownKeyB = FromSectors(extra["unknownKeyB"]);
        }
        return card;
    }

    public static CardSave FromJson(string json) =>
        FromJson(JsonNode.Parse(json) ?? throw new FormatException("Empty JSON document."));

    private static JsonArray ToArray(byte[] bytes) =>
        new(bytes.Select(b => (JsonNode)(int)b).ToArray());

    private static byte[] FromArray(JsonNode? node) => node is JsonArray a
        ? a.Select(v => (byte)((int?)v ?? 0)).ToArray()
        : Array.Empty<byte>();

    private static JsonArray ToSectors(IEnumerable<int> sectors) =>
        new(sectors.Distinct().Order().Select(s => (JsonNode)s).ToArray());

    private static List<int> FromSectors(JsonNode? node) => node is JsonArray a
        ? a.Select(v => (int?)v ?? -1).Where(s => s >= 0).Distinct().Order().ToList()
        : new List<int>();

    private static TagSpecificType ParseTag(JsonNode? node)
    {
        // Flutter writes the numeric tag value; tolerate enum names as well.
        if (node is JsonValue v)
        {
            if (v.TryGetValue<int>(out var n) && Enum.IsDefined(typeof(TagSpecificType), (ushort)n))
                return (TagSpecificType)n;
            if (v.TryGetValue<string>(out var s) && Enum.TryParse<TagSpecificType>(s, true, out var t))
                return t;
        }
        return TagSpecificType.Undefined;
    }

    /// <summary>Longest folder name kept.</summary>
    public const int MaxFolderLength = 64;

    public static string NormalizeFolder(string? folder)
    {
        var f = folder?.Trim() ?? string.Empty;
        return f.Length > MaxFolderLength ? f[..MaxFolderLength].TrimEnd() : f;
    }

    public static string NormalizeColor(string? color)
    {
        if (string.IsNullOrWhiteSpace(color)) return DefaultColor;
        var c = color.Trim().TrimStart('#');
        if (c.Length == 8) c = c[2..]; // AARRGGBB → RRGGBB
        return c.Length == 6 && Hex.IsHex(c) ? "#" + c.ToUpperInvariant() : DefaultColor;
    }
}

/// <summary>Hex string helpers tolerant of spaces, colons and dashes.</summary>
public static class Hex
{
    public static string Spaced(byte[] bytes) =>
        string.Join(' ', bytes.Select(b => b.ToString("X2")));

    public static string Compact(byte[] bytes) => Convert.ToHexString(bytes);

    public static byte[] Parse(string text)
    {
        var clean = new string(text.Where(c => !char.IsWhiteSpace(c) && c != ':' && c != '-').ToArray());
        if (clean.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) clean = clean[2..];
        if (clean.Length % 2 != 0) throw new FormatException("Hex value must have an even number of digits.");
        return Convert.FromHexString(clean);
    }

    public static bool TryParse(string? text, out byte[] bytes)
    {
        try { bytes = Parse(text ?? string.Empty); return true; }
        catch (FormatException) { bytes = Array.Empty<byte>(); return false; }
    }

    public static bool IsHex(string s) => s.All(Uri.IsHexDigit);
}
