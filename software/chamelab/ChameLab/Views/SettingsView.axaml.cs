using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ChameLab.ViewModels;

namespace ChameLab.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        Focusable = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (DataContext is SettingsViewModel vm && vm.Sync.HasEditingMember
            && (e.Source as Visual)?.FindAncestorOfType<TextBox>(includeSelf: true) is null)
            Focus(NavigationMethod.Pointer);
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

    private void OnDeviceCardTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: SyncMemberViewModel { IsThis: true, IsEditing: false } member })
        {
            member.BeginEdit();
            e.Handled = true;
        }
    }

    private void OnDeviceCardNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm || sender is not Control { DataContext: SyncMemberViewModel member }) return;
        if (e.Key == Key.Enter) vm.Sync.CommitInlineDeviceName(member);
        else if (e.Key == Key.Escape) member.CancelEdit();
        else return;
        Focus(NavigationMethod.Pointer);
        e.Handled = true;
    }

    private void OnDeviceCardNameLostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm && sender is Control { DataContext: SyncMemberViewModel member })
            vm.Sync.CommitInlineDeviceName(member);
    }
}
