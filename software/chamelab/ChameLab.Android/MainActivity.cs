using System;
using System.Collections.Generic;
using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Avalonia;
using Avalonia.Android;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ChameLab.Services;

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
        AndroidWakeLock.Window = Window;
        AndroidQrCamera.Activity = this;
        // Back on the start screen would only move the task to the background (Android 12+).
        PlatformExit.Quit = Finish;
        RequestBlePermissions();
    }

    protected override void OnDestroy()
    {
        // Only drop the hook if a newer activity hasn't already set its own.
        if (PlatformExit.Quit?.Target == this) PlatformExit.Quit = null;
        if (ReferenceEquals(AndroidWakeLock.Window, Window)) AndroidWakeLock.Window = null;
        if (ReferenceEquals(AndroidQrCamera.Activity, this)) AndroidQrCamera.Activity = null;
        base.OnDestroy();
    }

    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);
        if (hasFocus) Dispatcher.UIThread.Post(RefreshView, DispatcherPriority.Background);
    }

    /// <summary>
    /// Content that changed while another activity covered the app (the QR scanner, a file picker) can
    /// come back half drawn: controls missing, buttons drawn over each other. Back in front, the whole
    /// view is laid out and drawn again.
    /// </summary>
    private static void RefreshView()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not ISingleViewApplicationLifetime { MainView: { } view })
            return;
        var root = (Visual?)TopLevel.GetTopLevel(view) ?? view;
        foreach (var visual in root.GetSelfAndVisualDescendants())
        {
            if (visual is Layoutable layoutable) layoutable.InvalidateMeasure();
            visual.InvalidateVisual();
        }
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
