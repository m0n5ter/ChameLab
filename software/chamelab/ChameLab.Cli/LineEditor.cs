using System.Text;

namespace ChameLab.Cli;

internal sealed class LineEditor
{
    private readonly CommandHistory _commands = new(CommandHistory.DefaultFile);
    private IReadOnlyList<string> History => _commands.Lines;

    public string? Read(string prompt)
    {
        var previous = Console.TreatControlCAsInput;
        Console.TreatControlCAsInput = true;
        try { return ReadLine(prompt); }
        finally { Console.TreatControlCAsInput = previous; }
    }

    private string? ReadLine(string prompt)
    {
        Console.Write(prompt);
        var text = new StringBuilder(); var cursor = 0; var history = History.Count;
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key == ConsoleKey.C)
            { Console.WriteLine("^C"); return ""; }
            if (key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key is ConsoleKey.D or ConsoleKey.Z && text.Length == 0)
            { Console.WriteLine(); return null; }
            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    Console.WriteLine(); var line = text.ToString();
                    _commands.Add(line);
                    return line;
                case ConsoleKey.LeftArrow: cursor = Math.Max(0, cursor - 1); break;
                case ConsoleKey.RightArrow: cursor = Math.Min(text.Length, cursor + 1); break;
                case ConsoleKey.Home: cursor = 0; break;
                case ConsoleKey.End: cursor = text.Length; break;
                case ConsoleKey.Backspace: if (cursor > 0) text.Remove(--cursor, 1); break;
                case ConsoleKey.Delete: if (cursor < text.Length) text.Remove(cursor, 1); break;
                case ConsoleKey.UpArrow:
                    if (history > 0) { text.Clear().Append(History[--history]); cursor = text.Length; } break;
                case ConsoleKey.DownArrow:
                    if (history < History.Count) { history++; text.Clear().Append(history == History.Count ? "" : History[history]); cursor = text.Length; } break;
                case ConsoleKey.Tab:
                    var prefix = text.ToString(0, cursor);
                    var matches = Complete(prefix);
                    if (matches.Length == 1)
                    {
                        var suffix = text.ToString(cursor, text.Length - cursor);
                        text.Clear().Append(matches[0]).Append(' '); cursor = text.Length; text.Append(suffix);
                    }
                    else if (matches.Length > 1) { Console.WriteLine(); Console.WriteLine(string.Join("  ", matches)); }
                    break;
                default: if (!char.IsControl(key.KeyChar)) text.Insert(cursor++, key.KeyChar); break;
            }
            // Keep one physical line, scrolling horizontally instead of wrapping and corrupting history.
            var width = Math.Max(1, Console.WindowWidth - 1);
            var shownPrompt = prompt[..Math.Min(prompt.Length, Math.Max(0, width - 1))];
            var capacity = width - shownPrompt.Length;
            var start = Math.Max(0, cursor - capacity + 1);
            var shown = text.ToString(start, Math.Min(capacity, text.Length - start));
            Console.Write('\r' + shownPrompt + shown + new string(' ', capacity - shown.Length));
            Console.Write('\r' + shownPrompt + text.ToString(start, cursor - start));
        }
    }

    internal static string[] Complete(string prefix) => CommandCatalog.Complete(prefix);
}
