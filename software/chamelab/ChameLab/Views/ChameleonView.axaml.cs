using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ChameLab.Views;

public partial class ChameleonView : UserControl
{
    public ChameleonView()
    {
        InitializeComponent();
    }

    // Confirm buttons run their command via binding; this just closes whichever flyout is open.
    private void OnCloseFlyout(object? sender, RoutedEventArgs e)
    {
        ResetButton.Flyout?.Hide();
        WipeButton.Flyout?.Hide();
        ClearBondsButton.Flyout?.Hide();
    }
}
