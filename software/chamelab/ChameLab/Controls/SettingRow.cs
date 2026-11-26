using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;

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
    public static readonly StyledProperty<double> MinStackedTextWidthProperty =
        AvaloniaProperty.Register<SettingRow, double>(nameof(MinStackedTextWidth), 140);

    /// <summary>
    /// Stack only when the title doesn't fit on one line beside the control (at least
    /// <see cref="MinTextWidth"/> still applies), for rows with a short title and a wide control.
    /// </summary>
    public static readonly StyledProperty<bool> FitTitleProperty =
        AvaloniaProperty.Register<SettingRow, bool>(nameof(FitTitle));

    /// <summary>
    /// Target width for a trailing button when plenty of space is available; shrinks down toward
    /// its labeled width as space tightens, and collapses to <c>iconic</c> when text no longer fits.
    /// </summary>
    public static readonly StyledProperty<double> ControlTargetWidthProperty =
        AvaloniaProperty.Register<SettingRow, double>(nameof(ControlTargetWidth), double.NaN);

    /// <summary>
    /// Optional group name shared by sibling <see cref="SettingRow"/> controls in the same panel:
    /// they shrink together and collapse to <c>iconic</c> simultaneously when any visible member's
    /// label no longer fits.
    /// </summary>
    public static readonly StyledProperty<string?> ControlGroupProperty =
        AvaloniaProperty.Register<SettingRow, string?>(nameof(ControlGroup));

    /// <summary>Set while stacked, so styles can make the control narrower.</summary>
    public const string StackedClass = "stacked";

    static SettingRow()
    {
        AffectsMeasure<SettingRow>(SpacingProperty, RowSpacingProperty, MinTextWidthProperty,
            MinStackedTextWidthProperty, FitTitleProperty, ControlTargetWidthProperty, ControlGroupProperty);
        IsVisibleProperty.Changed.AddClassHandler<SettingRow>((row, _) => row.InvalidateGroupSiblings());
    }

    public double Spacing { get => GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }
    public double RowSpacing { get => GetValue(RowSpacingProperty); set => SetValue(RowSpacingProperty, value); }
    public double MinTextWidth { get => GetValue(MinTextWidthProperty); set => SetValue(MinTextWidthProperty, value); }
    public double MinStackedTextWidth { get => GetValue(MinStackedTextWidthProperty); set => SetValue(MinStackedTextWidthProperty, value); }
    public bool FitTitle { get => GetValue(FitTitleProperty); set => SetValue(FitTitleProperty, value); }
    public double ControlTargetWidth { get => GetValue(ControlTargetWidthProperty); set => SetValue(ControlTargetWidthProperty, value); }
    public string? ControlGroup { get => GetValue(ControlGroupProperty); set => SetValue(ControlGroupProperty, value); }

    private bool _stacked;
    private double _labeledWidth;
    private IDisposable? _controlWidth;

    private Control? Child(int i) => i < Children.Count && Children[i].IsVisible ? Children[i] : null;

    private void ReleaseControlWidth()
    {
        _controlWidth?.Dispose();
        _controlWidth = null;
    }

    private double LabeledControlWidth() =>
        _labeledWidth > 0 ? _labeledWidth
        : Child(3) is { } control ? (_labeledWidth = Measure(control, double.PositiveInfinity).Width)
        : 0;

    private double GroupLabeledWidth(double ownWidth)
    {
        var max = ownWidth;
        if (ControlGroup is { } group && Parent is Panel panel)
        {
            foreach (var child in panel.Children)
            {
                if (child is SettingRow row && !ReferenceEquals(row, this)
                    && row.IsVisible && string.Equals(row.ControlGroup, group, StringComparison.Ordinal))
                {
                    max = Math.Max(max, row.LabeledControlWidth());
                }
            }
        }
        return max;
    }

    private void InvalidateGroupSiblings()
    {
        if (ControlGroup is not { } group || Parent is not Panel panel) return;
        foreach (var child in panel.Children)
        {
            if (child is SettingRow row && !ReferenceEquals(row, this)
                && string.Equals(row.ControlGroup, group, StringComparison.Ordinal))
            {
                row.InvalidateMeasure();
            }
        }
    }

    private static Size Measure(Control? child, double width)
    {
        if (child == null) return default;
        child.Measure(new Size(Math.Max(0, width), double.PositiveInfinity));
        return child.DesiredSize;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        ReleaseControlWidth();
        var width = availableSize.Width;
        var icon = Measure(Child(0), double.PositiveInfinity);
        var lead = icon.Width > 0 ? icon.Width + Spacing : 0;
        var controlChild = Child(3);
        var minText = FitTitle ? Math.Max(MinTextWidth, Measure(Child(1), double.PositiveInfinity).Width) : MinTextWidth;

        if (controlChild is not null && !double.IsNaN(ControlTargetWidth))
        {
            ActionRow.SetIconic(controlChild, false);
            var labeled = Measure(controlChild, double.PositiveInfinity);
            _labeledWidth = labeled.Width;
            var groupLabeled = GroupLabeledWidth(_labeledWidth);
            var target = Math.Max(ControlTargetWidth, groupLabeled);
            _stacked = !double.IsInfinity(width) && (width - lead - target - Spacing) < minText;
            Classes.Set(StackedClass, _stacked);

            var iconic = false;
            var controlWidth = target;
            if (_stacked)
            {
                var room = width - lead - Spacing - MinStackedTextWidth;
                if (room >= groupLabeled)
                    controlWidth = Math.Clamp(room, groupLabeled, target);
                else
                    iconic = true;
            }

            ActionRow.SetIconic(controlChild, iconic);
            if (!iconic)
            {
                _controlWidth = controlChild.SetValue(WidthProperty, controlWidth, BindingPriority.Animation);
                ActionRow.InvalidateSubtree(controlChild);
            }
            InvalidateArrange();
            return MeasureRow(width, icon, lead, Measure(controlChild, double.PositiveInfinity));
        }

        // The decision is made with the control at its wide size, so styles that shrink it when
        // stacked cannot flip the row back.
        Classes.Set(StackedClass, false);
        var control = Measure(controlChild, double.PositiveInfinity);
        var trail = control.Width > 0 ? control.Width + Spacing : 0;
        _stacked = !double.IsInfinity(width) && (width - lead - trail) < minText;
        if (_stacked)
        {
            Classes.Set(StackedClass, true);
            control = Measure(controlChild, double.PositiveInfinity);
        }
        return MeasureRow(width, icon, lead, control);
    }

    private Size MeasureRow(double width, Size icon, double lead, Size control)
    {
        var trail = control.Width > 0 ? control.Width + Spacing : 0;
        var textWidth = Math.Max(0, width - lead - trail);
        if (!_stacked)
        {
            var title = Measure(Child(1), textWidth);
            var description = Measure(Child(2), textWidth);
            return new Size(
                double.IsInfinity(width) ? lead + Math.Max(title.Width, description.Width) + trail : width,
                Math.Max(Math.Max(icon.Height, title.Height + description.Height), control.Height));
        }
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
