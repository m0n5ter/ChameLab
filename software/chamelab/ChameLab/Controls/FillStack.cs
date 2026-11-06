using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace ChameLab.Controls;

/// <summary>
/// A vertical stack that fills a scroller's viewport: every child at its natural height, except the
/// ones marked <see cref="FillProperty">Fill</see>, which share what is left evenly (like "*" rows)
/// but never get less than their own MinHeight. Inside a ScrollViewer it fills the viewport (less its
/// own margin): the content fits the viewport while it can, and only once the fixed parts plus the
/// fill minimums are taller does the scroller scroll.
/// </summary>
public class FillStack : Panel
{
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<FillStack, double>(nameof(Spacing));

    /// <summary>The child shares the height left over by the others.</summary>
    public static readonly AttachedProperty<bool> FillProperty =
        AvaloniaProperty.RegisterAttached<FillStack, Control, bool>("Fill");

    static FillStack()
    {
        AffectsMeasure<FillStack>(SpacingProperty);
        FillProperty.Changed.AddClassHandler<Control>((c, _) => (c.Parent as FillStack)?.InvalidateMeasure());
    }

    public double Spacing { get => GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    public static bool GetFill(Control control) => control.GetValue(FillProperty);
    public static void SetFill(Control control, bool value) => control.SetValue(FillProperty, value);

    // Each fill child's share of the height, decided by the last measure, reused by arrange.
    private double _share;

    private ScrollViewer? _scroller;

    /// <remarks>
    /// The scroller measures its content with an unbounded height, the same each time, so a new
    /// viewport height would not reach this panel by itself.
    /// </remarks>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _scroller = this.FindAncestorOfType<ScrollViewer>();
        if (_scroller != null) _scroller.PropertyChanged += OnScrollerPropertyChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_scroller != null) _scroller.PropertyChanged -= OnScrollerPropertyChanged;
        _scroller = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnScrollerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ScrollViewer.ViewportProperty &&
            e.GetOldValue<Size>().Height != e.GetNewValue<Size>().Height)
            InvalidateMeasure();
    }

    private Control[] Visible() => Children.Where(c => c.IsVisible).ToArray();

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = Visible();
        var width = availableSize.Width;
        // Under an unbounded height, the scroller's viewport is the height to fill.
        var height = availableSize.Height;
        if (double.IsInfinity(height))
            height = Math.Max(MinHeight, (_scroller?.Viewport.Height ?? 0) - Margin.Top - Margin.Bottom);
        var natural = new Size(width, double.PositiveInfinity);

        var total = Math.Max(0, children.Length - 1) * Spacing;
        var fills = 0;
        var widest = 0.0;
        foreach (var child in children)
        {
            if (GetFill(child)) { fills++; continue; }
            child.Measure(natural);
            total += child.DesiredSize.Height;
            widest = Math.Max(widest, child.DesiredSize.Width);
        }

        // Past the fill minimums the panel outgrows the height and the scroller scrolls.
        _share = fills == 0 ? 0 : Math.Max(0, height - total) / fills;
        foreach (var child in children.Where(GetFill))
        {
            var h = Math.Max(_share, child.MinHeight);
            child.Measure(new Size(width, h));
            total += h;
            widest = Math.Max(widest, child.DesiredSize.Width);
        }
        return new Size(double.IsInfinity(width) ? widest : width, total);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var y = 0.0;
        foreach (var child in Visible())
        {
            // A fill child gets its whole share, stretched or aligned within it as it asks.
            var h = GetFill(child) ? Math.Max(_share, child.MinHeight) : child.DesiredSize.Height;
            child.Arrange(new Rect(0, y, finalSize.Width, h));
            y += h + Spacing;
        }
        return finalSize;
    }
}
