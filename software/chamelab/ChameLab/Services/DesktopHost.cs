using System;
using Avalonia.Controls;

namespace ChameLab.Services;

/// <summary>
/// Optional desktop-only window activation and tray balloon hooks registered by the desktop head
/// before Avalonia starts, keeping platform P/Invokes and helper invocations out of the shared UI assembly.
/// </summary>
public static class DesktopHost
{
    public static Action<Window>? BringToFront { get; set; }
    public static Action<TrayIcon, string, string>? ShowTrayBalloon { get; set; }
}
