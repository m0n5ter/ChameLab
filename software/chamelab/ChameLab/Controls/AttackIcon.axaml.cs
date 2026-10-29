using Avalonia;
using Avalonia.Controls;

namespace ChameLab.Controls;

/// <summary>A shared card silhouette with a readable attack abbreviation, including in icon-only toolbars.</summary>
public partial class AttackIcon : UserControl
{
    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<AttackIcon, string>(nameof(Label), "");

    public string Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public AttackIcon() => InitializeComponent();
}
