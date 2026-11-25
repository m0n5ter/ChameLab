using Avalonia;
using Avalonia.Controls;

namespace ChameLab.Controls;

/// <summary>
/// Circular slot indicator matching the Chameleon page: split HF | LF LED colors,
/// dashed outline when empty, accent ring when active, dimmed when disabled.
/// </summary>
public partial class SlotBadge : UserControl
{
    public static readonly StyledProperty<int> IndexProperty =
        AvaloniaProperty.Register<SlotBadge, int>(nameof(Index));

    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<SlotBadge, bool>(nameof(IsActive));

    public static readonly StyledProperty<bool> IsEmptyProperty =
        AvaloniaProperty.Register<SlotBadge, bool>(nameof(IsEmpty), true);

    public static readonly StyledProperty<bool> IsSlotEnabledProperty =
        AvaloniaProperty.Register<SlotBadge, bool>(nameof(IsSlotEnabled), true);

    public static readonly StyledProperty<string?> LedLeftProperty =
        AvaloniaProperty.Register<SlotBadge, string?>(nameof(LedLeft));

    public static readonly StyledProperty<string?> LedRightProperty =
        AvaloniaProperty.Register<SlotBadge, string?>(nameof(LedRight));

    public SlotBadge() => InitializeComponent();

    public int Index { get => GetValue(IndexProperty); set => SetValue(IndexProperty, value); }
    public bool IsActive { get => GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public bool IsEmpty { get => GetValue(IsEmptyProperty); set => SetValue(IsEmptyProperty, value); }
    public bool IsSlotEnabled { get => GetValue(IsSlotEnabledProperty); set => SetValue(IsSlotEnabledProperty, value); }
    public string? LedLeft { get => GetValue(LedLeftProperty); set => SetValue(LedLeftProperty, value); }
    public string? LedRight { get => GetValue(LedRightProperty); set => SetValue(LedRightProperty, value); }
}
