using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.TextInput;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace ChameLab.Controls;

/// <summary>
/// Text box for a short hex value (UID, SAK, ATQA…). Accepts only hex digits,
/// shows them upper-case in space-separated byte pairs, overwrites once the
/// value is full and shows the byte count, flagged when the length isn't one
/// of <see cref="ByteLengths"/>. Pasting "04:a1-b2 c3" or "0x04A1" just works.
/// With a maximum length, its natural width fits a full value, so layouts that size
/// by content (e.g. <see cref="FlowGrid"/>) don't reflow while it is typed.
/// </summary>
public class HexBox : TextBox
{
    /// <summary>Allowed lengths in bytes, e.g. "4,7,10"; empty allows any length.</summary>
    public static readonly StyledProperty<string?> ByteLengthsProperty =
        AvaloniaProperty.Register<HexBox, string?>(nameof(ByteLengths));

    public static readonly DirectProperty<HexBox, int> ByteCountProperty =
        AvaloniaProperty.RegisterDirect<HexBox, int>(nameof(ByteCount), o => o.ByteCount);

    private readonly TextBlock _counter = new() { VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
    private int[] _lengths = Array.Empty<int>();
    private bool _normalizing;
    private int? _caretHold;
    private readonly List<(string Text, int Caret)> _undo = new();

    public HexBox()
    {
        Classes.Add("hex");
        Classes.Add("mono");
        _counter.Classes.Add("hexcount");
        InnerRightContent = _counter;
        TextInputOptions.SetShowSuggestions(this, false);
        TextInputOptions.SetAutoCapitalization(this, false);
        TextInputOptions.SetUppercase(this, true);
        UpdateCounter();
        AddHandler(PastingFromClipboardEvent, OnPasting);
    }

    protected override Type StyleKeyOverride => typeof(TextBox);

    public string? ByteLengths { get => GetValue(ByteLengthsProperty); set => SetValue(ByteLengthsProperty, value); }

    public int ByteCount
    {
        get;
        private set => SetAndRaise(ByteCountProperty, ref field, value);
    }

    private int MaxBytes => _lengths.Length > 0 ? _lengths.Max() : 0;

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = base.MeasureOverride(availableSize);
        if (MaxBytes == 0) return size;
        // Grow by however much a full value is wider than what is shown now (text, or the placeholder when empty).
        var shown = TextWidth(string.IsNullOrEmpty(Text) ? PlaceholderText : Text);
        var full = TextWidth(string.Join(' ', Enumerable.Repeat("00", MaxBytes)));
        var width = Math.Min(size.Width + Math.Max(0, full - shown), availableSize.Width);
        return new Size(Math.Max(size.Width, width), size.Height);
    }

    private double TextWidth(string? text) => string.IsNullOrEmpty(text)
        ? 0
        : new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily, FontStyle, FontWeight), FontSize, null).WidthIncludingTrailingWhitespace;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ByteLengthsProperty)
        {
            _lengths = (ByteLengths ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.TryParse(s, out var n) ? n : 0).Where(n => n > 0).ToArray();
            Normalize();
        }
        else if (change.Property == TextProperty)
        {
            if (!IsKeyboardFocusWithin) _undo.Clear();
            Normalize();
        }
        else if (_caretHold is { } hold && !_normalizing &&
                 (change.Property == SelectionStartProperty || change.Property == SelectionEndProperty ||
                  change.Property == CaretIndexProperty) &&
                 (SelectionStart != hold || SelectionEnd != hold))
        {
            // The Android IME puts the caret at "insert point + typed length" after handing
            // us the text, unaware of the separator we just added; keep ours instead.
            _normalizing = true;
            try { SelectionStart = SelectionEnd = CaretIndex = Math.Min(hold, (Text ?? string.Empty).Length); }
            finally { _normalizing = false; }
        }
        if (change.Property == ByteLengthsProperty || change.Property == PlaceholderTextProperty) InvalidateMeasure();
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        // Insert ourselves: TextBox would move the caret after we re-format.
        e.Handled = true;
        if (!IsReadOnly) Insert(e.Text ?? string.Empty);
    }

    private async void OnPasting(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (IsReadOnly || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        string? text;
        try { text = await clipboard.TryGetTextAsync(); }
        catch { return; }
        if (text is null) return;
        var trimmed = text.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[2..];
        Insert(trimmed);
    }

    /// <summary>Puts hex digits at the caret (replacing the selection); overwrites once the value is full.</summary>
    private void Insert(string input)
    {
        var digits = new string(input.Where(Uri.IsHexDigit).ToArray());
        if (digits.Length == 0) return;

        var text = Text ?? string.Empty;
        int start = Math.Min(SelectionStart, SelectionEnd), end = Math.Max(SelectionStart, SelectionEnd);
        start = Math.Clamp(start, 0, text.Length);
        end = Math.Clamp(end, start, text.Length);

        if (MaxBytes > 0)
        {
            var room = MaxBytes * 2 - DigitCount(text) + DigitCount(text[start..end]);
            for (var extra = digits.Length - room; extra > 0 && end < text.Length; end++)
                if (Uri.IsHexDigit(text[end])) extra--;
        }

        Remember();
        var before = text[..start];
        Normalize(before + digits + text[end..], DigitCount(before) + digits.Length);

        // Hold the caret until the input method has finished this edit.
        var posted = _caretHold is not null;
        _caretHold = CaretIndex;
        if (!posted) Dispatcher.UIThread.Post(() => _caretHold = null);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // TextBox's own undo doesn't see our re-formatting, so keep a simple history here.
        var cmd = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (cmd && e.Key == Key.Z)
        {
            e.Handled = true;
            _caretHold = null;
            if (_undo.Count > 0)
            {
                var (t, caret) = _undo[^1];
                _undo.RemoveAt(_undo.Count - 1);
                Text = t;
                SelectionStart = SelectionEnd = CaretIndex = Math.Min(caret, t.Length);
            }
            return;
        }
        if (e.Key is Key.Back or Key.Delete || (cmd && e.Key == Key.X)) Remember();

        // Step over the separator so Backspace / Delete remove a digit, not the space.
        var text = Text ?? string.Empty;
        if (SelectionStart == SelectionEnd)
        {
            if (e.Key == Key.Back && CaretIndex > 1 && text[CaretIndex - 1] == ' ') CaretIndex--;
            else if (e.Key == Key.Delete && CaretIndex < text.Length - 1 && text[CaretIndex] == ' ') CaretIndex++;
        }
        base.OnKeyDown(e);
    }

    private void Remember()
    {
        var text = Text ?? string.Empty;
        if (_undo.Count > 0 && _undo[^1].Text == text) return;
        _undo.Add((text, CaretIndex));
        if (_undo.Count > 50) _undo.RemoveAt(0);
    }

    private static int DigitCount(string s) => s.Count(Uri.IsHexDigit);

    /// <summary>
    /// Re-formats <paramref name="raw"/> as "AA BB CC" and, when given, puts the
    /// caret after that many digits.
    /// </summary>
    private void Normalize(string? raw = null, int? caretDigits = null)
    {
        if (_normalizing) return;
        var text = raw ?? Text ?? string.Empty;

        var clean = new string(text.Where(c => !char.IsWhiteSpace(c) && c != ':' && c != '-').ToArray());
        if (clean.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) clean = clean[2..];
        var digits = new string(clean.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        if (MaxBytes > 0 && digits.Length > MaxBytes * 2) digits = digits[..(MaxBytes * 2)];

        var sb = new StringBuilder(digits.Length * 3 / 2);
        for (var i = 0; i < digits.Length; i++)
        {
            if (i > 0 && i % 2 == 0) sb.Append(' ');
            sb.Append(digits[i]);
        }
        var formatted = sb.ToString();

        if (formatted != Text || caretDigits is not null)
        {
            var want = caretDigits ?? DigitCount(text[..Math.Min(CaretIndex, text.Length)]);
            _normalizing = true;
            try
            {
                if (formatted != Text) Text = formatted;
                int caret = 0, seen = 0;
                while (caret < formatted.Length && seen < want)
                    if (Uri.IsHexDigit(formatted[caret++])) seen++;
                // Hop over the separator after a complete byte so typing continues naturally.
                if (caret < formatted.Length && formatted[caret] == ' ' && seen % 2 == 0) caret++;
                SelectionStart = SelectionEnd = CaretIndex = caret;
            }
            finally { _normalizing = false; }
        }

        ByteCount = (digits.Length + 1) / 2;
        UpdateCounter(digits.Length);
    }

    private void UpdateCounter(int digits = 0)
    {
        var odd = digits % 2 != 0;
        var badLength = digits > 0 && _lengths.Length > 0 && !_lengths.Contains(digits / 2);
        PseudoClasses.Set(":invalid", odd || badLength);

        _counter.Text = digits == 0
            ? (_lengths.Length > 0 ? string.Join("/", _lengths) + " B" : string.Empty)
            : odd ? "½ byte" : $"{digits / 2} B";
        _counter.IsVisible = _counter.Text.Length > 0;
        ToolTip.SetTip(_counter, _lengths.Length > 0 ? $"Expected {string.Join(", ", _lengths)} bytes" : null);
    }
}
