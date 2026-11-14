using System;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Localization;
using ChameLab.Models;
using ChameLab.Services;

namespace ChameLab.Browser;

/// <summary>
/// The web app's camera QR scanner (qr-scan.js): getUserMedia in an overlay over the app, frames decoded
/// by the browser's BarcodeDetector where it has one, otherwise by ZXing here.
/// </summary>
[SupportedOSPlatform("browser")]
internal sealed partial class WebQrCamera : IQrCamera
{
    private static Task? _import;

    public static bool IsAvailable()
    {
        var navigator = JSHost.GlobalThis.GetPropertyAsJSObject("navigator");
        return navigator?.GetPropertyAsJSObject("mediaDevices") is { } media && media.HasProperty("getUserMedia");
    }

    public async Task<bool> ScanAsync(string status, Func<string, QrScanStep> onCode, CancellationToken ct = default)
    {
        var baseUri = JSHost.GlobalThis.GetPropertyAsJSObject("document")!.GetPropertyAsString("baseURI")!;
        await (_import ??= JSHost.ImportAsync("qr-scan", new Uri(new Uri(baseUri), "qr-scan.js").AbsoluteUri));

        var opened = await OpenAsync(status, Strings.Common_Cancel);
        switch (opened)
        {
            case "ok": break;
            case "denied": throw new QrCameraUnavailableException(Strings.Qr_CameraDenied);
            case "nocamera": throw new QrCameraUnavailableException(Strings.Qr_NoCamera);
            default: throw new QrCameraUnavailableException(opened.StartsWith("error:", StringComparison.Ordinal) ? opened[6..] : opened);
        }

        using var registration = ct.Register(Close);
        string? last = null;
        byte[] buffer = Array.Empty<byte>();
        try
        {
            while (true)
            {
                var frame = await NextFrameAsync();
                if (frame == "closed") return false;

                string? text = null;
                if (frame.StartsWith("text:", StringComparison.Ordinal))
                    text = frame[5..];
                else if (frame.StartsWith("frame:", StringComparison.Ordinal))
                {
                    var size = frame.Split(':');
                    var (w, h) = (int.Parse(size[1]), int.Parse(size[2]));
                    if (buffer.Length != w * h) buffer = new byte[w * h];
                    CopyFrame(new ArraySegment<byte>(buffer));
                    text = QrCodes.Decode(buffer, w, h);
                }
                // The same code stays in view for many frames: handle it once.
                if (text is null || text == last) continue;
                last = text;

                var step = onCode(text);
                SetStatus(step.Status);
                if (step.Done) return true;
            }
        }
        finally
        {
            Close();
        }
    }

    [JSImport("open", "qr-scan")]
    private static partial Task<string> OpenAsync(string status, string cancelLabel);

    [JSImport("nextFrame", "qr-scan")]
    private static partial Task<string> NextFrameAsync();

    [JSImport("copyFrame", "qr-scan")]
    private static partial void CopyFrame([JSMarshalAs<JSType.MemoryView>] ArraySegment<byte> target);

    [JSImport("setStatus", "qr-scan")]
    private static partial void SetStatus(string text);

    [JSImport("close", "qr-scan")]
    private static partial void Close();
}
