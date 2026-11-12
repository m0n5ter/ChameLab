using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using ChameLab.Services;
using UIKit;

namespace ChameLab.iOS;

internal static class IosWakeLock
{
    public static async Task<IAsyncDisposable?> AcquireAsync() =>
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var application = UIApplication.SharedApplication;
            var wasDisabled = application.IdleTimerDisabled;
            application.IdleTimerDisabled = true;
            return WakeLockService.ReleaseWith(async () =>
            {
                await Dispatcher.UIThread.InvokeAsync(() => application.IdleTimerDisabled = wasDisabled);
            });
        });
}
