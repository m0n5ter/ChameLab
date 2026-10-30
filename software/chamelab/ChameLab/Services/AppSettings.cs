using System;
using System.Text.Json.Nodes;

namespace ChameLab.Services;

/// <summary>
/// App preferences (accent, theme, list sort orders, window placement) kept together
/// as one JSON object under a single store entry, one property per preference.
/// </summary>
public static class AppSettings
{
    private const string StoreKey = "settings";

    private static readonly object Sync = new();

    private static JsonObject Settings => field ??= Load();

    /// <summary>A copy of the saved value, or null when none is saved.</summary>
    public static JsonNode? Get(string name)
    {
        lock (Sync) return Settings[name]?.DeepClone();
    }

    /// <summary>The saved value when it is a string, else null.</summary>
    public static string? GetString(string name) =>
        Get(name) is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>Remembers a value and saves every preference at once (best effort).</summary>
    public static void Set(string name, JsonNode? value) => Update(name, _ => value);

    /// <summary>Reads, changes and saves a preference without losing concurrent changes.</summary>
    internal static void Update(string name, Func<JsonNode?, JsonNode?> update)
    {
        lock (Sync)
        {
            Settings[name] = update(Settings[name]?.DeepClone());
            try { AppServices.Store.Write(StoreKey, Settings.ToJsonString()); }
            catch { /* best effort: the value still holds for this session */ }
        }
    }

    private static JsonObject Load()
    {
        try { return JsonNode.Parse(AppServices.Store.Read(StoreKey) ?? "{}") as JsonObject ?? new JsonObject(); }
        catch { return new JsonObject(); /* unreadable: start from the defaults */ }
    }
}
