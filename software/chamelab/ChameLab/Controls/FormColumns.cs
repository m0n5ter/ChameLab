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
/// Contents marked <see cref="BesideProperty">Beside</see> add a widest layout on top: when the labels,
/// the other contents and the Beside ones all fit side by side, the Beside ones form a third column
/// next to the row they follow. Otherwise they are ordinary rows and the two layouts above apply.
/// </para>
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

    /// <summary>
    /// When there is width for it, the content (whose label is ignored) moves into a third column, beside
    /// the content of the nearest row above that is not Beside; several such rows stack there in order.
    /// Without that width the row is an ordinary one.
    /// </summary>
    public static readonly AttachedProperty<bool> BesideProperty =
        AvaloniaProperty.RegisterAttached<FormColumns, Control, bool>("Beside");

    /// <summary>Set on a content whose <see cref="ShownProperty">Shown</see> is false.</summary>
    public const string UnshownClass = "unshown";

    static FormColumns()
    {
        AffectsMeasure<FormColumns>(ColumnSpacingProperty, RowSpacingProperty, LabelSpacingProperty);
        FreeProperty.Changed.AddClassHandler<Control>((c, _) => (c.Parent as FormColumns)?.InvalidateMeasure());
        BesideProperty.Changed.AddClassHandler<Control>((c, _) => (c.Parent as FormColumns)?.InvalidateMeasure());
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
    public static bool GetBeside(Control control) => control.GetValue(BesideProperty);
    public static void SetBeside(Control control, bool value) => control.SetValue(BesideProperty, value);

    // Decided by the last measure, reused by arrange.
    private bool _stacked;
    private bool _threeColumns;
    private double _labelWidth;
    private double _contentWidth;
    private double _besideWidth;

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

    // The first row never goes beside: there is nothing above to sit next to.
    private bool IsBeside(int row) => row > 0 && Child(2 * row + 1) is { } content && GetBeside(content);

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = availableSize.Width;

        // Natural widths decide the layout, so the result cannot flip back and forth.
        var labelWidth = 0.0;
        var contentWidth = 0.0;
        var mainWidth = 0.0;
        var besideWidth = 0.0;
        var anyBeside = false;
        for (var r = 0; r < Rows; r++)
        {
            var beside = IsBeside(r);
            anyBeside |= beside;
            labelWidth = Math.Max(labelWidth, Measure(Child(2 * r), double.PositiveInfinity).Width);
            var content = Child(2 * r + 1);
            if (IsFree(content)) continue;
            var natural = Measure(content, double.PositiveInfinity).Width;
            contentWidth = Math.Max(contentWidth, natural);
            if (beside) besideWidth = Math.Max(besideWidth, natural);
            else mainWidth = Math.Max(mainWidth, natural);
        }
        _labelWidth = labelWidth;
        _threeColumns = anyBeside && !double.IsInfinity(width)
                        && labelWidth + ColumnSpacing + mainWidth + ColumnSpacing + besideWidth <= width;
        if (_threeColumns)
        {
            _stacked = false;
            _contentWidth = mainWidth;
            _besideWidth = besideWidth;
            for (var r = 0; r < Rows; r++)
            {
                var content = Child(2 * r + 1);
                if (IsBeside(r))
                {
                    Measure(content, IsFree(content) ? BesideColumnWidth(width) : besideWidth);
                    continue;
                }
                Measure(Child(2 * r), labelWidth);
                Measure(content, IsFree(content) ? width - labelWidth - ColumnSpacing : mainWidth);
            }
            return new Size(width, LayoutThreeColumns(width, false));
        }
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

    private double BesideColumnWidth(double width) =>
        Math.Max(0, width - _labelWidth - ColumnSpacing - _contentWidth - ColumnSpacing);

    /// <summary>
    /// Three columns: each ordinary row with the Beside rows after it stacked in the third column. The
    /// label and content are centered on the first beside item, so the rest (errors, notes) hang below.
    /// Returns the height; arranges the children when asked to.
    /// </summary>
    private double LayoutThreeColumns(double width, bool arrange)
    {
        var contentLeft = _labelWidth + ColumnSpacing;
        var besideLeft = contentLeft + _contentWidth + ColumnSpacing;
        var besideColumn = BesideColumnWidth(width);
        var y = 0.0;
        var first = true;
        var r = 0;
        while (r < Rows)
        {
            var labelChild = Child(2 * r);
            var contentChild = Child(2 * r + 1);
            var label = labelChild?.DesiredSize ?? default;
            var content = IsShown(contentChild) ? contentChild?.DesiredSize ?? default : default;
            var contentWidth = IsFree(contentChild) ? Math.Max(0, width - contentLeft) : _contentWidth;
            var top = y + (first ? 0 : RowSpacing);

            var end = r + 1;
            while (end < Rows && IsBeside(end)) end++;

            // The beside stack, before placing the row, as its first item sets the band height.
            var firstBeside = 0.0;
            var stack = 0.0;
            var any = false;
            for (var b = r + 1; b < end; b++)
            {
                var child = Child(2 * b + 1)!;
                var size = IsShown(child) ? child.DesiredSize : default;
                if (!HasSize(size)) continue;
                if (!any) firstBeside = size.Height;
                stack += (any ? RowSpacing : 0) + size.Height;
                any = true;
            }

            if (HasSize(label) || HasSize(content) || any)
            {
                var band = Math.Max(Math.Max(label.Height, content.Height), firstBeside);
                if (arrange)
                {
                    labelChild?.Arrange(new Rect(0, top, _labelWidth, band));
                    contentChild?.Arrange(new Rect(contentLeft, top, contentWidth, IsShown(contentChild) ? band : contentChild!.DesiredSize.Height));
                    var by = top;
                    var placed = false;
                    for (var b = r + 1; b < end; b++)
                    {
                        // Beside rows have no label of their own here.
                        Child(2 * b)?.Arrange(new Rect(0, top, 0, 0));
                        var child = Child(2 * b + 1)!;
                        var w = IsFree(child) ? besideColumn : Math.Min(_besideWidth, besideColumn);
                        // Unshown: laid out where the stack starts; the style keeps it unseen.
                        if (!IsShown(child))
                        {
                            child.Arrange(new Rect(besideLeft, top, w, child.DesiredSize.Height));
                            continue;
                        }
                        if (!HasSize(child.DesiredSize)) continue;
                        by += placed ? RowSpacing : 0;
                        child.Arrange(new Rect(besideLeft, by, w, child.DesiredSize.Height));
                        by += child.DesiredSize.Height;
                        placed = true;
                    }
                }
                y = top + Math.Max(band, stack);
                first = false;
            }
            else if (arrange)
            {
                // Nothing to show in the group, but unshown children still get a place.
                if (contentChild != null) contentChild.Arrange(new Rect(contentLeft, top, contentWidth, contentChild.DesiredSize.Height));
                for (var b = r + 1; b < end; b++)
                {
                    Child(2 * b)?.Arrange(new Rect(0, top, 0, 0));
                    var child = Child(2 * b + 1)!;
                    child.Arrange(new Rect(besideLeft, top, Math.Min(_besideWidth, besideColumn), child.DesiredSize.Height));
                }
            }
            r = end;
        }
        return y;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var width = finalSize.Width;
        if (_threeColumns)
        {
            LayoutThreeColumns(width, true);
            return finalSize;
        }
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
