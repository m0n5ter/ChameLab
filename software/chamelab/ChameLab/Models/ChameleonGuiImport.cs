using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace ChameLab.Models;

/// <summary>What a Chameleon Ultra GUI file holds: cards (filed by <see cref="CardSave.Folder"/>), every folder name (empty ones too) and key dictionaries.</summary>
public sealed record ChameleonGuiLibrary(
    IReadOnlyList<CardSave> Cards,
    IReadOnlyList<string> Folders,
    IReadOnlyList<KeyDictionary> Dictionaries);

/// <summary>
/// Reads the saved cards and their folders out of the Flutter Chameleon Ultra GUI's files:
/// <list type="bullet">
/// <item>its settings export (Settings → Export settings → JSON file), which holds the whole library;</item>
/// <item>its folder export (<c>"format": "chameleon-ultra-gui-folder"</c>);</item>
/// <item>its desktop <c>shared_preferences.json</c> (keys prefixed <c>flutter.</c>, entries as JSON strings).</item>
/// </list>
/// The GUI nests folders; ChameLab keeps one level, so a nested folder becomes the path of
/// names from the top, joined by <see cref="PathSeparator"/>.
/// </summary>
public static class ChameleonGuiImport
{
    public const string FolderFormat = "chameleon-ultra-gui-folder";
    public const string PathSeparator = " / ";

    /// <summary>The library in the file, or null when it is not a Chameleon Ultra GUI library file.</summary>
    public static ChameleonGuiLibrary? TryRead(byte[] bytes)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(Encoding.UTF8.GetString(bytes).TrimStart('﻿')); }
        catch (Exception) { return null; }
        return node is null ? null : TryRead(node);
    }

    /// <inheritdoc cref="TryRead(byte[])"/>
    public static ChameleonGuiLibrary? TryRead(JsonNode node)
    {
        if (node is not JsonObject o) return null;

        if (o["format"] is { } format)
        {
            if ((string?)format != FolderFormat) return null;
            return Read(Items(o["cards"]), Items(o["folders"]), [], (string?)o["rootFolderId"]);
        }

        var cards = o["cards"] ?? o["flutter.cards"];
        var folders = o["card_folders"] ?? o["flutter.card_folders"];
        if (cards is not JsonArray && folders is not JsonArray) return null;
        return Read(Items(cards), Items(folders), Items(o["dictionaries"] ?? o["flutter.dictionaries"]), null);
    }

    /// <summary>The objects of a list; shared_preferences keeps each one as a JSON string.</summary>
    private static List<JsonObject> Items(JsonNode? node)
    {
        var items = new List<JsonObject>();
        if (node is not JsonArray list) return items;
        foreach (var item in list)
        {
            if (item is JsonObject obj) items.Add(obj);
            else if (item is JsonValue v && v.TryGetValue<string>(out var s) && JsonNode.Parse(s) is JsonObject parsed)
                items.Add(parsed);
        }
        return items;
    }

    private static ChameleonGuiLibrary Read(
        List<JsonObject> cardItems, List<JsonObject> folderItems, List<JsonObject> dictItems, string? rootFolderId)
    {
        var folders = new Dictionary<string, (string Name, string? Parent)>();
        foreach (var f in folderItems)
            if ((string?)f["id"] is { } id)
                // A folder export's top folder may point at a parent the file does not hold.
                folders[id] = ((string?)f["name"] ?? string.Empty, id == rootFolderId ? null : (string?)f["parentId"]);

        var paths = new Dictionary<string, string>();
        string PathOf(string id)
        {
            if (paths.TryGetValue(id, out var known)) return known;
            var names = new List<string>();
            var seen = new HashSet<string>();
            for (var at = id; at is not null && seen.Add(at) && folders.TryGetValue(at, out var f); at = f.Parent)
                if (f.Name.Trim().Length > 0) names.Add(f.Name.Trim());
            names.Reverse();
            return paths[id] = CardSave.NormalizeFolder(string.Join(PathSeparator, names));
        }

        var cards = new List<CardSave>();
        foreach (var item in cardItems)
        {
            var card = CardSave.FromJson(item);
            card.Folder = (string?)item["folderId"] is { } folderId && folders.ContainsKey(folderId)
                ? PathOf(folderId)
                : string.Empty;
            cards.Add(card);
        }

        var folderNames = folders.Keys.Select(PathOf).Where(n => n.Length > 0)
            .Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        var dictionaries = dictItems.Select(d => KeyDictionary.FromJson(d)).ToList();
        return new ChameleonGuiLibrary(cards, folderNames, dictionaries);
    }
}
