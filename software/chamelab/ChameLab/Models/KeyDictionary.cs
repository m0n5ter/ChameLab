using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace ChameLab.Models;

/// <summary>
/// A named set of MIFARE Classic keys (6 bytes each). The JSON shape matches
/// the Flutter ChameleonUltraGUI <c>Dictionary</c> so dictionaries move between
/// the two apps; a plain <c>.dic</c> / <c>.keys</c> / <c>.txt</c> file (one hex key per line)
/// also imports and exports.
/// </summary>
public sealed class KeyDictionary
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Color { get; set; } = CardSave.DefaultColor;

    /// <summary>6-byte keys.</summary>
    public List<byte[]> Keys { get; set; } = new();

    public int Count => Keys.Count;

    public KeyDictionary Clone()
    {
        var copy = FromJson(ToJson());
        copy.Id = Guid.NewGuid().ToString();
        return copy;
    }

    // ---- JSON (Flutter-compatible) ------------------------------------

    public JsonObject ToJson() => new()
    {
        ["id"] = Id,
        ["name"] = Name,
        ["color"] = Color,
        ["keyLength"] = 12,
        ["keys"] = new JsonArray(Keys.Select(k =>
            (JsonNode)new JsonArray(k.Select(b => (JsonNode)(int)b).ToArray())).ToArray()),
    };

    public static KeyDictionary FromJson(JsonNode node)
    {
        if (node is not JsonObject o) throw new FormatException("A dictionary must be a JSON object.");
        var dict = new KeyDictionary
        {
            Id = (string?)o["id"] ?? Guid.NewGuid().ToString(),
            Name = (string?)o["name"] ?? string.Empty,
            Color = CardSave.NormalizeColor((string?)o["color"]),
        };
        if (o["keys"] is JsonArray keys)
            foreach (var k in keys)
                if (k is JsonArray a)
                {
                    var bytes = a.Select(v => (byte)((int?)v ?? 0)).ToArray();
                    if (bytes.Length == 6) dict.Keys.Add(bytes);
                }
        return dict;
    }

    public static KeyDictionary FromJson(string json) =>
        FromJson(JsonNode.Parse(json) ?? throw new FormatException("Empty JSON document."));

    // ---- Plain text (.dic) --------------------------------------------

    /// <summary>Parses a <c>.dic</c> / <c>.txt</c> file: one 12-hex-char key per line, '#' comments.</summary>
    public static KeyDictionary FromText(string name, string text)
    {
        var dict = new KeyDictionary { Name = name };
        var seen = new HashSet<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (Hex.TryParse(line, out var key) && key.Length == 6 && seen.Add(Convert.ToHexString(key)))
                dict.Keys.Add(key);
        }
        return dict;
    }

    public string ToText()
    {
        var sb = new StringBuilder();
        foreach (var k in Keys) sb.AppendLine(Convert.ToHexString(k));
        return sb.ToString();
    }

    /// <summary>Parses the keys from an editor text box; throws on an invalid line.</summary>
    public static List<byte[]> ParseKeys(string text) => ParseKeys(text, out _);

    /// <summary>
    /// Parses the keys from an editor text box, dropping repeats (counted in
    /// <paramref name="duplicates"/>); throws on an invalid line.
    /// </summary>
    public static List<byte[]> ParseKeys(string text, out int duplicates)
    {
        var keys = new List<byte[]>();
        var seen = new HashSet<string>();
        var lineNo = 0;
        duplicates = 0;
        foreach (var raw in text.Split('\n'))
        {
            lineNo++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (!Hex.TryParse(line, out var key) || key.Length != 6)
                throw new FormatException($"Line {lineNo}: “{line}” is not a 12-digit hex key.");
            if (seen.Add(Convert.ToHexString(key))) keys.Add(key);
            else duplicates++;
        }
        return keys;
    }
}
