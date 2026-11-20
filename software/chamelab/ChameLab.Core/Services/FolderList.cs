using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using ChameLab.Models;

namespace ChameLab.Services;

/// <summary>
/// A library whose items are filed in nested folders (see <see cref="FolderPath"/>). Folders are
/// kept apart from the items, so a folder stays while it is empty.
/// </summary>
public interface IFolderLibrary
{
    /// <summary>Every folder's path, in tree order (a folder, then the folders in it).</summary>
    IReadOnlyList<string> Folders { get; }

    /// <summary>The folder each item is in (empty for none), one entry per item.</summary>
    IEnumerable<string> ItemFolders { get; }

    /// <summary>The folder at that path as it is spelled here (names ignore case), or null.</summary>
    string? FindFolder(string? path);

    /// <summary>Creates a folder (and those it is in); its path, or null when it is empty or there already.</summary>
    string? AddFolder(string path);

    /// <summary>Renames a folder, refiling what is in it; its new path, or null when the name is empty or taken.</summary>
    string? RenameFolder(string path, string newName);

    /// <summary>Moves a folder, with what is in it, into another (empty for the top); its new path, or null when refused.</summary>
    string? MoveFolder(string path, string parent);

    /// <summary>Deletes a folder and the folders in it; their items move up to the folder that held it.</summary>
    void RemoveFolder(string path);

    event Action? Changed;
}

/// <summary>
/// The folder paths of a library in tree order, with the items filed in them: creating, renaming,
/// moving and removing folders refiles the items to match. The owner saves afterwards.
/// </summary>
public sealed class FolderList<T>(List<T> items, Func<T, string> getFolder, Action<T, string> setFolder)
{
    private readonly List<string> _paths = new();

    public IReadOnlyList<string> Paths => _paths;

    /// <summary>A folder moved or was renamed (old path, new path); raised before the owner saves.</summary>
    public event Action<string, string>? Relocated;

    /// <summary>Puts exactly these folders in place of the current ones (sync applying another device's changes).</summary>
    public void ReplaceAll(IEnumerable<string> paths)
    {
        _paths.Clear();
        foreach (var path in paths) Add(path);
    }

    public IEnumerable<string> ItemFolders => items.Select(getFolder);

    public string? Find(string? path)
    {
        var n = FolderPath.Normalize(path);
        return n.Length == 0 ? null : _paths.FirstOrDefault(p => FolderPath.Same(p, n));
    }

    /// <summary>Adds the folder and those it is in (unless there already); its path as spelled here, empty for none.</summary>
    public string Add(string? path)
    {
        var at = string.Empty;
        foreach (var name in FolderPath.Normalize(path).Split(FolderPath.Separator, StringSplitOptions.RemoveEmptyEntries))
        {
            var next = FolderPath.Combine(at, name);
            at = Find(next) ?? Insert(next);
        }
        return at;
    }

    private string Insert(string path)
    {
        var i = _paths.FindIndex(p => FolderPath.TreeOrder.Compare(p, path) > 0);
        _paths.Insert(i < 0 ? _paths.Count : i, path);
        return path;
    }

    /// <summary>Files an item in a folder (empty for none), creating the folder if needed; the folder's path.</summary>
    public string File(T item, string? folder)
    {
        var path = Add(folder);
        setFolder(item, path);
        return path;
    }

    /// <summary>Adds the folders listed, then files every item in its folder as spelled here.</summary>
    public void Load(JsonNode? names)
    {
        if (names is JsonArray list)
            foreach (var node in list)
                if (node is JsonValue v && v.TryGetValue<string>(out var name)) Add(name);
        foreach (var item in items) File(item, getFolder(item));
    }

    public JsonArray ToJson() => new(_paths.Select(p => (JsonNode)p).ToArray());

    public string? Rename(string path, string newName)
    {
        var name = FolderPath.NormalizeName(newName);
        if (Find(path) is not { } old || name.Length == 0) return null;
        return Relocate(old, FolderPath.Combine(FolderPath.Parent(old), name));
    }

    public string? Move(string path, string parent)
    {
        if (Find(path) is not { } old) return null;
        var into = FolderPath.Normalize(parent).Length == 0 ? string.Empty : Find(parent);
        // Not into itself or a folder of its own.
        if (into is null || into.Length > 0 && FolderPath.IsWithin(into, old)) return null;
        return Relocate(old, FolderPath.Combine(into, FolderPath.Name(old)));
    }

    /// <summary>Moves a folder (with the folders and items in it) to a new path, unless another folder has it.</summary>
    private string? Relocate(string old, string to)
    {
        if (to == old) return old;
        // Only the spelling may change to that of another folder: its own.
        if (Find(to) is { } taken && !FolderPath.Same(taken, old)) return null;
        var moved = _paths.Where(p => FolderPath.IsWithin(p, old)).ToList();
        _paths.RemoveAll(moved.Contains);
        foreach (var p in moved) Insert(FolderPath.Rebase(p, old, to));
        foreach (var item in items.Where(i => FolderPath.IsWithin(getFolder(i), old)))
            setFolder(item, FolderPath.Rebase(getFolder(item), old, to));
        Relocated?.Invoke(old, to);
        return to;
    }

    /// <summary>Removes a folder and those in it; their items go to the folder that held it.</summary>
    public bool Remove(string path)
    {
        if (Find(path) is not { } old) return false;
        _paths.RemoveAll(p => FolderPath.IsWithin(p, old));
        var parent = FolderPath.Parent(old);
        foreach (var item in items.Where(i => FolderPath.IsWithin(getFolder(i), old))) setFolder(item, parent);
        return true;
    }
}
