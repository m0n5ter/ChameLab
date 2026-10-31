using System.Collections.Generic;
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
        // Web Serial (navigator.serial) reaches USB-serial devices from the page, and Web
        // Bluetooth (navigator.bluetooth, where the browser has it) nearby ones over BLE;
        // TCP is added automatically for a USB-serial→TCP bridge.
        var bluetooth = WebBluetoothInterop.IsAvailable();
        var scanners = new List<ITransportScanner> { new WebSerialScanner() };
        if (bluetooth) scanners.Add(new WebBluetoothScanner());
        AppServices.Register(scanners, new LocalStorageStore(), feedback: new WebFeedbackPlayer(),
            hardnested: new WebHardnestedEngine());

        return BuildAvaloniaApp()
            .WithInterFont()
            .UseReactiveUI(_ => { })
            // Warm up the JS modules now so the first Rescan or Pair over Bluetooth click spends its
            // (time-limited) user-activation on the chooser, not the import.
            .AfterSetup(builder =>
            {
                _ = WebSerialInterop.EnsureModuleAsync();
                if (bluetooth) _ = WebBluetoothInterop.EnsureModuleAsync();
                _ = WebFeedbackInterop.EnsureModuleAsync();
            })
            // NOTE: Avalonia DevTools is not WebAssembly-safe (uses System.Diagnostics.Process),
            // so it is intentionally not attached for the browser head.
            .StartBrowserAppAsync("out");
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>();
}
