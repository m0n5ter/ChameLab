using Android.Content;
using Android.Net.Wifi;

namespace ChameLab.Android;

/// <summary>
/// Android drops multicast packets (mDNS) to save power unless an app holds a multicast lock:
/// device sync holds one while it runs, to find the user's other devices on the network.
/// </summary>
internal static class AndroidMulticast
{
    private static WifiManager.MulticastLock? _lock;

    public static void Set(bool on)
    {
        try
        {
            if (on)
            {
                if (_lock is null)
                {
                    var wifi = global::Android.App.Application.Context.GetSystemService(Context.WifiService) as WifiManager;
                    _lock = wifi?.CreateMulticastLock("chamelab-sync");
                    _lock?.SetReferenceCounted(false);
                }
                _lock?.Acquire();
            }
            else if (_lock is { IsHeld: true })
            {
                _lock.Release();
            }
        }
        catch (Java.Lang.SecurityException)
        {
            // Without the permission local discovery just finds less; the relay still works.
        }
    }
}
