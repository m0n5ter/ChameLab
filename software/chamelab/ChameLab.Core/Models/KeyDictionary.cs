using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using ChameLab.Localization;

namespace ChameLab.Models;

public enum KeyDictionaryType { MifareClassic, T55xx, Generic }

/// <summary>
/// A named set of keys of 4 to 16 bytes, possibly of different lengths. The JSON shape matches
/// the Flutter ChameleonUltraGUI <c>Dictionary</c> so dictionaries move between
/// the two apps; a plain <c>.dic</c> / <c>.keys</c> / <c>.txt</c> file (one hex key per line)
/// also imports and exports.
/// </summary>
public sealed class KeyDictionary
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Color { get; set; } = CardSave.DefaultColor;
    public const int MinKeyLength = 4;
    public const int MaxKeyLength = 16;
    private int _keyLength = 6;

    // Retained for callers and older JSON; length is the source of truth.
    public KeyDictionaryType Type
    {
        get => KeyLength switch { 4 => KeyDictionaryType.T55xx, 6 => KeyDictionaryType.MifareClassic, _ => KeyDictionaryType.Generic };
        set
        {
            if (value == KeyDictionaryType.T55xx) KeyLength = 4;
            else if (value == KeyDictionaryType.MifareClassic) KeyLength = 6;
        }
    }

    /// <summary>
    /// Length most of the keys have (the first key's among equally common ones), or the length
    /// the dictionary is made for while it has no keys. Stored as Flutter's <c>keyLength</c>.
    /// </summary>
    public int KeyLength
    {
        get => Keys.Count == 0 ? _keyLength
            : Keys.GroupBy(k => k.Length).OrderByDescending(g => g.Count()).First().Key;
        set
        {
            if (value is < MinKeyLength or > MaxKeyLength) throw new ArgumentOutOfRangeException(nameof(value));
            _keyLength = value;
        }
    }

    /// <summary>The keys, each 4 to 16 bytes.</summary>
    public List<byte[]> Keys { get; set; } = new();

    /// <summary>Lengths of the keys, shortest first (the dictionary's length while it has none).</summary>
    public IReadOnlyList<int> KeyLengths => Keys.Count == 0 ? [_keyLength]
        : Keys.Select(k => k.Length).Distinct().Order().ToArray();

    /// <summary>The dictionary has keys of this length (or is made for it while empty).</summary>
    public bool HasKeysOfLength(int length) =>
        Keys.Count == 0 ? _keyLength == length : Keys.Any(k => k.Length == length);

    /// <summary>The keys of one length, e.g. 6-byte MIFARE Classic keys from a mixed dictionary.</summary>
    public IReadOnlyList<byte[]> KeysOfLength(int length) => Keys.Where(k => k.Length == length).ToArray();

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
            KeyLength = o["keyLength"] is { } length
                ? ReadKeyLength(length.GetValue<int>())
                : (string?)o["type"] == nameof(KeyDictionaryType.T55xx) ? 4 : 6,
        };
        if (o["keys"] is JsonArray keys)
            foreach (var k in keys)
                if (k is JsonArray a)
                {
                    var bytes = a.Select(v => (byte)((int?)v ?? 0)).ToArray();
                    if (bytes.Length is < MinKeyLength or > MaxKeyLength)
                        throw new FormatException(Strings.Dictionaries_InvalidKeyLength);
                    dict.Keys.Add(bytes);
                }
        return dict;
    }

    public static KeyDictionary FromJson(string json) =>
        FromJson(JsonNode.Parse(json) ?? throw new FormatException(Strings.Import_EmptyJson));

    private static int ReadKeyLength(int hexLength) =>
        hexLength % 2 == 0 && hexLength / 2 is >= MinKeyLength and <= MaxKeyLength
            ? hexLength / 2 : throw new FormatException(Strings.Dictionaries_InvalidKeyLength);

    // ---- Plain text (.dic) --------------------------------------------

    /// <summary>
    /// Parses one hex key per line, with '#' comments; lines that aren't a 4- to 16-byte key are
    /// skipped. Keys may differ in length.
    /// </summary>
    public static KeyDictionary FromText(string name, string text, KeyDictionaryType? type = null)
    {
        var dict = new KeyDictionary { Name = name, Type = type ?? KeyDictionaryType.MifareClassic };
        var seen = new HashSet<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (!Hex.TryParse(line, out var key) || key.Length is < MinKeyLength or > MaxKeyLength) continue;
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
        => ParseKeys(text, out duplicates, type == KeyDictionaryType.T55xx ? 4 : 6);

    public static List<byte[]> ParseKeys(string text, out int duplicates, int keyLength)
    {
        if (keyLength is < MinKeyLength or > MaxKeyLength) throw new ArgumentOutOfRangeException(nameof(keyLength));
        var keys = new List<byte[]>();
        var seen = new HashSet<string>();
        var lineNo = 0;
        duplicates = 0;
        foreach (var raw in text.Split('\n'))
        {
            lineNo++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (!Hex.TryParse(line, out var key) || key.Length != keyLength)
                throw new FormatException(string.Format(Strings.Dictionaries_BadKeyLine, lineNo, keyLength));
            if (seen.Add(Convert.ToHexString(key))) keys.Add(key);
            else duplicates++;
        }
        return keys;
    }
}
