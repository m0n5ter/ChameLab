using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChameLab.Models;
using ChameLab.Localization;

namespace ChameLab.Services;

/// <summary>
/// The user's saved cards, persisted as a JSON array through an
/// <see cref="IKeyValueStore"/>. Every mutation saves immediately and raises
/// <see cref="Changed"/> so open pages can refresh. Cards are filed in nested folders by
/// <see cref="CardSave.Folder"/> (see <see cref="FolderPath"/>); the folders are kept apart as
/// well, so a folder stays while it is empty.
/// </summary>
public sealed class CardLibrary : IFolderLibrary
{
    private const string StoreKey = "cards";
    private const string FoldersKey = "cardFolders";
    private readonly IKeyValueStore _store;
    private readonly List<CardSave> _cards = new();
    private readonly FolderList<CardSave> _folders;

    public CardLibrary(IKeyValueStore store)
    {
        _store = store;
        _folders = new FolderList<CardSave>(_cards, c => c.Folder, (c, f) => c.Folder = f);
        _folders.Relocated += (from, to) => FolderRelocated?.Invoke(from, to);
        Load();
    }

    public IReadOnlyList<CardSave> Cards => _cards;

    /// <summary>Folder paths in tree order, names A–Z, numbers by value (Folder 2 before Folder 10).</summary>
    public IReadOnlyList<string> Folders => _folders.Paths;

    public IEnumerable<string> ItemFolders => _folders.ItemFolders;

    /// <summary>Set when the stored library could not be parsed (it is left untouched on disk).</summary>
    public string? LoadError { get; private set; }

    public event Action? Changed;

    /// <summary>A folder moved or was renamed (old path, new path), raised before <see cref="Changed"/>.</summary>
    public event Action<string, string>? FolderRelocated;

    /// <summary>
    /// Puts exactly these cards and folders in the library, in this order (sync applying other
    /// devices' changes). Entries that did not change keep their objects, so open pages keep them.
    /// </summary>
    public void ReplaceFromSync(IReadOnlyList<CardSave> items, IEnumerable<string> folders)
    {
        if (LoadError is not null) return;
        var current = _cards.GroupBy(i => i.Id).ToDictionary(g => g.Key, g => g.First());
        var next = new List<CardSave>(items.Count);
        foreach (var item in items)
            next.Add(current.TryGetValue(item.Id, out var old) && JsonNode.DeepEquals(old.ToJson(), item.ToJson()) ? old : item);
        _cards.Clear();
        _cards.AddRange(next);
        _folders.ReplaceAll(folders);
        IsApplyingSync = true;
        try
        {
            foreach (var item in _cards) _folders.File(item, item.Folder);
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
            // No cards saved yet (empty folders may still be).
            if (!string.IsNullOrWhiteSpace(json) && JsonNode.Parse(json) is JsonArray list)
                foreach (var node in list)
                    if (node is not null) _cards.Add(CardSave.FromJson(node));
        }
        catch (Exception ex)
        {
            LoadError = string.Format(Strings.Library_CardsLoadError, ex.Message);
            _cards.Clear();
        }
        LoadFolders();
    }

    private void LoadFolders()
    {
        JsonNode? names = null;
        try { names = JsonNode.Parse(_store.Read(FoldersKey) ?? "[]"); }
        catch (Exception)
        {
            // Lost names only drop empty folders; the cards' own folders come back below.
        }
        _folders.Load(names);
    }

    private void Save()
    {
        // Never overwrite a library we failed to parse.
        if (LoadError is not null) return;
        var array = new JsonArray(_cards.Select(c => (JsonNode)c.ToJson()).ToArray());
        _store.Write(StoreKey, array.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        _store.Write(FoldersKey, _folders.ToJson().ToJsonString());
        Changed?.Invoke();
    }

    public void Add(CardSave card) => AddRange(new[] { card });

    public void AddRange(IEnumerable<CardSave> cards)
    {
        foreach (var card in cards)
        {
            // Imports of an already-saved card keep both copies.
            if (_cards.Any(c => c.Id == card.Id)) card.Id = Guid.NewGuid().ToString();
            _cards.Add(card);
            _folders.File(card, card.Folder);
        }
        Save();
    }

    /// <summary>Creates the folders listed (empty ones too), e.g. from a backup.</summary>
    public void AddFolders(IEnumerable<string> folders)
    {
        foreach (var folder in folders) _folders.Add(folder);
        Save();
    }

    /// <summary>
    /// Brings in another app's library: creates its folders (empty ones too) and adds its
    /// cards. A card already here (same id, e.g. imported before without folders) is not
    /// added again; if it is in no folder it is filed in the imported card's folder.
    /// </summary>
    /// <returns>Cards added, and cards already here that were filed in a folder.</returns>
    public (int Added, int Refiled) Merge(IEnumerable<CardSave> cards, IEnumerable<string> folders)
    {
        foreach (var folder in folders) _folders.Add(folder);
        int added = 0, refiled = 0;
        foreach (var card in cards)
        {
            var folder = _folders.Add(card.Folder);
            if (_cards.FirstOrDefault(c => c.Id == card.Id) is { } existing)
            {
                if (existing.Folder.Length > 0 || folder.Length == 0) continue;
                existing.Folder = folder;
                refiled++;
                continue;
            }
            card.Folder = folder;
            _cards.Add(card);
            added++;
        }
        Save();
        return (added, refiled);
    }

    public void Update(CardSave card)
    {
        var i = _cards.FindIndex(c => c.Id == card.Id);
        if (i < 0) _cards.Add(card); else _cards[i] = card;
        _folders.File(card, card.Folder);
        Save();
    }

    /// <summary>
    /// Puts the given cards in the given order. Only the places they already take in the
    /// library are reshuffled, so cards left out (filtered from view) keep theirs.
    /// </summary>
    public void Reorder(IReadOnlyList<string> ids)
    {
        var byId = _cards.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First());
        if (ids.Distinct().Count() != ids.Count || !ids.All(byId.ContainsKey)) return;
        var wanted = ids.ToHashSet();
        var slots = new List<int>();
        for (var i = 0; i < _cards.Count; i++)
            if (wanted.Contains(_cards[i].Id)) slots.Add(i);
        var moved = ids.Select(id => byId[id]).ToList();
        if (slots.Count != moved.Count) return;
        var changed = false;
        for (var i = 0; i < slots.Count; i++)
        {
            if (ReferenceEquals(_cards[slots[i]], moved[i])) continue;
            _cards[slots[i]] = moved[i];
            changed = true;
        }
        if (changed) Save();
    }

    public void Remove(CardSave card)
    {
        if (_cards.RemoveAll(c => c.Id == card.Id) > 0) Save();
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

    /// <summary>Deletes a folder and the folders in it; their cards move up to the folder that held it.</summary>
    public void RemoveFolder(string path)
    {
        if (_folders.Remove(path)) Save();
    }

    /// <summary>Files a card in a folder (empty for none), creating the folder if needed.</summary>
    public void MoveToFolder(CardSave card, string folder)
    {
        if (_cards.FirstOrDefault(c => c.Id == card.Id) is not { } saved) return;
        var path = _folders.Find(folder) ?? FolderPath.Normalize(folder);
        if (saved.Folder == path) return;
        path = _folders.File(saved, path);
        if (!ReferenceEquals(saved, card)) card.Folder = path;
        Save();
    }
}
