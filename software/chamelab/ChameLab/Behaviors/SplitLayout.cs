using System;
using Avalonia;
using Avalonia.Controls;

namespace ChameLab.Behaviors;

/// <summary>
/// Attached behavior for a list / details page: <c>Grid b:SplitLayout.DetailWidth="460,580"</c>.
/// The grid must have two columns (<c>*,Auto</c>). While the list keeps at least
/// <see cref="ListMinWidthProperty"/>, the details column gets as much of the rest as it can use
/// (between the two widths); below that the grid gets the <c>single</c> class and shows one pane at a time.
/// </summary>
public static class SplitLayout
{
    /// <summary>Gap between the list and the details, added to the details column.</summary>
    private const double Gap = 18;

    public static readonly AttachedProperty<Size> DetailWidthProperty =
        AvaloniaProperty.RegisterAttached<Grid, Size>("DetailWidth", typeof(SplitLayout));

    public static readonly AttachedProperty<double> ListMinWidthProperty =
        AvaloniaProperty.RegisterAttached<Grid, double>("ListMinWidth", typeof(SplitLayout), 280);

    static SplitLayout()
    {
        DetailWidthProperty.Changed.AddClassHandler<Grid>((grid, _) =>
        {
            grid.SizeChanged -= OnSizeChanged;
            grid.SizeChanged += OnSizeChanged;
            Apply(grid);
        });
    }

    /// <summary>Smallest and largest width of the details pane (Width, Height = min, max).</summary>
    public static Size GetDetailWidth(Grid element) => element.GetValue(DetailWidthProperty);
    public static void SetDetailWidth(Grid element, Size value) => element.SetValue(DetailWidthProperty, value);

    public static double GetListMinWidth(Grid element) => element.GetValue(ListMinWidthProperty);
    public static void SetListMinWidth(Grid element, double value) => element.SetValue(ListMinWidthProperty, value);

    private static void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is Grid grid && e.WidthChanged) Apply(grid);
    }

    private static void Apply(Grid grid)
    {
        if (grid.ColumnDefinitions.Count < 2 || grid.Bounds.Width <= 0) return;
        var (min, max) = (GetDetailWidth(grid).Width, GetDetailWidth(grid).Height);
        var forDetail = grid.Bounds.Width - GetListMinWidth(grid) - Gap;

        var single = forDetail < min;
        grid.Classes.Set("single", single);
        grid.ColumnDefinitions[1].Width = single
            ? GridLength.Auto
            : new GridLength(Math.Min(forDetail, max) + Gap);
    }
}
