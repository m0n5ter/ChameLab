using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Cli;
using ChameLab.Protocol;
using ChameLab.Transport;

namespace ChameLab.Services;

/// <summary>
/// Runs the console's commands over the app's own link: <c>hw connect</c> and <c>hw disconnect</c> connect and
/// disconnect the app as the Connect page would, and relative file names land in the folder <paramref name="folder"/>
/// gives (by default <see cref="DefaultFolder"/>).
/// The console provides the rest (typed input, clearing, the waveform).
/// </summary>
public sealed class AppCliHost(
    DeviceService device,
    Func<CancellationToken, Task<string?>> readLine,
    Action clear,
    Func<byte[], int, Task> showWaveform,
    Func<string> folder) : CliHost
{
    /// <summary>Where relative file names in commands point unless another folder is chosen: Documents/ChameLab.</summary>
    public static string DefaultFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) is { Length: > 0 } documents
            ? documents
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "ChameLab");

    public override ChameleonConnection? Connection => device.Client?.Connection;

    public override async Task<string> ConnectAsync(string? port, string? code, CancellationToken ct)
    {
        if (device.State != ConnectionState.Disconnected)
            throw new InvalidOperationException($"Already connected to {device.Candidate?.DisplayName}. Use hw disconnect first.");
        TransportCandidate candidate;
        if (code is not null) candidate = RelayScanner.Candidate(code);
        else if (port is not null && port.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(port);
            candidate = TcpScanner.Candidate($"{uri.Host}:{uri.Port}");
        }
        else
        {
            // Without a port only USB is looked at: a Bluetooth scan takes long and may need a chooser.
            var found = port is null ? await device.ScanAsync(TransportKind.Serial, ct) : await device.ScanAsync(AutoScan.Never, ct);
            var matching = port is null ? found.ToList()
                : found.Where(c => string.Equals(c.Id, port, StringComparison.OrdinalIgnoreCase)).ToList();
            candidate = matching.Count switch
            {
                0 => throw new IOException(port is null
                    ? "No Chameleon on USB. Connect it, specify hw connect -p PORT, or connect over Bluetooth on the Connect page."
                    : $"{port} not found. Found: {(found.Count == 0 ? "none" : string.Join(", ", found.Select(c => c.Id)))}"),
                1 => matching[0],
                _ => throw new ArgumentException("Multiple devices found; use -p: " + string.Join(", ", matching.Select(c => c.Id)))
            };
        }
        await device.ConnectAsync(candidate, ct);
        return candidate.DisplayName;
    }

    public override Task DisconnectAsync() => device.DisconnectAsync();

    public override Task ShareAsync(string? port, TextWriter output, CancellationToken ct) =>
        throw new NotSupportedException("Share the device from the Connect page; hw share works in the chamelab command line client.");

    public override Task ShowWaveformAsync(byte[] samples, int offset, CancellationToken ct) => showWaveform(samples, offset);

    public override Task<string?> ReadLineAsync(CancellationToken ct) => readLine(ct);

    public override void Clear() => clear();

    public override string ResolvePath(string path)
    {
        if (Path.IsPathRooted(path)) return path;
        var root = folder();
        Directory.CreateDirectory(root);
        return Path.Combine(root, path);
    }
}
