using ChameLab.Transport.Serial;
using System;
using Avalonia;
using ReactiveUI.Avalonia;
using ChameLab.Services;
using ChameLab.Transport;

namespace ChameLab.Desktop;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        var background = Array.Exists(args, static arg => arg == Autostart.BackgroundArgument);
        using var singleInstance = SingleInstance.TryAcquire(activateExisting: !background);
        if (singleInstance is null) return;
        singleInstance.Subscribe(App.ActivateDesktopAsync);
        DesktopHost.BringToFront = DesktopPlatform.BringToFront;
        DesktopHost.ShowTrayBalloon = DesktopPlatform.ShowTrayBalloon;

        AppServices.Register(new ITransportScanner[] { new SerialScanner() }, dfu: new IDfuLocator[] { new SerialDfuLocator() },
            hardnested: new ManagedHardnestedEngine(), wakeLock: new WakeLockService(DesktopWakeLock.AcquireAsync));
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .UseReactiveUI(_ => { })
            .LogToTrace();
}
