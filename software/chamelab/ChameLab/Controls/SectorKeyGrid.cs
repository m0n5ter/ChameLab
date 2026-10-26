using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using ChameLab.Models;

namespace ChameLab.Controls;

/// <summary>
/// The MIFARE Classic dump key grid: one cell per sector, laid out <see cref="Columns"/> per row
/// (16 by default, so a 1K card is one row and a 4K card three). Each cell is two stacked squares —
/// key A on top, key B below — colored by the sector's key-search state. Drawn as one control (like
/// <see cref="BlockStrip"/>) so its colors come from themed styles and it redraws on every tick.
/// A searching square fills from left to right with the share of candidate keys tried and pulses;
/// a square that settles (found / not found) gives off a short ring; the hovered sector is outlined.
/// </summary>
public class SectorKeyGrid : Control
{
    public static readonly StyledProperty<IReadOnlyList<ISectorKeys>?> SectorsProperty =
        AvaloniaProperty.Register<SectorKeyGrid, IReadOnlyList<ISectorKeys>?>(nameof(Sectors));

    public static readonly StyledProperty<int> ColumnsProperty =
        AvaloniaProperty.Register<SectorKeyGrid, int>(nameof(Columns), 16);

    /// <summary>Upper bound on a cell's height; narrower cells stay square.</summary>
    public static readonly StyledProperty<double> MaxSquareProperty =
        AvaloniaProperty.Register<SectorKeyGrid, double>(nameof(MaxSquare), 28);

    /// <summary>How wide a cell may stretch on wide layouts before spare width is left on the right.</summary>
    public static readonly StyledProperty<double> MaxCellWidthProperty =
        AvaloniaProperty.Register<SectorKeyGrid, double>(nameof(MaxCellWidth), 40);

    /// <summary>The spacing between cells: between columns and between a sector's A and B.</summary>
    public static readonly StyledProperty<double> GapProperty =
        AvaloniaProperty.Register<SectorKeyGrid, double>(nameof(Gap), 3);

    /// <summary>The spacing between sector rows (16 sectors each), so a 2K / 4K grid reads as separate rows.</summary>
    public static readonly StyledProperty<double> RowGapProperty =
        AvaloniaProperty.Register<SectorKeyGrid, double>(nameof(RowGap), 10);

    public static readonly StyledProperty<IBrush?> FoundBrushProperty =
        AvaloniaProperty.Register<SectorKeyGrid, IBrush?>(nameof(FoundBrush), Brushes.SeaGreen);
    public static readonly StyledProperty<IBrush?> SearchingBrushProperty =
        AvaloniaProperty.Register<SectorKeyGrid, IBrush?>(nameof(SearchingBrush), Brushes.MediumPurple);
    public static readonly StyledProperty<IBrush?> NotFoundBrushProperty =
        AvaloniaProperty.Register<SectorKeyGrid, IBrush?>(nameof(NotFoundBrush), Brushes.IndianRed);
    public static readonly StyledProperty<IBrush?> UnknownBrushProperty =
        AvaloniaProperty.Register<SectorKeyGrid, IBrush?>(nameof(UnknownBrush), Brushes.Gray);

    /// <summary>The A / B row labels and the hover outline; no labels when null.</summary>
    public static readonly StyledProperty<IBrush?> LabelBrushProperty =
        AvaloniaProperty.Register<SectorKeyGrid, IBrush?>(nameof(LabelBrush), Brushes.Gray);

    private const double MinSquare = 6;
    private const double Gutter = 16;    // left margin holding the A / B labels, gap included
    private const double Radius = 3;
    private const double FlashMs = 550;  // ring after a key search settles
    private const double PulseMs = 1100; // one breath of a searching square

    private readonly List<INotifyPropertyChanged> _tracked = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private KeyAnim[] _anims = Array.Empty<KeyAnim>(); // 2 per sector: A at 2i, B at 2i+1
    private double _lastFrameMs;
    private bool _frameQueued;
    private int _hover = -1;
    private FormattedText? _labelA, _labelB;
    private SectorTip? _tip;

    /// <summary>Per-square animation memory: the last state seen, when it settled, the drawn fill.</summary>
    private sealed class KeyAnim
    {
        public KeyCellState State;
        public double SettledAt = double.NegativeInfinity;
        public double Shown;
    }

    static SectorKeyGrid()
    {
        AffectsMeasure<SectorKeyGrid>(SectorsProperty, ColumnsProperty, MaxSquareProperty, MaxCellWidthProperty,
            GapProperty, RowGapProperty, LabelBrushProperty);
        AffectsRender<SectorKeyGrid>(SectorsProperty, FoundBrushProperty, SearchingBrushProperty,
            NotFoundBrushProperty, UnknownBrushProperty, LabelBrushProperty);
        SectorsProperty.Changed.AddClassHandler<SectorKeyGrid>((g, e) => g.OnSectorsChanged(e));
        LabelBrushProperty.Changed.AddClassHandler<SectorKeyGrid>((g, _) => g._labelA = g._labelB = null);
    }

    public IReadOnlyList<ISectorKeys>? Sectors { get => GetValue(SectorsProperty); set => SetValue(SectorsProperty, value); }
    public int Columns { get => GetValue(ColumnsProperty); set => SetValue(ColumnsProperty, value); }
    public double MaxSquare { get => GetValue(MaxSquareProperty); set => SetValue(MaxSquareProperty, value); }
    public double MaxCellWidth { get => GetValue(MaxCellWidthProperty); set => SetValue(MaxCellWidthProperty, value); }
    public double RowGap { get => GetValue(RowGapProperty); set => SetValue(RowGapProperty, value); }
    public double Gap { get => GetValue(GapProperty); set => SetValue(GapProperty, value); }
    public IBrush? FoundBrush { get => GetValue(FoundBrushProperty); set => SetValue(FoundBrushProperty, value); }
    public IBrush? SearchingBrush { get => GetValue(SearchingBrushProperty); set => SetValue(SearchingBrushProperty, value); }
    public IBrush? NotFoundBrush { get => GetValue(NotFoundBrushProperty); set => SetValue(NotFoundBrushProperty, value); }
    public IBrush? UnknownBrush { get => GetValue(UnknownBrushProperty); set => SetValue(UnknownBrushProperty, value); }
    public IBrush? LabelBrush { get => GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }

    private double Now => _clock.Elapsed.TotalMilliseconds;

    private void OnSectorsChanged(AvaloniaPropertyChangedEventArgs e)
    {
        if (e.OldValue is INotifyCollectionChanged oldColl) oldColl.CollectionChanged -= OnCollectionChanged;
        if (e.NewValue is INotifyCollectionChanged newColl) newColl.CollectionChanged += OnCollectionChanged;
        Resync();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Resync();
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>
    /// Re-attaches the per-sector change listeners to exactly the current sectors and restarts the
    /// animation memory from their present states (a fresh or reloaded grid doesn't flash).
    /// </summary>
    private void Resync()
    {
        foreach (var t in _tracked) t.PropertyChanged -= OnItemChanged;
        _tracked.Clear();
        _hover = -1;
        var sectors = Sectors;
        _anims = new KeyAnim[2 * (sectors?.Count ?? 0)];
        if (sectors is null) return;
        for (var i = 0; i < sectors.Count; i++)
        {
            var s = sectors[i];
            _anims[2 * i] = new KeyAnim { State = s.KeyAState, Shown = Math.Max(0, s.KeyAProgress) };
            _anims[2 * i + 1] = new KeyAnim { State = s.KeyBState, Shown = Math.Max(0, s.KeyBProgress) };
            if (s is INotifyPropertyChanged npc) { npc.PropertyChanged += OnItemChanged; _tracked.Add(npc); }
        }
        EnsureAnimating();
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Sectors is { } sectors)
            for (var i = 0; i < sectors.Count && 2 * i + 1 < _anims.Length; i++)
            {
                if (!ReferenceEquals(sectors[i], sender)) continue;
                Track(_anims[2 * i], sectors[i].KeyAState);
                Track(_anims[2 * i + 1], sectors[i].KeyBState);
                if (i == _hover) UpdateTip();
                break;
            }
        InvalidateVisual();
        EnsureAnimating();
    }

    private void Track(KeyAnim a, KeyCellState state)
    {
        if (a.State == state) return;
        if (state is KeyCellState.Found or KeyCellState.NotFound) a.SettledAt = Now;
        if (state == KeyCellState.Searching) a.Shown = 0;
        a.State = state;
    }

    // ---- animation loop: runs on the render clock only while something on the grid moves ----

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        EnsureAnimating();
    }

    private bool IsAnimating()
    {
        var now = Now;
        foreach (var a in _anims)
            if (a.State == KeyCellState.Searching || now - a.SettledAt < FlashMs) return true;
        return false;
    }

    private void EnsureAnimating()
    {
        if (_frameQueued || !IsAnimating() || TopLevel.GetTopLevel(this) is not { } top) return;
        _frameQueued = true;
        top.RequestAnimationFrame(_ =>
        {
            _frameQueued = false;
            InvalidateVisual();
            EnsureAnimating();
        });
    }

    // ---- layout ----

    private int Count => Sectors?.Count ?? 0;
    private int Cols => Math.Max(1, Math.Min(Columns, Math.Max(1, Count)));
    private int GridRows => Count == 0 ? 0 : (Count + Cols - 1) / Cols;

    private double Scale => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;

    /// <summary>
    /// Cell geometry in DIPs, every value a whole number of device pixels, so the grid stays uniform:
    /// all cells share one size, columns and A / B are <see cref="Gap"/> apart, sector rows
    /// <see cref="RowGap"/>. <see cref="Gap"/> also seeds the outline / ring offsets.
    /// </summary>
    private readonly record struct Metrics(double Left, double Width, double Height, double Gap, double RowGap,
        double Px, int Cols)
    {
        /// <summary>One sector row: the A row, a gap, the B row.</summary>
        public double PairHeight => 2 * Height + Gap;

        public Rect Cell(int sector, bool keyB) => new(
            Left + sector % Cols * (Width + Gap),
            sector / Cols * (PairHeight + RowGap) + (keyB ? Height + Gap : 0),
            Width, Height);

        public double GridHeight(int rows) => rows == 0 ? 0 : rows * PairHeight + (rows - 1) * RowGap;
    }

    /// <summary>
    /// Fits <see cref="Cols"/> cells into <paramref name="width"/>: cells widen with the control (up to
    /// <see cref="MaxCellWidth"/>) instead of the gaps growing; height follows the width up to
    /// <see cref="MaxSquare"/>, so narrow layouts keep squares. The pixel remainder stays on the right.
    /// </summary>
    private Metrics Layout(double width)
    {
        var scale = Scale;
        double Px(double dip) => Math.Round(dip * scale);
        var cols = Cols;
        var gap = Math.Max(1, Px(Gap));
        var rowGap = Math.Max(gap, Px(RowGap));
        var left = LabelBrush is null ? 0 : Px(Gutter);
        var min = Px(MinSquare);
        var avail = Math.Floor(Math.Max(0, width) * scale) - left;
        var w = Math.Clamp(Math.Floor((avail - (cols - 1) * gap) / cols), min, Math.Max(min, Px(MaxCellWidth)));
        var h = Math.Max(min, Math.Min(w, Px(MaxSquare)));
        return new Metrics(left / scale, w / scale, h / scale, gap / scale, rowGap / scale, 1 / scale, cols);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Count == 0) return default;
        var w = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        var m = Layout(double.IsInfinity(availableSize.Width) ? double.MaxValue / 4 : w);
        return new Size(w, m.GridHeight(GridRows));
    }

    // ---- drawing ----

    public override void Render(DrawingContext ctx)
    {
        if (Sectors is not { Count: > 0 } sectors) return;
        var now = Now;
        var dt = Math.Clamp(now - _lastFrameMs, 0, 100);
        _lastFrameMs = now;
        // Fill eases toward the tried share at ~1/12 s, so coarse ticks still read as smooth motion.
        var ease = 1 - Math.Exp(-dt / 80);

        var m = Layout(Bounds.Width);
        // Paint the whole area so the pointer stays on the grid over the gaps between squares
        // (hit testing only sees what is drawn); otherwise the hover outline and tooltip flicker off.
        ctx.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        var pulse = 0.5 - 0.5 * Math.Cos(now / PulseMs * 2 * Math.PI); // 0..1..0

        for (var i = 0; i < sectors.Count && 2 * i + 1 < _anims.Length; i++)
        {
            Square(ctx, m, m.Cell(i, false), _anims[2 * i], sectors[i].KeyAState, sectors[i].KeyAProgress, now, ease, pulse);
            Square(ctx, m, m.Cell(i, true), _anims[2 * i + 1], sectors[i].KeyBState, sectors[i].KeyBProgress, now, ease, pulse);
        }

        // The hovered sector's outline runs down the middle of the surrounding gap, clear of its neighbors.
        if (_hover >= 0 && _hover < sectors.Count && LabelBrush is { } hb)
        {
            var t = Math.Max(m.Px, Math.Floor(m.Gap / m.Px / 2) * m.Px);
            var box = m.Cell(_hover, false).Union(m.Cell(_hover, true)).Inflate(m.Gap / 2);
            ctx.DrawRectangle(null, new Pen(hb, t), new RoundedRect(box, Radius + m.Gap / 2));
        }

        DrawLabels(ctx, m, sectors.Count);
    }

    private void Square(DrawingContext ctx, Metrics m, Rect r, KeyAnim anim, KeyCellState state, double progress,
        double now, double ease, double pulse)
    {
        var shape = new RoundedRect(r, Radius);
        if (state == KeyCellState.Searching)
        {
            if (UnknownBrush is { } bg) ctx.DrawRectangle(bg, null, shape);
            if (SearchingBrush is not { } sb) return;

            // Breathing tint across the whole square: says "working" even before the first count arrives.
            using (ctx.PushOpacity(0.18 + 0.22 * pulse))
                ctx.DrawRectangle(sb, null, shape);

            if (progress >= 0)
            {
                anim.Shown = progress < anim.Shown ? progress : anim.Shown + (progress - anim.Shown) * ease;
                // Whole device pixels, so the fill edge is as crisp as the cell edges.
                var w = Math.Round(r.Width * anim.Shown / m.Px) * m.Px;
                if (w > 0)
                    using (ctx.PushClip(shape))
                        ctx.DrawRectangle(sb, null, new Rect(r.X, r.Y, w, r.Height));
            }

            var border = Math.Max(1, Math.Round(1.5 / m.Px)) * m.Px;
            ctx.DrawRectangle(null, new Pen(sb, border), new RoundedRect(r.Deflate(border / 2), Radius - border / 2));
            return;
        }

        var brush = state switch
        {
            KeyCellState.Found => FoundBrush,
            KeyCellState.NotFound => NotFoundBrush,
            _ => UnknownBrush,
        };
        if (brush is null) return;
        ctx.DrawRectangle(brush, null, shape);

        // The square just settled: a ring of its color grows outward and fades.
        var t = (now - anim.SettledAt) / FlashMs;
        if (t is >= 0 and < 1)
        {
            var e = 1 - (1 - t) * (1 - t) * (1 - t); // ease-out cubic
            using (ctx.PushOpacity(1 - t))
                ctx.DrawRectangle(null, new Pen(brush, 2 * (1 - e) + 0.75),
                    new RoundedRect(r.Inflate(m.Gap * (0.5 + 1.5 * e)), Radius + m.Gap * (0.5 + 1.5 * e)));
        }
    }

    /// <summary>"A" / "B" in the gutter at the start of every row, centered on its square.</summary>
    private void DrawLabels(DrawingContext ctx, Metrics m, int count)
    {
        if (LabelBrush is not { } lb) return;
        _labelA ??= Label("A", lb);
        _labelB ??= Label("B", lb);
        for (var i = 0; i < count; i += m.Cols)
        {
            var a = m.Cell(i, false);
            var b = m.Cell(i, true);
            ctx.DrawText(_labelA, new Point((m.Left - m.Gap - _labelA.Width) / 2, a.Y + (a.Height - _labelA.Height) / 2));
            ctx.DrawText(_labelB, new Point((m.Left - m.Gap - _labelB.Width) / 2, b.Y + (b.Height - _labelB.Height) / 2));
        }
    }

    private static FormattedText Label(string text, IBrush brush) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold), 10, brush);

    // ---- hover / tooltip ----

    /// <summary>Sector under <paramref name="p"/>, counting the gap after each cell as part of it so
    /// moving between squares doesn't flicker the outline; -1 outside the grid.</summary>
    private int HitTest(Point p)
    {
        var count = Count;
        if (count == 0) return -1;
        var m = Layout(Bounds.Width);
        if (p.X < m.Left || p.Y < 0) return -1;
        int col = (int)((p.X - m.Left) / (m.Width + m.Gap)), row = (int)(p.Y / (m.PairHeight + m.RowGap));
        var i = row * m.Cols + col;
        return col < m.Cols && i < count ? i : -1;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        SetHover(HitTest(e.GetPosition(this)));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        // A tap on touch screens selects the sector and opens its tooltip (no hover there).
        SetHover(HitTest(e.GetPosition(this)));
        if (_hover >= 0 && e.Pointer.Type != PointerType.Mouse) ToolTip.SetIsOpen(this, true);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        SetHover(-1);
    }

    private void SetHover(int i)
    {
        if (i == _hover) return;
        _hover = i;
        UpdateTip();
        InvalidateVisual();
    }

    private void UpdateTip()
    {
        var sectors = Sectors;
        if (sectors is null || _hover < 0 || _hover >= sectors.Count) { ToolTip.SetTip(this, null); return; }
        _tip ??= new SectorTip();
        _tip.Show(sectors[_hover]);
        ToolTip.SetTip(this, _tip.Root);
    }

    /// <summary>
    /// The sector tooltip: a two-column table, captions dimmed on the left, values bold on the
    /// right; recovered keys in a monospace font so their digits line up. Built once and refilled.
    /// </summary>
    private sealed class SectorTip
    {
        private static readonly FontFamily Mono = new("Cascadia Code,Consolas,Menlo,monospace");
        private readonly TextBlock _sector = Value(), _blocks = Value(), _keyA = Value(), _keyB = Value();

        public Grid Root { get; } = new()
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"),
            ColumnSpacing = 14,
            RowSpacing = 3,
        };

        public SectorTip()
        {
            Add(0, "Sector", _sector);
            Add(1, "Blocks", _blocks);
            Add(2, "Key A", _keyA);
            Add(3, "Key B", _keyB);
        }

        public void Show(ISectorKeys s)
        {
            _sector.Text = s.Sector.ToString(CultureInfo.InvariantCulture);
            _blocks.Text = $"{s.FirstBlock}–{s.LastBlock}";
            Key(_keyA, s.KeyAState, s.KeyAText);
            Key(_keyB, s.KeyBState, s.KeyBText);
        }

        private static void Key(TextBlock t, KeyCellState state, string text)
        {
            t.Text = text;
            if (state == KeyCellState.Found) t.FontFamily = Mono;
            else t.ClearValue(TextBlock.FontFamilyProperty);
        }

        private void Add(int row, string caption, TextBlock value)
        {
            var label = new TextBlock { Text = caption, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(label, row);
            Grid.SetRow(value, row);
            Grid.SetColumn(value, 1);
            Root.Children.Add(label);
            Root.Children.Add(value);
        }

        private static TextBlock Value() =>
            new() { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    }
}
