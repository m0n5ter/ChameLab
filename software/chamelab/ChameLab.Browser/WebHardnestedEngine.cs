using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Services;

namespace ChameLab.Browser;

[SupportedOSPlatform("browser")]
internal static partial class WebHardnestedInterop
{
    private static Task? _import;
    public static async Task EnsureModuleAsync()
    {
        var page = JSHost.GlobalThis.GetPropertyAsJSObject("document")!.GetPropertyAsString("baseURI")!;
        try { await (_import ??= JSHost.ImportAsync("hardnested", new Uri(new Uri(page), "hardnested.js").AbsoluteUri)); }
        catch { _import = null; throw; }
    }
    [JSImport("recover", "hardnested")]
    public static partial Task<string> Recover(string id, double uid, string capture);
    [JSImport("takeProgress", "hardnested")]
    public static partial string TakeProgress(string id);
    [JSImport("cancel", "hardnested")]
    public static partial void Cancel(string id);
}

[SupportedOSPlatform("browser")]
public sealed class WebHardnestedEngine : IHardnestedEngine
{
    private static readonly SemaphoreSlim RunLock = new(1, 1);
    public string? UnavailableReason => null;

    public async Task<ulong?> RecoverAsync(HardnestedCapture capture, IProgress<string>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var text = HardnestedCaptureCodec.Encode(capture);
        await WebHardnestedInterop.EnsureModuleAsync().WaitAsync(ct);
        await RunLock.WaitAsync(ct);
        var id = Guid.NewGuid().ToString("N");
        try
        {
            ct.ThrowIfCancellationRequested();
            var task = WebHardnestedInterop.Recover(id, capture.AuthenticationUid, text);
            while (!task.IsCompleted)
            {
                await Task.WhenAny(task, Task.Delay(250, ct));
                ct.ThrowIfCancellationRequested();
                var update = WebHardnestedInterop.TakeProgress(id);
                if (update.Length > 0) progress?.Report(update);
            }
            var key = await task;
            ct.ThrowIfCancellationRequested();
            return key.Length == 0 ? null : ulong.Parse(key, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }
        finally
        {
            try { WebHardnestedInterop.Cancel(id); }
            finally { RunLock.Release(); }
        }
    }
}

/// <summary>The worker loads the app assemblies without starting the UI.</summary>
[SupportedOSPlatform("browser")]
public static partial class HardnestedWorkerExports
{
    [JSImport("reportProgress", "hardnested-worker")]
    internal static partial void ReportProgress(string message);

    [JSExport]
    public static string Execute(double uid, string text)
    {
        if (uid < 0 || uid > uint.MaxValue || uid != Math.Truncate(uid)) throw new ArgumentException("Invalid UID.");
        var samples = new List<HardnestedSample>();
        foreach (var line in text.AsSpan().EnumerateLines())
        {
            if (line.IsEmpty) continue;
            int split = line.IndexOf('|');
            if (split <= 0) throw new ArgumentException("Invalid nonce capture.");
            samples.Add(new(uint.Parse(line[..split], CultureInfo.InvariantCulture),
                byte.Parse(line[(split + 1)..], CultureInfo.InvariantCulture)));
        }
        var key = ManagedHardnestedEngine.Recover(new((uint)uid, samples), new WorkerProgress());
        return key?.ToString("X12", CultureInfo.InvariantCulture) ?? "";
    }

    private sealed class WorkerProgress : IProgress<string>
    {
        public void Report(string value) => ReportProgress(value);
    }
}
