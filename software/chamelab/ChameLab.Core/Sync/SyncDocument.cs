using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace ChameLab.Sync;

/// <summary>One value a field had: the operation that set it, when, and what it saw before.</summary>
public sealed record FieldVersion(OpId Op, Hlc Time, JsonNode? Value, OpId? Base);

/// <summary>
/// A field of an entity: its recent values (newest first; the first is the current one) and, for
/// fields whose concurrent edits are all kept, the values no later edit has built on (heads).
/// </summary>
public sealed class FieldState
{
    public List<FieldVersion> Versions { get; } = new();
    public List<FieldVersion> Heads { get; } = new();
    public HashSet<OpId> Superseded { get; } = new();

    public FieldVersion Current => Versions[0];
}

/// <summary>A deletion and, per field, the operation whose value the deleting device saw.</summary>
public sealed record DeleteState(OpId Op, Hlc Time, IReadOnlyDictionary<string, OpId> Base);

/// <summary>Everything known about one entity, merged from every operation on it.</summary>
public sealed class EntityState(string type, string id)
{
    public string Type { get; } = type;
    public string Id { get; } = id;
    public SortedDictionary<string, FieldState> Fields { get; } = new(StringComparer.Ordinal);
    public DeleteState? Delete { get; set; }

    /// <summary>
    /// Deleted while the deletion saw the current value of every field: an edit the deleting device
    /// did not know about (or one made after it) brings the entity back, so no edit is lost.
    /// </summary>
    public bool IsDeleted =>
        Delete is { } d && Fields.All(f => d.Base.TryGetValue(f.Key, out var b) && b == f.Value.Current.Op);

    public JsonNode? Value(string field) => Fields.TryGetValue(field, out var f) ? f.Current.Value : null;

    public JsonObject Values() =>
        new(Fields.Select(f => KeyValuePair.Create(f.Key, f.Value.Current.Value?.DeepClone())));

    public Hlc LastChanged =>
        Fields.Values.Select(f => f.Current.Time).Append(Delete?.Time ?? Hlc.Zero).Max();
}

/// <summary>An entity as the library shows it: its current values; a conflict copy names its original.</summary>
public sealed record EntityView(string Type, string Id, JsonObject Fields, string? CopyOf = null);

/// <summary>One point in an entity's history: the operation, when and by whom, and what it changed.</summary>
public sealed record HistoryEntry(OpId Op, Hlc Time, IReadOnlyList<string> Fields, bool Deleted);

public enum ApplyResult { Applied, Duplicate, Gap, Rejected }

/// <summary>
/// The group's data on one device: every entity merged from all operations, the version vector of
/// operations applied, and the recent operations themselves for peers that lack them.
/// <para>
/// Merging is commutative and idempotent, per field: the newest timestamp wins, deletions keep
/// track of what they saw, and concurrent edits of a dump or a key list are all kept. Two devices
/// that applied the same operations, in any order, hold the same state; a whole state
/// (<see cref="Snapshot"/>) merges into another the same way.
/// </para>
/// Not thread-safe: the owner serializes access.
/// </summary>
public sealed class SyncDocument
{
    /// <summary>Values kept per field for history.</summary>
    public const int HistoryDepth = 10;

    private readonly Dictionary<(string Type, string Id), EntityState> _entities = new();
    private readonly List<SyncOp> _log = new();

    public SyncDocument(string deviceId, Func<long>? now = null)
    {
        DeviceId = deviceId;
        Clock = new HlcClock(deviceId, now);
    }

    public string DeviceId { get; }
    public HlcClock Clock { get; }

    /// <summary>Per author, the last operation applied (operations of an author apply in order).</summary>
    public Dictionary<string, long> Vector { get; } = new(StringComparer.Ordinal);

    /// <summary>Per author, the last operation no longer in <see cref="Log"/>: peers behind it need a snapshot.</summary>
    public Dictionary<string, long> Floor { get; } = new(StringComparer.Ordinal);

    /// <summary>Recent operations, in the order they were applied.</summary>
    public IReadOnlyList<SyncOp> Log => _log;

    public IEnumerable<EntityState> Entities => _entities.Values;

    public EntityState? Find(string type, string id) => _entities.GetValueOrDefault((type, id));

    public long Seq(string author) => Vector.GetValueOrDefault(author);

    // ---- Local changes -------------------------------------------------------

    /// <summary>
    /// Records a local edit: the fields that differ from the current values (all given fields when
    /// the entity is deleted, which brings it back). Null when nothing changed.
    /// </summary>
    public SyncOp? Change(string type, string id, IReadOnlyDictionary<string, JsonNode?> fields)
    {
        var entity = Find(type, id);
        var revive = entity?.IsDeleted == true;
        var set = new Dictionary<string, JsonNode?>();
        var bases = new Dictionary<string, OpId>();
        foreach (var (name, value) in fields)
        {
            var current = entity?.Fields.GetValueOrDefault(name);
            if (current is not null)
            {
                if (!revive && JsonNode.DeepEquals(current.Current.Value, value)) continue;
                bases[name] = current.Current.Op;
            }
            set[name] = value?.DeepClone();
        }
        if (set.Count == 0) return null;
        return Local(type, id, set, bases, delete: false);
    }

    /// <summary>Records a local deletion; null when the entity is not there.</summary>
    public SyncOp? Delete(string type, string id)
    {
        var entity = Find(type, id);
        // A conflict copy exists before anyone edits it: deleting it is how the conflict is settled.
        if (entity is null && id.StartsWith(CopyPrefix, StringComparison.Ordinal))
            return Local(type, id, new Dictionary<string, JsonNode?>(), new Dictionary<string, OpId>(), delete: true);
        if (entity is not { IsDeleted: false }) return null;
        var bases = entity.Fields.ToDictionary(f => f.Key, f => f.Value.Current.Op);
        return Local(type, id, new Dictionary<string, JsonNode?>(), bases, delete: true);
    }

    private SyncOp Local(string type, string id, Dictionary<string, JsonNode?> set, Dictionary<string, OpId> bases, bool delete)
    {
        var op = new SyncOp
        {
            Author = DeviceId,
            Seq = Seq(DeviceId) + 1,
            Time = Clock.Next(),
            Type = type,
            Id = id,
            Set = set,
            Base = bases,
            Delete = delete,
        };
        Apply(op);
        return op;
    }

    // ---- Remote operations ---------------------------------------------------

    /// <summary>Applies an operation; an author's operations must come in order.</summary>
    public ApplyResult Apply(SyncOp op)
    {
        var last = Seq(op.Author);
        if (op.Seq <= last) return ApplyResult.Duplicate;
        if (op.Seq != last + 1) return ApplyResult.Gap;
        if (RevokedAfter(op.Author) is { } limit && op.Seq > limit) return ApplyResult.Rejected;

        Clock.Observe(op.Time);
        var entity = GetOrAdd(op.Type, op.Id);
        foreach (var (name, value) in op.Set)
            MergeVersion(entity, name, new FieldVersion(op.OpId, op.Time, value?.DeepClone(), op.Base.TryGetValue(name, out var b) ? b : null));
        if (op.Delete) MergeDelete(entity, new DeleteState(op.OpId, op.Time, op.Base));
        Vector[op.Author] = op.Seq;
        _log.Add(op);
        return ApplyResult.Applied;
    }

    /// <summary>
    /// The operations a peer at <paramref name="peer"/> lacks, oldest first (so every operation
    /// comes after those it built on); null when some are no longer kept and it needs a snapshot.
    /// </summary>
    public IReadOnlyList<SyncOp>? MissingFor(IReadOnlyDictionary<string, long> peer)
    {
        foreach (var (author, floor) in Floor)
            if (peer.GetValueOrDefault(author) < floor && Seq(author) > peer.GetValueOrDefault(author)) return null;
        return _log.Where(op => op.Seq > peer.GetValueOrDefault(op.Author)).OrderBy(op => op.Time).ToList();
    }

    /// <summary>Whether this device has operations the peer lacks.</summary>
    public bool IsAheadOf(IReadOnlyDictionary<string, long> peer) =>
        Vector.Any(v => v.Value > peer.GetValueOrDefault(v.Key));

    /// <summary>Drops the oldest operations beyond <paramref name="keep"/>; peers behind them get a snapshot.</summary>
    public void TrimLog(int keep)
    {
        if (_log.Count <= keep) return;
        foreach (var op in _log.Take(_log.Count - keep))
            if (op.Seq > Floor.GetValueOrDefault(op.Author)) Floor[op.Author] = op.Seq;
        _log.RemoveRange(0, _log.Count - keep);
    }

    /// <summary>Forgets the operation log (after a snapshot was written); every author's floor moves to what is applied.</summary>
    public void ClearLog()
    {
        foreach (var (author, seq) in Vector) Floor[author] = seq;
        _log.Clear();
    }

    // ---- Merging -------------------------------------------------------------

    private EntityState GetOrAdd(string type, string id)
    {
        if (!_entities.TryGetValue((type, id), out var entity))
            _entities[(type, id)] = entity = new EntityState(type, id);
        return entity;
    }

    private static void MergeVersion(EntityState entity, string name, FieldVersion version)
    {
        if (!entity.Fields.TryGetValue(name, out var field))
            entity.Fields[name] = field = new FieldState();
        if (!field.Versions.Any(v => v.Op == version.Op))
        {
            var at = field.Versions.FindIndex(v => v.Time < version.Time);
            field.Versions.Insert(at < 0 ? field.Versions.Count : at, version);
            if (field.Versions.Count > HistoryDepth) field.Versions.RemoveRange(HistoryDepth, field.Versions.Count - HistoryDepth);
        }
        if (!EntityTypes.IsConflictKept(entity.Type, name)) return;
        if (version.Base is { } superseded)
        {
            field.Superseded.Add(superseded);
            field.Heads.RemoveAll(h => h.Op == superseded);
        }
        if (!field.Superseded.Contains(version.Op) && !field.Heads.Any(h => h.Op == version.Op))
            field.Heads.Add(version);
    }

    private static void MergeDelete(EntityState entity, DeleteState delete)
    {
        if (entity.Delete is null || entity.Delete.Time < delete.Time) entity.Delete = delete;
    }

    // ---- Views ---------------------------------------------------------------

    /// <summary>
    /// The entities of a type as the library shows them: the live ones, then a copy of each for
    /// every concurrent dump (or key list) edit that lost, so neither edit is gone.
    /// </summary>
    public IEnumerable<EntityView> View(string type)
    {
        foreach (var entity in _entities.Values.Where(e => e.Type == type && !e.IsDeleted && e.Fields.Count > 0))
        {
            if (entity.Id.StartsWith(CopyPrefix, StringComparison.Ordinal)) continue;
            var values = entity.Values();
            yield return new EntityView(type, entity.Id, values);
            foreach (var (name, field) in entity.Fields)
            {
                if (field.Heads.Count < 2) continue;
                foreach (var loser in field.Heads.Where(h => h.Op != field.Current.Op).OrderBy(h => h.Time))
                {
                    var copyId = CopyId(entity.Id, loser.Op);
                    var own = Find(type, copyId);
                    if (own?.IsDeleted == true) continue;
                    var copy = (JsonObject)values.DeepClone();
                    copy[name] = loser.Value?.DeepClone();
                    if (own is not null)
                        foreach (var (f, state) in own.Fields) copy[f] = state.Current.Value?.DeepClone();
                    yield return new EntityView(type, copyId, copy, entity.Id);
                }
            }
        }
    }

    /// <summary>Copies of conflicting edits have ids derived from the original and the losing edit, the same on every device.</summary>
    public const string CopyPrefix = "conflict-";

    public static string CopyId(string id, OpId loser) =>
        CopyPrefix + Convert.ToHexString(SyncCrypto.Sha256(Encoding.UTF8.GetBytes($"{id}|{loser}")))[..24].ToLowerInvariant();

    /// <summary>Deleted entities of a type, newest deletion first, for restoring them.</summary>
    public IEnumerable<EntityState> Deleted(string type) =>
        _entities.Values.Where(e => e.Type == type && e.IsDeleted && e.Fields.Count > 0
                                    && !e.Id.StartsWith(CopyPrefix, StringComparison.Ordinal))
            .OrderByDescending(e => e.Delete!.Time);

    /// <summary>The kept history of an entity, newest first.</summary>
    public IReadOnlyList<HistoryEntry> History(string type, string id)
    {
        if (Find(type, id) is not { } entity) return Array.Empty<HistoryEntry>();
        var entries = entity.Fields
            .SelectMany(f => f.Value.Versions.Select(v => (v.Op, v.Time, Field: f.Key)))
            .GroupBy(v => v.Op)
            .Select(g => new HistoryEntry(g.Key, g.First().Time, g.Select(v => v.Field).OrderBy(f => f, StringComparer.Ordinal).ToList(), false))
            .ToList();
        if (entity.Delete is { } d) entries.Add(new HistoryEntry(d.Op, d.Time, Array.Empty<string>(), true));
        return entries.OrderByDescending(e => e.Time).ToList();
    }

    /// <summary>The values an entity had right after <paramref name="time"/>, as far as history goes back.</summary>
    public JsonObject ValuesAt(string type, string id, Hlc time)
    {
        var values = new JsonObject();
        if (Find(type, id) is not { } entity) return values;
        foreach (var (name, field) in entity.Fields)
        {
            var version = field.Versions.FirstOrDefault(v => v.Time <= time) ?? field.Current;
            values[name] = version.Value?.DeepClone();
        }
        return values;
    }

    // ---- Members ---------------------------------------------------------------

    /// <summary>The last operation accepted from a removed device, or null while it is a member.</summary>
    public long? RevokedAfter(string deviceId) =>
        AsLong(Find(EntityTypes.Member, deviceId)?.Value("revoked"));

    /// <summary>A JSON number as a long, however it was made (parsed, or from an int or a long).</summary>
    public static long? AsLong(JsonNode? node) =>
        node is JsonValue v && long.TryParse(v.ToJsonString(), System.Globalization.NumberStyles.AllowLeadingSign,
            System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;

    // ---- Snapshots ---------------------------------------------------------------

    /// <summary>The whole state, for a peer behind <see cref="Floor"/> and for saving.</summary>
    public JsonObject Snapshot()
    {
        var entities = new JsonArray();
        foreach (var entity in _entities.Values.OrderBy(e => e.Type, StringComparer.Ordinal).ThenBy(e => e.Id, StringComparer.Ordinal))
        {
            var fields = new JsonObject();
            foreach (var (name, field) in entity.Fields)
            {
                var f = new JsonObject { ["versions"] = Versions(field.Versions) };
                if (field.Heads.Count > 0) f["heads"] = Versions(field.Heads);
                if (field.Superseded.Count > 0)
                    f["superseded"] = new JsonArray(field.Superseded.Select(s => (JsonNode)s.ToString()).ToArray());
                fields[name] = f;
            }
            var e = new JsonObject { ["type"] = entity.Type, ["id"] = entity.Id, ["fields"] = fields };
            if (entity.Delete is { } d)
                e["delete"] = new JsonObject
                {
                    ["op"] = d.Op.ToString(),
                    ["time"] = d.Time.ToString(),
                    ["base"] = new JsonObject(d.Base.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value.ToString()))),
                };
            entities.Add(e);
        }
        return new JsonObject
        {
            ["version"] = 1,
            ["vector"] = VectorJson(Vector),
            ["entities"] = entities,
        };
    }

    /// <summary>Merges a whole state in, as if every operation in it had been applied.</summary>
    public void MergeSnapshot(JsonObject snapshot)
    {
        if ((int?)snapshot["version"] != 1) throw new FormatException("Unknown sync snapshot version.");
        if (snapshot["entities"] is JsonArray entities)
        {
            foreach (var node in entities)
            {
                if (node is not JsonObject e || (string?)e["type"] is not { } type || (string?)e["id"] is not { } id) continue;
                var entity = GetOrAdd(type, id);
                if (e["fields"] is JsonObject fields)
                {
                    foreach (var (name, f) in fields)
                    {
                        if (f is not JsonObject fo) continue;
                        if (fo["superseded"] is JsonArray superseded && EntityTypes.IsConflictKept(type, name))
                        {
                            if (!entity.Fields.TryGetValue(name, out var state)) entity.Fields[name] = state = new FieldState();
                            foreach (var s in superseded)
                                if ((string?)s is { } text)
                                {
                                    var gone = OpId.Parse(text);
                                    state.Superseded.Add(gone);
                                    state.Heads.RemoveAll(h => h.Op == gone);
                                }
                        }
                        foreach (var version in ReadVersions(fo["versions"]).Concat(ReadVersions(fo["heads"])))
                        {
                            Clock.Observe(version.Time);
                            MergeVersion(entity, name, version);
                        }
                    }
                }
                if (e["delete"] is JsonObject d && (string?)d["op"] is { } op && (string?)d["time"] is { } time)
                {
                    var bases = new Dictionary<string, OpId>();
                    if (d["base"] is JsonObject b)
                        foreach (var (name, value) in b)
                            if ((string?)value is { } text) bases[name] = OpId.Parse(text);
                    MergeDelete(entity, new DeleteState(OpId.Parse(op), Hlc.Parse(time), bases));
                }
            }
        }
        foreach (var (author, seq) in ReadVector(snapshot["vector"]))
        {
            if (seq <= Seq(author)) continue;
            Vector[author] = seq;
            Floor[author] = Math.Max(Floor.GetValueOrDefault(author), seq);
        }
    }

    private static JsonArray Versions(IEnumerable<FieldVersion> versions) =>
        new(versions.Select(v =>
        {
            var o = new JsonObject { ["op"] = v.Op.ToString(), ["time"] = v.Time.ToString(), ["value"] = v.Value?.DeepClone() };
            if (v.Base is { } b) o["base"] = b.ToString();
            return (JsonNode)o;
        }).ToArray());

    private static IEnumerable<FieldVersion> ReadVersions(JsonNode? node)
    {
        if (node is not JsonArray list) yield break;
        foreach (var item in list)
            if (item is JsonObject o && (string?)o["op"] is { } op && (string?)o["time"] is { } time)
                yield return new FieldVersion(OpId.Parse(op), Hlc.Parse(time), o["value"]?.DeepClone(),
                    (string?)o["base"] is { } b ? OpId.Parse(b) : null);
    }

    public static JsonObject VectorJson(IReadOnlyDictionary<string, long> vector) =>
        new(vector.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => KeyValuePair.Create(v.Key, (JsonNode?)v.Value)));

    public static Dictionary<string, long> ReadVector(JsonNode? node)
    {
        var vector = new Dictionary<string, long>(StringComparer.Ordinal);
        if (node is JsonObject o)
            foreach (var (author, value) in o)
                if (AsLong(value) is { } seq && seq > 0) vector[author] = seq;
        return vector;
    }
}
