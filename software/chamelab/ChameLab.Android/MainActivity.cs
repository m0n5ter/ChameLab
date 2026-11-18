using System;
using System.Collections.Generic;
using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Avalonia;
using Avalonia.Android;
using Avalonia.Controls;
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

    protected override void OnResume()
    {
        base.OnResume();
        WatchSurface();
    }

    // ---- Layout after another activity covered the app ----------------------------------------

    private SurfaceView? _watchedSurface;
    private SurfaceWatcher? _surfaceWatcher;

    /// <summary>
    /// While another activity covers the app (the QR scanner, a file picker), Avalonia's surface is
    /// gone and it lays the view out at 1×1 pixel, so whatever changes meanwhile (a card scanned from
    /// a QR code) is measured at no width. When the surface comes back, Avalonia measures the view
    /// again, but a control measured anew whose place didn't change is not arranged again: its
    /// children keep the places of the squeezed layout, drawn over each other. So each time the
    /// surface gets its size, the whole view is invalidated, measure and arrange.
    /// </summary>
    private void WatchSurface()
    {
        if (_watchedSurface is { Handle: not 0 }) return;
        _watchedSurface = FindSurface(Window?.DecorView);
        _surfaceWatcher ??= new SurfaceWatcher(() => Dispatcher.UIThread.Post(RefreshView, DispatcherPriority.Render));
        _watchedSurface?.Holder?.AddCallback(_surfaceWatcher);
    }

    private static SurfaceView? FindSurface(global::Android.Views.View? view)
    {
        if (view is SurfaceView surface) return surface;
        if (view is not ViewGroup group) return null;
        for (var i = 0; i < group.ChildCount; i++)
            if (FindSurface(group.GetChildAt(i)) is { } found)
                return found;
        return null;
    }

    private sealed class SurfaceWatcher(Action changed) : Java.Lang.Object, ISurfaceHolderCallback
    {
        public void SurfaceChanged(ISurfaceHolder holder, global::Android.Graphics.Format format, int width, int height) =>
            changed();

        public void SurfaceCreated(ISurfaceHolder holder) { }

        public void SurfaceDestroyed(ISurfaceHolder holder) { }
    }

    private void RefreshView()
    {
        if (Content is not Visual view) return;
        var root = (Visual?)TopLevel.GetTopLevel(view) ?? view;
        foreach (var visual in root.GetSelfAndVisualDescendants())
            if (visual is Layoutable layoutable) layoutable.InvalidateMeasure();
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
