using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using ChameLab.Localization;

namespace ChameLab.Services;

/// <summary>
/// Automatically learned keys, isolated from editable dictionaries and indexed by full UID: MIFARE Classic
/// keys grouped by UID at the root, and the last known Ultralight / NTAG password per UID in its own section.
/// </summary>
public sealed class KnownCardKeys
{
    public const string Id = "known-card-keys";
    public static string Name => Strings.Dictionaries_KnownCardKeys;
    /// <summary>Root entry holding the NTAG / Ultralight EV1 passwords; never a hex UID, so it can't clash with one.</summary>
    public const string PasswordsSection = "ntagPasswords";
    private readonly IKeyValueStore _store;
    private Dictionary<string, List<string>> _cards = new(StringComparer.Ordinal);
    private Dictionary<string, string> _passwords = new(StringComparer.Ordinal);

    public KnownCardKeys(IKeyValueStore store)
    {
        _store = store;
        try
        {
            if (store.Read(Id) is { } json) (_cards, _passwords) = Parse(JsonNode.Parse(json));
        }
        catch (Exception ex) { LoadError = string.Format(Strings.Library_KnownKeysLoadError, ex.Message); }
    }

    public string? LoadError { get; }
    public event Action? Changed;
    public int CardCount => _cards.Count;
    public int KeyCount => _cards.Values.Sum(keys => keys.Count);
    public int PasswordCount => _passwords.Count;
    public IReadOnlyList<string> Uids => _cards.Keys.OrderBy(uid => uid, StringComparer.Ordinal).ToArray();

    /// <summary>Commit edited UID groups together, leaving unrelated learned keys intact.</summary>
    public void ApplyEdits(IReadOnlyDictionary<string, IReadOnlyList<byte[]>?> edits)
    {
        if (LoadError is not null) throw new InvalidOperationException(LoadError);
        var next = _cards.ToDictionary(c => c.Key, c => new List<string>(c.Value), StringComparer.Ordinal);
        foreach (var (uid, keys) in edits)
        {
            var normalized = Convert.ToHexString(Convert.FromHexString(uid));
            if (normalized.Length == 0) throw new FormatException(Strings.Keys_InvalidUid);
            if (keys is null) { next.Remove(normalized); continue; }
            if (keys.Any(k => k.Length != 6)) throw new FormatException(Strings.Keys_KeyLength);
            next[normalized] = keys.Select(k => Convert.ToHexString(k)).Distinct().ToList();
        }
        Save(next, _passwords);
    }
    public IReadOnlyList<byte[]> ForUid(byte[] uid) =>
        _cards.TryGetValue(Convert.ToHexString(uid), out var keys)
            ? keys.Select(Convert.FromHexString).ToArray() : Array.Empty<byte[]>();

    /// <summary>The 4-byte PWD the card with this UID last accepted or was given; null when unknown.</summary>
    public byte[]? PasswordForUid(byte[] uid) =>
        _passwords.TryGetValue(Convert.ToHexString(uid), out var pwd) ? Convert.FromHexString(pwd) : null;

    public string DisplayText => string.Join("\n\n", _cards.OrderBy(c => c.Key)
        .Select(c => $"UID {c.Key}\n" + string.Join("\n", c.Value)));

    public JsonObject ToJson() => ToJson(_cards, _passwords);

    public void Merge(JsonNode node)
    {
        // Validate the entire backup before adding any keys.
        var (cards, passwords) = Parse(node);
        foreach (var (uid, keys) in cards)
            Remember(Convert.FromHexString(uid), keys.Select(Convert.FromHexString));
        // A password already known here is newer than the backup's.
        foreach (var (uid, pwd) in passwords)
            if (!_passwords.ContainsKey(uid)) RememberPassword(Convert.FromHexString(uid), Convert.FromHexString(pwd));
    }

    public void Remember(byte[] uid, IEnumerable<byte[]> keys)
    {
        if (uid.Length == 0) return;
        var hex = Convert.ToHexString(uid);
        var next = _cards.ToDictionary(c => c.Key, c => new List<string>(c.Value), StringComparer.Ordinal);
        var values = next.TryGetValue(hex, out var previous) ? previous : new List<string>();
        var seen = values.ToHashSet(StringComparer.Ordinal);
        foreach (var key in keys)
            if (key.Length == 6 && seen.Add(Convert.ToHexString(key))) values.Add(Convert.ToHexString(key));
        if (values.Count == 0 || (_cards.TryGetValue(hex, out var old) && old.Count == values.Count)) return;
        if (LoadError is not null) throw new InvalidOperationException(LoadError);
        next[hex] = values;
        Save(next, _passwords);
    }

    /// <summary>Adds or replaces the card's password: a card has one PWD at a time.</summary>
    public void RememberPassword(byte[] uid, byte[] pwd)
    {
        if (uid.Length == 0 || pwd.Length != 4) return;
        var hex = Convert.ToHexString(uid);
        var value = Convert.ToHexString(pwd);
        if (_passwords.TryGetValue(hex, out var old) && old == value) return;
        if (LoadError is not null) throw new InvalidOperationException(LoadError);
        var next = new Dictionary<string, string>(_passwords, StringComparer.Ordinal) { [hex] = value };
        Save(_cards, next);
    }

    private void Save(Dictionary<string, List<string>> cards, Dictionary<string, string> passwords)
    {
        _store.Write(Id, ToJson(cards, passwords).ToJsonString());
        _cards = cards;
        _passwords = passwords;
        Changed?.Invoke();
    }

    private static JsonObject ToJson(Dictionary<string, List<string>> cards, Dictionary<string, string> passwords)
    {
        var json = new JsonObject(cards.Select(c => new KeyValuePair<string, JsonNode?>(c.Key,
            new JsonArray(c.Value.Select(k => (JsonNode?)JsonValue.Create(k)).ToArray()))));
        if (passwords.Count > 0)
            json[PasswordsSection] = new JsonObject(passwords.Select(p =>
                new KeyValuePair<string, JsonNode?>(p.Key, JsonValue.Create(p.Value))));
        return json;
    }

    private static (Dictionary<string, List<string>>, Dictionary<string, string>) Parse(JsonNode? node)
    {
        if (node is not JsonObject cards) throw new FormatException(Strings.Keys_ExpectedGrouped);
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var passwords = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (uid, value) in cards)
        {
            if (uid == PasswordsSection)
            {
                if (value is not JsonObject section) throw new FormatException(Strings.Keys_InvalidPasswordSection);
                foreach (var (cardUid, pwd) in section)
                {
                    var normalized = Convert.ToHexString(Convert.FromHexString(cardUid));
                    var bytes = Convert.FromHexString(pwd?.GetValue<string>() ?? "");
                    if (normalized.Length == 0 || bytes.Length != 4) throw new FormatException(Strings.Keys_InvalidPassword);
                    passwords[normalized] = Convert.ToHexString(bytes);
                }
                continue;
            }
            var normalizedUid = Convert.ToHexString(Convert.FromHexString(uid));
            if (normalizedUid.Length == 0 || value is not JsonArray keys) throw new FormatException(Strings.Keys_InvalidUidGroup);
            var values = new List<string>();
            foreach (var key in keys)
            {
                var bytes = Convert.FromHexString(key?.GetValue<string>() ?? "");
                if (bytes.Length != 6) throw new FormatException(Strings.Keys_InvalidKey);
                values.Add(Convert.ToHexString(bytes));
            }
            if (!result.TryGetValue(normalizedUid, out var existing)) result[normalizedUid] = existing = new();
            foreach (var key in values) if (!existing.Contains(key)) existing.Add(key);
        }
        return (result, passwords);
    }
}
