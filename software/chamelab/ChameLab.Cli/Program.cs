using System.Globalization;
using System.Text;

namespace ChameLab.Cli;

public static class Program
{
    [STAThread]
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--plot-capture")
        {
            Plot.WaveformApplication.Run(args[1], int.Parse(args[2], CultureInfo.InvariantCulture));
            return 0;
        }
        Console.OutputEncoding = Encoding.UTF8;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        if (args.Length == 1 && args[0] is "-h" or "--help")
        {
            Console.WriteLine("ChameLab CLI\nchamelab [-c|--command \"COMMAND\"]...\nWithout -c, starts an interactive console. Ctrl+C cancels the current operation.\nUse dump_help to list commands; COMMAND --help lists its options.");
            return 0;
        }
        var commands = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "-c" or "--command" && i + 1 < args.Length) commands.Add(args[++i]);
            else if (args[i].StartsWith("--command=", StringComparison.Ordinal)) commands.Add(args[i][10..]);
            else { Console.Error.WriteLine("Expected -c/--command followed by a command, or --help."); return 2; }
        }
        if (commands.Count == 0 && Console.IsInputRedirected)
        { Console.Error.WriteLine("An interactive terminal is required unless --command is used."); return 2; }
        await using var session = new CliSession();
        CancellationTokenSource? active = null;
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; active?.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            if (commands.Count > 0)
            {
                foreach (var command in commands)
                {
                    using var cts = new CancellationTokenSource(); active = cts;
                    var code = await session.ExecuteAsync(command, cts.Token); active = null;
                    if (code != 0 || session.ExitRequested) return code;
                }
                return 0;
            }
            Console.WriteLine("ChameLab — type help for commands, exit to quit.");
            var editor = new LineEditor();
            while (!session.ExitRequested)
            {
                var line = editor.Read(session.Connected ? "[USB/TCP] chameleon --> " : "[Offline] chameleon --> ");
                if (line is null) break;
                using var cts = new CancellationTokenSource(); active = cts;
                await session.ExecuteAsync(line, cts.Token); active = null;
            }
            return 0;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }
}
