using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChameLab.Models;

namespace ChameLab.Services;

/// <summary>
/// The user's saved cards, persisted as a JSON array through an
/// <see cref="IKeyValueStore"/>. Every mutation saves immediately and raises
/// <see cref="Changed"/> so open pages can refresh.
/// </summary>
public sealed class CardLibrary
{
    private const string StoreKey = "cards";
    private readonly IKeyValueStore _store;
    private readonly List<CardSave> _cards = new();

    public CardLibrary(IKeyValueStore store)
    {
        _store = store;
        Load();
    }

    public IReadOnlyList<CardSave> Cards => _cards;

    /// <summary>Set when the stored library could not be parsed (it is left untouched on disk).</summary>
    public string? LoadError { get; private set; }

    public event Action? Changed;

    private void Load()
    {
        string? json;
        try { json = _store.Read(StoreKey); }
        catch (Exception ex) { LoadError = ex.Message; return; }
        if (string.IsNullOrWhiteSpace(json)) return;

        try
        {
            if (JsonNode.Parse(json) is JsonArray list)
                foreach (var node in list)
                    if (node is not null) _cards.Add(CardSave.FromJson(node));
        }
        catch (Exception ex)
        {
            LoadError = $"Saved cards could not be read: {ex.Message}";
            _cards.Clear();
        }
    }

    private void Save()
    {
        // Never overwrite a library we failed to parse.
        if (LoadError is not null) return;
        var array = new JsonArray(_cards.Select(c => (JsonNode)c.ToJson()).ToArray());
        _store.Write(StoreKey, array.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
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
        }
        Save();
    }

    public void Update(CardSave card)
    {
        var i = _cards.FindIndex(c => c.Id == card.Id);
        if (i < 0) _cards.Add(card); else _cards[i] = card;
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
}
