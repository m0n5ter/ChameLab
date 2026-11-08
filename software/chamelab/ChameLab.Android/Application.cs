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
            var usb = new AndroidUsbCdcHost();
            AppServices.Register(new ITransportScanner[] { new UsbCdcScanner(usb), new BleScanner() },
                dfu: new IDfuLocator[] { new UsbCdcDfuLocator(usb), new BleDfuLocator() },
                feedback: new AndroidFeedbackPlayer(), hardnested: new ManagedHardnestedEngine());
            return base.CustomizeAppBuilder(builder)
            .WithInterFont()
            .UseReactiveUI(_ => { });
        }
    }
}
