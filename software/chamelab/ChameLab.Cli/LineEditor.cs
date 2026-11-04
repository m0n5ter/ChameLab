using System.Text;

namespace ChameLab.Cli;

internal sealed class LineEditor
{
    private readonly string _historyFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".chamelab_history");
    private readonly List<string> _history = [];
    public LineEditor()
    {
        try { if (File.Exists(_historyFile)) _history.AddRange(File.ReadLines(_historyFile).TakeLast(1000)); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public string? Read(string prompt)
    {
        Console.Write(prompt);
        var text = new StringBuilder(); var cursor = 0; var history = _history.Count; var painted = 0;
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key is ConsoleKey.D or ConsoleKey.Z && text.Length == 0)
            { Console.WriteLine(); return null; }
            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    Console.WriteLine(); var line = text.ToString();
                    if (line.Length > 0)
                    {
                        _history.Add(line);
                        try { File.WriteAllLines(_historyFile, _history.TakeLast(1000)); }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                    return line;
                case ConsoleKey.LeftArrow: cursor = Math.Max(0, cursor - 1); break;
                case ConsoleKey.RightArrow: cursor = Math.Min(text.Length, cursor + 1); break;
                case ConsoleKey.Home: cursor = 0; break;
                case ConsoleKey.End: cursor = text.Length; break;
                case ConsoleKey.Backspace: if (cursor > 0) text.Remove(--cursor, 1); break;
                case ConsoleKey.Delete: if (cursor < text.Length) text.Remove(cursor, 1); break;
                case ConsoleKey.UpArrow:
                    if (history > 0) { text.Clear().Append(_history[--history]); cursor = text.Length; } break;
                case ConsoleKey.DownArrow:
                    if (history < _history.Count) { history++; text.Clear().Append(history == _history.Count ? "" : _history[history]); cursor = text.Length; } break;
                case ConsoleKey.Tab:
                    var prefix = text.ToString(0, cursor);
                    var matches = Complete(prefix);
                    if (matches.Length == 1) { text.Clear().Append(matches[0]).Append(' '); cursor = text.Length; }
                    else if (matches.Length > 1) { Console.WriteLine(); Console.WriteLine(string.Join("  ", matches)); painted = 0; }
                    break;
                default: if (!char.IsControl(key.KeyChar)) text.Insert(cursor++, key.KeyChar); break;
            }
            Console.Write('\r' + prompt + text + new string(' ', Math.Max(0, painted - text.Length)));
            painted = text.Length;
            // ANSI cursor movement also handles command lines wider than the terminal.
            if (cursor < text.Length) Console.Write($"\u001b[{text.Length - cursor}D");
        }
    }

    internal static string[] Complete(string prefix)
    {
        var paths = CommandCatalog.Commands.Select(c => c.Name).Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        if (paths.Length > 0) return paths;
        var (command, _) = CommandCatalog.Find(prefix.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (command is null) return [];
        var last = prefix.LastIndexOf(' ') + 1;
        return command.Options.SelectMany(o => o.Names).Where(n => n.StartsWith(prefix[last..], StringComparison.Ordinal))
            .Select(n => prefix[..last] + n).ToArray();
    }
}
