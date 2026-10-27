using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChameLab.Models;

namespace ChameLab.Services;

/// <summary>The user's saved key dictionaries, persisted via an <see cref="IKeyValueStore"/>.</summary>
public sealed class DictionaryLibrary
{
    private const string StoreKey = "dictionaries";
    private readonly IKeyValueStore _store;
    private readonly List<KeyDictionary> _dicts = new();

    public DictionaryLibrary(IKeyValueStore store)
    {
        _store = store;
        KnownKeys = new KnownCardKeys(store);
        KnownKeys.Changed += () => Changed?.Invoke();
        Load();
    }

    public IReadOnlyList<KeyDictionary> Dictionaries => _dicts;
    public KnownCardKeys KnownKeys { get; }
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
                    if (node is not null) _dicts.Add(KeyDictionary.FromJson(node));
        }
        catch (Exception ex)
        {
            LoadError = $"Saved dictionaries could not be read: {ex.Message}";
            _dicts.Clear();
        }
    }

    private void Save()
    {
        if (LoadError is not null) return;
        var array = new JsonArray(_dicts.Select(d => (JsonNode)d.ToJson()).ToArray());
        _store.Write(StoreKey, array.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        Changed?.Invoke();
    }

    public void Add(KeyDictionary dict) => AddRange(new[] { dict });

    public void AddRange(IEnumerable<KeyDictionary> dicts)
    {
        foreach (var dict in dicts)
        {
            if (_dicts.Any(d => d.Id == dict.Id)) dict.Id = Guid.NewGuid().ToString();
            _dicts.Add(dict);
        }
        Save();
    }

    public void Update(KeyDictionary dict)
    {
        var i = _dicts.FindIndex(d => d.Id == dict.Id);
        if (i < 0) _dicts.Add(dict); else _dicts[i] = dict;
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

    /// <summary>All keys from every dictionary, de-duplicated, for a dump pass.</summary>
    public IReadOnlyList<byte[]> AllKeys()
    {
        var seen = new HashSet<string>();
        var keys = new List<byte[]>();
        foreach (var k in _dicts.SelectMany(d => d.Keys))
            if (seen.Add(Convert.ToHexString(k))) keys.Add(k);
        return keys;
    }
}
