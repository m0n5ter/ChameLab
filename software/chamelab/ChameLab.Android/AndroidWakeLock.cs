using System;
using System.Threading.Tasks;
using Android.Views;
using Avalonia.Threading;
using ChameLab.Services;

namespace ChameLab.Android;

internal static class AndroidWakeLock
{
    private static Window? _window;
    private static bool _enabled, _wasSet;

    // Android can recreate the activity while an operation is still running.
    public static Window? Window
    {
        get => _window;
        set
        {
            if (_enabled) Restore();
            _window = value;
            if (_enabled) Apply();
        }
    }

    private static void Apply()
    {
        if (_window is not { } window) return;
        _wasSet = (window.Attributes!.Flags & WindowManagerFlags.KeepScreenOn) != 0;
        window.AddFlags(WindowManagerFlags.KeepScreenOn);
    }

    private static void Restore()
    {
        if (!_wasSet) _window?.ClearFlags(WindowManagerFlags.KeepScreenOn);
    }

    public static async Task<IAsyncDisposable?> AcquireAsync() =>
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _enabled = true;
            Apply();
            return WakeLockService.ReleaseWith(async () =>
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Restore();
                    _enabled = false;
                });
            });
        });
}
