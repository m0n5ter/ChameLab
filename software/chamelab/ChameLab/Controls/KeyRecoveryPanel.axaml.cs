using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ChameLab.Controls;

public partial class KeyRecoveryPanel : UserControl
{
    public static readonly StyledProperty<IBrush?> ProgressDetailForegroundProperty =
        AvaloniaProperty.Register<KeyRecoveryPanel, IBrush?>(nameof(ProgressDetailForeground), Brushes.Gray);

    /// <summary>Default color of the second progress line; semantic text classes can override it.</summary>
    public IBrush? ProgressDetailForeground
    {
        get => GetValue(ProgressDetailForegroundProperty);
        set => SetValue(ProgressDetailForegroundProperty, value);
    }

    public static readonly StyledProperty<object?> ToolbarContentProperty =
        AvaloniaProperty.Register<KeyRecoveryPanel, object?>(nameof(ToolbarContent));

    /// <summary>Controls shown right of the dump and attack buttons, on the same line (hidden with them).</summary>
    public object? ToolbarContent
    {
        get => GetValue(ToolbarContentProperty);
        set => SetValue(ToolbarContentProperty, value);
    }

    public KeyRecoveryPanel() => InitializeComponent();

    private void OnMemoryToolbarSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is Control toolbar) toolbar.Classes.Set("compact", e.NewSize.Width < 480);
    }
}
