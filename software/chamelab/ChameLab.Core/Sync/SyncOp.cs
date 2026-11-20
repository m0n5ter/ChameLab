using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

namespace ChameLab.Sync;

/// <summary>An operation's identity: its author device and that device's sequence number.</summary>
public readonly record struct OpId(string Author, long Seq)
{
    public override string ToString() => $"{Author}:{Seq.ToString(CultureInfo.InvariantCulture)}";

    public static OpId Parse(string text)
    {
        var colon = text.LastIndexOf(':');
        if (colon <= 0) throw new FormatException($"Not an operation id: {text}");
        return new OpId(text[..colon], long.Parse(text.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture));
    }
}

/// <summary>The kinds of things sync keeps.</summary>
public static class EntityTypes
{
    public const string Card = "card";
    public const string CardFolder = "cardFolder";
    public const string Dictionary = "dictionary";
    public const string DictionaryFolder = "dictionaryFolder";
    public const string Member = "member";
    public const string Group = "group";

    /// <summary>
    /// Fields whose concurrent edits are both kept (the loser as a copy of the entity) rather than
    /// one silently replacing the other: a dump or a key list is too much work to lose.
    /// </summary>
    public static bool IsConflictKept(string type, string field) =>
        type == Card && field == "data" || type == Dictionary && field == "keys";
}

/// <summary>
/// One change by one device: sets some fields of an entity, or deletes it. <see cref="Base"/>
/// names, per field, the operation whose value the author saw when it made the change (for a
/// delete: every field), which tells concurrent edits from later ones.
/// </summary>
public sealed class SyncOp
{
    public required string Author { get; init; }
    public required long Seq { get; init; }
    public required Hlc Time { get; init; }
    public required string Type { get; init; }
    public required string Id { get; init; }
    public Dictionary<string, JsonNode?> Set { get; init; } = new();
    public Dictionary<string, OpId> Base { get; init; } = new();
    public bool Delete { get; init; }

    public OpId OpId => new(Author, Seq);

    public JsonObject ToJson()
    {
        var o = new JsonObject
        {
            ["author"] = Author,
            ["seq"] = Seq,
            ["time"] = Time.ToString(),
            ["type"] = Type,
            ["id"] = Id,
        };
        if (Set.Count > 0)
            o["set"] = new JsonObject(Set.Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone())));
        if (Base.Count > 0)
            o["base"] = new JsonObject(Base.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value.ToString())));
        if (Delete) o["delete"] = true;
        return o;
    }

    public static SyncOp FromJson(JsonNode node)
    {
        if (node is not JsonObject o) throw new FormatException("An operation must be a JSON object.");
        var set = new Dictionary<string, JsonNode?>();
        if (o["set"] is JsonObject s)
            foreach (var (key, value) in s) set[key] = value?.DeepClone();
        var bases = new Dictionary<string, OpId>();
        if (o["base"] is JsonObject b)
            foreach (var (key, value) in b)
                if ((string?)value is { } text) bases[key] = OpId.Parse(text);
        var op = new SyncOp
        {
            Author = Required(o, "author"),
            Seq = (long?)o["seq"] ?? throw new FormatException("Operation without a seq."),
            Time = Hlc.Parse(Required(o, "time")),
            Type = Required(o, "type"),
            Id = Required(o, "id"),
            Set = set,
            Base = bases,
            Delete = (bool?)o["delete"] ?? false,
        };
        if (op.Seq < 1 || op.Time.Node != op.Author) throw new FormatException("Malformed operation.");
        return op;
    }

    private static string Required(JsonObject o, string name) =>
        (string?)o[name] is { Length: > 0 } value ? value : throw new FormatException($"Operation without {name}.");
}
