using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;

namespace ChameLab.Controls;

/// <summary>
/// A message followed by action buttons. The first child is the message, the rest are the
/// buttons, kept right-aligned at their natural size. When the message fits on one line
/// beside the buttons everything shares a row; otherwise the message takes its own row
/// above them, so the buttons never stretch to the height of wrapped text.
/// </summary>
public class ActionBar : Panel
{
    public static readonly StyledProperty<double> ColumnSpacingProperty =
        AvaloniaProperty.Register<ActionBar, double>(nameof(ColumnSpacing), 8);

    public static readonly StyledProperty<double> RowSpacingProperty =
        AvaloniaProperty.Register<ActionBar, double>(nameof(RowSpacing), 10);

    static ActionBar()
    {
        AffectsMeasure<ActionBar>(ColumnSpacingProperty, RowSpacingProperty);
    }

    public double ColumnSpacing
    {
        get => GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    public double RowSpacing
    {
        get => GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    // Decided by the last measure, reused by arrange.
    private bool _stacked;
    private double _buttonsWidth, _buttonsHeight;

    private Control? Message => Children.Count > 0 && Children[0].IsVisible ? Children[0] : null;

    private IEnumerable<Control> Buttons()
    {
        for (var i = 1; i < Children.Count; i++)
            if (Children[i].IsVisible) yield return Children[i];
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var limit = availableSize.Width;

        _buttonsWidth = _buttonsHeight = 0;
        var count = 0;
        foreach (var button in Buttons())
        {
            button.Measure(Size.Infinity);
            _buttonsWidth += button.DesiredSize.Width;
            _buttonsHeight = Math.Max(_buttonsHeight, button.DesiredSize.Height);
            count++;
        }
        if (count > 1) _buttonsWidth += (count - 1) * ColumnSpacing;

        var message = Message;
        if (message is null)
        {
            _stacked = false;
            return new Size(double.IsInfinity(limit) ? _buttonsWidth : limit, _buttonsHeight);
        }

        // One line of message beside the buttons, if it fits.
        message.Measure(Size.Infinity);
        var gap = count > 0 ? ColumnSpacing : 0;
        _stacked = !double.IsInfinity(limit) && message.DesiredSize.Width + gap + _buttonsWidth > limit;

        if (!_stacked)
        {
            var width = double.IsInfinity(limit) ? message.DesiredSize.Width + gap + _buttonsWidth : limit;
            return new Size(width, Math.Max(message.DesiredSize.Height, _buttonsHeight));
        }

        message.Measure(new Size(limit, double.PositiveInfinity));
        var height = message.DesiredSize.Height + (count > 0 ? RowSpacing + _buttonsHeight : 0);
        return new Size(limit, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var message = Message;
        double buttonsTop;
        if (message is null)
        {
            buttonsTop = (finalSize.Height - _buttonsHeight) / 2;
        }
        else if (_stacked)
        {
            message.Arrange(new Rect(0, 0, finalSize.Width, message.DesiredSize.Height));
            buttonsTop = message.DesiredSize.Height + RowSpacing;
        }
        else
        {
            var gap = _buttonsWidth > 0 ? ColumnSpacing : 0;
            var h = message.DesiredSize.Height;
            message.Arrange(new Rect(0, (finalSize.Height - h) / 2,
                Math.Max(0, finalSize.Width - _buttonsWidth - gap), h));
            buttonsTop = (finalSize.Height - _buttonsHeight) / 2;
        }

        var x = finalSize.Width - _buttonsWidth;
        foreach (var button in Buttons())
        {
            var size = button.DesiredSize;
            button.Arrange(new Rect(x, buttonsTop + (_buttonsHeight - size.Height) / 2, size.Width, size.Height));
            x += size.Width + ColumnSpacing;
        }
        return finalSize;
    }
}
