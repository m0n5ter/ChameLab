using System;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using ChameLab.Services;

namespace ChameLab.Browser;

/// <summary>
/// The browser's Back goes to the app (<see cref="SystemBack"/>) instead of leaving the page,
/// see history-guard.js. It replaces Avalonia's handling, whose history entry the browser skips.
/// </summary>
[SupportedOSPlatform("browser")]
internal static partial class WebHistoryGuard
{
    public static async Task InstallAsync()
    {
        var baseUri = JSHost.GlobalThis.GetPropertyAsJSObject("document")!.GetPropertyAsString("baseURI")!;
        await JSHost.ImportAsync("history-guard", new Uri(new Uri(baseUri), "history-guard.js").AbsoluteUri);
        Install(() => SystemBack.Handler?.Invoke() ?? false);
    }

    [JSImport("install", "history-guard")]
    private static partial void Install([JSMarshalAs<JSType.Function<JSType.Boolean>>] Func<bool> onBack);
}
