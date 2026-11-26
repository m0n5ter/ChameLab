using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChameLab.Models;
using ChameLab.Localization;

namespace ChameLab.Services;

/// <summary>
/// The user's saved key dictionaries, persisted via an <see cref="IKeyValueStore"/>. Dictionaries
/// are filed in nested folders like cards (see <see cref="FolderPath"/>); the folders are kept
/// apart as well, so a folder stays while it is empty.
/// </summary>
public sealed class DictionaryLibrary : IFolderLibrary
{
    private const string StoreKey = "dictionaries";
    private const string FoldersKey = "dictionaryFolders";
    private readonly IKeyValueStore _store;
    private readonly List<KeyDictionary> _dicts = new();
    private readonly FolderList<KeyDictionary> _folders;

    public DictionaryLibrary(IKeyValueStore store)
    {
        _store = store;
        _folders = new FolderList<KeyDictionary>(_dicts, d => d.Folder, (d, f) => d.Folder = f);
        KnownKeys = new KnownCardKeys(store);
        KnownKeys.Changed += () => Changed?.Invoke();
        _folders.Relocated += (from, to) => FolderRelocated?.Invoke(from, to);
        Load();
    }

    public IReadOnlyList<KeyDictionary> Dictionaries => _dicts;
    public KnownCardKeys KnownKeys { get; }

    /// <summary>Folder paths in tree order.</summary>
    public IReadOnlyList<string> Folders => _folders.Paths;

    public IEnumerable<string> ItemFolders => _folders.ItemFolders;

    public string? LoadError { get; private set; }

    public event Action? Changed;

    /// <summary>A folder moved or was renamed (old path, new path), raised before <see cref="Changed"/>.</summary>
    public event Action<string, string>? FolderRelocated;

    /// <summary>
    /// Puts exactly these dictionaries and folders in the library, in this order (sync applying other
    /// devices' changes). Entries that did not change keep their objects, so open pages keep them.
    /// </summary>
    public void ReplaceFromSync(IReadOnlyList<KeyDictionary> items, IEnumerable<string> folders)
    {
        if (LoadError is not null) return;
        var current = _dicts.GroupBy(i => i.Id).ToDictionary(g => g.Key, g => g.First());
        var next = new List<KeyDictionary>(items.Count);
        foreach (var item in items)
            next.Add(current.TryGetValue(item.Id, out var old) && JsonNode.DeepEquals(old.ToJson(), item.ToJson()) ? old : item);
        _dicts.Clear();
        _dicts.AddRange(next);
        _folders.ReplaceAll(folders);
        IsApplyingSync = true;
        try
        {
            foreach (var item in _dicts) _folders.File(item, item.Folder);
            Save();
        }
        finally { IsApplyingSync = false; }
    }

    /// <summary>True while <see cref="Changed"/> reports changes made on another device.</summary>
    public bool IsApplyingSync { get; private set; }

    private void Load()
    {
        string? json;
        try { json = _store.Read(StoreKey); }
        catch (Exception ex) { LoadError = ex.Message; return; }

        try
        {
            if (!string.IsNullOrWhiteSpace(json) && JsonNode.Parse(json) is JsonArray list)
                foreach (var node in list)
                    if (node is not null) _dicts.Add(KeyDictionary.FromJson(node));
        }
        catch (Exception ex)
        {
            LoadError = string.Format(Strings.Library_DictionariesLoadError, ex.Message);
            _dicts.Clear();
        }

        JsonNode? names = null;
        try { names = JsonNode.Parse(_store.Read(FoldersKey) ?? "[]"); }
        catch (Exception)
        {
            // Lost names only drop empty folders; the dictionaries' own folders come back.
        }
        _folders.Load(names);
    }

    private void Save()
    {
        if (LoadError is not null) return;
        var array = new JsonArray(_dicts.Select(d => (JsonNode)d.ToJson()).ToArray());
        _store.Write(StoreKey, array.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        _store.Write(FoldersKey, _folders.ToJson().ToJsonString());
        Changed?.Invoke();
    }

    public void Add(KeyDictionary dict) => AddRange(new[] { dict });

    public void AddRange(IEnumerable<KeyDictionary> dicts)
    {
        foreach (var dict in dicts)
        {
            if (_dicts.Any(d => d.Id == dict.Id)) dict.Id = Guid.NewGuid().ToString();
            _dicts.Add(dict);
            _folders.File(dict, dict.Folder);
        }
        Save();
    }

    /// <summary>Creates the folders listed (empty ones too), e.g. from a backup.</summary>
    public void AddFolders(IEnumerable<string> folders)
    {
        foreach (var folder in folders) _folders.Add(folder);
        Save();
    }

    public void Update(KeyDictionary dict)
    {
        var i = _dicts.FindIndex(d => d.Id == dict.Id);
        if (i < 0) _dicts.Add(dict); else _dicts[i] = dict;
        _folders.File(dict, dict.Folder);
        Save();
    }

    /// <summary>
    /// Puts the given dictionaries in the given order. Only the places they already take in the
    /// library are reshuffled, so dictionaries left out (filtered from view) keep theirs.
    /// </summary>
    public void Reorder(IReadOnlyList<string> ids)
    {
        var byId = _dicts.GroupBy(d => d.Id).ToDictionary(g => g.Key, g => g.First());
        if (ids.Distinct().Count() != ids.Count || !ids.All(byId.ContainsKey)) return;
        var wanted = ids.ToHashSet();
        var slots = new List<int>();
        for (var i = 0; i < _dicts.Count; i++)
            if (wanted.Contains(_dicts[i].Id)) slots.Add(i);
        var moved = ids.Select(id => byId[id]).ToList();
        if (slots.Count != moved.Count) return;
        var changed = false;
        for (var i = 0; i < slots.Count; i++)
        {
            if (ReferenceEquals(_dicts[slots[i]], moved[i])) continue;
            _dicts[slots[i]] = moved[i];
            changed = true;
        }
        if (changed) Save();
    }

    public void Remove(KeyDictionary dict)
    {
        if (_dicts.RemoveAll(d => d.Id == dict.Id) > 0) Save();
    }

    /// <summary>
    /// Returns <paramref name="name"/>, or appends / increments a trailing <c>(n)</c> index
    /// when another saved dictionary already uses it (case-insensitive).
    /// </summary>
    public string FreeName(string? name) => UniqueName.Next(name, _dicts.Select(d => d.Name));

    /// <summary>
    /// Assigns unique indexed names to <paramref name="dicts"/> against the library and each other.
    /// </summary>
    public void EnsureUniqueNames(IEnumerable<KeyDictionary> dicts)
    {
        var taken = _dicts.Select(d => d.Name.Trim()).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        foreach (var dict in dicts) dict.Name = UniqueName.Next(dict.Name, taken);
    }

    // ---- Folders --------------------------------------------------------

    public string? FindFolder(string? path) => _folders.Find(path);

    public string? AddFolder(string path)
    {
        if (FolderPath.Normalize(path).Length == 0 || _folders.Find(path) is not null) return null;
        var added = _folders.Add(path);
        Save();
        return added;
    }

    public string? RenameFolder(string path, string newName)
    {
        var old = _folders.Find(path);
        var renamed = _folders.Rename(path, newName);
        if (renamed is not null && renamed != old) Save();
        return renamed;
    }

    public string? MoveFolder(string path, string parent)
    {
        var old = _folders.Find(path);
        var moved = _folders.Move(path, parent);
        if (moved is not null && moved != old) Save();
        return moved;
    }

    /// <summary>Deletes a folder and the folders in it; their dictionaries move up to the folder that held it.</summary>
    public void RemoveFolder(string path)
    {
        if (_folders.Remove(path)) Save();
    }

    /// <summary>Files a dictionary in a folder (empty for none), creating the folder if needed.</summary>
    public void MoveToFolder(KeyDictionary dict, string folder)
    {
        if (_dicts.FirstOrDefault(d => d.Id == dict.Id) is not { } saved) return;
        var path = _folders.Find(folder) ?? FolderPath.Normalize(folder);
        if (saved.Folder == path) return;
        path = _folders.File(saved, path);
        if (!ReferenceEquals(saved, dict)) dict.Folder = path;
        Save();
    }

    /// <summary>All keys from every dictionary, de-duplicated, for a dump pass.</summary>
    public IReadOnlyList<byte[]> AllKeys(KeyDictionaryType type = KeyDictionaryType.MifareClassic)
    {
        var seen = new HashSet<string>();
        var keys = new List<byte[]>();
        var length = type == KeyDictionaryType.T55xx ? 4 : 6;
        foreach (var k in _dicts.SelectMany(d => d.Keys).Where(k => k.Length == length))
            if (seen.Add(Convert.ToHexString(k))) keys.Add(k);
        return keys;
    }
}
