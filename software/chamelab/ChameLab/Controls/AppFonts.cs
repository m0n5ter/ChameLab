using Avalonia.Media;

namespace ChameLab.Controls;

/// <summary>Fonts that ship with the app, so text looks the same on every platform.</summary>
public static class AppFonts
{
    /// <summary>
    /// Cascadia Mono (Regular and SemiBold, SIL OFL; see Assets/Fonts): hex, keys and code.
    /// Embedded because the system fallbacks differ: Android has none of the usual monospace
    /// names and drew these in Roboto. The Mono cut has no ligatures, so "-&gt;" stays as typed.
    /// </summary>
    public static FontFamily Mono { get; } = new("avares://ChameLab/Assets/Fonts#Cascadia Mono");
}
