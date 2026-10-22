using System;
using System.IO;

namespace ChameLab.Services;

/// <summary>
/// Small persistent string store for app data (saved cards, preferences).
/// Platform heads can swap the implementation, e.g. the browser head uses
/// <c>localStorage</c> because WebAssembly has no persistent file system.
/// </summary>
public interface IKeyValueStore
{
    string? Read(string key);
    void Write(string key, string value);
}

/// <summary>Stores each key as a file under the per-user application data folder.</summary>
public sealed class FileKeyValueStore(string? root = null) : IKeyValueStore
{
    private readonly string _root = root ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ChameLab");

    private string PathFor(string key) => Path.Combine(_root, key + ".json");

    public string? Read(string key)
    {
        var path = PathFor(key);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    public void Write(string key, string value)
    {
        Directory.CreateDirectory(_root);
        // Write-then-rename so a crash mid-save never truncates the library.
        var path = PathFor(key);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, value);
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>Fallback that keeps data for the session only.</summary>
public sealed class MemoryKeyValueStore : IKeyValueStore
{
    private readonly System.Collections.Generic.Dictionary<string, string> _data = new();
    public string? Read(string key) => _data.TryGetValue(key, out var v) ? v : null;
    public void Write(string key, string value) => _data[key] = value;
}
