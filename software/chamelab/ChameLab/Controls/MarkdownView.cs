using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;

namespace ChameLab.Controls;

/// <summary>
/// Shows the Markdown of release notes: headings, paragraphs, bullet and numbered lists,
/// fenced code, rules, and inline bold, italic, code and links. Enough for GitHub release
/// bodies, not a full CommonMark implementation.
/// </summary>
public partial class MarkdownView : StackPanel
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(Markdown));

    public static readonly StyledProperty<IBrush?> LinkForegroundProperty =
        AvaloniaProperty.Register<MarkdownView, IBrush?>(nameof(LinkForeground));

    public static readonly StyledProperty<IBrush?> CodeBackgroundProperty =
        AvaloniaProperty.Register<MarkdownView, IBrush?>(nameof(CodeBackground));

    // No Cascadia Code: its ligatures turn file names like "chamelab-<os>.zip" into arrows.
    private static readonly FontFamily Mono = new("Consolas,Menlo,monospace");

    static MarkdownView()
    {
        MarkdownProperty.Changed.AddClassHandler<MarkdownView>((v, _) => v.Rebuild());
        LinkForegroundProperty.Changed.AddClassHandler<MarkdownView>((v, _) => v.Rebuild());
        CodeBackgroundProperty.Changed.AddClassHandler<MarkdownView>((v, _) => v.Rebuild());
    }

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public IBrush? LinkForeground
    {
        get => GetValue(LinkForegroundProperty);
        set => SetValue(LinkForegroundProperty, value);
    }

    public IBrush? CodeBackground
    {
        get => GetValue(CodeBackgroundProperty);
        set => SetValue(CodeBackgroundProperty, value);
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.*?)\s*#*$")]
    private static partial Regex HeadingLine();

    [GeneratedRegex(@"^(?:-{3,}|\*{3,}|_{3,})$")]
    private static partial Regex RuleLine();

    [GeneratedRegex(@"^(\s*)([-*+]|\d+[.)])\s+(.*)$")]
    private static partial Regex ItemLine();

    // Code first, so nothing inside it is read as emphasis or a link.
    [GeneratedRegex(@"`([^`]+)`|\*\*(.+?)\*\*|__(.+?)__|(?<![\w*])\*(?!\s)(.+?)(?<!\s)\*(?![\w*])|(?<![\w_])_(?!\s)(.+?)(?<!\s)_(?![\w_])|\[([^\]]+)\]\(([^)\s]+)\)|(https?://[^\s<>()]+[^\s<>().,;:!?'""])")]
    private static partial Regex InlineToken();

    private enum Block { Paragraph, Item }

    private void Rebuild()
    {
        Children.Clear();
        var lines = (Markdown ?? string.Empty).Replace("\r\n", "\n").Split('\n');

        // The paragraph or list item being collected: following lines continue it until a blank line.
        Block? open = null;
        var text = new List<string>();
        var marker = string.Empty;
        var indent = 0;
        var tightAfterItem = false;

        void Close()
        {
            if (open is null) return;
            var inline = string.Join(" ", text);
            Add(open == Block.Item ? Item(marker, indent, inline) : Paragraph(inline), open == Block.Item && tightAfterItem);
            tightAfterItem = open == Block.Item;
            open = null;
            text.Clear();
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                Close();
                var code = new List<string>();
                while (++i < lines.Length && !lines[i].Trim().StartsWith("```", StringComparison.Ordinal)) code.Add(lines[i]);
                Add(CodeBlock(string.Join("\n", code)));
                tightAfterItem = false;
            }
            else if (trimmed.Length == 0)
            {
                Close();
                // A blank line between list items still keeps the list together.
            }
            else if (HeadingLine().Match(trimmed) is { Success: true } heading)
            {
                Close();
                Add(Heading(heading.Groups[1].Length, heading.Groups[2].Value));
                tightAfterItem = false;
            }
            else if (RuleLine().IsMatch(trimmed))
            {
                Close();
                Add(new Separator { Margin = new Thickness(0, 4) });
                tightAfterItem = false;
            }
            else if (ItemLine().Match(line) is { Success: true } item)
            {
                Close();
                open = Block.Item;
                indent = item.Groups[1].Value.Replace("\t", "    ").Length / 2;
                marker = char.IsDigit(item.Groups[2].Value[0]) ? item.Groups[2].Value : "•";
                text.Add(item.Groups[3].Value.Trim());
            }
            else
            {
                if (open is null)
                {
                    open = Block.Paragraph;
                    tightAfterItem = false;
                }
                text.Add(trimmed.TrimStart('>').Trim());
            }
        }
        Close();
    }

    private void Add(Control block, bool tight = false)
    {
        if (Children.Count > 0) block.Margin += new Thickness(0, tight ? 3 : 8, 0, 0);
        Children.Add(block);
    }

    private Control Paragraph(string markdown) => TextBlock(markdown);

    private Control Heading(int level, string markdown)
    {
        var block = TextBlock(markdown);
        block.FontWeight = FontWeight.SemiBold;
        block.FontSize = level switch { 1 => 17, 2 => 15, _ => 14 };
        return block;
    }

    private Control Item(string marker, int indent, string markdown)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(indent * 16, 0, 0, 0),
        };
        var bullet = new TextBlock { Text = marker, MinWidth = 14, Margin = new Thickness(2, 0, 6, 0) };
        var content = TextBlock(markdown);
        Grid.SetColumn(content, 1);
        grid.Children.Add(bullet);
        grid.Children.Add(content);
        return grid;
    }

    private Control CodeBlock(string code) => new Border
    {
        Background = CodeBackground,
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(10, 8),
        Child = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = new SelectableTextBlock { Text = code, FontFamily = Mono },
        },
    };

    private LinkTextBlock TextBlock(string markdown)
    {
        var block = new LinkTextBlock { TextWrapping = TextWrapping.Wrap };
        AddInlines(block, block.Inlines!, markdown, null, null);
        return block;
    }

    private void AddInlines(LinkTextBlock block, InlineCollection target, string markdown, FontWeight? weight, FontStyle? style)
    {
        var at = 0;
        foreach (Match m in InlineToken().Matches(markdown))
        {
            if (m.Index > at) target.Add(Run(block, markdown[at..m.Index], weight, style));
            var g = m.Groups;
            if (g[1].Success)
            {
                var run = Run(block, g[1].Value, weight, style);
                run.FontFamily = Mono;
                run.Background = CodeBackground;
                target.Add(run);
            }
            else if (g[2].Success || g[3].Success)
                AddInlines(block, target, g[2].Success ? g[2].Value : g[3].Value, FontWeight.SemiBold, style);
            else if (g[4].Success || g[5].Success)
                AddInlines(block, target, g[4].Success ? g[4].Value : g[5].Value, weight, FontStyle.Italic);
            else
            {
                var url = g[7].Success ? g[7].Value : g[8].Value;
                var start = block.Length;
                var run = Run(block, g[6].Success ? g[6].Value : url, weight, style);
                run.Foreground = LinkForeground;
                run.TextDecorations = TextDecorations.Underline;
                target.Add(run);
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri)) block.Links.Add((start, block.Length, uri));
            }
            at = m.Index + m.Length;
        }
        if (at < markdown.Length) target.Add(Run(block, markdown[at..], weight, style));
    }

    /// <summary>A run that inherits the block's font unless emphasis overrides it.</summary>
    private static Run Run(LinkTextBlock block, string text, FontWeight? weight, FontStyle? style)
    {
        block.Length += text.Length;
        var run = new Run(text);
        if (weight is { } w) run.FontWeight = w;
        if (style is { } s) run.FontStyle = s;
        return run;
    }

    /// <summary>Selectable text whose link runs open in the browser on a click that selected nothing.</summary>
    private sealed class LinkTextBlock : SelectableTextBlock
    {
        private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

        protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

        /// <summary>Character ranges of the links, in the order the runs were added.</summary>
        public List<(int Start, int End, Uri Uri)> Links { get; } = new();

        public int Length { get; set; }

        private Uri? LinkAt(Point point)
        {
            if (Links.Count == 0) return null;
            var hit = TextLayout.HitTestPoint(point - new Point(Padding.Left, Padding.Top));
            if (!hit.IsInside) return null;
            foreach (var (start, end, uri) in Links)
                if (hit.TextPosition >= start && hit.TextPosition < end) return uri;
            return null;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (Links.Count == 0) return;
            if (LinkAt(e.GetPosition(this)) is null) ClearValue(CursorProperty);
            else Cursor = HandCursor;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (e.InitialPressMouseButton != MouseButton.Left || SelectionStart != SelectionEnd) return;
            if (LinkAt(e.GetPosition(this)) is { } uri && TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
            {
                e.Handled = true;
                _ = launcher.LaunchUriAsync(uri);
            }
        }
    }
}
