using System;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Gms.Tasks;
using CancellationToken = System.Threading.CancellationToken;
using Android.Widget;
using ChameLab.Localization;
using ChameLab.Services;
using Xamarin.Google.MLKit.Vision.Barcode.Common;
using Xamarin.Google.MLKit.Vision.CodeScanner;

namespace ChameLab.Android;

/// <summary>
/// QR codes through Google Play services' code scanner: its own full-screen camera view, no camera
/// permission for the app. It returns one code per run, so a series is scanned run after run, with the
/// status (which parts are missing) shown as a toast in between.
/// </summary>
internal sealed class AndroidQrCamera : IQrCamera
{
    /// <summary>The activity the scanner opens over; set by <see cref="MainActivity"/>.</summary>
    public static Activity? Activity { get; set; }

    public async Task<bool> ScanAsync(string status, Func<string, QrScanStep> onCode, CancellationToken ct = default)
    {
        var activity = Activity ?? throw new QrCameraUnavailableException(Strings.Qr_NoCamera);
        var options = new GmsBarcodeScannerOptions.Builder()
            .SetBarcodeFormats(Barcode.FormatQrCode)
            .EnableAutoZoom()
            .Build();
        var scanner = GmsBarcodeScanning.GetClient(activity, options);

        Toast(activity, status);
        string? last = null;
        while (!ct.IsCancellationRequested)
        {
            string? text;
            try { text = await ScanOnceAsync(scanner); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // No Play services, or its scanner module can't be downloaded.
                throw new QrCameraUnavailableException(Strings.Qr_ScannerUnavailable, ex);
            }
            if (text is null) return false;
            if (text == last)
            {
                // The part shown when the scanner opened again: wait for the next one to come up.
                await System.Threading.Tasks.Task.Delay(800, ct);
                continue;
            }
            last = text;

            var step = onCode(text);
            if (step.Done) return true;
            Toast(activity, step.Status);
        }
        return false;
    }

    private static void Toast(Activity activity, string text) =>
        activity.RunOnUiThread(() => global::Android.Widget.Toast.MakeText(activity, text, ToastLength.Long)?.Show());

    /// <summary>One run of the scanner: the code's text, or null when the user backed out.</summary>
    private static Task<string?> ScanOnceAsync(IGmsBarcodeScanner scanner)
    {
        var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var listener = new Listener(done);
        scanner.StartScan()
            .AddOnSuccessListener(listener)
            .AddOnCanceledListener(listener)
            .AddOnFailureListener(listener);
        return done.Task;
    }

    private sealed class Listener(TaskCompletionSource<string?> done)
        : Java.Lang.Object, IOnSuccessListener, IOnCanceledListener, IOnFailureListener
    {
        public void OnSuccess(Java.Lang.Object? result) => done.TrySetResult((result as Barcode)?.RawValue);

        public void OnCanceled() => done.TrySetResult(null);

        public void OnFailure(Java.Lang.Exception e) => done.TrySetException(e);
    }
}
