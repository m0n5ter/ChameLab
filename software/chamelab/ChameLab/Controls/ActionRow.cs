using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace ChameLab.Controls;

/// <summary>
/// Buttons kept on one line, left-aligned at their natural size. When they don't fit, buttons
/// get the <c>iconic</c> class one at a time (styles hide their label, leaving the icon) until
/// the row fits: lowest <see cref="PriorityProperty"/> first, and among equals the last button
/// first. Labels come back as soon as there is room again.
/// Without <see cref="Wrap"/>, a row too narrow even for its icons then squeezes: first the gaps
/// between the buttons and the buttons' side padding shrink together, in proportion, down to
/// <see cref="MinGap"/>; past that the icons themselves narrow.
/// </summary>
public class ActionRow : Panel
{
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<ActionRow, double>(nameof(Spacing), 8);
    public static readonly StyledProperty<bool> WrapProperty =
        AvaloniaProperty.Register<ActionRow, bool>(nameof(Wrap));

    /// <summary>Higher keeps its label longer when the row runs out of room.</summary>
    public static readonly AttachedProperty<int> PriorityProperty =
        AvaloniaProperty.RegisterAttached<ActionRow, Control, int>("Priority");

    public const string IconicClass = "iconic";

    /// <summary>The least a squeezed row leaves between buttons and on either side of an icon.</summary>
    public const double MinGap = 4;
    /// <summary>The narrowest an icon gets, relative to its natural width.</summary>
    public const double MinIconScale = 0.2;

    static ActionRow()
    {
        AffectsMeasure<ActionRow>(SpacingProperty, WrapProperty);
        PriorityProperty.Changed.AddClassHandler<Control>((c, _) => (c.GetVisualParent() as ActionRow)?.InvalidateMeasure());
    }

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }
    /// <summary>Wrap only when even all icon buttons cannot fit on one line.</summary>
    public bool Wrap { get => GetValue(WrapProperty); set => SetValue(WrapProperty, value); }

    public static int GetPriority(Control control) => control.GetValue(PriorityProperty);
    public static void SetPriority(Control control, int value) => control.SetValue(PriorityProperty, value);

    /// <summary>The row's width with every label hidden, as of the last measure.</summary>
    public double IconicWidth { get; private set; }

    private readonly List<Visual> _ancestors = new();
    // Values a squeeze puts on the buttons, above their styles and local values; disposing them
    // gives the buttons their own values back.
    private readonly List<IDisposable> _squeeze = new();
    // The gap between buttons as last laid out (less than Spacing while squeezed).
    private double _gap = 8;

    private List<Control> Visible() => Children.Where(c => c.IsVisible).ToList();

    /// <remarks>
    /// Avalonia skips measuring controls under a hidden ancestor, and when that ancestor shows
    /// again this row and its buttons keep the sizes cached before: a button that changed while
    /// hidden (its label, a part shown) is laid out at its old width. So the row's subtree is
    /// remeasured whenever an ancestor becomes visible.
    /// </remarks>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _ancestors.AddRange(this.GetVisualAncestors());
        foreach (var ancestor in _ancestors) ancestor.PropertyChanged += OnAncestorPropertyChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        foreach (var ancestor in _ancestors) ancestor.PropertyChanged -= OnAncestorPropertyChanged;
        _ancestors.Clear();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        OnAncestorPropertyChanged(this, change);
    }

    private void OnAncestorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != IsVisibleProperty || e.NewValue is not true) return;
        foreach (var layoutable in this.GetSelfAndVisualDescendants().OfType<Layoutable>())
            layoutable.InvalidateMeasure();
    }

    /// <summary>Visible buttons in the order they lose their label.</summary>
    private static List<Control> CollapseOrder(List<Control> visible) => visible
        .Select((c, i) => (Control: c, Index: i))
        .OrderBy(x => GetPriority(x.Control))
        .ThenByDescending(x => x.Index)
        .Select(x => x.Control)
        .ToList();

    /// <remarks>
    /// Every pass measures each button both with and without its label, so the choice depends
    /// only on the width offered: no state carried between passes, nothing to oscillate.
    /// </remarks>
    protected override Size MeasureOverride(Size availableSize)
    {
        // Hidden buttons keep their label, so they come back whole.
        foreach (var child in Children.Where(c => !c.IsVisible)) SetIconic(child, false);
        ReleaseSqueeze();
        _gap = Spacing;

        var order = CollapseOrder(Visible());
        var full = order.Select(c => WidthOf(c, iconic: false)).ToArray();
        var icon = order.Select(c => WidthOf(c, iconic: true)).ToArray();

        var limit = availableSize.Width;
        var gaps = Math.Max(0, order.Count - 1) * Spacing;
        IconicWidth = icon.Sum() + gaps;
        var width = full.Sum() + gaps;
        var collapsed = 0;
        while (width > limit && collapsed < order.Count)
        {
            width += icon[collapsed] - full[collapsed];
            collapsed++;
        }

        var height = 0.0;
        for (var i = 0; i < order.Count; i++)
        {
            SetIconic(order[i], i < collapsed);
            order[i].Measure(Size.Infinity);
            height = Math.Max(height, order[i].DesiredSize.Height);
        }
        if (!Wrap && collapsed == order.Count && width > limit) width = Squeeze(width - limit);
        return new Size(Math.Min(width, limit), Wrap ? LayoutRows(limit, arrange: false) : height);
    }

    /// <summary>A squeezable button's natural measures, all as laid out with its label hidden.</summary>
    private readonly record struct Fit(Button Button, Control Content, double Width,
        double ReducibleLeft, double ReducibleRight, double ContentWidth);

    /// <summary>
    /// How hard to squeeze a row <paramref name="excess"/> wider than its room. <c>Factor</c>
    /// (0..1) is the share of the reducible gaps and padding taken away; <c>Scale</c> the width
    /// left to the icons once all of that is gone.
    /// </summary>
    internal static (double Factor, double Scale) Plan(double excess, double reducible, double contentWidth)
    {
        if (excess <= 0) return (0, 1);
        if (excess <= reducible) return (excess / reducible, 1);
        var scale = contentWidth > 0 ? Math.Max(MinIconScale, 1 - (excess - reducible) / contentWidth) : 1;
        return (reducible > 0 ? 1 : 0, scale);
    }

    /// <summary>Squeezes the measured icon-only row by <paramref name="excess"/>; returns its new width.</summary>
    private double Squeeze(double excess)
    {
        var visible = Visible();
        var gapCount = Math.Max(0, visible.Count - 1);
        var gapReducible = Math.Max(0, Spacing - MinGap);
        var fits = visible.Select(FitOf).OfType<Fit>().ToList();

        var reducible = gapCount * gapReducible + fits.Sum(f => f.ReducibleLeft + f.ReducibleRight);
        var (factor, scale) = Plan(excess, reducible, fits.Sum(f => f.ContentWidth));
        _gap = Spacing - gapReducible * factor;

        foreach (var fit in fits)
        {
            var button = fit.Button;
            var narrower = fit.ContentWidth * (1 - scale);
            if (double.IsNaN(button.Width))
            {
                var padding = button.Padding;
                Hold(button.SetValue(Button.PaddingProperty, new Thickness(padding.Left - fit.ReducibleLeft * factor,
                    padding.Top, padding.Right - fit.ReducibleRight * factor, padding.Bottom), BindingPriority.Animation));
            }
            else
            {
                var width = fit.Width - (fit.ReducibleLeft + fit.ReducibleRight) * factor - narrower;
                Hold(button.SetValue(WidthProperty, width, BindingPriority.Animation));
            }
            if (scale >= 1) continue;
            // The content keeps its natural layout and narrows only as drawn: a negative right
            // margin makes it ask for the narrower slot, and a scale from its left edge draws it there.
            var margin = fit.Content.Margin;
            Hold(fit.Content.SetValue(MarginProperty,
                new Thickness(margin.Left, margin.Top, margin.Right - narrower, margin.Bottom), BindingPriority.Animation));
            Hold(fit.Content.SetValue(RenderTransformOriginProperty,
                new RelativePoint(0, 0.5, RelativeUnit.Relative), BindingPriority.Animation));
            Hold(fit.Content.SetValue(RenderTransformProperty, new ScaleTransform(scale, 1), BindingPriority.Animation));
        }

        var total = 0.0;
        foreach (var child in visible)
        {
            foreach (var layoutable in child.GetSelfAndVisualDescendants().OfType<Layoutable>())
                layoutable.InvalidateMeasure();
            child.Measure(Size.Infinity);
            total += child.DesiredSize.Width;
        }
        return total + gapCount * _gap;
    }

    /// <summary>
    /// What a button can give up: the room beside its content past <see cref="MinGap"/>, and its
    /// content's width. Null for anything else, which squeezes only by its gaps.
    /// </summary>
    private static Fit? FitOf(Control child)
    {
        if (child is not Button { Content: Control content } button) return null;
        var width = button.DesiredSize.Width;
        var contentWidth = content.DesiredSize.Width - content.Margin.Left - content.Margin.Right;
        double left, right;
        if (double.IsNaN(button.Width)) (left, right) = (button.Padding.Left, button.Padding.Right);
        else
        {
            // A fixed-width button centers its content in what its border leaves.
            var border = button.BorderThickness.Left + button.BorderThickness.Right;
            left = right = (width - border - content.DesiredSize.Width) / 2;
        }
        return new Fit(button, content, width,
            Math.Max(0, left - MinGap), Math.Max(0, right - MinGap), Math.Max(0, contentWidth));
    }

    private void Hold(IDisposable? value)
    {
        if (value is not null) _squeeze.Add(value);
    }

    private void ReleaseSqueeze()
    {
        foreach (var value in _squeeze) value.Dispose();
        _squeeze.Clear();
    }

    private static double WidthOf(Control child, bool iconic)
    {
        SetIconic(child, iconic);
        child.Measure(Size.Infinity);
        return child.DesiredSize.Width;
    }

    /// <summary>
    /// Switches the label off or on. The style change hides a nested TextBlock, which does not
    /// invalidate the button's cached size by the time it is measured again, so the whole
    /// subtree is invalidated here.
    /// </summary>
    private static void SetIconic(Control child, bool iconic)
    {
        if (child.Classes.Contains(IconicClass) == iconic) return;
        child.Classes.Set(IconicClass, iconic);
        foreach (var layoutable in child.GetSelfAndVisualDescendants().OfType<Layoutable>())
            layoutable.InvalidateMeasure();
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Wrap)
        {
            LayoutRows(finalSize.Width, arrange: true);
            return finalSize;
        }
        // Stretched buttons take the row's height, so icon-only ones match their labeled neighbors.
        var x = 0.0;
        foreach (var child in Visible())
        {
            var size = child.DesiredSize;
            var height = child.VerticalAlignment == VerticalAlignment.Stretch ? finalSize.Height : size.Height;
            child.Arrange(new Rect(x, (finalSize.Height - height) / 2, size.Width, height));
            x += size.Width + _gap;
        }
        return finalSize;
    }

    private double LayoutRows(double limit, bool arrange)
    {
        var children = Visible();
        double y = 0;
        for (var start = 0; start < children.Count;)
        {
            var end = start + 1;
            var width = children[start].DesiredSize.Width;
            var height = children[start].DesiredSize.Height;
            while (end < children.Count && width + Spacing + children[end].DesiredSize.Width <= limit)
            {
                width += Spacing + children[end].DesiredSize.Width;
                height = Math.Max(height, children[end].DesiredSize.Height);
                end++;
            }
            double x = 0;
            for (var i = start; i < end; i++)
            {
                var size = children[i].DesiredSize;
                if (arrange) children[i].Arrange(new Rect(x, y + (height - size.Height) / 2, size.Width, size.Height));
                x += size.Width + Spacing;
            }
            y += height + Spacing;
            start = end;
        }
        return children.Count == 0 ? 0 : y - Spacing;
    }
}
