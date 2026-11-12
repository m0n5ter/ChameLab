using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using ChameLab.Services;
using Microsoft.Win32.SafeHandles;

namespace ChameLab.Desktop;

internal static class DesktopWakeLock
{
    private const string Reason = "ChameLab operation in progress";

    public static Task<IAsyncDisposable?> AcquireAsync() => Task.FromResult<IAsyncDisposable?>(
        OperatingSystem.IsWindows() ? AcquireWindows() : AcquireUnix());

    private static IAsyncDisposable AcquireWindows()
    {
        var reason = Marshal.StringToHGlobalUni(Reason);
        SafeFileHandle handle;
        try
        {
            var context = new ReasonContext { Flags = 1, Reason = new ReasonUnion { SimpleReason = reason } };
            handle = PowerCreateRequest(ref context);
        }
        finally { Marshal.FreeHGlobal(reason); }

        if (handle.IsInvalid)
        {
            var error = new Win32Exception(Marshal.GetLastWin32Error());
            handle.Dispose();
            throw error;
        }
        try
        {
            // Unlike SetThreadExecutionState, this handle can be released on another thread.
            if (!PowerSetRequest(handle, 1) || !PowerSetRequest(handle, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch { handle.Dispose(); throw; }

        return WakeLockService.ReleaseWith(() =>
        {
            PowerClearRequest(handle, 0);
            PowerClearRequest(handle, 1);
            handle.Dispose();
            return ValueTask.CompletedTask;
        });
    }

    private static IAsyncDisposable? AcquireUnix()
    {
        var start = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true };
        if (OperatingSystem.IsMacOS())
        {
            start.FileName = "/usr/bin/caffeinate";
            start.ArgumentList.Add("-di");
            // A crashed parent must not leave a helper keeping the computer awake.
            start.ArgumentList.Add("-w");
            start.ArgumentList.Add(Environment.ProcessId.ToString());
        }
        else if (OperatingSystem.IsLinux())
        {
            start.FileName = "systemd-inhibit";
            start.RedirectStandardInput = true;
            start.ArgumentList.Add("--what=idle:sleep");
            start.ArgumentList.Add("--who=ChameLab");
            start.ArgumentList.Add("--why=" + Reason);
            start.ArgumentList.Add("--mode=block");
            start.ArgumentList.Add("--no-ask-password");
            start.ArgumentList.Add("cat");
            // cat exits on EOF if the parent crashes, releasing logind's inhibitor.
        }
        else return null;

        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not inhibit sleep.");
        return WakeLockService.ReleaseWith(async () =>
        {
            try
            {
                if (start.RedirectStandardInput) process.StandardInput.Close();
                else if (!process.HasExited) process.Kill();
                await process.WaitForExitAsync();
            }
            finally { process.Dispose(); }
        });
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        public ReasonUnion Reason;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct ReasonUnion
    {
        [FieldOffset(0)] public IntPtr SimpleReason;
        [FieldOffset(0)] public DetailedReason Detailed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DetailedReason
    {
        public IntPtr Module;
        public uint Id;
        public uint Count;
        public IntPtr Strings;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle PowerCreateRequest(ref ReasonContext context);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerSetRequest(SafeFileHandle handle, int type);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerClearRequest(SafeFileHandle handle, int type);
}
