using System;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using ChameLab.Services;

namespace ChameLab.Browser;

[SupportedOSPlatform("browser")]
internal static partial class WebWakeLock
{
    private static Task? _import;

    public static async Task<IAsyncDisposable?> AcquireAsync()
    {
        var baseUri = JSHost.GlobalThis.GetPropertyAsJSObject("document")!.GetPropertyAsString("baseURI")!;
        await (_import ??= JSHost.ImportAsync("wake-lock", new Uri(new Uri(baseUri), "wake-lock.js").AbsoluteUri));
        await SetEnabledAsync(true);
        return WakeLockService.ReleaseWith(() => new ValueTask(SetEnabledAsync(false)));
    }

    [JSImport("setEnabled", "wake-lock")]
    private static partial Task SetEnabledAsync(bool enabled);
}
