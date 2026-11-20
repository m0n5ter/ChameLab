using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using ChameLab.Models;
using ChameLab.Services;

namespace ChameLab.Sync;

/// <summary>What sync needs from a library of items filed in folders (cards, key dictionaries).</summary>
public interface ISyncedLibrary
{
    string ItemType { get; }
    string FolderType { get; }

    /// <summary>False when the stored library could not be read: then it is neither synced nor overwritten.</summary>
    bool IsLoaded { get; }

    IReadOnlyList<string> Folders { get; }

    /// <summary>Every item in library order: its id, its folder path and its JSON (id and folder included).</summary>
    IEnumerable<(string Id, string Folder, JsonObject Json)> Items();

    /// <summary>Puts exactly these items (JSON with id and folder path) and folders in the library; false for an item it cannot read.</summary>
    void Replace(IReadOnlyList<JsonObject> items, IReadOnlyList<string> folders, Action<string> unreadable);

    event Action? Changed;
    event Action<string, string>? FolderRelocated;
}

public sealed class CardLibrarySync(CardLibrary library) : ISyncedLibrary
{
    public string ItemType => EntityTypes.Card;
    public string FolderType => EntityTypes.CardFolder;
    public bool IsLoaded => library.LoadError is null;
    public IReadOnlyList<string> Folders => library.Folders;

    public IEnumerable<(string Id, string Folder, JsonObject Json)> Items() =>
        library.Cards.Select(c => (c.Id, c.Folder, c.ToJson()));

    public void Replace(IReadOnlyList<JsonObject> items, IReadOnlyList<string> folders, Action<string> unreadable)
    {
        var cards = new List<CardSave>();
        foreach (var json in items)
        {
            try { cards.Add(CardSave.FromJson(json)); }
            catch (Exception) { unreadable((string?)json["id"] ?? ""); }
        }
        library.ReplaceFromSync(cards, folders);
    }

    public event Action? Changed { add => library.Changed += value; remove => library.Changed -= value; }

    public event Action<string, string>? FolderRelocated
    {
        add => library.FolderRelocated += value;
        remove => library.FolderRelocated -= value;
    }
}

public sealed class DictionaryLibrarySync(DictionaryLibrary library) : ISyncedLibrary
{
    public string ItemType => EntityTypes.Dictionary;
    public string FolderType => EntityTypes.DictionaryFolder;
    public bool IsLoaded => library.LoadError is null;
    public IReadOnlyList<string> Folders => library.Folders;

    public IEnumerable<(string Id, string Folder, JsonObject Json)> Items() =>
        library.Dictionaries.Select(d => (d.Id, d.Folder, d.ToJson()));

    public void Replace(IReadOnlyList<JsonObject> items, IReadOnlyList<string> folders, Action<string> unreadable)
    {
        var dicts = new List<KeyDictionary>();
        foreach (var json in items)
        {
            try { dicts.Add(KeyDictionary.FromJson(json)); }
            catch (Exception) { unreadable((string?)json["id"] ?? ""); }
        }
        library.ReplaceFromSync(dicts, folders);
    }

    public event Action? Changed { add => library.Changed += value; remove => library.Changed -= value; }

    public event Action<string, string>? FolderRelocated
    {
        add => library.FolderRelocated += value;
        remove => library.FolderRelocated -= value;
    }
}

/// <summary>
/// Keeps one library and the <see cref="SyncDocument"/> in step: local edits become operations
/// (<see cref="Capture"/>), merged state becomes the library (<see cref="Materialize"/>).
/// <para>
/// In the document a folder is an entity with an id, a name and a parent folder id, and an item
/// names its folder by id, so renaming or moving a folder is one operation and cannot race with
/// an item being filed in it elsewhere. The library keeps folder paths; this class maps between
/// the two. Item order is a fractional <c>order</c> key, so a reorder changes only the moved items.
/// </para>
/// Changes are told apart against a baseline (the library as last captured or materialized), so
/// only what was edited here becomes an operation. Both methods run on the thread that edits the
/// library; the caller holds the document's lock.
/// </summary>
public sealed class LibrarySync
{
    private const string FolderField = "folder";
    private const string OrderField = "order";

    private readonly ISyncedLibrary _library;
    private readonly Dictionary<string, string> _folderIds = new(StringComparer.CurrentCultureIgnoreCase);
    private readonly Dictionary<string, JsonObject> _baseline = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonObject> _folderBaseline = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unreadable = new(StringComparer.Ordinal);

    public LibrarySync(ISyncedLibrary library)
    {
        _library = library;
        _library.FolderRelocated += OnFolderRelocated;
    }

    public ISyncedLibrary Library => _library;

    /// <summary>The suffix a conflict copy's name gets, in the user's language.</summary>
    public static Func<string> ConflictSuffix { get; set; } = () => " (conflict)";

    /// <summary>Folder ids follow a renamed or moved folder (and the folders inside it).</summary>
    private void OnFolderRelocated(string from, string to)
    {
        foreach (var path in _folderIds.Keys.Where(p => FolderPath.IsWithin(p, from)).ToList())
        {
            var id = _folderIds[path];
            _folderIds.Remove(path);
            _folderIds[FolderPath.Rebase(path, from, to)] = id;
        }
    }

    /// <summary>
    /// Turns what changed in the library since the last call into operations.
    /// <paramref name="joining"/>: this device just joined a group: items the group already has
    /// keep the group's version, and folders the group has under the same path are the same folders.
    /// </summary>
    public List<SyncOp> Capture(SyncDocument doc, bool joining = false)
    {
        var ops = new List<SyncOp>();
        if (!_library.IsLoaded) return ops;
        if (joining) MapFolders(doc);

        // Folders, parents before the folders in them.
        var folders = _library.Folders.ToList();
        foreach (var path in folders)
        {
            if (!_folderIds.TryGetValue(path, out var id)) _folderIds[path] = id = Guid.NewGuid().ToString();
            var parent = FolderPath.Parent(path);
            var fields = new JsonObject
            {
                ["name"] = FolderPath.Name(path),
                ["parent"] = parent.Length == 0 ? "" : _folderIds.GetValueOrDefault(parent, ""),
            };
            Add(ops, doc.Change(_library.FolderType, id, Changed(_folderBaseline.GetValueOrDefault(id), fields)));
            _folderBaseline[id] = fields;
        }
        var live = folders.ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        foreach (var (path, id) in _folderIds.Where(p => !live.Contains(p.Key)).ToList())
        {
            _folderIds.Remove(path);
            if (_folderBaseline.Remove(id)) Add(ops, doc.Delete(_library.FolderType, id));
        }

        // Items.
        var items = _library.Items().ToList();
        var known = joining
            ? doc.View(_library.ItemType).Select(v => v.Id).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var orders = Orders(doc, items.Select(i => i.Id).Where(id => !known.Contains(id)).ToList());
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, folder, json) in items)
        {
            if (!seen.Add(id) || known.Contains(id)) continue;
            var fields = Fields(json, folder);
            var change = Changed(_baseline.GetValueOrDefault(id), fields);
            if (orders.TryGetValue(id, out var order)) change[OrderField] = order;
            Add(ops, doc.Change(_library.ItemType, id, change));
            _baseline[id] = fields;
        }
        foreach (var id in _baseline.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            _baseline.Remove(id);
            Add(ops, doc.Delete(_library.ItemType, id));
        }
        return ops;
    }

    private static void Add(List<SyncOp> ops, SyncOp? op)
    {
        if (op is not null) ops.Add(op);
    }

    /// <summary>The fields of <paramref name="now"/> that differ from <paramref name="before"/> (all of them without one).</summary>
    private static Dictionary<string, JsonNode?> Changed(JsonObject? before, JsonObject now)
    {
        var changed = new Dictionary<string, JsonNode?>();
        foreach (var (name, value) in now)
            if (before is null || !before.TryGetPropertyValue(name, out var old) || !JsonNode.DeepEquals(old, value))
                changed[name] = value?.DeepClone();
        return changed;
    }

    /// <summary>An item's synced fields: its JSON without the id, the folder as a folder id.</summary>
    private JsonObject Fields(JsonObject json, string folder)
    {
        var fields = new JsonObject();
        foreach (var (name, value) in json)
            if (name is not ("id" or FolderField)) fields[name] = value?.DeepClone();
        fields[FolderField] = folder.Length == 0 ? "" : _folderIds.GetValueOrDefault(folder, "");
        return fields;
    }

    /// <summary>
    /// New order keys for the items whose place changed: the longest run already in key order stays,
    /// the rest get keys between their neighbours.
    /// </summary>
    private Dictionary<string, JsonNode?> Orders(SyncDocument doc, List<string> ids)
    {
        var keys = ids.Select(id => doc.Find(_library.ItemType, id) is { IsDeleted: false } e
                                    && (string?)e.Value(OrderField) is { } k && FractionalIndex.IsValid(k) ? k : null).ToList();
        var keep = LongestIncreasing(ids, keys);
        var result = new Dictionary<string, JsonNode?>();
        string? previous = null;
        for (var i = 0; i < ids.Count; i++)
        {
            if (keep.Contains(i)) { previous = keys[i]; continue; }
            var next = Enumerable.Range(i + 1, ids.Count - i - 1).Where(keep.Contains).Select(j => keys[j]).FirstOrDefault();
            if (previous is not null && next is not null && string.CompareOrdinal(previous, next) >= 0)
                return Renumber(ids);
            previous = FractionalIndex.Between(previous, next);
            result[ids[i]] = previous;
        }
        return result;
    }

    private static Dictionary<string, JsonNode?> Renumber(List<string> ids)
    {
        var result = new Dictionary<string, JsonNode?>();
        string? previous = null;
        foreach (var id in ids) result[id] = previous = FractionalIndex.Between(previous, null);
        return result;
    }

    /// <summary>Indexes of a longest strictly increasing run of (key, id) among the items that have a key.</summary>
    private static HashSet<int> LongestIncreasing(List<string> ids, List<string?> keys)
    {
        var tails = new List<int>();
        var parent = new int[ids.Count];
        int Compare(int a, int b)
        {
            var c = string.CompareOrdinal(keys[a], keys[b]);
            return c != 0 ? c : string.CompareOrdinal(ids[a], ids[b]);
        }
        for (var i = 0; i < ids.Count; i++)
        {
            if (keys[i] is null) continue;
            int lo = 0, hi = tails.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (Compare(tails[mid], i) < 0) lo = mid + 1; else hi = mid;
            }
            parent[i] = lo > 0 ? tails[lo - 1] : -1;
            if (lo == tails.Count) tails.Add(i); else tails[lo] = i;
        }
        var keep = new HashSet<int>();
        for (var i = tails.Count > 0 ? tails[^1] : -1; i >= 0; i = parent[i]) keep.Add(i);
        return keep;
    }

    /// <summary>
    /// Puts the document's state in the library. Folder names that would clash (two folders of the
    /// same name in one place after a merge) get a number; a folder whose parent is gone, or that
    /// ended up inside itself, goes to the top. Every device resolves these the same way.
    /// </summary>
    public void Materialize(SyncDocument doc)
    {
        if (!_library.IsLoaded) return;
        var paths = FolderPaths(doc);
        _folderIds.Clear();
        _folderBaseline.Clear();
        AdoptFolders(paths);

        var views = doc.View(_library.ItemType)
            .OrderBy(v => (string?)v.Fields[OrderField] ?? "", StringComparer.Ordinal)
            .ThenBy(v => v.Id, StringComparer.Ordinal)
            .ToList();
        var items = new List<JsonObject>();
        _baseline.Clear();
        _unreadable.Clear();
        foreach (var view in views)
        {
            var fields = (JsonObject)view.Fields.DeepClone();
            // A copy shows it is one until someone renames it.
            if (view.CopyOf is not null && doc.Find(_library.ItemType, view.Id)?.Fields.ContainsKey("name") != true)
                fields["name"] = ((string?)fields["name"] ?? "") + ConflictSuffix();
            var json = (JsonObject)fields.DeepClone();
            json.Remove(OrderField);
            json["id"] = view.Id;
            var folderId = (string?)fields[FolderField] ?? "";
            var path = paths.GetValueOrDefault(folderId, "");
            if (path.Length > 0) json[FolderField] = path; else json.Remove(FolderField);
            items.Add(json);
            fields.Remove(OrderField);
            if (path.Length == 0) fields[FolderField] = "";
            _baseline[view.Id] = fields;
        }
        _library.Replace(items, paths.Values.OrderBy(p => p, FolderPath.TreeOrder).ToList(), id => _unreadable.Add(id));
        foreach (var id in _unreadable) _baseline.Remove(id);
    }

    /// <summary>Folder id → path, from the folder entities.</summary>
    private Dictionary<string, string> FolderPaths(SyncDocument doc)
    {
        var folders = doc.View(_library.FolderType)
            .ToDictionary(v => v.Id, v => (Name: FolderPath.NormalizeName((string?)v.Fields["name"]), Parent: (string?)v.Fields["parent"] ?? ""));
        // Parent of each folder, with dangling parents and cycles cut at the top.
        var parents = new Dictionary<string, string>();
        foreach (var (id, folder) in folders)
            parents[id] = folders.ContainsKey(folder.Parent) && folder.Parent != id ? folder.Parent : "";
        foreach (var id in folders.Keys.OrderBy(i => i, StringComparer.Ordinal))
        {
            var seen = new HashSet<string> { id };
            for (var at = parents[id]; at.Length > 0; at = parents[at])
            {
                if (!seen.Add(at)) { parents[id] = ""; break; }
            }
        }
        // Names unique among siblings: the folder with the smallest id keeps the name.
        var names = new Dictionary<string, string>();
        foreach (var siblings in folders.Keys.GroupBy(id => parents[id]))
        {
            var taken = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
            foreach (var id in siblings.OrderBy(i => i, StringComparer.Ordinal))
            {
                var name = folders[id].Name.Length > 0 ? folders[id].Name : "Folder";
                var unique = name;
                for (var n = 2; !taken.Add(unique); n++) unique = $"{name} ({n})";
                names[id] = unique;
            }
        }
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        string PathOf(string id) =>
            paths.TryGetValue(id, out var p) ? p
                : paths[id] = FolderPath.Combine(parents[id].Length == 0 ? "" : PathOf(parents[id]), names[id]);
        foreach (var id in folders.Keys) PathOf(id);
        return paths;
    }

    /// <summary>Joining: local folders whose path the group already has become the group's folders.</summary>
    private void MapFolders(SyncDocument doc) => AdoptFolders(FolderPaths(doc));

    /// <summary>Takes the document's folders (id → path) as the known ones, as they are in the document.</summary>
    private void AdoptFolders(Dictionary<string, string> paths)
    {
        foreach (var (id, path) in paths) _folderIds[path] = id;
        foreach (var (id, path) in paths)
        {
            var parent = FolderPath.Parent(path);
            _folderBaseline[id] = new JsonObject
            {
                ["name"] = FolderPath.Name(path),
                ["parent"] = parent.Length == 0 ? "" : _folderIds.GetValueOrDefault(parent, ""),
            };
        }
    }
}
