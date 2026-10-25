using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChameLab.Models;

namespace ChameLab.Services;

/// <summary>
/// The user's saved cards, persisted as a JSON array through an
/// <see cref="IKeyValueStore"/>. Every mutation saves immediately and raises
/// <see cref="Changed"/> so open pages can refresh. Cards are filed in one level of
/// folders by <see cref="CardSave.Folder"/>; the folder names are kept apart as well, so a
/// folder stays while it is empty.
/// </summary>
public sealed class CardLibrary
{
    private const string StoreKey = "cards";
    private const string FoldersKey = "cardFolders";
    private readonly IKeyValueStore _store;
    private readonly List<CardSave> _cards = new();
    private readonly List<string> _folders = new();

    public CardLibrary(IKeyValueStore store)
    {
        _store = store;
        Load();
    }

    public IReadOnlyList<CardSave> Cards => _cards;

    /// <summary>Folder names, A–Z, numbers by value (Folder 2 before Folder 10).</summary>
    public IReadOnlyList<string> Folders => _folders;

    /// <summary>Set when the stored library could not be parsed (it is left untouched on disk).</summary>
    public string? LoadError { get; private set; }

    public event Action? Changed;

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
            LoadError = $"Saved cards could not be read: {ex.Message}";
            _cards.Clear();
        }
        LoadFolders();
    }

    private void LoadFolders()
    {
        try
        {
            if (JsonNode.Parse(_store.Read(FoldersKey) ?? "[]") is JsonArray list)
                foreach (var node in list)
                    AddFolderName((string?)node);
        }
        catch (Exception)
        {
            // Lost names only drop empty folders; the cards' own folders come back below.
        }
        foreach (var card in _cards) AddFolderName(card.Folder);
    }

    private void Save()
    {
        // Never overwrite a library we failed to parse.
        if (LoadError is not null) return;
        var array = new JsonArray(_cards.Select(c => (JsonNode)c.ToJson()).ToArray());
        _store.Write(StoreKey, array.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        var folders = new JsonArray(_folders.Select(f => (JsonNode)f).ToArray());
        _store.Write(FoldersKey, folders.ToJsonString());
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
            AddFolderName(card.Folder);
        }
        Save();
    }

    public void Update(CardSave card)
    {
        var i = _cards.FindIndex(c => c.Id == card.Id);
        if (i < 0) _cards.Add(card); else _cards[i] = card;
        AddFolderName(card.Folder);
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

    private static readonly StringComparer FolderOrder = MakeFolderOrder();

    /// <summary>A–Z ignoring case, with runs of digits compared as numbers where the platform can.</summary>
    private static StringComparer MakeFolderOrder()
    {
        try
        {
            var comparer = CultureInfo.CurrentCulture.CompareInfo
                .GetStringComparer(CompareOptions.IgnoreCase | CompareOptions.NumericOrdering);
            comparer.Compare("a2", "a10");
            return comparer;
        }
        catch (Exception)
        {
            return StringComparer.CurrentCultureIgnoreCase;
        }
    }

    /// <summary>The folder of that name as it is spelled in the library (names ignore case), or null.</summary>
    public string? FindFolder(string? name)
    {
        var n = CardSave.NormalizeFolder(name);
        return _folders.FirstOrDefault(f => string.Equals(f, n, StringComparison.CurrentCultureIgnoreCase));
    }

    /// <summary>Adds the name to the folder list (kept A–Z) unless it is empty or already there.</summary>
    private void AddFolderName(string? name)
    {
        var n = CardSave.NormalizeFolder(name);
        if (n.Length == 0 || FindFolder(n) is not null) return;
        var i = _folders.FindIndex(f => FolderOrder.Compare(f, n) > 0);
        _folders.Insert(i < 0 ? _folders.Count : i, n);
    }

    /// <summary>Creates an empty folder. False when the name is empty or taken.</summary>
    public bool AddFolder(string name)
    {
        var n = CardSave.NormalizeFolder(name);
        if (n.Length == 0 || FindFolder(n) is not null) return false;
        AddFolderName(n);
        Save();
        return true;
    }

    /// <summary>Renames a folder and refiles its cards. False when the new name is empty or taken.</summary>
    public bool RenameFolder(string from, string to)
    {
        var n = CardSave.NormalizeFolder(to);
        if (FindFolder(from) is not { } old || n.Length == 0) return false;
        // Only the spelling may change to that of another folder: its own.
        if (FindFolder(n) is { } taken && taken != old) return false;
        if (n == old) return true;
        _folders.Remove(old);
        AddFolderName(n);
        foreach (var card in _cards.Where(c => c.Folder == old)) card.Folder = n;
        Save();
        return true;
    }

    /// <summary>Deletes a folder; its cards stay in the library, in no folder.</summary>
    public void RemoveFolder(string name)
    {
        if (FindFolder(name) is not { } old) return;
        _folders.Remove(old);
        foreach (var card in _cards.Where(c => c.Folder == old)) card.Folder = string.Empty;
        Save();
    }

    /// <summary>Files a card in a folder (empty for none), creating the folder if needed.</summary>
    public void MoveToFolder(CardSave card, string folder)
    {
        var n = FindFolder(folder) ?? CardSave.NormalizeFolder(folder);
        if (_cards.FirstOrDefault(c => c.Id == card.Id) is not { } saved || saved.Folder == n) return;
        saved.Folder = n;
        if (!ReferenceEquals(saved, card)) card.Folder = n;
        AddFolderName(n);
        Save();
    }
}
