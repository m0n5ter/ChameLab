using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Services;

namespace ChameLab.Mobile;

/// <summary>Serialized native recovery on a background thread, with cooperative cancellation.</summary>
public sealed class InProcessHardnestedEngine : IHardnestedEngine
{
    private static readonly SemaphoreSlim RunLock = new(1, 1);
    // The core needs roughly 1 GiB just for nonce bit arrays. Bound all its allocations;
    // allocation failures are returned to the UI instead of calling exit() in the host.
    private const uint MemoryLimit = 1792 * 1024 * 1024;
#if IOS
    private const string Library = "__Internal";
#else
    private const string Library = "chamelab-hardnested-embedded";
#endif
    public string? UnavailableReason => null;

    public async Task<ulong?> RecoverAsync(HardnestedCapture capture, IProgress<string>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var text = HardnestedCaptureCodec.Encode(capture);
        await RunLock.WaitAsync(ct);
        var path = Path.Combine(Path.GetTempPath(), "ChameLab-hardnested-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            await File.WriteAllTextAsync(path, text, new UTF8Encoding(false), ct);
            return await Task.Run(() => Recover(capture.AuthenticationUid, path, progress, ct), CancellationToken.None);
        }
        finally
        {
            try { File.Delete(path); }
            finally { RunLock.Release(); }
        }
    }

    private sealed record Session(IProgress<string>? Progress, CancellationToken Cancellation);

    private static unsafe ulong? Recover(uint uid, string path, IProgress<string>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var session = GCHandle.Alloc(new Session(progress, ct));
        try
        {
            var status = NativeRecover(uid, path, out var key, MemoryLimit, &Receive, GCHandle.ToIntPtr(session));
            ct.ThrowIfCancellationRequested();
            return status switch
            {
                1 => key,
                0 => null,
                -2 => throw new OperationCanceledException(ct),
                -3 => throw new InvalidOperationException("Hardnested needs more memory than is available within the mobile memory limit. Try a desktop device."),
                -1 => throw new InvalidOperationException("The hardnested capture is invalid or incomplete."),
                _ => throw new InvalidOperationException($"Hardnested failed (native status {status}).")
            };
        }
        finally { session.Free(); }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int Receive(int kind, nint message, nint context)
    {
        // Exceptions must never unwind through a native callback.
        try
        {
            var session = (Session)GCHandle.FromIntPtr(context).Target!;
            if (session.Cancellation.IsCancellationRequested) return 1;
            if (kind == 1 && message != 0)
                session.Progress?.Report(Marshal.PtrToStringUTF8(message) ?? "Calculating hardnested…");
            return 0;
        }
        catch { return 1; }
    }

    [DllImport(Library, EntryPoint = "chamelab_hardnested_embedded", CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int NativeRecover(uint uid, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        out ulong key, nuint memoryLimit, delegate* unmanaged[Cdecl]<int, nint, nint, int> callback, nint context);
}
