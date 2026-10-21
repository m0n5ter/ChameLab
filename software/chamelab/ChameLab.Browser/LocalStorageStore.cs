using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using ChameLab.Services;

namespace ChameLab.Browser;

/// <summary>
/// Persists app data in the page's <c>localStorage</c>: the WebAssembly file
/// system is in-memory and would lose saved cards on reload.
/// </summary>
[SupportedOSPlatform("browser")]
internal sealed partial class LocalStorageStore : IKeyValueStore
{
    private const string Prefix = "chamelab:";

    [JSImport("globalThis.localStorage.getItem")]
    private static partial string? GetItem(string key);

    [JSImport("globalThis.localStorage.setItem")]
    private static partial void SetItem(string key, string value);

    public string? Read(string key) => GetItem(Prefix + key);

    public void Write(string key, string value) => SetItem(Prefix + key, value);
}
