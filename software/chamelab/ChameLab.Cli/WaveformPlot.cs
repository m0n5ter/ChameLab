using System.Diagnostics;
using System.Reflection;

namespace ChameLab.Cli;

internal static class WaveformPlot
{
    public static async Task ShowAsync(byte[] samples, int offset, CancellationToken ct)
    {
        var file = Path.Combine(Path.GetTempPath(), "chamelab-lf-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            await File.WriteAllBytesAsync(file, samples, ct);
            var host = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate CLI host.");
            var start = new ProcessStartInfo(host) { UseShellExecute = false };
            if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
            start.ArgumentList.Add("--plot-capture"); start.ArgumentList.Add(file); start.ArgumentList.Add(offset.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var process = Process.Start(start) ?? throw new IOException("Could not open waveform window.");
            try { await process.WaitForExitAsync(ct); }
            catch (OperationCanceledException) { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
            if (process.ExitCode != 0) throw new IOException("Waveform viewer exited with code " + process.ExitCode);
        }
        finally { if (File.Exists(file)) File.Delete(file); }
    }
}
