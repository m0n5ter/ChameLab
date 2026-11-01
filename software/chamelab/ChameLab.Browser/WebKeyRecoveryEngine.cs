using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Services;

namespace ChameLab.Browser;

[SupportedOSPlatform("browser")]
internal static partial class WebKeyRecoveryInterop
{
    private static Task? _import;
    public static async Task EnsureModuleAsync()
    {
        var page = JSHost.GlobalThis.GetPropertyAsJSObject("document")!.GetPropertyAsString("baseURI")!;
        try { await (_import ??= JSHost.ImportAsync("key-recovery", new Uri(new Uri(page), "key-recovery.js").AbsoluteUri)); }
        catch { _import = null; throw; }
    }

    [JSImport("recover", "key-recovery")]
    public static partial Task<string> Recover(string id, string request);

    [JSImport("cancel", "key-recovery")]
    public static partial void Cancel(string id);
}

/// <summary>CPU work runs in an independent .NET runtime inside a Web Worker.</summary>
[SupportedOSPlatform("browser")]
public sealed class WebKeyRecoveryEngine : IKeyRecoveryEngine
{
    private static readonly SemaphoreSlim RunLock = new(1, 1);

    public async Task<IReadOnlyList<ulong>> RecoverAsync(KeyRecoveryRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await WebKeyRecoveryInterop.EnsureModuleAsync().WaitAsync(ct);
        await RunLock.WaitAsync(ct);
        var id = Guid.NewGuid().ToString("N");
        try
        {
            ct.ThrowIfCancellationRequested();
            var payload = JsonSerializer.Serialize(request, KeyRecoveryJsonContext.Default.KeyRecoveryRequest);
            var result = await WebKeyRecoveryInterop.Recover(id, payload).WaitAsync(ct);
            ct.ThrowIfCancellationRequested();
            return JsonSerializer.Deserialize(result, KeyRecoveryJsonContext.Default.UInt64Array)
                ?? throw new InvalidOperationException("Invalid key recovery worker response.");
        }
        finally
        {
            try { WebKeyRecoveryInterop.Cancel(id); }
            finally { RunLock.Release(); }
        }
    }
}

// The worker loads the same assemblies but never starts Program.Main or Avalonia.
[SupportedOSPlatform("browser")]
public static partial class KeyRecoveryWorkerExports
{
    [JSExport]
    public static string Execute(string payload)
    {
        var request = JsonSerializer.Deserialize(payload, KeyRecoveryJsonContext.Default.KeyRecoveryRequest)
            ?? throw new ArgumentException("Missing recovery request.", nameof(payload));
        var keys = ManagedKeyRecoveryEngine.Execute(request);
        return JsonSerializer.Serialize(keys.ToArray(), KeyRecoveryJsonContext.Default.UInt64Array);
    }
}
