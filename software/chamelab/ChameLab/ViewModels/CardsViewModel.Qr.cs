using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using ChameLab.Localization;
using ChameLab.Models;
using ChameLab.Services;
using ChameLab.Sync;
using ReactiveUI;

namespace ChameLab.ViewModels;

/// <summary>Sharing cards as QR codes: showing the open card's codes, and scanning codes into a new card.</summary>
public sealed partial class CardsViewModel
{
    /// <summary>How long each code of a series stays on screen.</summary>
    private static readonly TimeSpan QrPartInterval = TimeSpan.FromSeconds(1.2);

    private IReadOnlyList<string> _qrParts = Array.Empty<string>();
    private int _qrIndex;
    private DispatcherTimer? _qrTimer;
    private readonly CardQrCollector _qrCollector = new();

    public ICommand ShareQrCommand { get; private set; } = null!;
    public ICommand QrPreviousCommand { get; private set; } = null!;
    public ICommand QrNextCommand { get; private set; } = null!;
    public ICommand QrPlayPauseCommand { get; private set; } = null!;
    public ICommand ScanQrCommand { get; private set; } = null!;
    public ICommand ScanQrImageCommand { get; private set; } = null!;

    private void InitQr()
    {
        ShareQrCommand = ReactiveCommand.Create(() => Toggle(CardAction.Qr));
        QrPreviousCommand = ReactiveCommand.Create(() => { PauseQr(); ShowQrPart(_qrIndex - 1); });
        QrNextCommand = ReactiveCommand.Create(() => { PauseQr(); ShowQrPart(_qrIndex + 1); });
        QrPlayPauseCommand = ReactiveCommand.Create(() => { if (IsQrPlaying) PauseQr(); else PlayQr(); });
        ScanQrCommand = ReactiveCommand.CreateFromTask(ScanQrAsync);
        ScanQrImageCommand = ReactiveCommand.CreateFromTask(ScanQrImageAsync);
    }

    public bool IsQrOpen => _action == CardAction.Qr;

    // ---- Showing --------------------------------------------------------

    /// <summary>Builds the open card's codes when the QR panel opens; stops the series when it closes.</summary>
    private void OnQrActionChanged()
    {
        if (_action == CardAction.Qr)
        {
            try { _qrParts = ActionCard() is { } card ? CardQr.Encode(card) : Array.Empty<string>(); }
            catch (FormatException ex) { _qrParts = Array.Empty<string>(); ReportError(ex.Message); }
            ShowQrPart(0);
            if (_qrParts.Count > 1) PlayQr();
        }
        else
        {
            PauseQr();
            _qrParts = Array.Empty<string>();
            ShowQrPart(0);
        }
    }

    /// <summary>The link the shown code carries.</summary>
    public string? QrText => _qrIndex < _qrParts.Count ? _qrParts[_qrIndex] : null;

    /// <summary>The card takes several codes, shown one after another.</summary>
    public bool IsQrSeries => _qrParts.Count > 1;

    public string QrPartLabel => string.Format(Strings.Qr_PartOf, _qrIndex + 1, _qrParts.Count);

    public bool IsQrPlaying => _qrTimer is { IsEnabled: true };

    public string QrPlayPauseTip => IsQrPlaying ? Strings.Qr_Pause : Strings.Qr_Play;

    public string QrHint => IsQrSeries ? Strings.Qr_SeriesHint : Strings.Qr_SingleHint;

    private void ShowQrPart(int index)
    {
        _qrIndex = _qrParts.Count == 0 ? 0 : (index % _qrParts.Count + _qrParts.Count) % _qrParts.Count;
        this.RaisePropertyChanged(nameof(QrText));
        this.RaisePropertyChanged(nameof(IsQrSeries));
        this.RaisePropertyChanged(nameof(QrPartLabel));
        this.RaisePropertyChanged(nameof(QrHint));
    }

    private void PlayQr()
    {
        _qrTimer ??= new DispatcherTimer(QrPartInterval, DispatcherPriority.Background, (_, _) => ShowQrPart(_qrIndex + 1));
        _qrTimer.Start();
        RaiseQrPlaying();
    }

    private void PauseQr()
    {
        _qrTimer?.Stop();
        RaiseQrPlaying();
    }

    private void RaiseQrPlaying()
    {
        this.RaisePropertyChanged(nameof(IsQrPlaying));
        this.RaisePropertyChanged(nameof(QrPlayPauseTip));
    }

    // ---- Scanning -------------------------------------------------------

    /// <summary>The camera scanner of this platform, if any; elsewhere codes are read from images.</summary>
    public bool HasQrCamera => AppServices.QrCamera is not null;

    /// <summary>Which parts of a series came in so far (“Part 1 of 3 read…”); null when none is pending.</summary>
    public string? QrScanStatus
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    private Task ScanQrAsync() => RunAsync(async () =>
    {
        if (AppServices.QrCamera is not { } camera)
        {
            await ScanQrImageCoreAsync();
            return;
        }
        try
        {
            var start = _qrCollector.Count > 0 ? PendingStatus() : Strings.Qr_PointAtCode;
            await camera.ScanAsync(start, text => AddQrCode(text, fromCamera: true));
        }
        catch (QrCameraUnavailableException ex)
        {
            // No camera to use: the message points to the image tile next to it. Not opened from here: a
            // browser only shows a file picker right after a click, and the camera prompt used that up.
            ReportError(ex.Message);
        }
    });

    private Task ScanQrImageAsync() => RunAsync(ScanQrImageCoreAsync);

    private async Task ScanQrImageCoreAsync()
    {
        if (!StorageService.IsAvailable) return;
        var picked = await StorageService.OpenAsync(Strings.Qr_OpenImageTitle, "png", "jpg", "jpeg", "gif", "bmp", "webp");
        if (picked is not { } file) return;
        var text = await Task.Run(() => QrImage.Decode(file.Data));
        if (text is null) throw new FormatException(string.Format(Strings.Qr_NoCodeInImage, file.Name));
        if (!CardQr.TryParse(text, out _) && PairingCode.Parse(text) is null) throw new FormatException(Strings.Qr_NotACardCode);
        AddQrCode(text, fromCamera: false);
    }

    /// <summary>
    /// A link that opened the app (the web app's <c>#card=</c>): shown like a scanned code, in the add pane.
    /// </summary>
    public void ReceiveQrLink(string text)
    {
        if (!CardQr.TryParse(text, out _)) return;
        LeaveThen(() =>
        {
            if (_pane != CardsPane.Add) BeginAdd();
            try { AddQrCode(text, fromCamera: false); }
            catch (FormatException ex) { ReportError(ex.Message); }
        });
    }

    /// <summary>A card link pasted in the add pane (Export → Copy to clipboard, or a code's text).</summary>
    public void PasteCardLink(string? text)
    {
        text = text?.Trim();
        if (!CardQr.TryParse(text, out _))
        {
            ReportError(Strings.Cards_NoCardLinkInClipboard);
            return;
        }
        try { AddQrCode(text!, fromCamera: false); }
        catch (FormatException ex) { ReportError(ex.Message); }
    }

    /// <summary>Adds a scanned code; called on the scanner's thread, so the UI is updated through the dispatcher.</summary>
    private QrScanStep AddQrCode(string text, bool fromCamera)
    {
        // Another device showing cards to send.
        if (TryReceiveScanned(text)) return new QrScanStep(true, Strings.Sync_Connecting);
        CardQrScan result;
        CardSave? card;
        string status;
        lock (_qrCollector)
        {
            try { result = _qrCollector.Add(text); }
            catch (FormatException)
            {
                // A damaged series: start again rather than keep the parts that don't fit.
                _qrCollector.Reset();
                Dispatcher.UIThread.Post(() => QrScanStatus = null);
                if (fromCamera) return new QrScanStep(false, Strings.Qr_Damaged);
                throw;
            }
            card = _qrCollector.Card;
            status = result switch
            {
                CardQrScan.NotACard => Strings.Qr_NotACardCode,
                _ => PendingStatus(),
            };
            if (result == CardQrScan.Complete) _qrCollector.Reset();
        }

        if (result == CardQrScan.NotACard)
        {
            if (fromCamera) return new QrScanStep(false, status);
            throw new FormatException(status);
        }
        if (result != CardQrScan.Complete)
        {
            Dispatcher.UIThread.Post(() => QrScanStatus = status);
            return new QrScanStep(false, status);
        }

        Dispatcher.UIThread.Post(() =>
        {
            ScanFeedback.Found();
            // Reviewed (name, color, identity) before it is saved, like an imported file.
            ResetAddState();
            QrScanStatus = null;
            Form = new CardEditorViewModel(card!);
            SetScanMatches(card);
            FormSource = Strings.Qr_FromQrCode;
        });
        return new QrScanStep(true, Strings.Qr_CardReceived);
    }

    /// <summary>“Part 2 of 3 read. Missing: 1, 3.” for the series being collected.</summary>
    private string PendingStatus()
    {
        var missing = _qrCollector.Missing.ToList();
        return missing.Count == 0
            ? Strings.Qr_CardReceived
            : string.Format(Strings.Qr_PartsRead, _qrCollector.Received, _qrCollector.Count, string.Join(", ", missing));
    }
}
