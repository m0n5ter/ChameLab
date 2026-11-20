using System;
using System.IO;
using System.Security;

namespace ChameLab.Services;

/// <summary>
/// Starting ChameLab in the background when the user signs in, so the desktop keeps syncing for
/// the user's other devices: a <c>Run</c> registry value on Windows, a LaunchAgent on macOS, an
/// autostart entry on Linux. Each points at this executable with <see cref="BackgroundArgument"/>.
/// </summary>
public static class Autostart
{
    /// <summary>Started this way, ChameLab shows only its tray icon.</summary>
    public const string BackgroundArgument = "--background";

    private const string Name = "ChameLab";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsSupported =>
        (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
        && Environment.ProcessPath is not null;

    private static string Executable => Environment.ProcessPath ?? throw new InvalidOperationException();

    private static string MacAgent =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", "io.github.m0n5ter.chamelab.plist");

    private static string LinuxEntry =>
        Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config
            ? config
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"), "autostart", "chamelab.desktop");

    public static bool IsEnabled
    {
        get
        {
            if (!IsSupported) return false;
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
                    return key?.GetValue(Name) is string;
                }
                return File.Exists(OperatingSystem.IsMacOS() ? MacAgent : LinuxEntry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                return false;
            }
        }
        set
        {
            if (!IsSupported) return;
            if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
                if (value) key.SetValue(Name, $"\"{Executable}\" {BackgroundArgument}");
                else key.DeleteValue(Name, throwOnMissingValue: false);
                return;
            }
            var path = OperatingSystem.IsMacOS() ? MacAgent : LinuxEntry;
            if (!value)
            {
                File.Delete(path);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, OperatingSystem.IsMacOS() ? Plist() : DesktopEntry());
        }
    }

    private static string Plist() =>
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
          <key>Label</key><string>io.github.m0n5ter.chamelab</string>
          <key>ProgramArguments</key>
          <array><string>{SecurityElement.Escape(Executable)}</string><string>{BackgroundArgument}</string></array>
          <key>RunAtLoad</key><true/>
        </dict>
        </plist>
        """;

    private static string DesktopEntry() =>
        $"""
        [Desktop Entry]
        Type=Application
        Name=ChameLab
        Exec="{Executable.Replace("\"", "\\\"")}" {BackgroundArgument}
        X-GNOME-Autostart-enabled=true
        NoDisplay=true
        """;
}
