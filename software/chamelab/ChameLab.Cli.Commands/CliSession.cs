using System.Buffers.Binary;
using System.Text.Json;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;
using ChameLab.Transport;

namespace ChameLab.Cli;

public sealed partial class CliSession : IAsyncDisposable
{
    private readonly Dictionary<string, Func<CliArguments, CancellationToken, Task>> _handlers = new();
    private readonly bool _ownsHost;
    public TextWriter Output { get; }
    public TextWriter Error { get; }
    /// <summary>Where the session runs: its device link, typed input and files.</summary>
    public CliHost Host { get; }
    private ChameleonClient Client => Host.Connection is { IsClosed: false } c ? new(c)
        : throw new InvalidOperationException("Please connect to chameleon device first (use 'hw connect').");
    public bool Connected => Host.Connection is { IsClosed: false, IsConnected: true };
    public bool ExitRequested { get; private set; }
    private byte[]? _lfCapture;
    public IReadOnlyCollection<string> ImplementedCommands => _handlers.Keys;

    /// <param name="host">Where the session runs; without one it owns its own link, closed when the session is disposed.</param>
    public CliSession(TextWriter? output = null, TextWriter? error = null, CliHost? host = null)
    {
        Output = output ?? Console.Out; Error = error ?? Console.Error;
        _ownsHost = host is null;
        Host = host ?? new StandaloneCliHost();
        Add("exit", (_, _) => { ExitRequested = true; return Task.CompletedTask; });
        Add("clear", (_, _) => { Host.Clear(); return Task.CompletedTask; });
        Add("rem", (a, _) => { Output.WriteLine($"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.ffffff}Z remark: {string.Join(' ', a.Many("comment"))}"); return Task.CompletedTask; });
        Add("dump_help", (a, _) =>
        {
            if (a.Flag("show_groups"))
                foreach (var group in CommandCatalog.Commands.SelectMany(c =>
                    Enumerable.Range(1, c.Name.Split(' ').Length - 1).Select(n => string.Join(' ', c.Name.Split(' ').Take(n)))).Distinct())
                    Output.WriteLine($"== {group} ==");
            if (a.Flag("show_desc")) foreach (var c in CommandCatalog.Commands) CommandCatalog.Help(Output, c);
            else CommandCatalog.Help(Output);
            return Task.CompletedTask;
        });
        RegisterHardware();
        RegisterLf();
        RegisterHf();
        RegisterMifare();
        RegisterUltralight();
        RegisterIso14443();
    }

    private void Add(string name, Func<CliArguments, CancellationToken, Task> action) => _handlers.Add(name, action);

    public async Task<int> ExecuteAsync(string command, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(command)) return 0;
            if (";#%".Contains(command.TrimStart()[0])) command = "rem " + command.TrimStart()[1..];
            var words = CommandLine.Split(command);
            if (words.Length == 1 && words[0] is "q" or "quit" or "e") words[0] = "exit";
            var (spec, args) = CommandCatalog.Find(words);
            if (spec is null)
            {
                var prefix = string.Join(' ', words);
                if (prefix is "help" or "--help" or "-h") prefix = "";
                if (prefix.Length > 0 && !CommandCatalog.Commands.Any(c => c.Name.StartsWith(prefix + " ", StringComparison.Ordinal)))
                    throw new ArgumentException("Unknown command: " + prefix);
                CommandCatalog.Help(Output, prefix: prefix); return 0;
            }
            if (args.Any(a => a is "-h" or "--help")) { CommandCatalog.Help(Output, spec); return 0; }
            var parsed = CliArguments.Parse(spec, args);
            if (!_handlers.TryGetValue(spec.Name, out var handler)) throw new NotSupportedException("Command is not migrated: " + spec.Name);
            var offline = spec.Name == "hf mfu ulcg" && parsed.Flag("offline")
                || spec.Name == "hf mf view" && parsed.Has("dump_file");
            if (spec.Device && !offline)
            {
                // Nothing else (the app's own pages) may talk to the device in the middle of a command.
                await Client.Connection.RunExclusiveAsync(() => RunAsync(spec, parsed, handler, offline, ct), ct);
                return 0;
            }
            await RunAsync(spec, parsed, handler, offline, ct);
            return 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { Error.WriteLine("Cancelled."); return 130; }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        { Error.WriteLine(ex.Message); return 2; }
        catch (Exception ex) { Error.WriteLine(ex.Message); return 1; }
    }

    /// <summary>Runs a parsed command, in reader mode and on its <c>--slot</c> when it asks for them, and puts both back.</summary>
    private async Task RunAsync(CommandSpec spec, CliArguments parsed, Func<CliArguments, CancellationToken, Task> handler,
        bool offline, CancellationToken ct)
    {
        DeviceMode? previousMode = null;
        int? previousSlot = null;
        var succeeded = false;
        try
        {
            if (spec.Reader && !offline)
            {
                previousMode = await Client.GetDeviceModeAsync(ct);
                if (previousMode != DeviceMode.Reader) await Client.SetDeviceModeAsync(DeviceMode.Reader, ct);
            }
            if (spec.ScopedSlot && parsed.Has("slot"))
            {
                previousSlot = await Client.GetActiveSlotAsync(ct);
                await Client.SetActiveSlotAsync(parsed.Int("slot"), ct);
            }
            await handler(parsed, ct); succeeded = true;
        }
        finally
        {
            if (Connected && (previousSlot is not null || previousMode is not null))
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    if (previousSlot is { } slot) await Client.SetActiveSlotAsync(slot, cleanup.Token);
                    if (previousMode is { } mode && mode != DeviceMode.Reader) await Client.SetDeviceModeAsync(mode, cleanup.Token);
                }
                catch (Exception ex)
                {
                    if (succeeded) throw new IOException("Could not restore device state: " + ex.Message, ex);
                    Error.WriteLine("Could not restore device state: " + ex.Message);
                }
            }
        }
    }

    /// <summary>Takes <paramref name="transport"/> as the link of a session that owns its own (the terminal, tests).</summary>
    public Task AttachAsync(ITransport transport, CancellationToken ct = default) =>
        (Host as StandaloneCliHost ?? throw new InvalidOperationException("This session uses its host's link."))
            .AttachAsync(transport, ct);

    private async Task ConnectAsync(CliArguments a, CancellationToken ct)
    {
        var what = await Host.ConnectAsync(a.Get("port"), a.Get("code"), ct);
        try
        {
            var model = await Client.GetDeviceModelAsync(ct);
            var version = await Client.GetFullVersionAsync(ct);
            Output.WriteLine($"Chameleon {model} connected ({what}): {version}");
        }
        catch { await DisconnectAsync(); throw; }
    }

    private Task ShareAsync(CliArguments a, CancellationToken ct) => Host.ShareAsync(a.Get("port"), Output, ct);

    private Task DisconnectAsync() => Host.DisconnectAsync();

    /// <summary>The file <paramref name="name"/> stands for where the session runs.</summary>
    private string FilePath(string name) => Host.ResolvePath(name);

    public async ValueTask DisposeAsync()
    {
        if (_ownsHost) await Host.DisposeAsync();
    }
    private void Print(object? value) => Output.WriteLine(value is byte[] b ? Convert.ToHexString(b) : value);
    private void Json(object value) => Output.WriteLine(JsonSerializer.Serialize(value, value.GetType(), CliJsonContext.Default));
    private async Task<byte[]> Wire(int command, byte[]? data, CancellationToken ct, int timeout = 3000)
    {
        var f = await Client.Connection.SendAsync((Command)command, data, timeoutMs: timeout, ct: ct);
        if (!f.Status.IsOk()) throw new ChameleonException($"Command {command}: {f.Status.Describe()}", f.Status);
        return f.Data;
    }
    private static byte B(int n) => checked((byte)n);
    private static byte[] U16(int n) { var b = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, checked((ushort)n)); return b; }
    private static byte[] U32(uint n) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, n); return b; }
    private static int Range(int n, int min, int max, string name)
        => n >= min && n <= max ? n : throw new ArgumentException($"{name} must be between {min} and {max}.");
    private static void Force(CliArguments a) { if (!a.Flag("force")) throw new ArgumentException("Repeat with --force to confirm this operation."); }
    private static MfKeyType KeyType(CliArguments a, string prefix = "") => a.Flag(prefix + "b") ? MfKeyType.B : MfKeyType.A;
    private static TagSenseType Sense(CliArguments a) => a.Flag("hf") ? TagSenseType.Hf : TagSenseType.Lf;
    private async Task<int> Slot(CliArguments a, CancellationToken ct) => a.Has("slot") ? Range(a.Int("slot"), 1, 8, "slot") : await Client.GetActiveSlotAsync(ct);
    private partial void RegisterHardware();
    private partial void RegisterLf();
    private partial void RegisterHf();
    private partial void RegisterMifare();
    private partial void RegisterUltralight();
    private partial void RegisterIso14443();
}
