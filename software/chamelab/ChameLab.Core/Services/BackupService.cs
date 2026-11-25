using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChameLab.Models;
using ChameLab.Localization;

namespace ChameLab.Services;

public enum BackupConflictResolution
{
    Skip,
    Overwrite,
    KeepBoth,
}

public sealed class BackupCardConflict
{
    public BackupCardConflict(CardSave existing, CardSave imported)
    {
        Existing = existing;
        Imported = imported;
    }

    public CardSave Existing { get; }
    public CardSave Imported { get; }
    public BackupConflictResolution Resolution { get; set; } = BackupConflictResolution.Skip;
}

public sealed class BackupDictionaryConflict
{
    public BackupDictionaryConflict(KeyDictionary existing, KeyDictionary imported)
    {
        Existing = existing;
        Imported = imported;
    }

    public KeyDictionary Existing { get; }
    public KeyDictionary Imported { get; }
    public BackupConflictResolution Resolution { get; set; } = BackupConflictResolution.Skip;
}

public sealed class BackupImportPlan
{
    public List<CardSave> NewCards { get; } = new();
    public List<KeyDictionary> NewDictionaries { get; } = new();
    public List<BackupCardConflict> CardConflicts { get; } = new();
    public List<BackupDictionaryConflict> DictionaryConflicts { get; } = new();
    public int SkippedCards { get; set; }
    public int SkippedDictionaries { get; set; }
    public List<string> CardFolders { get; } = new();
    public List<string> DictionaryFolders { get; } = new();
    public JsonNode? KnownCardKeys { get; set; }

    public bool HasConflicts => CardConflicts.Count > 0 || DictionaryConflicts.Count > 0;
}

/// <summary>
/// Exports and imports the app's saved cards and key dictionaries as a single
/// JSON backup. Supports both ChameLab backups (<c>"format": "chamelab-backup"</c>)
/// and ChameleonUltraGUI exports.
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
            ["cardFolders"] = new JsonArray(cards.Folders.Select(f => (JsonNode)f).ToArray()),
            ["dictionaryFolders"] = new JsonArray(dictionaries.Folders.Select(f => (JsonNode)f).ToArray()),
            ["knownCardKeys"] = dictionaries.KnownKeys.ToJson(),
        };
        return Encoding.UTF8.GetBytes(o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// Parses a ChameLab or ChameleonUltraGUI backup and classifies every card and dictionary
    /// as new (no match), exact match (skipped), or partial match (conflict requiring resolution).
    /// </summary>
    public static BackupImportPlan PlanImport(byte[] data, CardLibrary cards, DictionaryLibrary dictionaries)
    {
        var text = Encoding.UTF8.GetString(data).TrimStart('\uFEFF');
        if (JsonNode.Parse(text) is not JsonObject o)
            throw new FormatException(Strings.Backup_Invalid);

        var plan = new BackupImportPlan();
        IReadOnlyList<CardSave> importedCards;
        IReadOnlyList<KeyDictionary> importedDicts;

        if ((string?)o["format"] == FormatTag)
        {
            importedCards = o["cards"] is JsonArray ca
                ? ca.Where(n => n is not null).Select(n => CardSave.FromJson(n!)).ToList()
                : new List<CardSave>();
            importedDicts = o["dictionaries"] is JsonArray da
                ? da.Where(n => n is not null).Select(n => KeyDictionary.FromJson(n!)).ToList()
                : new List<KeyDictionary>();
            plan.KnownCardKeys = o["knownCardKeys"];
            plan.CardFolders.AddRange(Names(o["cardFolders"]));
            plan.DictionaryFolders.AddRange(Names(o["dictionaryFolders"]));
        }
        else if (ChameleonGuiImport.TryRead(o) is { } gui)
        {
            importedCards = gui.Cards;
            importedDicts = gui.Dictionaries;
            plan.CardFolders.AddRange(gui.Folders);
        }
        else
        {
            throw new FormatException(Strings.Backup_NotChameLab);
        }

        foreach (var card in importedCards)
        {
            var match = FindCardMatch(card, cards.Cards);
            if (match is null)
                plan.NewCards.Add(card);
            else if (CardsEqual(match, card))
                plan.SkippedCards++;
            else
                plan.CardConflicts.Add(new BackupCardConflict(match, card));
        }

        foreach (var dict in importedDicts)
        {
            var match = FindDictionaryMatch(dict, dictionaries.Dictionaries);
            if (match is null)
                plan.NewDictionaries.Add(dict);
            else if (DictionariesEqual(match, dict))
                plan.SkippedDictionaries++;
            else
                plan.DictionaryConflicts.Add(new BackupDictionaryConflict(match, dict));
        }

        return plan;
    }

    /// <summary>Applies a planned import using each conflict's chosen resolution.</summary>
    public static (int cards, int dictionaries) ApplyImport(
        BackupImportPlan plan, CardLibrary cards, DictionaryLibrary dictionaries)
    {
        if (plan.KnownCardKeys is { } knownKeys)
            dictionaries.KnownKeys.Merge(knownKeys);
        if (plan.CardFolders.Count > 0)
            cards.AddFolders(plan.CardFolders);
        if (plan.DictionaryFolders.Count > 0)
            dictionaries.AddFolders(plan.DictionaryFolders);

        var addedCards = 0;
        if (plan.NewCards.Count > 0)
        {
            cards.AddRange(plan.NewCards);
            addedCards += plan.NewCards.Count;
        }
        foreach (var conflict in plan.CardConflicts)
        {
            switch (conflict.Resolution)
            {
                case BackupConflictResolution.Overwrite:
                    conflict.Imported.Id = conflict.Existing.Id;
                    cards.Update(conflict.Imported);
                    addedCards++;
                    break;
                case BackupConflictResolution.KeepBoth:
                    conflict.Imported.Id = Guid.NewGuid().ToString();
                    cards.Add(conflict.Imported);
                    addedCards++;
                    break;
            }
        }

        var addedDicts = 0;
        if (plan.NewDictionaries.Count > 0)
        {
            dictionaries.AddRange(plan.NewDictionaries);
            addedDicts += plan.NewDictionaries.Count;
        }
        foreach (var conflict in plan.DictionaryConflicts)
        {
            switch (conflict.Resolution)
            {
                case BackupConflictResolution.Overwrite:
                    conflict.Imported.Id = conflict.Existing.Id;
                    dictionaries.Update(conflict.Imported);
                    addedDicts++;
                    break;
                case BackupConflictResolution.KeepBoth:
                    conflict.Imported.Id = Guid.NewGuid().ToString();
                    dictionaries.Add(conflict.Imported);
                    addedDicts++;
                    break;
            }
        }

        return (addedCards, addedDicts);
    }

    /// <summary>Imports a backup non-interactively, skipping exact matches and unresolved conflicts.</summary>
    public static (int cards, int dictionaries) Import(byte[] data, CardLibrary cards, DictionaryLibrary dictionaries)
    {
        var plan = PlanImport(data, cards, dictionaries);
        return ApplyImport(plan, cards, dictionaries);
    }

    private static CardSave? FindCardMatch(CardSave imported, IReadOnlyList<CardSave> existingCards)
    {
        foreach (var existing in existingCards)
            if (existing.Id == imported.Id) return existing;

        foreach (var existing in existingCards)
            if (CardsEqual(existing, imported)) return existing;

        if (imported.Uid.Length > 0)
        {
            CardSave? uidMatch = null;
            foreach (var existing in existingCards)
            {
                if (existing.Tag != imported.Tag || !existing.Uid.AsSpan().SequenceEqual(imported.Uid))
                    continue;
                if (string.Equals(existing.Name, imported.Name, StringComparison.OrdinalIgnoreCase))
                    return existing;
                uidMatch ??= existing;
            }
            if (uidMatch is not null) return uidMatch;
        }

        if (!string.IsNullOrWhiteSpace(imported.Name))
        {
            foreach (var existing in existingCards)
                if (existing.Tag == imported.Tag && string.Equals(existing.Name, imported.Name, StringComparison.OrdinalIgnoreCase))
                    return existing;
        }

        return null;
    }

    private static KeyDictionary? FindDictionaryMatch(KeyDictionary imported, IReadOnlyList<KeyDictionary> existingDicts)
    {
        foreach (var existing in existingDicts)
            if (existing.Id == imported.Id) return existing;

        foreach (var existing in existingDicts)
            if (DictionariesEqual(existing, imported)) return existing;

        if (!string.IsNullOrWhiteSpace(imported.Name))
        {
            foreach (var existing in existingDicts)
                if (string.Equals(existing.Name, imported.Name, StringComparison.OrdinalIgnoreCase))
                    return existing;
        }

        return null;
    }

    internal static bool CardsEqual(CardSave a, CardSave b) =>
        string.Equals(a.Name, b.Name, StringComparison.Ordinal)
        && a.Tag == b.Tag
        && a.Uid.AsSpan().SequenceEqual(b.Uid)
        && a.Sak == b.Sak
        && a.Atqa.AsSpan().SequenceEqual(b.Atqa)
        && a.Ats.AsSpan().SequenceEqual(b.Ats)
        && string.Equals(CardSave.NormalizeColor(a.Color), CardSave.NormalizeColor(b.Color), StringComparison.OrdinalIgnoreCase)
        && string.Equals(CardSave.NormalizeFolder(a.Folder), CardSave.NormalizeFolder(b.Folder), StringComparison.CurrentCultureIgnoreCase)
        && ByteListsEqual(a.Data, b.Data)
        && a.Extra.UltralightVersion.AsSpan().SequenceEqual(b.Extra.UltralightVersion)
        && a.Extra.UltralightSignature.AsSpan().SequenceEqual(b.Extra.UltralightSignature)
        && a.Extra.UltralightCounters.SequenceEqual(b.Extra.UltralightCounters)
        && a.Extra.UnknownKeyA.SequenceEqual(b.Extra.UnknownKeyA)
        && a.Extra.UnknownKeyB.SequenceEqual(b.Extra.UnknownKeyB);

    internal static bool DictionariesEqual(KeyDictionary a, KeyDictionary b) =>
        string.Equals(a.Name, b.Name, StringComparison.Ordinal)
        && string.Equals(CardSave.NormalizeColor(a.Color), CardSave.NormalizeColor(b.Color), StringComparison.OrdinalIgnoreCase)
        && string.Equals(FolderPath.Normalize(a.Folder), FolderPath.Normalize(b.Folder), StringComparison.CurrentCultureIgnoreCase)
        && a.KeyLength == b.KeyLength
        && ByteListsEqual(a.Keys, b.Keys);

    private static bool ByteListsEqual(IReadOnlyList<byte[]> a, IReadOnlyList<byte[]> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (!a[i].AsSpan().SequenceEqual(b[i])) return false;
        return true;
    }

    private static List<string> Names(JsonNode? node) => node is JsonArray list
        ? list.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToList()
        : new();
}
