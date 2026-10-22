using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using ChameLab.Protocol;

namespace ChameLab.Models;

/// <summary>Ultralight / NTAG extras that are not part of the page data.</summary>
public sealed class CardSaveExtra
{
    public byte[] UltralightVersion { get; set; } = Array.Empty<byte>();
    public byte[] UltralightSignature { get; set; } = Array.Empty<byte>();
    public List<int> UltralightCounters { get; set; } = new();

    public bool IsEmpty =>
        UltralightVersion.Length == 0 && UltralightSignature.Length == 0 && UltralightCounters.Count == 0;
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
        o["extra"] = extra;
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
        };

        if (o["data"] is JsonArray data)
            card.Data = data.Select(FromArray).ToList();

        if (o["extra"] is JsonObject extra)
        {
            card.Extra.UltralightSignature = FromArray(extra["ultralightSignature"]);
            card.Extra.UltralightVersion = FromArray(extra["ultralightVersion"]);
            if (extra["ultralightCounters"] is JsonArray counters)
                card.Extra.UltralightCounters = counters.Select(c => (int?)c ?? 0).ToList();
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
