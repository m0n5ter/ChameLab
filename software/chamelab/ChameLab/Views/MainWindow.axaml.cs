using System;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using ChameLab.Services;

namespace ChameLab.Views;

public partial class MainWindow : Window
{
    private const string Setting = "window";

    // Last size and position while the window was neither maximized nor minimized,
    // so closing a maximized window still restores to the size it had before.
    private PixelPoint _normalPosition;
    private Size _normalSize;
    // Maximized or not, ignoring minimize: a window closed from the taskbar while minimized
    // comes back the way it was before.
    private bool _maximized;

    public MainWindow()
    {
        InitializeComponent();
        _normalSize = new Size(Width, Height);
        Restore();

        // Maximizing moves and resizes the window before WindowState says so, so the normal
        // bounds are taken only once the state has settled.
        PositionChanged += (_, _) => Dispatcher.UIThread.Post(TrackNormalBounds, DispatcherPriority.Background);
        Resized += (_, _) => Dispatcher.UIThread.Post(TrackNormalBounds, DispatcherPriority.Background);
        Opened += (_, _) => TrackNormalBounds();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty && WindowState != WindowState.Minimized)
                _maximized = WindowState == WindowState.Maximized;
        };
        Closing += (_, _) => Save();
    }

    /// <summary>Puts the window back where it was last closed, if that spot is still on a screen.</summary>
    private void Restore()
    {
        if (AppSettings.Get(Setting) is not JsonObject saved) return;

        try
        {
            var width = Math.Max(MinWidth, (double)saved["width"]!);
            var height = Math.Max(MinHeight, (double)saved["height"]!);
            var position = new PixelPoint((int)saved["x"]!, (int)saved["y"]!);

            Width = width;
            Height = height;
            _normalSize = new Size(width, height);

            // The title bar must land on a connected screen, or the window could not be dragged back.
            if (Screens.ScreenFromPoint(position + new PixelPoint(40, 10)) is not null)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Position = position;
                _normalPosition = position;
            }

            if ((bool?)saved["maximized"] == true)
            {
                WindowState = WindowState.Maximized;
                _maximized = true;
            }
        }
        catch { /* malformed placement: keep the defaults */ }
    }

    private void TrackNormalBounds()
    {
        if (WindowState != WindowState.Normal) return;
        _normalPosition = Position;
        _normalSize = ClientSize;
    }

    private void Save()
    {
        TrackNormalBounds();

        var placement = new JsonObject
        {
            ["x"] = _normalPosition.X,
            ["y"] = _normalPosition.Y,
            ["width"] = _normalSize.Width,
            ["height"] = _normalSize.Height,
            ["maximized"] = _maximized,
        };
        AppSettings.Set(Setting, placement);
    }
}
