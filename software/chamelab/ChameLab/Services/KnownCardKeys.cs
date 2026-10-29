using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace ChameLab.Services;

/// <summary>Automatically learned keys, isolated from editable dictionaries and indexed by full UID.</summary>
public sealed class KnownCardKeys
{
    public const string Id = "known-card-keys";
    public const string Name = "Known card keys";
    private readonly IKeyValueStore _store;
    private Dictionary<string, List<string>> _cards = new(StringComparer.Ordinal);

    public KnownCardKeys(IKeyValueStore store)
    {
        _store = store;
        try
        {
            if (store.Read(Id) is { } json) _cards = Parse(JsonNode.Parse(json));
        }
        catch (Exception ex) { LoadError = $"Known card keys could not be read: {ex.Message}"; }
    }

    public string? LoadError { get; }
    public event Action? Changed;
    public int CardCount => _cards.Count;
    public int KeyCount => _cards.Values.Sum(keys => keys.Count);
    public IReadOnlyList<string> Uids => _cards.Keys.OrderBy(uid => uid, StringComparer.Ordinal).ToArray();

    /// <summary>Commit edited UID groups together, leaving unrelated learned keys intact.</summary>
    public void ApplyEdits(IReadOnlyDictionary<string, IReadOnlyList<byte[]>?> edits)
    {
        if (LoadError is not null) throw new InvalidOperationException(LoadError);
        var next = _cards.ToDictionary(c => c.Key, c => new List<string>(c.Value), StringComparer.Ordinal);
        foreach (var (uid, keys) in edits)
        {
            var normalized = Convert.ToHexString(Convert.FromHexString(uid));
            if (normalized.Length == 0) throw new FormatException("Invalid UID.");
            if (keys is null) { next.Remove(normalized); continue; }
            if (keys.Any(k => k.Length != 6)) throw new FormatException("Each key must contain 6 bytes.");
            next[normalized] = keys.Select(k => Convert.ToHexString(k)).Distinct().ToList();
        }
        var json = new JsonObject(next.Select(c => new KeyValuePair<string, JsonNode?>(c.Key,
            new JsonArray(c.Value.Select(k => (JsonNode?)JsonValue.Create(k)).ToArray()))));
        _store.Write(Id, json.ToJsonString());
        _cards = next;
        Changed?.Invoke();
    }
    public IReadOnlyList<byte[]> ForUid(byte[] uid) =>
        _cards.TryGetValue(Convert.ToHexString(uid), out var keys)
            ? keys.Select(Convert.FromHexString).ToArray() : Array.Empty<byte[]>();

    public string DisplayText => string.Join("\n\n", _cards.OrderBy(c => c.Key)
        .Select(c => $"UID {c.Key}\n" + string.Join("\n", c.Value)));

    public JsonObject ToJson() => new(_cards.Select(c =>
        new KeyValuePair<string, JsonNode?>(c.Key, new JsonArray(c.Value.Select(k => (JsonNode?)JsonValue.Create(k)).ToArray()))));

    public void Merge(JsonNode node)
    {
        // Validate the entire backup before adding any keys.
        var cards = Parse(node);
        foreach (var (uid, keys) in cards)
            Remember(Convert.FromHexString(uid), keys.Select(Convert.FromHexString));
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
        var json = new JsonObject(next.Select(c => new KeyValuePair<string, JsonNode?>(c.Key,
            new JsonArray(c.Value.Select(k => (JsonNode?)JsonValue.Create(k)).ToArray()))));
        _store.Write(Id, json.ToJsonString());
        _cards = next;
        Changed?.Invoke();
    }

    private static Dictionary<string, List<string>> Parse(JsonNode? node)
    {
        if (node is not JsonObject cards) throw new FormatException("Expected keys grouped by UID.");
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (uid, value) in cards)
        {
            var normalizedUid = Convert.ToHexString(Convert.FromHexString(uid));
            if (normalizedUid.Length == 0 || value is not JsonArray keys) throw new FormatException("Invalid UID group.");
            var values = new List<string>();
            foreach (var key in keys)
            {
                var bytes = Convert.FromHexString(key?.GetValue<string>() ?? "");
                if (bytes.Length != 6) throw new FormatException("Invalid card key.");
                values.Add(Convert.ToHexString(bytes));
            }
            if (!result.TryGetValue(normalizedUid, out var existing)) result[normalizedUid] = existing = new();
            foreach (var key in values) if (!existing.Contains(key)) existing.Add(key);
        }
        return result;
    }
}
