using System;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using ChameLab.Services;

namespace ChameLab.Browser;

/// <summary>JS interop bindings to <c>feedback.js</c> (scan sounds and vibration).</summary>
[SupportedOSPlatform("browser")]
internal static partial class WebFeedbackInterop
{
    private static Task? _import;

    public static Task EnsureModuleAsync() =>
        // Resolved against the page for the same reason as serial.js (see WebSerialInterop).
        _import ??= JSHost.ImportAsync("feedback", new Uri(new Uri(PageBaseUri()), "feedback.js").AbsoluteUri);

    private static string PageBaseUri() =>
        JSHost.GlobalThis.GetPropertyAsJSObject("document")!.GetPropertyAsString("baseURI")!;

    [JSImport("play", "feedback")]
    public static partial void Play(string base64);

    [JSImport("vibrate", "feedback")]
    public static partial void Vibrate(bool found);
}

/// <summary>
/// Scan sounds through an audio element, and vibration through navigator.vibrate on touch
/// devices (desktop browsers have the call too, but nothing to shake).
/// </summary>
[SupportedOSPlatform("browser")]
public sealed class WebFeedbackPlayer : IFeedbackPlayer
{
    public bool CanVibrate { get; } =
        JSHost.GlobalThis.GetPropertyAsJSObject("navigator") is { } navigator
        && navigator.HasProperty("vibrate") && navigator.GetPropertyAsInt32("maxTouchPoints") > 0;

    public void Play(ScanSound sound) => _ = PlayAsync(Convert.ToBase64String(sound.Wav));

    public void Vibrate(ScanOutcome outcome) => _ = VibrateAsync(outcome == ScanOutcome.Found);

    private static async Task PlayAsync(string base64)
    {
        try
        {
            await WebFeedbackInterop.EnsureModuleAsync();
            WebFeedbackInterop.Play(base64);
        }
        catch { /* no audio: stay silent */ }
    }

    private static async Task VibrateAsync(bool found)
    {
        try
        {
            await WebFeedbackInterop.EnsureModuleAsync();
            WebFeedbackInterop.Vibrate(found);
        }
        catch { /* no vibrator */ }
    }
}
