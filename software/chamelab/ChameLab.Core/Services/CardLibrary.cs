using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChameLab.Models;
using ChameLab.Protocol;
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
    private readonly Dictionary<string, List<CardSave>> _byHash = new(StringComparer.Ordinal);
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
        RebuildIndex();
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
        RebuildIndex();
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
            IndexCard(card);
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
    /// cards. When <paramref name="baseFolder"/> is given, imported folders and loose cards
    /// land under it instead of the library root. A card already here (same id, e.g. imported
    /// before without folders) is not added again; if it is in no folder it is filed in the
    /// imported card's folder.
    /// </summary>
    /// <returns>Cards added, and cards already here that were filed in a folder.</returns>
    public (int Added, int Refiled) Merge(
        IEnumerable<CardSave> cards, IEnumerable<string> folders, string baseFolder = "")
    {
        var target = FolderPath.Normalize(baseFolder);
        if (target.Length > 0) _folders.Add(target);
        foreach (var folder in folders)
            _folders.Add(FolderPath.Combine(target, FolderPath.Normalize(folder)));
        int added = 0, refiled = 0;
        var taken = _cards.Select(c => c.Name.Trim()).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        foreach (var card in cards)
        {
            var folder = _folders.Add(FolderPath.Combine(target, FolderPath.Normalize(card.Folder)));
            if (_cards.FirstOrDefault(c => c.Id == card.Id) is { } existing)
            {
                if (existing.Folder.Length > 0 || folder.Length == 0) continue;
                existing.Folder = folder;
                refiled++;
                continue;
            }
            card.Folder = folder;
            card.Name = UniqueName.Next(card.Name, taken);
            _cards.Add(card);
            IndexCard(card);
            added++;
        }
        Save();
        return (added, refiled);
    }

    public void Update(CardSave card)
    {
        var i = _cards.FindIndex(c => c.Id == card.Id);
        if (i < 0) _cards.Add(card); else _cards[i] = card;
        RebuildIndex();
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
        if (changed)
        {
            RebuildIndex();
            Save();
        }
    }

    public void Remove(CardSave card)
    {
        if (_cards.RemoveAll(c => c.Id == card.Id) > 0)
        {
            RebuildIndex();
            Save();
        }
    }

    /// <summary>
    /// Returns <paramref name="name"/>, or appends / increments a trailing <c>(n)</c> index
    /// when another saved card already uses it (case-insensitive).
    /// </summary>
    public string FreeName(string? name) => UniqueName.Next(name, _cards.Select(c => c.Name));

    /// <summary>
    /// Assigns unique indexed names to <paramref name="cards"/> against the library and each other.
    /// </summary>
    public void EnsureUniqueNames(IEnumerable<CardSave> cards)
    {
        var taken = _cards.Select(c => c.Name.Trim()).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        foreach (var card in cards) card.Name = UniqueName.Next(card.Name, taken);
    }

    // ---- Content hash lookup -------------------------------------------

    /// <summary>
    /// Returns every saved card whose full slot-comparable content matches <paramref name="card"/>,
    /// in library order.
    /// </summary>
    public IReadOnlyList<CardSave> FindMatches(CardSave card)
    {
        if (!_byHash.TryGetValue(card.ContentHash(), out var bucket))
            return Array.Empty<CardSave>();
        return bucket.Where(c => CardSave.ContentEquals(c, card)).ToList();
    }

    /// <summary>
    /// Finds the saved card whose full slot-comparable content matches <paramref name="card"/>,
    /// picking among identical copies by <paramref name="preferredId"/>, <paramref name="preferredName"/>,
    /// then <paramref name="preferredNick"/> (against <see cref="ChameleonClient.SlotNickFor"/>),
    /// or the first match in library order.
    /// </summary>
    public CardSave? FindMatch(
        CardSave card, string? preferredNick = null, string? preferredId = null, string? preferredName = null)
    {
        if (!_byHash.TryGetValue(card.ContentHash(), out var bucket))
            return null;
        if (bucket.Count == 1)
            return CardSave.ContentEquals(bucket[0], card) ? bucket[0] : null;

        CardSave? first = null;
        CardSave? byName = null;
        CardSave? byNick = null;
        var hasNick = !string.IsNullOrEmpty(preferredNick);
        foreach (var candidate in bucket)
        {
            if (!CardSave.ContentEquals(candidate, card)) continue;
            first ??= candidate;
            if (preferredId is not null && candidate.Id == preferredId) return candidate;
            if (byName is null && preferredName is not null && candidate.Name == preferredName) byName = candidate;
            if (byNick is null && hasNick && ChameleonClient.SlotNickFor(candidate) == preferredNick) byNick = candidate;
        }
        return byName ?? byNick ?? first;
    }

    private void RebuildIndex()
    {
        _byHash.Clear();
        foreach (var card in _cards) IndexCard(card);
    }

    private void IndexCard(CardSave card)
    {
        var hash = card.ContentHash();
        if (!_byHash.TryGetValue(hash, out var bucket))
        {
            bucket = new List<CardSave>(1);
            _byHash[hash] = bucket;
        }
        bucket.Add(card);
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
