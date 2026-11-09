using System;
using Avalonia;
using Avalonia.Controls;

namespace ChameLab.Controls;

/// <summary>
/// A form whose children come in pairs: a label, then its content. The contents share one width, that
/// of the widest of them, and are arranged at it (so stretched ones line up); contents marked
/// <see cref="FreeProperty">Free</see>, such as wrapping notes, neither count toward that width nor
/// take it, and get all the width there is. When the labels and that width fit side by side, the
/// labels form a left column as wide as the widest label and the contents a right column, each row
/// centered on its taller half. Otherwise everything stacks in one column, each label above its
/// content. A label with no size (an empty placeholder) leaves its row's left cell blank; hidden
/// children are skipped.
/// <para>
/// A content with <see cref="ShownProperty">Shown</see> false gives up its row but still counts toward
/// the shared width, so swapping alternatives (one shown at a time) never changes the column. Such a
/// child stays IsVisible (Avalonia measures hidden controls as empty) and gets the
/// <see cref="UnshownClass"/> class instead, which a style turns invisible, inert and disabled.
/// </para>
/// </summary>
public class FormColumns : Panel
{
    public static readonly StyledProperty<double> ColumnSpacingProperty =
        AvaloniaProperty.Register<FormColumns, double>(nameof(ColumnSpacing), 14);
    public static readonly StyledProperty<double> RowSpacingProperty =
        AvaloniaProperty.Register<FormColumns, double>(nameof(RowSpacing), 10);
    /// <summary>Stacked only: the gap between a label and its content.</summary>
    public static readonly StyledProperty<double> LabelSpacingProperty =
        AvaloniaProperty.Register<FormColumns, double>(nameof(LabelSpacing), 6);

    /// <summary>The content takes the whole width there is instead of the shared content width.</summary>
    public static readonly AttachedProperty<bool> FreeProperty =
        AvaloniaProperty.RegisterAttached<FormColumns, Control, bool>("Free");

    /// <summary>False: the content takes no row, though it still counts toward the shared width.</summary>
    public static readonly AttachedProperty<bool> ShownProperty =
        AvaloniaProperty.RegisterAttached<FormColumns, Control, bool>("Shown", true);

    /// <summary>Set on a content whose <see cref="ShownProperty">Shown</see> is false.</summary>
    public const string UnshownClass = "unshown";

    static FormColumns()
    {
        AffectsMeasure<FormColumns>(ColumnSpacingProperty, RowSpacingProperty, LabelSpacingProperty);
        FreeProperty.Changed.AddClassHandler<Control>((c, _) => (c.Parent as FormColumns)?.InvalidateMeasure());
        ShownProperty.Changed.AddClassHandler<Control>((c, _) =>
        {
            c.Classes.Set(UnshownClass, !GetShown(c));
            (c.Parent as FormColumns)?.InvalidateMeasure();
        });
    }

    public double ColumnSpacing { get => GetValue(ColumnSpacingProperty); set => SetValue(ColumnSpacingProperty, value); }
    public double RowSpacing { get => GetValue(RowSpacingProperty); set => SetValue(RowSpacingProperty, value); }
    public double LabelSpacing { get => GetValue(LabelSpacingProperty); set => SetValue(LabelSpacingProperty, value); }

    public static bool GetFree(Control control) => control.GetValue(FreeProperty);
    public static void SetFree(Control control, bool value) => control.SetValue(FreeProperty, value);
    public static bool GetShown(Control control) => control.GetValue(ShownProperty);
    public static void SetShown(Control control, bool value) => control.SetValue(ShownProperty, value);

    // Decided by the last measure, reused by arrange.
    private bool _stacked;
    private double _labelWidth;
    private double _contentWidth;

    private int Rows => (Children.Count + 1) / 2;

    private Control? Child(int i) => i < Children.Count && Children[i].IsVisible ? Children[i] : null;

    private static Size Measure(Control? child, double width)
    {
        if (child == null) return default;
        child.Measure(new Size(Math.Max(0, width), double.PositiveInfinity));
        return child.DesiredSize;
    }

    private static bool HasSize(Size size) => size.Width > 0 || size.Height > 0;

    private static bool IsFree(Control? child) => child != null && GetFree(child);

    private static bool IsShown(Control? child) => child == null || GetShown(child);

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = availableSize.Width;

        // Natural widths decide the layout, so the result cannot flip back and forth.
        var labelWidth = 0.0;
        var contentWidth = 0.0;
        for (var r = 0; r < Rows; r++)
        {
            labelWidth = Math.Max(labelWidth, Measure(Child(2 * r), double.PositiveInfinity).Width);
            var content = Child(2 * r + 1);
            if (!IsFree(content)) contentWidth = Math.Max(contentWidth, Measure(content, double.PositiveInfinity).Width);
        }
        _labelWidth = labelWidth;
        _stacked = !double.IsInfinity(width) && labelWidth + ColumnSpacing + contentWidth > width;

        var columnWidth = _stacked ? width : double.IsInfinity(width) ? width : width - labelWidth - ColumnSpacing;
        _contentWidth = Math.Min(contentWidth, columnWidth);

        var height = 0.0;
        var first = true;
        for (var r = 0; r < Rows; r++)
        {
            var contentChild = Child(2 * r + 1);
            var label = Measure(Child(2 * r), _stacked ? width : labelWidth);
            var content = Measure(contentChild, IsFree(contentChild) ? columnWidth : _contentWidth);
            if (!IsShown(contentChild)) content = default;
            if (!HasSize(label) && !HasSize(content)) continue;
            height += first ? 0 : RowSpacing;
            first = false;
            if (!_stacked)
            {
                height += Math.Max(label.Height, content.Height);
                continue;
            }
            height += label.Height;
            if (HasSize(label) && HasSize(content)) height += LabelSpacing;
            height += content.Height;
        }
        return new Size(double.IsInfinity(width) ? labelWidth + ColumnSpacing + contentWidth : width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var width = finalSize.Width;
        var contentLeft = _stacked ? 0 : _labelWidth + ColumnSpacing;
        var columnWidth = Math.Max(0, width - contentLeft);
        var y = 0.0;
        var first = true;
        for (var r = 0; r < Rows; r++)
        {
            var labelChild = Child(2 * r);
            var contentChild = Child(2 * r + 1);
            var label = labelChild?.DesiredSize ?? default;
            var content = contentChild?.DesiredSize ?? default;
            var contentWidth = IsFree(contentChild) ? columnWidth : Math.Min(_contentWidth, columnWidth);
            if (!IsShown(contentChild))
            {
                // Laid out where its row would start, under the next row; the style keeps it unseen.
                contentChild!.Arrange(new Rect(contentLeft, y + (first ? 0 : RowSpacing), contentWidth, content.Height));
                content = default;
            }
            if (!HasSize(label) && !HasSize(content)) continue;
            y += first ? 0 : RowSpacing;
            first = false;

            if (!_stacked)
            {
                var rowHeight = Math.Max(label.Height, content.Height);
                labelChild?.Arrange(new Rect(0, y, _labelWidth, rowHeight));
                contentChild?.Arrange(new Rect(contentLeft, y, contentWidth, rowHeight));
                y += rowHeight;
                continue;
            }

            labelChild?.Arrange(new Rect(0, y, width, label.Height));
            y += label.Height;
            if (HasSize(label) && HasSize(content)) y += LabelSpacing;
            contentChild?.Arrange(new Rect(0, y, contentWidth, content.Height));
            y += content.Height;
        }
        return finalSize;
    }
}
