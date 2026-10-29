using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;
using ReactiveUI;

namespace ChameLab.ViewModels;

/// <summary>
/// HF sniffing: while the Chameleon answers a reader as the card in its active slot (<c>HF14A_SNIFF</c>), records
/// the reader's frames and its own answers, names them, sums up the card and session, and recovers MIFARE Classic
/// keys from the authentications seen (mfkey32 from two, mfkey64 from one the card accepted). Captures save to and
/// load from Proxmark3 <c>.trace</c> files.
/// </summary>
public sealed partial class SniffViewModel
{
    private bool? _isHfSupported;
    private int _hfSeconds = 5;
    private IReadOnlyList<HfFrame> _hfFrames = [];
    private string? _hfSource;
    private HfSniffSummary? _hfSummary;

    private void InitHf(IObservable<bool> idle)
    {
        var canCapture = this.WhenAnyValue(x => x.IsBusy, x => x.IsHfSupported,
            (busy, supported) => !busy && supported != false);
        var hasCapture = this.WhenAnyValue(x => x.IsBusy, x => x.HfHasCapture, (busy, has) => !busy && has);
        HfCaptureCommand = ReactiveCommand.CreateFromTask(HfCaptureAsync, canCapture);
        HfLoadCommand = ReactiveCommand.CreateFromTask(HfLoadAsync, idle);
        HfSaveCommand = ReactiveCommand.CreateFromTask(HfSaveAsync, hasCapture);
    }

    public ICommand HfCaptureCommand { get; private set; } = null!;
    public ICommand HfLoadCommand { get; private set; } = null!;
    public ICommand HfSaveCommand { get; private set; } = null!;

    /// <summary>Whether the firmware has <c>HF14A_SNIFF</c>; null until asked (or without a device).</summary>
    public bool? IsHfSupported
    {
        get => _isHfSupported;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isHfSupported, value);
            this.RaisePropertyChanged(nameof(IsHfUnsupported));
        }
    }

    public bool IsHfUnsupported => IsHfSupported == false;

    public IReadOnlyList<int> HfDurations { get; } = [3, 5, 10, 20, 30];

    /// <summary>How long to listen, in seconds; the firmware takes up to 30.</summary>
    public int HfSeconds
    {
        get => _hfSeconds;
        set => this.RaiseAndSetIfChanged(ref _hfSeconds, Math.Clamp(value, 1, 30));
    }

    // ---- Capture -------------------------------------------------------

    private IReadOnlyList<HfFrame> HfFrames
    {
        get => _hfFrames;
        set
        {
            _hfFrames = value;
            _hfSummary = HfSniff.Summarize(value);
            HfAnnotated = HfSniff.Annotate(value);
            HfAuthGroups.Clear();
            foreach (var g in HfSniff.GroupAuths(HfSniff.AuthExchanges(value)))
                HfAuthGroups.Add(new HfAuthGroupViewModel(g));
            this.RaisePropertyChanged(nameof(HfAnnotated));
            this.RaisePropertyChanged(nameof(HfHasCapture));
            this.RaisePropertyChanged(nameof(HfHasAuths));
            this.RaisePropertyChanged(nameof(HfCanRecoverAll));
            this.RaisePropertyChanged(nameof(HfCountsText));
            this.RaisePropertyChanged(nameof(HfCardText));
            this.RaisePropertyChanged(nameof(HfSessionText));
            this.RaisePropertyChanged(nameof(HfHasAids));
            this.RaisePropertyChanged(nameof(HfAidsText));
        }
    }

    public bool HfHasCapture => _hfFrames.Count > 0;

    /// <summary>Where the capture shown came from: the device and when, or the file it was loaded from.</summary>
    public string? HfSource
    {
        get => _hfSource;
        private set => this.RaiseAndSetIfChanged(ref _hfSource, value);
    }

    private Task HfCaptureAsync() => RunAsync(async () =>
    {
        var client = _device.Client ?? throw new InvalidOperationException("Connect a Chameleon to capture.");
        var mode = await client.GetDeviceModeAsync();
        byte[] buffer;
        try
        {
            // The firmware listens through the running emulator: it has to be answering as a card.
            if (mode != DeviceMode.Tag) await client.SetDeviceModeAsync(DeviceMode.Tag);
            buffer = await client.Hf14aSniffAsync(HfSeconds * 1000);
        }
        finally
        {
            if (mode != DeviceMode.Tag)
                try { await client.SetDeviceModeAsync(mode); } catch { /* the capture's outcome matters more */ }
        }
        var frames = HfSniff.ParseChameleon(buffer);
        if (frames.Count == 0)
        {
            ScanFeedback.Missed();
            throw new InvalidOperationException(
                "No reader spoke. Hold the Chameleon to the reader while it listens, with an HF card in its active slot.");
        }
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            HfFrames = frames;
            HfSource = $"Captured at {DateTime.Now:HH:mm:ss}";
        });
        ScanFeedback.Found();
    });

    private Task HfLoadAsync() => RunAsync(async () =>
    {
        if (await StorageService.OpenAsync("Load HF trace", "trace") is not { } file) return;
        var frames = HfSniff.ParseTrace(file.Data);
        if (frames.Count == 0) throw new InvalidOperationException($"{file.Name} holds no ISO14443-A frames.");
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            HfFrames = frames;
            HfSource = file.Name;
        });
    });

    private Task HfSaveAsync() => RunAsync(async () =>
    {
        if (_hfFrames.Count == 0) return;
        var name = await StorageService.SaveAsync("Save HF trace",
            $"hf-sniff-{DateTime.Now:yyyyMMdd-HHmmss}.trace", HfSniff.BuildTrace(_hfFrames), "trace");
        if (name is not null) SetStatus($"Trace saved: {name}.");
    });

    // ---- Summary -------------------------------------------------------

    public string HfCountsText => _hfSummary is { } s
        ? $"{s.ReaderFrames + s.CardFrames} frames: {s.ReaderFrames} from the reader, {s.CardFrames} from the card"
        : "";

    public string HfCardText => _hfSummary is not { } s ? "" : string.Join(" · ", new[]
    {
        s.Uid is { } uid ? $"UID {Convert.ToHexString(uid)}" : "No completed SELECT",
        s.Atqa is { } atqa ? $"ATQA {Convert.ToHexString(atqa)}" : null,
        s.Sak is { } sak ? $"SAK {sak:X2}" : null,
        s.Rats ? "ISO14443-4" : null,
    }.Where(x => x is not null));

    public string HfSessionText
    {
        get
        {
            if (_hfSummary is not { } s) return "";
            var parts = new List<string>();
            if (s.Auths.Count > 0)
                parts.Add("MIFARE Classic auth on " + string.Join(", ",
                    s.Auths.Select(a => $"block {a.Block} key {(a.KeyType == MfKeyType.A ? 'A' : 'B')}")));
            if (s.Arqc) parts.Add("EMV ARQC requested");
            if (s.Tc) parts.Add("EMV TC requested");
            if (s.Halted) parts.Add("ended with HALT");
            return parts.Count == 0 ? "Nothing more recognized" : string.Join(" · ", parts);
        }
    }

    public bool HfHasAids => _hfSummary is { Aids.Count: > 0 };
    public string HfAidsText => _hfSummary is { } s ? string.Join('\n', s.Aids) : "";

    // ---- Frames --------------------------------------------------------

    public IReadOnlyList<HfAnnotatedFrame> HfAnnotated { get; private set; } = [];

    /// <summary>Frames as text, one a line, for the clipboard.</summary>
    public static string FramesText(IEnumerable<HfAnnotatedFrame> frames) => string.Join('\n', frames
        .OrderBy(a => a.Index)
        .Select(a => $"{a.Index,4}  {(a.Frame.IsReader ? "Reader" : "  Card")}  {a.Frame.Hex}  {a.Label}"));

    // ---- Key recovery --------------------------------------------------

    public ObservableCollection<HfAuthGroupViewModel> HfAuthGroups { get; } = new();

    public bool HfHasAuths => HfAuthGroups.Count > 0;
    public bool HfCanRecoverAll => HfAuthGroups.Count(g => g.CanRecover) > 1;

    public ICommand HfRecoverAllCommand => _hfRecoverAll ??= ReactiveCommand.CreateFromTask(async () =>
    {
        foreach (var g in HfAuthGroups.Where(g => g.CanRecover && g.Key is null).ToList())
            await g.RecoverAsync();
    });

    private ICommand? _hfRecoverAll;

    // ---- View helpers --------------------------------------------------

    public static readonly Avalonia.Data.Converters.IValueConverter BandCaption =
        new Avalonia.Data.Converters.FuncValueConverter<bool, string>(hf => hf
            ? "Listen to a 13.56 MHz reader as a card: see what it asks for and recover MIFARE Classic keys from its authentications."
            : "Record the raw 125 kHz signal at the Chameleon's antenna and take it apart: levels, modulation and the bits it carries.");

    public static readonly Avalonia.Data.Converters.IValueConverter DirectionLabel =
        new Avalonia.Data.Converters.FuncValueConverter<bool, string>(reader => reader ? "Reader" : "Card");

    public static readonly Avalonia.Data.Converters.IValueConverter CaptureLabel =
        new Avalonia.Data.Converters.FuncValueConverter<bool, string>(busy => busy ? "Listening…" : "Capture");
}

/// <summary>One sniffed authentication group (card, block, key type) and the key recovered from it.</summary>
public sealed class HfAuthGroupViewModel : ViewModelBase
{
    private readonly HfAuthGroup _group;
    private string? _key;
    private string? _result;
    private string? _method;
    private bool _isRecovering;

    public HfAuthGroupViewModel(HfAuthGroup group)
    {
        _group = group;
        RecoverCommand = ReactiveCommand.CreateFromTask(RecoverAsync,
            this.WhenAnyValue(x => x.IsRecovering, recovering => !recovering));
    }

    public string Title =>
        $"Block {_group.Block} (sector {_group.Sector}) · key {_group.KeyChar} · UID {_group.Uid:X8}";

    public string Exchanges => string.Join('\n', _group.Exchanges.Select((e, i) =>
        $"{i + 1}. nt {e.Nt:X8}  nr {e.NrEnc:X8}  ar {e.ArEnc:X8}" + (e.AtEnc is { } at ? $"  at {at:X8}" : "")));

    /// <summary>mfkey64 needs the card's answer; mfkey32 two runs.</summary>
    public bool CanRecover => _group.Exchanges.Any(e => e.AtEnc is not null) || _group.Exchanges.Count >= 2;

    public string Hint => CanRecover
        ? ""
        : "One authentication only: capture again so the reader tries a second time.";

    /// <summary>The <c>mfkey32v2</c> command for the first two exchanges, to run elsewhere.</summary>
    public string? Mfkey32Command => _group.Exchanges.Count >= 2
        ? $"mfkey32v2 {_group.Uid:X8} {E(0).Nt:X8} {E(0).NrEnc:X8} {E(0).ArEnc:X8} {E(1).Nt:X8} {E(1).NrEnc:X8} {E(1).ArEnc:X8}"
        : null;

    private HfAuthExchange E(int i) => _group.Exchanges[i];

    public ICommand RecoverCommand { get; }

    public bool IsRecovering
    {
        get => _isRecovering;
        private set => this.RaiseAndSetIfChanged(ref _isRecovering, value);
    }

    public string? Key
    {
        get => _key;
        private set => this.RaiseAndSetIfChanged(ref _key, value);
    }

    /// <summary>Which attack found <see cref="Key"/>: mfkey32 or mfkey64.</summary>
    public string? Method
    {
        get => _method;
        private set => this.RaiseAndSetIfChanged(ref _method, value);
    }

    public string? Result
    {
        get => _result;
        private set => this.RaiseAndSetIfChanged(ref _result, value);
    }

    public async Task RecoverAsync()
    {
        if (IsRecovering || !CanRecover) return;
        IsRecovering = true;
        Result = "Recovering…";
        Key = null;
        try
        {
            var found = await Task.Run(() => Mfkey32Recovery.Recover(_group));
            Key = found is { } f ? string.Join(' ', f.Key.ToString("X12").Chunk(2).Select(c => new string(c))) : null;
            Method = found?.Method;
            Result = found is null ? "No unique key: capture more authentications." : null;
        }
        catch (Exception ex)
        {
            Result = ex.Message;
        }
        finally
        {
            IsRecovering = false;
        }
    }
}
