using System;
using System.IO;
using System.Linq;
using System.Text;
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
/// last <see cref="ConsoleLimit"/> characters.
/// </summary>
public sealed partial class ToolsViewModel
{
    private const int ConsoleLimit = 400_000;

    private readonly StringBuilder _console = new();
    private readonly object _consoleSync = new();
    private readonly CommandHistory _history = new(OperatingSystem.IsBrowser() ? null : CommandHistory.DefaultFile);
    private CliSession? _session;
    private CancellationTokenSource? _consoleCts;
    private TaskCompletionSource<string?>? _consoleLine;
    private bool _consoleFlushPosted;
    private bool _consoleCarriage;
    private int _historyIndex = -1;

    /// <summary>A terminal-like tool fits a desktop; phones and the browser get it later.</summary>
    public static bool IsConsoleAvailable => OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    public bool IsConsole => _tool == SniffTool.Console;

    public ICommand OpenConsoleCommand { get; private set; } = null!;
    public ICommand RunConsoleCommand { get; private set; } = null!;
    public ICommand CancelConsoleCommand { get; private set; } = null!;
    public ICommand ClearConsoleCommand { get; private set; } = null!;

    /// <summary>What the commands printed.</summary>
    public string ConsoleText
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    /// <summary>The line being typed.</summary>
    public string ConsoleInput
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    /// <summary>A command is running; Enter then answers what it asks for, if anything.</summary>
    public bool IsConsoleRunning
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public string ConsoleFolderText => string.Format(Strings.Tools_ConsoleFolder, AppCliHost.Folder);

    private void InitConsole()
    {
        OpenConsoleCommand = ReactiveCommand.Create(() => { Tool = SniffTool.Console; });
        RunConsoleCommand = ReactiveCommand.Create(SubmitConsoleLine);
        CancelConsoleCommand = ReactiveCommand.Create(CancelConsole);
        ClearConsoleCommand = ReactiveCommand.Create(ClearConsole);
    }

    private CliSession Session => _session ??= new CliSession(
        new ConsoleWriter(this), new ConsoleWriter(this),
        new AppCliHost(_device, ReadConsoleLineAsync,
            () => Dispatcher.UIThread.Post(ClearConsole),
            (samples, offset) =>
            {
                Dispatcher.UIThread.Post(() => ShowConsoleCapture(samples, offset));
                return Task.CompletedTask;
            }));

    /// <summary>Enter: runs the line, or hands it to the running command that asked for one.</summary>
    private void SubmitConsoleLine()
    {
        var line = ConsoleInput;
        ConsoleInput = "";
        _historyIndex = -1;
        var waiting = _consoleLine;
        _consoleLine = null;
        // A command canceled while it waited has nothing to hand the line to: it runs as a command.
        if (waiting is { Task.IsCompleted: false })
        {
            AppendConsole(line + "\n");
            waiting.TrySetResult(line);
            return;
        }
        if (IsConsoleRunning) return;
        AppendConsole("> " + line + "\n");
        if (string.IsNullOrWhiteSpace(line)) return;
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
        }
    }

    private Task<string?> ReadConsoleLineAsync(CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => tcs.TrySetCanceled(ct));
        Dispatcher.UIThread.Post(() => _consoleLine = tcs);
        return tcs.Task;
    }

    private void CancelConsole() => _consoleCts?.Cancel();

    private void ClearConsole()
    {
        lock (_consoleSync)
        {
            _console.Clear();
            _consoleCarriage = false;
        }
        ConsoleText = "";
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
        if (matches.Length > 1) AppendConsole(string.Join("  ", matches) + "\n");
        if (completion.Length <= prefix.Length) return caret;
        ConsoleInput = completion + text[caret..];
        return completion.Length;
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

    /// <summary>Adds output; from any thread, shown on the UI thread at most once a frame.</summary>
    private void AppendConsole(string text)
    {
        lock (_consoleSync)
        {
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
            }
            if (_consoleFlushPosted) return;
            _consoleFlushPosted = true;
        }
        Dispatcher.UIThread.Post(() =>
        {
            lock (_consoleSync)
            {
                _consoleFlushPosted = false;
                ConsoleText = _console.ToString();
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
