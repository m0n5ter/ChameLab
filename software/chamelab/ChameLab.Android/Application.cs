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
            AppUpdateService.UpdatedByStore = InstallerPackage() == "com.android.vending";
            var usb = new AndroidUsbCdcHost();
            AppServices.Register(new ITransportScanner[] { new UsbCdcScanner(usb), new BleScanner() },
                dfu: new IDfuLocator[] { new UsbCdcDfuLocator(usb), new BleDfuLocator() },
                feedback: new AndroidFeedbackPlayer(), hardnested: new ManagedHardnestedEngine());
            return base.CustomizeAppBuilder(builder)
            .WithInterFont()
            .UseReactiveUI(_ => { });
        }

        /// <summary>The package that installed the app: com.android.vending for Google Play, null for an APK installed by hand.</summary>
        private string? InstallerPackage()
        {
            try
            {
                var packageName = PackageName!;
                return System.OperatingSystem.IsAndroidVersionAtLeast(30)
                    ? PackageManager!.GetInstallSourceInfo(packageName).InstallingPackageName
#pragma warning disable CA1422 // the only way before Android 11
                    : PackageManager!.GetInstallerPackageName(packageName);
#pragma warning restore CA1422
            }
            catch (System.Exception)
            {
                return null;
            }
        }
    }
}
