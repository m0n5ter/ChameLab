using System.Linq;
using System.Text.Json.Nodes;

namespace ChameLab.Services;

/// <summary>Persistent choices, independent of which dictionaries are currently displayed.</summary>
internal static class KeySourceSelection
{
    private const string Setting = "dumpUncheckedDictionaries";

    private static System.Collections.Generic.HashSet<string> Load(JsonNode? value) =>
        value is JsonArray ids
            ? ids.Select(n => n is JsonValue v && v.TryGetValue<string>(out var id) ? id : null)
                .OfType<string>().ToHashSet()
            : new();

    public static bool IsSelected(string id) => !Load(AppSettings.Get(Setting)).Contains(id);

    public static void SetSelected(string id, bool selected)
    {
        // Merge only the explicit change. Another window or an unavailable library must
        // not erase saved choices for other dictionaries.
        AppSettings.Update(Setting, value =>
        {
            var uncheckedIds = Load(value);
            if (selected) uncheckedIds.Remove(id); else uncheckedIds.Add(id);
            return new JsonArray(uncheckedIds.Order().Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
        });
    }
}
