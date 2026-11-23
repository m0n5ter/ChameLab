using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ChameLab.ViewModels;

namespace ChameLab.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        Focusable = true;
    }

    private void OnDeviceNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        if (e.Key == Key.Enter) vm.Sync.CommitDeviceName();
        else if (e.Key == Key.Escape) vm.Sync.RevertDeviceName();
        else return;
        Focus(NavigationMethod.Pointer);
        e.Handled = true;
    }

    private void OnDeviceNameLostFocus(object? sender, RoutedEventArgs e) =>
        (DataContext as SettingsViewModel)?.Sync.CommitDeviceName();
}
