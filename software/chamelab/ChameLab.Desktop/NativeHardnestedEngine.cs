using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Services;

namespace ChameLab.Desktop;

public sealed class NativeHardnestedEngine : IHardnestedEngine
{
    private readonly string _worker;
    private readonly string _library;
    private static readonly SemaphoreSlim RunLock = new(1, 1);

    public NativeHardnestedEngine(string? directory = null)
    {
        directory ??= Path.Combine(AppContext.BaseDirectory, "hardnested");
        directory = Path.GetFullPath(directory);
        _worker = Path.Combine(directory, OperatingSystem.IsWindows()
            ? "chamelab-hardnested-worker.exe" : "chamelab-hardnested-worker");
        _library = Path.Combine(directory, OperatingSystem.IsWindows() ? "chamelab-hardnested.dll"
            : OperatingSystem.IsMacOS() ? "libchamelab-hardnested.dylib" : "libchamelab-hardnested.so");
    }

    public string? UnavailableReason => File.Exists(_worker) && File.Exists(_library) ? null :
        "The desktop hardnested component is missing. Reinstall ChameLab or build its native hardnested component.";

    public async Task<ulong?> RecoverAsync(HardnestedCapture capture, IProgress<string>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (UnavailableReason is { } unavailable) throw new NotSupportedException(unavailable);
        await RunLock.WaitAsync(ct);
        var directory = Path.Combine(Path.GetTempPath(), "ChameLab-hardnested-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            await WriteCaptureAsync(Path.Combine(directory, "nonces.txt"), capture, ct);
            var start = new ProcessStartInfo(_worker)
            {
                WorkingDirectory = directory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add(capture.AuthenticationUid.ToString("X8", CultureInfo.InvariantCulture));
            start.ArgumentList.Add("nonces.txt");
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start hardnested.");
            ulong? key = null;
            var output = ReadLinesAsync(process.StandardOutput, line =>
                {
                    const string keyPrefix = "CHAMELAB_KEY=";
                    const string progressPrefix = "CHAMELAB_PROGRESS=";
                    if (line.StartsWith(keyPrefix, StringComparison.Ordinal)
                        && line.Length == keyPrefix.Length + 12
                        && ulong.TryParse(line.AsSpan(keyPrefix.Length), NumberStyles.HexNumber,
                            CultureInfo.InvariantCulture, out var parsed)) key = parsed;
                    else if (line.StartsWith(progressPrefix, StringComparison.Ordinal))
                        progress?.Report(line[progressPrefix.Length..]);
                });
            var errors = new StringBuilder();
            var errorOutput = ReadLinesAsync(process.StandardError, line =>
                {
                    if (errors.Length < 2048) errors.AppendLine(line[..Math.Min(line.Length, 512)]);
                });
            try
            {
                await process.WaitForExitAsync(ct);
                await Task.WhenAll(output, errorOutput);
                ct.ThrowIfCancellationRequested();
                if (process.ExitCode == 0 && key.HasValue) return key;
                if (process.ExitCode == 2) return null;
                throw new InvalidOperationException(
                    $"Hardnested failed (exit code {process.ExitCode}). " +
                    (errors.Length > 0 ? errors.ToString().Trim() : "The capture may be invalid or there may be insufficient memory."));
            }
            finally
            {
                if (!process.HasExited)
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) when (process.HasExited) { }
                }
                await process.WaitForExitAsync();
                await Task.WhenAll(output, errorOutput);
            }
        }
        finally
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
            finally { RunLock.Release(); }
        }
    }

    private static async Task ReadLinesAsync(StreamReader reader, Action<string> receive)
    {
        while (await reader.ReadLineAsync() is { } line) receive(line);
    }

    private static async Task WriteCaptureAsync(string path, HardnestedCapture capture, CancellationToken ct)
    {
        if (capture.Samples.Count == 0) throw new ArgumentException("No hardnested nonces were collected.", nameof(capture));
        await using var writer = new StreamWriter(path, append: false, new UTF8Encoding(false));
        foreach (var sample in capture.Samples)
        {
            ct.ThrowIfCancellationRequested();
            if (sample.EncryptedParity > 15) throw new ArgumentException("Invalid encrypted nonce parity.", nameof(capture));
            await writer.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                $"{sample.EncryptedNonce}|{sample.EncryptedParity}"));
        }
    }
}
