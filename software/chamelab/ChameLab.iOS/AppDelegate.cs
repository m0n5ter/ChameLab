using Foundation;
using Avalonia;
using Avalonia.iOS;
using ReactiveUI.Avalonia;
using ChameLab.Mobile;
using ChameLab.Services;
using ChameLab.Transport;

namespace ChameLab.iOS;

// The UIApplicationDelegate for the application. This class is responsible for launching the 
// User Interface of the application, as well as listening (and optionally responding) to 
// application events from iOS.
[Register("AppDelegate")]
#pragma warning disable CA1711 // Identifiers should not have incorrect suffix
public partial class AppDelegate : AvaloniaAppDelegate<App>
#pragma warning restore CA1711 // Identifiers should not have incorrect suffix
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        AppServices.Register(new ITransportScanner[] { new BleScanner() }, dfu: new IDfuLocator[] { new BleDfuLocator() },
            feedback: new IosFeedbackPlayer());
        return base.CustomizeAppBuilder(builder)
            .WithInterFont()
            .UseReactiveUI(_ => { });
    }
}
