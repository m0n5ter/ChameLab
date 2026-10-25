using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using ChameLab.Models;

namespace ChameLab.Controls;

/// <summary>
/// One cell per card block (or Ultralight page), colored by its dump state, drawn as a single control so the
/// cells always split the full width exactly. Wraps into rows of at most <see cref="MaxColumns"/>.
/// </summary>
public class BlockStrip : Control
{
    public static readonly StyledProperty<IReadOnlyList<MifareBlockState>?> StatesProperty =
        AvaloniaProperty.Register<BlockStrip, IReadOnlyList<MifareBlockState>?>(nameof(States));

    public static readonly StyledProperty<int> MaxColumnsProperty =
        AvaloniaProperty.Register<BlockStrip, int>(nameof(MaxColumns), 64);

    public static readonly StyledProperty<double> CellHeightProperty =
        AvaloniaProperty.Register<BlockStrip, double>(nameof(CellHeight), 14);

    public static readonly StyledProperty<double> GapProperty =
        AvaloniaProperty.Register<BlockStrip, double>(nameof(Gap), 2);

    /// <summary>What a cell stands for in its tooltip (“Block”, “Page”).</summary>
    public static readonly StyledProperty<string> UnitProperty =
        AvaloniaProperty.Register<BlockStrip, string>(nameof(Unit), "Block");

    /// <summary>Tooltip state of a cell that could not be opened (no key, password-protected).</summary>
    public static readonly StyledProperty<string> LockedTextProperty =
        AvaloniaProperty.Register<BlockStrip, string>(nameof(LockedText), "No key");

    /// <summary>The strip tracks a write rather than a dump; only the tooltip wording changes.</summary>
    public static readonly StyledProperty<bool> IsWriteProperty =
        AvaloniaProperty.Register<BlockStrip, bool>(nameof(IsWrite));

    public static readonly StyledProperty<IBrush?> PendingBrushProperty =
        AvaloniaProperty.Register<BlockStrip, IBrush?>(nameof(PendingBrush), Brushes.Gray);

    public static readonly StyledProperty<IBrush?> ReadingBrushProperty =
        AvaloniaProperty.Register<BlockStrip, IBrush?>(nameof(ReadingBrush), Brushes.MediumPurple);

    public static readonly StyledProperty<IBrush?> ReadBrushProperty =
        AvaloniaProperty.Register<BlockStrip, IBrush?>(nameof(ReadBrush), Brushes.SeaGreen);

    public static readonly StyledProperty<IBrush?> ReadErrorBrushProperty =
        AvaloniaProperty.Register<BlockStrip, IBrush?>(nameof(ReadErrorBrush), Brushes.Orange);

    public static readonly StyledProperty<IBrush?> NoKeyBrushProperty =
        AvaloniaProperty.Register<BlockStrip, IBrush?>(nameof(NoKeyBrush), Brushes.IndianRed);

    static BlockStrip()
    {
        AffectsMeasure<BlockStrip>(StatesProperty, MaxColumnsProperty, CellHeightProperty, GapProperty);
        AffectsRender<BlockStrip>(StatesProperty, PendingBrushProperty, ReadingBrushProperty,
            ReadBrushProperty, ReadErrorBrushProperty, NoKeyBrushProperty);
    }

    public IReadOnlyList<MifareBlockState>? States
    {
        get => GetValue(StatesProperty);
        set => SetValue(StatesProperty, value);
    }

    public int MaxColumns
    {
        get => GetValue(MaxColumnsProperty);
        set => SetValue(MaxColumnsProperty, value);
    }

    public double CellHeight
    {
        get => GetValue(CellHeightProperty);
        set => SetValue(CellHeightProperty, value);
    }

    public double Gap
    {
        get => GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    public string Unit { get => GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public string LockedText { get => GetValue(LockedTextProperty); set => SetValue(LockedTextProperty, value); }
    public bool IsWrite { get => GetValue(IsWriteProperty); set => SetValue(IsWriteProperty, value); }

    public IBrush? PendingBrush { get => GetValue(PendingBrushProperty); set => SetValue(PendingBrushProperty, value); }
    public IBrush? ReadingBrush { get => GetValue(ReadingBrushProperty); set => SetValue(ReadingBrushProperty, value); }
    public IBrush? ReadBrush { get => GetValue(ReadBrushProperty); set => SetValue(ReadBrushProperty, value); }
    public IBrush? ReadErrorBrush { get => GetValue(ReadErrorBrushProperty); set => SetValue(ReadErrorBrushProperty, value); }
    public IBrush? NoKeyBrush { get => GetValue(NoKeyBrushProperty); set => SetValue(NoKeyBrushProperty, value); }

    private int Count => States?.Count ?? 0;
    private int Columns => Math.Max(1, Math.Min(MaxColumns, Count));
    private int Rows => Count == 0 ? 0 : (Count + Columns - 1) / Columns;

    protected override Size MeasureOverride(Size availableSize)
    {
        var h = Rows == 0 ? 0 : Rows * CellHeight + (Rows - 1) * Gap;
        var w = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        return new Size(w, h);
    }

    /// <summary>Cell rectangle in fractional pixels: the last cell of a row ends at the right edge.</summary>
    private Rect CellRect(int index)
    {
        var cols = Columns;
        var step = (Bounds.Width + Gap) / cols;
        int row = index / cols, col = index % cols;
        return new Rect(col * step, row * (CellHeight + Gap), Math.Max(0, step - Gap), CellHeight);
    }

    public override void Render(DrawingContext context)
    {
        if (States is not { Count: > 0 } states) return;
        for (var i = 0; i < states.Count; i++)
        {
            var brush = states[i] switch
            {
                MifareBlockState.Reading => ReadingBrush,
                MifareBlockState.Read => ReadBrush,
                MifareBlockState.ReadError => ReadErrorBrush,
                MifareBlockState.NoKey => NoKeyBrush,
                _ => PendingBrush,
            };
            if (brush is not null)
                context.DrawRectangle(brush, null, new RoundedRect(CellRect(i), 2));
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (States is not { Count: > 0 } states) return;
        var p = e.GetPosition(this);
        var cols = Columns;
        var col = (int)(p.X / ((Bounds.Width + Gap) / cols));
        var row = (int)(p.Y / (CellHeight + Gap));
        var index = row * cols + col;
        ToolTip.SetTip(this, index >= 0 && index < states.Count && col < cols
            ? $"{Unit} {index}: {Describe(states[index])}"
            : null);
    }

    private string Describe(MifareBlockState s) => IsWrite
        ? s switch
        {
            MifareBlockState.Reading => "writing…",
            MifareBlockState.Read => "written",
            MifareBlockState.ReadError => "write failed",
            MifareBlockState.NoKey => LockedText.ToLowerInvariant(),
            _ => "not written",
        }
        : s switch
        {
            MifareBlockState.Reading => "reading…",
            MifareBlockState.Read => "read",
            MifareBlockState.ReadError => "read failed",
            MifareBlockState.NoKey => LockedText.ToLowerInvariant(),
            _ => "not read yet",
        };
}
