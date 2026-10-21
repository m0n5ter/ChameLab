using System.Threading.Tasks;
using Avalonia;
using Avalonia.Browser;
using ReactiveUI.Avalonia;
using ChameLab;
using ChameLab.Browser;
using ChameLab.Services;
using ChameLab.Transport;

internal sealed partial class Program
{
    private static Task Main(string[] args)
    {
        // Web Serial (navigator.serial) reaches USB-serial devices from the page;
        // TCP is added automatically for a USB-serial→TCP bridge.
        AppServices.Register(new ITransportScanner[] { new WebSerialScanner() }, new LocalStorageStore());

        return BuildAvaloniaApp()
            .WithInterFont()
            .UseReactiveUI(_ => { })
            // Warm up the Web Serial JS module now so the first Rescan click spends
            // its (time-limited) user-activation on requestPort(), not the import.
            .AfterSetup(builder => { _ = WebSerialInterop.EnsureModuleAsync(); })
            // NOTE: Avalonia DevTools is not WebAssembly-safe (uses System.Diagnostics.Process),
            // so it is intentionally not attached for the browser head.
            .StartBrowserAppAsync("out");
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>();
}
