using System;
using System.Collections.Generic;
using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Avalonia.Android;

namespace ChameLab.Android;

[Activity(
    Label = "ChameLab",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    // Before Android 15 the window shrinks above the keyboard instead of panning; from 15 the app
    // draws under it and MainView keeps clear of it itself.
    WindowSoftInputMode = SoftInput.AdjustResize,
    // Rotation, split screen and a keyboard coming or going resize the view instead of recreating
    // the activity, which would drop the connection and whatever is open.
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode
                           | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize
                           | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.Navigation)]
public class MainActivity : AvaloniaMainActivity
{
    private const int BlePermissionRequest = 1001;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        RequestBlePermissions();
    }

    private void RequestBlePermissions()
    {
        var needed = new List<string>();
        if (OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            needed.Add(global::Android.Manifest.Permission.BluetoothScan);
            needed.Add(global::Android.Manifest.Permission.BluetoothConnect);
        }
        else
        {
            needed.Add(global::Android.Manifest.Permission.AccessFineLocation);
        }

        var toRequest = new List<string>();
        foreach (var p in needed)
            if (CheckSelfPermission(p) != Permission.Granted)
                toRequest.Add(p);

        if (toRequest.Count > 0)
            RequestPermissions(toRequest.ToArray(), BlePermissionRequest);
    }
}
