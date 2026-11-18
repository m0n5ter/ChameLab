using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using ChameLab.ViewModels;

namespace ChameLab.Controls;

public partial class EmulatorCapturePanel : UserControl
{
    public EmulatorCapturePanel() => InitializeComponent();

    private void OnSaveMenuOpening(object? sender, System.EventArgs e)
    {
        if (DataContext is EmulatorCaptureViewModel vm) vm.RebuildKeysTargets();
    }

    private async void OnCopyKeys(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not EmulatorCaptureViewModel vm || vm.KeysText is not { } text
            || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        try { await clipboard.SetTextAsync(text); }
        catch { return; }
        vm.NotifyKeysCopied();
    }
}
