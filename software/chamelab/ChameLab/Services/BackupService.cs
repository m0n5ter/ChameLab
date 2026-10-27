using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChameLab.Models;

namespace ChameLab.Services;

/// <summary>
/// Exports and imports the app's saved cards and key dictionaries as a single
/// JSON backup. The shape is <c>{ "format": "chamelab-backup",
/// "cards": [...], "dictionaries": [...] }</c>.
/// </summary>
public static class BackupService
{
    public const string FormatTag = "chamelab-backup";

    public static byte[] Export(CardLibrary cards, DictionaryLibrary dictionaries)
    {
        var o = new JsonObject
        {
            ["format"] = FormatTag,
            ["version"] = 1,
            ["cards"] = new JsonArray(cards.Cards.Select(c => (JsonNode)c.ToJson()).ToArray()),
            ["dictionaries"] = new JsonArray(dictionaries.Dictionaries.Select(d => (JsonNode)d.ToJson()).ToArray()),
            ["knownCardKeys"] = dictionaries.KnownKeys.ToJson(),
        };
        return Encoding.UTF8.GetBytes(o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Imports a backup, appending its cards and dictionaries. Returns the counts added.</summary>
    public static (int cards, int dictionaries) Import(byte[] data, CardLibrary cards, DictionaryLibrary dictionaries)
    {
        var text = Encoding.UTF8.GetString(data).TrimStart('﻿');
        if (JsonNode.Parse(text) is not JsonObject o)
            throw new FormatException("Not a valid backup file.");
        if ((string?)o["format"] != FormatTag)
            throw new FormatException("This is not a ChameLab backup.");

        var importedCards = o["cards"] is JsonArray ca
            ? ca.Where(n => n is not null).Select(n => CardSave.FromJson(n!)).ToList()
            : new();
        var importedDicts = o["dictionaries"] is JsonArray da
            ? da.Where(n => n is not null).Select(n => KeyDictionary.FromJson(n!)).ToList()
            : new();

        if (o["knownCardKeys"] is { } knownKeys) dictionaries.KnownKeys.Merge(knownKeys);
        if (importedCards.Count > 0) cards.AddRange(importedCards);
        if (importedDicts.Count > 0) dictionaries.AddRange(importedDicts);
        return (importedCards.Count, importedDicts.Count);
    }
}
