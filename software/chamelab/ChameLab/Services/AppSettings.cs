using System.Text.Json.Nodes;

namespace ChameLab.Services;

/// <summary>
/// App preferences (accent, theme, list sort orders, window placement) kept together
/// as one JSON object under a single store entry, one property per preference.
/// </summary>
public static class AppSettings
{
    private const string StoreKey = "settings";

    private static JsonObject? _settings;

    private static JsonObject Settings => _settings ??= Load();

    /// <summary>A copy of the saved value, or null when none is saved.</summary>
    public static JsonNode? Get(string name) => Settings[name]?.DeepClone();

    /// <summary>The saved value when it is a string, else null.</summary>
    public static string? GetString(string name) =>
        Settings[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>Remembers a value and saves every preference at once (best effort).</summary>
    public static void Set(string name, JsonNode? value)
    {
        Settings[name] = value;
        try { AppServices.Store.Write(StoreKey, Settings.ToJsonString()); }
        catch { /* best effort: the value still holds for this session */ }
    }

    private static JsonObject Load()
    {
        try { return JsonNode.Parse(AppServices.Store.Read(StoreKey) ?? "{}") as JsonObject ?? new JsonObject(); }
        catch { return new JsonObject(); /* unreadable: start from the defaults */ }
    }
}
