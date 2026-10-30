using System;
using Avalonia;
using Avalonia.Controls;

namespace ChameLab.Controls;

/// <summary>
/// One setting: children are the icon, the title, the description and the control, in that order.
/// Wide: icon, title over description, and the control on the right. When the text would get narrower
/// than <see cref="MinTextWidth"/>, the title gets a line of its own beside the icon and the description
/// sits under it, next to the control.
/// </summary>
public class SettingRow : Panel
{
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<SettingRow, double>(nameof(Spacing), 14);
    public static readonly StyledProperty<double> RowSpacingProperty =
        AvaloniaProperty.Register<SettingRow, double>(nameof(RowSpacing), 10);
    public static readonly StyledProperty<double> MinTextWidthProperty =
        AvaloniaProperty.Register<SettingRow, double>(nameof(MinTextWidth), 200);

    /// <summary>Set while stacked, so styles can make the control narrower.</summary>
    public const string StackedClass = "stacked";

    static SettingRow()
    {
        AffectsMeasure<SettingRow>(SpacingProperty, RowSpacingProperty, MinTextWidthProperty);
    }

    public double Spacing { get => GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }
    public double RowSpacing { get => GetValue(RowSpacingProperty); set => SetValue(RowSpacingProperty, value); }
    public double MinTextWidth { get => GetValue(MinTextWidthProperty); set => SetValue(MinTextWidthProperty, value); }

    private bool _stacked;

    private Control? Child(int i) => i < Children.Count && Children[i].IsVisible ? Children[i] : null;

    private static Size Measure(Control? child, double width)
    {
        if (child == null) return default;
        child.Measure(new Size(Math.Max(0, width), double.PositiveInfinity));
        return child.DesiredSize;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = availableSize.Width;
        var icon = Measure(Child(0), double.PositiveInfinity);
        // The decision is made with the control at its wide size, so styles that shrink it when
        // stacked cannot flip the row back.
        Classes.Set(StackedClass, false);
        var control = Measure(Child(3), double.PositiveInfinity);
        var lead = icon.Width > 0 ? icon.Width + Spacing : 0;
        var trail = control.Width > 0 ? control.Width + Spacing : 0;

        var textWidth = width - lead - trail;
        _stacked = !double.IsInfinity(width) && textWidth < MinTextWidth;
        if (!_stacked)
        {
            var title = Measure(Child(1), textWidth);
            var description = Measure(Child(2), textWidth);
            return new Size(
                double.IsInfinity(width) ? lead + Math.Max(title.Width, description.Width) + trail : width,
                Math.Max(Math.Max(icon.Height, title.Height + description.Height), control.Height));
        }

        Classes.Set(StackedClass, true);
        control = Measure(Child(3), double.PositiveInfinity);
        trail = control.Width > 0 ? control.Width + Spacing : 0;
        textWidth = width - lead - trail;
        var stackedTitle = Measure(Child(1), width - lead);
        var stackedDescription = Measure(Child(2), textWidth);
        return new Size(width, Math.Max(icon.Height,
            stackedTitle.Height + RowSpacing + Math.Max(stackedDescription.Height, control.Height)));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var width = finalSize.Width;
        var icon = Child(0)?.DesiredSize ?? default;
        var control = Child(3)?.DesiredSize ?? default;
        var title = Child(1)?.DesiredSize ?? default;
        var description = Child(2)?.DesiredSize ?? default;
        var lead = icon.Width > 0 ? icon.Width + Spacing : 0;
        var trail = control.Width > 0 ? control.Width + Spacing : 0;

        if (!_stacked)
        {
            var height = finalSize.Height;
            var textTop = (height - title.Height - description.Height) / 2;
            var textWidth = Math.Max(0, width - lead - trail);
            Child(0)?.Arrange(new Rect(0, (height - icon.Height) / 2, icon.Width, icon.Height));
            Child(1)?.Arrange(new Rect(lead, textTop, textWidth, title.Height));
            Child(2)?.Arrange(new Rect(lead, textTop + title.Height, textWidth, description.Height));
            Child(3)?.Arrange(new Rect(width - control.Width, (height - control.Height) / 2, control.Width, control.Height));
            return finalSize;
        }

        var bottom = Math.Max(description.Height, control.Height);
        var content = title.Height + RowSpacing + bottom;
        var top = (finalSize.Height - content) / 2;
        Child(0)?.Arrange(new Rect(0, (finalSize.Height - icon.Height) / 2, icon.Width, icon.Height));
        Child(1)?.Arrange(new Rect(lead, top, Math.Max(0, width - lead), title.Height));

        var y = top + title.Height + RowSpacing;
        Child(2)?.Arrange(new Rect(lead, y + (bottom - description.Height) / 2, Math.Max(0, width - lead - trail), description.Height));
        Child(3)?.Arrange(new Rect(width - control.Width, y + (bottom - control.Height) / 2, control.Width, control.Height));
        return finalSize;
    }
}
