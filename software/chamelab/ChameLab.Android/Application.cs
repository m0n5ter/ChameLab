using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using ReactiveUI.Avalonia;
using ChameLab.Mobile;
using ChameLab.Services;
using ChameLab.Transport;

namespace ChameLab.Android
{
    [Application]
    public class Application : AvaloniaAndroidApplication<App>
    {
        protected Application(nint javaReference, JniHandleOwnership transfer) : base(javaReference, transfer)
        {
        }

        protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
        {
            AppServices.Register(new ITransportScanner[] { new BleScanner() }, dfu: new IDfuLocator[] { new BleDfuLocator() },
                feedback: new AndroidFeedbackPlayer());
            return base.CustomizeAppBuilder(builder)
            .WithInterFont()
            .UseReactiveUI(_ => { });
        }
    }
}
