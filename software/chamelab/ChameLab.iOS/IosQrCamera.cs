using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AVFoundation;
using ChameLab.Localization;
using ChameLab.Services;
using CoreFoundation;
using CoreGraphics;
using Foundation;
using UIKit;

namespace ChameLab.iOS;

/// <summary>
/// QR codes through AVFoundation: a full-screen camera view with a status line and Cancel, reading
/// codes continuously, so a series is collected while its codes change on the other screen.
/// </summary>
internal sealed class IosQrCamera : IQrCamera
{
    public async Task<bool> ScanAsync(string status, Func<string, QrScanStep> onCode, CancellationToken ct = default)
    {
        var granted = AVCaptureDevice.GetAuthorizationStatus(AVAuthorizationMediaType.Video) switch
        {
            AVAuthorizationStatus.Authorized => true,
            AVAuthorizationStatus.NotDetermined => await AVCaptureDevice.RequestAccessForMediaTypeAsync(AVAuthorizationMediaType.Video),
            _ => false,
        };
        if (!granted) throw new QrCameraUnavailableException(Strings.Qr_CameraDenied);
        var device = AVCaptureDevice.GetDefaultDevice(AVMediaTypes.Video)
            ?? throw new QrCameraUnavailableException(Strings.Qr_NoCamera);

        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var scanner = new ScannerViewController(device, status, onCode, done);
        var presenter = TopViewController() ?? throw new QrCameraUnavailableException(Strings.Qr_NoCamera);
        presenter.PresentViewController(scanner, true, null);
        using var registration = ct.Register(() => scanner.BeginInvokeOnMainThread(() => scanner.Finish(false)));
        return await done.Task;
    }

    private static UIViewController? TopViewController()
    {
        var window = UIApplication.SharedApplication.ConnectedScenes
            .OfType<UIWindowScene>()
            .SelectMany(s => s.Windows)
            .FirstOrDefault(w => w.IsKeyWindow);
        var top = window?.RootViewController;
        while (top?.PresentedViewController is { } presented) top = presented;
        return top;
    }

    private sealed class ScannerViewController : UIViewController
    {
        private readonly AVCaptureDevice _device;
        private readonly string _status;
        private readonly Func<string, QrScanStep> _onCode;
        private readonly TaskCompletionSource<bool> _done;
        private readonly AVCaptureSession _session = new();
        private AVCaptureVideoPreviewLayer? _preview;
        private UILabel? _label;
        private MetadataDelegate? _delegate;
        private string? _last;
        private bool _finished;

        public ScannerViewController(AVCaptureDevice device, string status, Func<string, QrScanStep> onCode,
            TaskCompletionSource<bool> done)
        {
            _device = device;
            _status = status;
            _onCode = onCode;
            _done = done;
            ModalPresentationStyle = UIModalPresentationStyle.FullScreen;
        }

        public override void ViewDidLoad()
        {
            base.ViewDidLoad();
            View!.BackgroundColor = UIColor.Black;

            var input = AVCaptureDeviceInput.FromDevice(_device, out var error);
            if (input is null || error is not null || !_session.CanAddInput(input))
            {
                Finish(false);
                return;
            }
            _session.AddInput(input);
            var output = new AVCaptureMetadataOutput();
            if (!_session.CanAddOutput(output))
            {
                Finish(false);
                return;
            }
            _session.AddOutput(output);
            _delegate = new MetadataDelegate(this);
            output.SetDelegate(_delegate, DispatchQueue.MainQueue);
            output.MetadataObjectTypes = AVMetadataObjectType.QRCode;

            _preview = new AVCaptureVideoPreviewLayer(_session) { VideoGravity = AVLayerVideoGravity.ResizeAspectFill };
            View.Layer.AddSublayer(_preview);

            _label = new UILabel
            {
                Text = _status,
                TextColor = UIColor.White,
                TextAlignment = UITextAlignment.Center,
                Lines = 0,
                TranslatesAutoresizingMaskIntoConstraints = false,
            };
            _label.Layer.ShadowColor = UIColor.Black.CGColor;
            _label.Layer.ShadowOpacity = 0.8f;
            _label.Layer.ShadowRadius = 3;
            _label.Layer.ShadowOffset = new CGSize(0, 1);

            var cancel = UIButton.FromType(UIButtonType.System);
            cancel.SetTitle(Strings.Common_Cancel, UIControlState.Normal);
            cancel.SetTitleColor(UIColor.Black, UIControlState.Normal);
            cancel.BackgroundColor = UIColor.White;
            cancel.Layer.CornerRadius = 22;
            cancel.TranslatesAutoresizingMaskIntoConstraints = false;
            cancel.TouchUpInside += (_, _) => Finish(false);

            View.AddSubview(_label);
            View.AddSubview(cancel);
            var guide = View.SafeAreaLayoutGuide;
            NSLayoutConstraint.ActivateConstraints(new[]
            {
                cancel.CenterXAnchor.ConstraintEqualTo(View.CenterXAnchor),
                cancel.BottomAnchor.ConstraintEqualTo(guide.BottomAnchor, -24),
                cancel.HeightAnchor.ConstraintEqualTo(44),
                cancel.WidthAnchor.ConstraintGreaterThanOrEqualTo(140),
                _label.LeadingAnchor.ConstraintEqualTo(guide.LeadingAnchor, 20),
                _label.TrailingAnchor.ConstraintEqualTo(guide.TrailingAnchor, -20),
                _label.BottomAnchor.ConstraintEqualTo(cancel.TopAnchor, -16),
            });

            // StartRunning blocks until the camera is up: off the main thread.
            DispatchQueue.GetGlobalQueue(DispatchQueuePriority.Default).DispatchAsync(_session.StartRunning);
        }

        public override void ViewDidLayoutSubviews()
        {
            base.ViewDidLayoutSubviews();
            if (_preview is not null) _preview.Frame = View!.Bounds;
        }

        private void OnCode(string text)
        {
            if (_finished || text == _last) return;
            _last = text;
            var step = _onCode(text);
            if (_label is not null) _label.Text = step.Status;
            if (step.Done) Finish(true);
        }

        public void Finish(bool scanned)
        {
            if (_finished) return;
            _finished = true;
            DispatchQueue.GetGlobalQueue(DispatchQueuePriority.Default).DispatchAsync(() =>
            {
                if (_session.Running) _session.StopRunning();
            });
            if (PresentingViewController is not null) DismissViewController(true, null);
            _done.TrySetResult(scanned);
        }

        private sealed class MetadataDelegate(ScannerViewController owner) : AVCaptureMetadataOutputObjectsDelegate
        {
            public override void DidOutputMetadataObjects(AVCaptureMetadataOutput captureOutput,
                AVMetadataObject[] metadataObjects, AVCaptureConnection connection)
            {
                foreach (var code in metadataObjects.OfType<AVMetadataMachineReadableCodeObject>())
                    if (code.StringValue is { } text)
                    {
                        owner.OnCode(text);
                        return;
                    }
            }
        }
    }
}
