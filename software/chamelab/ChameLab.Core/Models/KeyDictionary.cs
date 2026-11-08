using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using ChameLab.Localization;

namespace ChameLab.Models;

public enum KeyDictionaryType { MifareClassic, T55xx }

/// <summary>
/// A named set of MIFARE Classic keys (6 bytes) or T55xx passwords (4 bytes). The JSON shape matches
/// the Flutter ChameleonUltraGUI <c>Dictionary</c> so dictionaries move between
/// the two apps; a plain <c>.dic</c> / <c>.keys</c> / <c>.txt</c> file (one hex key per line)
/// also imports and exports.
/// </summary>
public sealed class KeyDictionary
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Color { get; set; } = CardSave.DefaultColor;
    public KeyDictionaryType Type { get; set; }
    public int KeyLength => Type == KeyDictionaryType.T55xx ? 4 : 6;

    /// <summary>Keys of the dictionary's type.</summary>
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
        ["type"] = Type.ToString(),
        ["keyLength"] = KeyLength * 2,
        ["keys"] = new JsonArray(Keys.Select(k =>
            (JsonNode)new JsonArray(k.Select(b => (JsonNode)(int)b).ToArray())).ToArray()),
    };

    public static KeyDictionary FromJson(JsonNode node)
    {
        if (node is not JsonObject o) throw new FormatException(Strings.Import_DictNotObject);
        var dict = new KeyDictionary
        {
            Id = (string?)o["id"] ?? Guid.NewGuid().ToString(),
            Name = (string?)o["name"] ?? string.Empty,
            Color = CardSave.NormalizeColor((string?)o["color"]),
            // Older dictionaries have no type. Flutter stores the length in hex characters.
            Type = (string?)o["type"] == nameof(KeyDictionaryType.T55xx) || (int?)o["keyLength"] == 8
                ? KeyDictionaryType.T55xx : KeyDictionaryType.MifareClassic,
        };
        if (o["keys"] is JsonArray keys)
            foreach (var k in keys)
                if (k is JsonArray a)
                {
                    var bytes = a.Select(v => (byte)((int?)v ?? 0)).ToArray();
                    if (bytes.Length == dict.KeyLength) dict.Keys.Add(bytes);
                }
        return dict;
    }

    public static KeyDictionary FromJson(string json) =>
        FromJson(JsonNode.Parse(json) ?? throw new FormatException(Strings.Import_EmptyJson));

    // ---- Plain text (.dic) --------------------------------------------

    /// <summary>Parses one hex key per line, with '#' comments; detects the type from the first valid key.</summary>
    public static KeyDictionary FromText(string name, string text, KeyDictionaryType? type = null)
    {
        var dict = new KeyDictionary { Name = name, Type = type ?? KeyDictionaryType.MifareClassic };
        var seen = new HashSet<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (!Hex.TryParse(line, out var key) || key.Length is not (4 or 6)) continue;
            if (dict.Count == 0 && type is null)
                dict.Type = key.Length == 4 ? KeyDictionaryType.T55xx : KeyDictionaryType.MifareClassic;
            if (key.Length != dict.KeyLength) throw new FormatException(Strings.Dictionaries_MixedKeyTypes);
            if (seen.Add(Convert.ToHexString(key))) dict.Keys.Add(key);
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
    public static List<byte[]> ParseKeys(string text, out int duplicates, KeyDictionaryType type = KeyDictionaryType.MifareClassic)
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
            if (!Hex.TryParse(line, out var key) || key.Length != (type == KeyDictionaryType.T55xx ? 4 : 6))
                throw new FormatException(string.Format(Strings.Import_BadKeyLine, lineNo, line));
            if (seen.Add(Convert.ToHexString(key))) keys.Add(key);
            else duplicates++;
        }
        return keys;
    }
}
