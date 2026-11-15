using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using ChameLab.Cli;
using ChameLab.Localization;
using ChameLab.Services;
using ReactiveUI;

namespace ChameLab.ViewModels;

/// <summary>
/// The console: the command line client's commands, run over the app's own link. While one runs it has the device
/// to itself (the app's pages wait for it); afterwards they read the device again when shown. Output is kept to the
/// last <see cref="ConsoleLimit"/> characters; the lines typed are marked in it so the view can tell them apart.
/// </summary>
public sealed partial class ToolsViewModel
{
    private const int ConsoleLimit = 200_000;
    private const string WrapSetting = "consoleWrap";
    private const string AutoscrollSetting = "consoleAutoscroll";
    private const string FolderSetting = "consoleFolder";

    private readonly StringBuilder _console = new();
    // Where the typed lines are in _console, oldest first.
    private readonly List<ConsoleSpan> _typed = new();
    private readonly object _consoleSync = new();
    private readonly CommandHistory _history = new(OperatingSystem.IsBrowser() ? null : CommandHistory.DefaultFile);
    private CliSession? _session;
    private CancellationTokenSource? _consoleCts;
    private TaskCompletionSource<string?>? _consoleLine;
    private bool _consoleFlushPosted;
    private bool _consoleCarriage;
    private int _historyIndex = -1;

    /// <summary>A phone or tablet: no Tab key, and a soft keyboard that should not pop up unasked.</summary>
    public static bool IsTouchConsole => OperatingSystem.IsAndroid() || OperatingSystem.IsIOS();

    /// <summary>
    /// A folder can be picked only where the system's picker hands back a real path: on the desktop. On phones and
    /// in the browser the commands' files stay in the app's own storage.
    /// </summary>
    public static bool CanChooseConsoleFolder => !OperatingSystem.IsBrowser() && !IsTouchConsole;

    /// <summary>Where the commands' files go when that is not a folder of the user's choosing; empty on the desktop.</summary>
    public static string ConsoleFilesNote =>
        OperatingSystem.IsBrowser() ? Strings.Tools_ConsoleFilesBrowser
        : IsTouchConsole ? Strings.Tools_ConsoleFilesInApp
        : "";

    public static bool HasConsoleFilesNote => ConsoleFilesNote.Length > 0;

    public static string ConsolePlaceholder =>
        IsTouchConsole ? Strings.Tools_ConsolePlaceholderTouch : Strings.Tools_ConsolePlaceholder;

    public bool IsConsole => _tool == SniffTool.Console;

    public ICommand OpenConsoleCommand { get; private set; } = null!;
    public ICommand RunConsoleCommand { get; private set; } = null!;
    public ICommand CancelConsoleCommand { get; private set; } = null!;
    public ICommand ClearConsoleCommand { get; private set; } = null!;

    public ICommand ChooseConsoleFolderCommand { get; private set; } = null!;
    public ICommand ResetConsoleFolderCommand { get; private set; } = null!;

    /// <summary>What was typed and what the commands printed.</summary>
    public ConsoleOutput ConsoleText
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = ConsoleOutput.Empty;

    /// <summary>The line being typed.</summary>
    public string ConsoleInput
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            (ConsoleHintWords, ConsoleHint) = HintFor(value);
        }
    } = "";

    /// <summary>What may come next in the line being typed: the command words or options, one badge each.</summary>
    public string[] ConsoleHintWords
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = HintFor("").Words;

    /// <summary>Once nothing more can be added: what the command does.</summary>
    public string ConsoleHint
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    /// <summary>Long lines wrap; otherwise the output scrolls sideways.</summary>
    public bool ConsoleWrap
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            AppSettings.Set(WrapSetting, value);
        }
    } = ReadFlag(WrapSetting, true);

    /// <summary>The output follows the newest line.</summary>
    public bool ConsoleAutoscroll
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            AppSettings.Set(AutoscrollSetting, value);
        }
    } = ReadFlag(AutoscrollSetting, true);

    /// <summary>Where file names without a full path point.</summary>
    public string ConsoleFolder
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(ConsoleFolderText));
            this.RaisePropertyChanged(nameof(IsConsoleFolderChosen));
        }
    } = CanChooseConsoleFolder && AppSettings.GetString(FolderSetting) is { Length: > 0 } saved && Path.IsPathRooted(saved)
        ? saved
        : AppCliHost.DefaultFolder;

    public bool IsConsoleFolderChosen => ConsoleFolder != AppCliHost.DefaultFolder;

    /// <summary>A command is running; Enter then answers what it asks for, if anything.</summary>
    public bool IsConsoleRunning
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>Cancel was pressed; the command ends once the Chameleon answers the request it is working on.</summary>
    public bool IsConsoleStopping
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(ConsoleCancelLabel));
        }
    }

    public string ConsoleCancelLabel => IsConsoleStopping ? Strings.Common_Stopping : Strings.Common_Cancel;

    public string ConsoleFolderText => string.Format(Strings.Tools_ConsoleFolder, ConsoleFolder);

    public static string ConsoleRunTip => Strings.Tools_ConsoleRun + " (Enter)";
    public static string ConsoleCancelTip => Strings.Common_Cancel + " (Ctrl+C)";
    public static string ConsoleClearTip => Strings.Common_Clear + " (Esc)";

    private static bool ReadFlag(string name, bool fallback) =>
        AppSettings.Get(name) is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : fallback;

    private void InitConsole()
    {
        OpenConsoleCommand = ReactiveCommand.Create(() => { Tool = SniffTool.Console; });
        // Run stays off while the line is blank: there is nothing to run.
        RunConsoleCommand = ReactiveCommand.Create(SubmitConsoleLine,
            this.WhenAnyValue(x => x.ConsoleInput, line => !string.IsNullOrWhiteSpace(line)));
        CancelConsoleCommand = ReactiveCommand.Create(CancelConsole, this.WhenAnyValue(x => x.IsConsoleStopping, stopping => !stopping));
        ClearConsoleCommand = ReactiveCommand.Create(ClearConsole);
        // The folder stays put while a command may be writing to it.
        var idleConsole = this.WhenAnyValue(x => x.IsConsoleRunning, running => !running);
        ChooseConsoleFolderCommand = ReactiveCommand.CreateFromTask(ChooseConsoleFolderAsync, idleConsole);
        ResetConsoleFolderCommand = ReactiveCommand.Create(() => SetConsoleFolder(AppCliHost.DefaultFolder), idleConsole);
    }

    private async Task ChooseConsoleFolderAsync()
    {
        string? picked;
        try { picked = await StorageService.PickFolderAsync(Strings.Tools_ConsoleChooseFolder, ConsoleFolder); }
        catch { return; /* no folder picker here */ }
        if (picked is { Length: > 0 }) SetConsoleFolder(picked);
    }

    private void SetConsoleFolder(string folder)
    {
        ConsoleFolder = folder;
        AppSettings.Set(FolderSetting, folder == AppCliHost.DefaultFolder ? null : folder);
    }

    private CliSession Session => _session ??= new CliSession(
        new ConsoleWriter(this), new ConsoleWriter(this),
        new AppCliHost(_device, ReadConsoleLineAsync,
            () => Dispatcher.UIThread.Post(ClearConsole),
            (samples, offset) =>
            {
                Dispatcher.UIThread.Post(() => ShowConsoleCapture(samples, offset));
                return Task.CompletedTask;
            },
            () => ConsoleFolder));

    /// <summary>
    /// Enter: hands the line to the running command that asked for one (blank answers too), or runs it. A blank
    /// line is not a command, and while one runs the line waits for it to end.
    /// </summary>
    public void SubmitConsoleLine()
    {
        var line = ConsoleInput;
        var waiting = _consoleLine;
        // A command canceled while it waited has nothing to hand the line to: it runs as a command.
        if (waiting is { Task.IsCompleted: false })
        {
            _consoleLine = null;
            ConsoleInput = "";
            _historyIndex = -1;
            AppendConsole(line + "\n", typed: true);
            waiting.TrySetResult(line);
            return;
        }
        if (IsConsoleRunning || string.IsNullOrWhiteSpace(line)) return;
        _consoleLine = null;
        ConsoleInput = "";
        _historyIndex = -1;
        AppendConsole("> " + line + "\n", typed: true);
        _history.Add(line);
        _ = RunConsoleAsync(line);
    }

    private async Task RunConsoleAsync(string line)
    {
        using var cts = new CancellationTokenSource();
        _consoleCts = cts;
        IsConsoleRunning = true;
        try
        {
            var session = Session;
            // Off the UI thread: commands may compute for long (key recovery) between device calls.
            await RunAwakeAsync(() => Task.Run(() => session.ExecuteAsync(line, cts.Token)));
            if (session.ExitRequested)
            {
                _session = null;
                Tool = SniffTool.None;
            }
        }
        finally
        {
            _consoleCts = null;
            _consoleLine?.TrySetResult(null);
            _consoleLine = null;
            IsConsoleRunning = false;
            IsConsoleStopping = false;
        }
    }

    private Task<string?> ReadConsoleLineAsync(CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => tcs.TrySetCanceled(ct));
        Dispatcher.UIThread.Post(() => _consoleLine = tcs);
        return tcs.Task;
    }

    /// <summary>
    /// Cancel: says so at once, since the Chameleon cannot drop a command it has started (a batch of keys, a
    /// nonce collection) and the command ends only once it answers.
    /// </summary>
    private void CancelConsole()
    {
        if (_consoleCts is not { IsCancellationRequested: false } cts) return;
        IsConsoleStopping = true;
        AppendConsole("Stopping: the Chameleon finishes the step it is on first.\n");
        cts.Cancel();
    }

    private void ClearConsole()
    {
        lock (_consoleSync)
        {
            _console.Clear();
            _typed.Clear();
            _consoleCarriage = false;
        }
        ConsoleText = ConsoleOutput.Empty;
    }

    /// <summary><c>data plot</c>: the samples open in Sniff LF.</summary>
    private void ShowConsoleCapture(byte[] samples, int offset)
    {
        Samples = samples;
        Source = offset == 0 ? Strings.Tools_FromTheConsole : $"{Strings.Tools_FromTheConsole}, +{offset}";
        Tool = SniffTool.Lf;
    }

    /// <summary>Up and Down: walks the commands typed before; past the newest, an empty line.</summary>
    public void ConsoleHistory(int step)
    {
        var lines = _history.Lines;
        if (lines.Count == 0) return;
        var index = _historyIndex < 0 ? lines.Count : _historyIndex;
        index = Math.Clamp(index + step, 0, lines.Count);
        _historyIndex = index == lines.Count ? -1 : index;
        ConsoleInput = index == lines.Count ? "" : lines[index];
    }

    /// <summary>Tab: completes the command or option at <paramref name="caret"/>; returns the new caret.</summary>
    public int CompleteConsole(int caret)
    {
        var text = ConsoleInput;
        caret = Math.Clamp(caret, 0, text.Length);
        var prefix = text[..caret];
        var matches = CommandCatalog.Complete(prefix);
        if (matches.Length == 0) return caret;
        var completion = matches.Length == 1 ? matches[0] + " " : CommonPrefix(matches);
        if (completion.Length <= prefix.Length) return caret;
        ConsoleInput = completion + text[caret..];
        return completion.Length;
    }

    /// <summary>
    /// A hint badge clicked: its word completes the word being typed, or follows the line when that word is
    /// whole. An option goes in by its long name.
    /// </summary>
    public void UseConsoleHint(string hint)
    {
        var word = hint.TrimEnd('*').Split(", ")[^1];
        var text = ConsoleInput;
        var last = text.LastIndexOf(' ') + 1;
        var current = text[last..];
        var line = current.Length > 0 && word.StartsWith(current, StringComparison.Ordinal) && word != current
            ? text[..last] + word
            : text.Length == 0 || text.EndsWith(' ') ? text + word : text + " " + word;
        ConsoleInput = line + " ";
        _historyIndex = -1;
    }

    private static string CommonPrefix(string[] items)
    {
        var first = items[0];
        var length = first.Length;
        foreach (var item in items.Skip(1))
        {
            length = Math.Min(length, item.Length);
            for (var i = 0; i < length; i++)
                if (item[i] != first[i]) { length = i; break; }
        }
        return first[..length];
    }

    /// <summary>
    /// The hint line: the words that may come next (commands, or the command's options, required ones starred);
    /// once nothing more can be added, what the command does.
    /// </summary>
    internal static (string[] Words, string Text) HintFor(string text)
    {
        text = text.TrimStart();
        var words = NextWords(text);
        var current = text[(text.LastIndexOf(' ') + 1)..];
        // A whole word typed: what may follow it.
        if (current.Length > 0 && words is [var only] && only == current) words = NextWords(text + " ");
        if (words.Length > 0) return (words, "");
        var (command, _) = CommandCatalog.Find(text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return ([], command?.Description.Split('\n')[0] ?? "");
    }

    private static string[] NextWords(string text)
    {
        var last = text.LastIndexOf(' ') + 1;
        var paths = CommandCatalog.Commands.Select(c => c.Name).Where(n => n.StartsWith(text, StringComparison.Ordinal)).ToArray();
        if (paths.Length > 0)
            return paths.Select(p => p[last..].Split(' ')[0]).Where(w => w.Length > 0).Distinct().ToArray();
        var (command, _) = CommandCatalog.Find(text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var current = text[last..];
        // A value being typed: nothing to suggest.
        if (command is null || (current.Length > 0 && !current.StartsWith('-'))) return [];
        return command.Options.Where(o => o.Names.Any(n => n.StartsWith(current, StringComparison.Ordinal)))
            .Select(o => o.Required ? o.Label + "*" : o.Label).ToArray();
    }

    /// <summary>Adds output, or a line the user <paramref name="typed"/>; from any thread, shown on the UI thread at most once a frame.</summary>
    private void AppendConsole(string text, bool typed = false)
    {
        lock (_consoleSync)
        {
            if (typed)
            {
                // Output left on the line (a prompt) stays; the typed text follows it.
                _consoleCarriage = false;
                _typed.Add(new ConsoleSpan(_console.Length, text.TrimEnd('\n').Length));
            }
            foreach (var c in text)
            {
                // A lone carriage return (progress redrawn in place): the next text replaces the line.
                if (_consoleCarriage && c != '\n')
                {
                    var start = LastLineStart();
                    _console.Length = start;
                }
                _consoleCarriage = c == '\r';
                if (c != '\r') _console.Append(c);
            }
            if (_console.Length > ConsoleLimit)
            {
                var cut = _console.Length - ConsoleLimit * 3 / 4;
                for (var i = cut; i < _console.Length; i++)
                    if (_console[i] == '\n') { cut = i + 1; break; }
                _console.Remove(0, cut);
                for (var i = _typed.Count - 1; i >= 0; i--)
                {
                    var span = _typed[i];
                    if (span.Start + span.Length <= cut) { _typed.RemoveRange(0, i + 1); break; }
                    _typed[i] = span.Start >= cut ? span with { Start = span.Start - cut }
                        : new ConsoleSpan(0, span.Length - (cut - span.Start));
                }
            }
            if (_consoleFlushPosted) return;
            _consoleFlushPosted = true;
        }
        Dispatcher.UIThread.Post(() =>
        {
            lock (_consoleSync)
            {
                _consoleFlushPosted = false;
                ConsoleText = new ConsoleOutput(_console.ToString(), _typed.ToArray());
            }
        }, DispatcherPriority.Background);
    }

    private int LastLineStart()
    {
        for (var i = _console.Length - 1; i >= 0; i--)
            if (_console[i] == '\n') return i + 1;
        return 0;
    }

    /// <summary>The session's output and errors, into the console.</summary>
    private sealed class ConsoleWriter(ToolsViewModel owner) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) => owner.AppendConsole(value.ToString());
        public override void Write(string? value) { if (!string.IsNullOrEmpty(value)) owner.AppendConsole(value); }
        public override void Write(char[] buffer, int index, int count) => owner.AppendConsole(new string(buffer, index, count));
        public override void WriteLine(string? value) => owner.AppendConsole(value + "\n");
    }
}

/// <summary>Part of the console's text: <see cref="Start"/> and <see cref="Length"/> in characters.</summary>
public readonly record struct ConsoleSpan(int Start, int Length);

/// <summary>The console's text and where in it the typed lines are.</summary>
public sealed record ConsoleOutput(string Text, ConsoleSpan[] Typed)
{
    public static ConsoleOutput Empty { get; } = new("", []);
}
