using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using ChameLab.ViewModels;

namespace ChameLab.Views;

public partial class ConnectView : UserControl
{
    // The device list never grows past this, and never shrinks below about one and a half rows.
    private const double ListMaxHeight = 260;
    private const double ListMinHeight = 90;

    private bool _shortHero;
    private double _heroTallHeight;

    public ConnectView()
    {
        InitializeComponent();
        // Double-click / double-tap a detected device to connect straight away.
        DeviceList.DoubleTapped += OnDeviceDoubleTapped;
        Page.LayoutUpdated += (_, _) => Fit();
    }

    private void OnDeviceDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is ConnectViewModel { Selected: not null } vm && vm.ConnectCommand.CanExecute(null))
            vm.ConnectCommand.Execute(null);
    }

    /// <summary>
    /// Gives the device list whatever height the window leaves; when even its minimum does not fit,
    /// moves the device picture beside the title, and only then lets the page scroll.
    /// </summary>
    private void Fit()
    {
        var viewport = Scroller.Bounds.Height;
        if (viewport <= 0) return;

        var rest = Page.DesiredSize.Height - DeviceList.DesiredSize.Height;
        var space = viewport - rest;
        var need = DeviceList.IsVisible ? ListMinHeight : 0;

        if (!_shortHero && space < need)
        {
            _heroTallHeight = Hero.DesiredSize.Height;
            SetShortHero(true);
            return;
        }
        // Back to the tall hero only once it fits with some slack, so the layout does not flicker.
        if (_shortHero && space - (_heroTallHeight - Hero.DesiredSize.Height) >= need + 8)
        {
            SetShortHero(false);
            return;
        }

        var max = Math.Clamp(space, ListMinHeight, ListMaxHeight);
        if (Math.Abs(DeviceList.MaxHeight - max) > 0.5) DeviceList.MaxHeight = max;
    }

    private void SetShortHero(bool on)
    {
        _shortHero = on;
        Grid.SetRow(HeroText, on ? 0 : 1);
        Grid.SetColumn(HeroText, on ? 1 : 0);
        Grid.SetColumnSpan(HeroText, on ? 1 : 2);
        Grid.SetColumnSpan(HeroImage, on ? 1 : 2);
        HeroImage.Height = on ? 72 : 120;
        HeroImage.Margin = on ? new Thickness(10, 0, 18, 0) : default;
        HeroText.Margin = on ? new Thickness(0, 0, 10, 0) : new Thickness(0, 16, 0, 0);
        Hero.Margin = on ? new Thickness(0, 0, 0, 4) : new Thickness(0, 8, 0, 4);
        var align = on ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        HeroTitle.HorizontalAlignment = align;
        HeroCaption.HorizontalAlignment = align;
        HeroCaption.TextAlignment = on ? TextAlignment.Left : TextAlignment.Center;
        HeroCaption.MaxWidth = on ? 270 : 380;
    }
}
