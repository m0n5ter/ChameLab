using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia.Controls;

namespace ChameLab.Desktop;

/// <summary>
/// Native desktop helpers for foreground window activation and one-time tray notifications.
/// </summary>
internal static class DesktopPlatform
{
    private const int SwRestore = 9;
    private const uint NimModify = 0x00000001;
    private const uint NifInfo = 0x00000010;
    private const uint NiifNone = 0x00000000;

    internal static int NotifyIconDataSize => Marshal.SizeOf<NotifyIconData>();

    public static void BringToFront(Window window)
    {
        if (OperatingSystem.IsWindows())
        {
            if (window.TryGetPlatformHandle() is { HandleDescriptor: "HWND", Handle: var hwnd } && hwnd != IntPtr.Zero)
            {
                if (IsIconic(hwnd)) ShowWindow(hwnd, SwRestore);
                SetForegroundWindow(hwnd);
            }
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            try
            {
                var nsAppClass = objc_getClass("NSApplication");
                var sharedAppSel = sel_registerName("sharedApplication");
                var nsApp = objc_msgSend(nsAppClass, sharedAppSel);
                if (nsApp != IntPtr.Zero)
                    objc_msgSend(nsApp, sel_registerName("activateIgnoringOtherApps:"), (byte)1);

                if (window.TryGetPlatformHandle() is { HandleDescriptor: "NSWindow", Handle: var nsWindow } && nsWindow != IntPtr.Zero)
                    objc_msgSend(nsWindow, sel_registerName("makeKeyAndOrderFront:"), IntPtr.Zero);
            }
            catch
            {
                // Best effort: Avalonia's Window.Activate() already ran.
            }
        }
    }

    public static void ShowTrayBalloon(TrayIcon tray, string title, string message)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                if (!TryGetWin32TrayIdentity(tray, out var hWnd, out var uId)) return;

                var data = new NotifyIconData
                {
                    cbSize = Marshal.SizeOf<NotifyIconData>(),
                    hWnd = hWnd,
                    uID = uId,
                    uFlags = NifInfo,
                    szTip = string.Empty,
                    szInfoTitle = Truncate(title, 63),
                    szInfo = Truncate(message, 255),
                    dwInfoFlags = NiifNone,
                };
                Shell_NotifyIcon(NimModify, ref data);
            }
            catch
            {
                // Best effort: missing shell support must not disrupt hiding to the tray.
            }
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            RunProcessAsync(BuildMacNotification(title, message));
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            RunProcessAsync(BuildLinuxNotification(title, message));
        }
    }

    internal static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || maxLength <= 0) return string.Empty;
        if (text.Length <= maxLength) return text;
        if (char.IsHighSurrogate(text[maxLength - 1])) maxLength--;
        return maxLength > 0 ? text[..maxLength] : string.Empty;
    }

    internal static ProcessStartInfo BuildMacNotification(string title, string message)
    {
        var start = new ProcessStartInfo("/usr/bin/osascript")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add("on run argv\ndisplay notification (item 2 of argv) with title (item 1 of argv)\nend run");
        start.ArgumentList.Add("--");
        start.ArgumentList.Add(title);
        start.ArgumentList.Add(message);
        return start;
    }

    internal static ProcessStartInfo BuildLinuxNotification(string title, string message)
    {
        var start = new ProcessStartInfo("notify-send")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-a");
        start.ArgumentList.Add("ChameLab");
        start.ArgumentList.Add("--");
        start.ArgumentList.Add(title);
        start.ArgumentList.Add(message);
        return start;
    }

    private static void RunProcessAsync(ProcessStartInfo start) => _ = Task.Run(async () =>
    {
        try
        {
            using var process = Process.Start(start);
            if (process is not null) await process.WaitForExitAsync();
        }
        catch
        {
            // Missing notification tools (e.g. notify-send) must not fail the app.
        }
    });

    private static bool TryGetWin32TrayIdentity(TrayIcon tray, out IntPtr hWnd, out int uId)
    {
        hWnd = IntPtr.Zero;
        uId = 0;

        var impl = typeof(TrayIcon).GetProperty("Impl", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(tray);
        if (impl is null) return false;

        var implType = impl.GetType();
        if (implType.GetField("_uniqueId", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(impl) is not int id || id <= 0)
            return false;

        var platformType = implType.Assembly.GetType("Avalonia.Win32.Win32Platform");
        var platform = platformType?.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
        if (platform is null) return false;

        if (platformType?.GetProperty("Handle", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(platform) is not IntPtr handle
            || handle == IntPtr.Zero)
            return false;

        hWnd = handle;
        uId = id;
        return true;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NotifyIconData lpData);

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr objc_getClass(string className);

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr sel_registerName(string selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend(IntPtr receiver, IntPtr selector, byte arg1);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend(IntPtr receiver, IntPtr selector, IntPtr arg1);
}
