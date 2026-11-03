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
using ChameLab.Localization;

namespace ChameLab.ViewModels;

/// <summary>
/// HF sniffing: while the Chameleon answers a reader as the card in its active slot (<c>HF14A_SNIFF</c>), records
/// the reader's frames and its own answers, names them, sums up the card and session, and recovers MIFARE Classic
/// keys from the authentications seen (mfkey32 from two, mfkey64 from one the card accepted). Captures save to and
/// load from Proxmark3 <c>.trace</c> files.
/// </summary>
public sealed partial class ToolsViewModel
{
    private IReadOnlyList<HfFrame> _hfFrames = [];
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
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(IsHfUnsupported));
        }
    }

    public bool IsHfUnsupported => IsHfSupported == false;

    public IReadOnlyList<int> HfDurations { get; } = [3, 5, 10, 20, 30];

    /// <summary>How long to listen, in seconds; the firmware takes up to 30.</summary>
    public int HfSeconds
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, Math.Clamp(value, 1, 30));
    } = 5;

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
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    private Task HfCaptureAsync() => RunAsync(async () =>
    {
        var client = _device.Client ?? throw new InvalidOperationException(Strings.Tools_ConnectToCapture);
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
                Strings.Tools_NoReaderSpoke);
        }
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            HfFrames = frames;
            HfSource = string.Format(Strings.Tools_CapturedAt, DateTime.Now);
        });
        ScanFeedback.Found();
    });

    private Task HfLoadAsync() => RunAsync(async () =>
    {
        if (await StorageService.OpenAsync(Strings.Tools_LoadHfTitle, "trace") is not { } file) return;
        var frames = HfSniff.ParseTrace(file.Data);
        if (frames.Count == 0) throw new InvalidOperationException(string.Format(Strings.Tools_NoIsoFrames, file.Name));
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            HfFrames = frames;
            HfSource = file.Name;
        });
    });

    private Task HfSaveAsync() => RunAsync(async () =>
    {
        if (_hfFrames.Count == 0) return;
        var name = await StorageService.SaveAsync(Strings.Tools_SaveHfTitle,
            $"hf-sniff-{DateTime.Now:yyyyMMdd-HHmmss}.trace", HfSniff.BuildTrace(_hfFrames), "trace");
        if (name is not null) Notify(string.Format(Strings.Tools_TraceSaved, name));
    });

    // ---- Summary -------------------------------------------------------

    public string HfCountsText => _hfSummary is { } s
        ? string.Format(Strings.Tools_FramesSummary, s.ReaderFrames + s.CardFrames, s.ReaderFrames, s.CardFrames)
        : "";

    public string HfCardText => _hfSummary is not { } s ? "" : string.Join(" · ", new[]
    {
        s.Uid is { } uid ? $"UID {Convert.ToHexString(uid)}" : Strings.Tools_NoSelect,
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
                parts.Add(Strings.Tools_ClassicAuthOn + string.Join(", ",
                    s.Auths.Select(a => string.Format(Strings.Tools_BlockKey, a.Block, (a.KeyType == MfKeyType.A ? 'A' : 'B')))));
            if (s.Arqc) parts.Add(Strings.Tools_ArqcRequested);
            if (s.Tc) parts.Add(Strings.Tools_TcRequested);
            if (s.Halted) parts.Add(Strings.Tools_EndedHalt);
            return parts.Count == 0 ? Strings.Tools_NothingMore : string.Join(" · ", parts);
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

    public ICommand HfRecoverAllCommand => field ??= ReactiveCommand.CreateFromTask(async () =>
    {
        foreach (var g in HfAuthGroups.Where(g => g is { CanRecover: true, Key: null }).ToList())
            await g.RecoverAsync();
    });

    // ---- View helpers --------------------------------------------------

    public static readonly Avalonia.Data.Converters.IValueConverter ToolTitle =
        new Avalonia.Data.Converters.FuncValueConverter<bool, string>(hf => hf ? Strings.Tools_SniffHF : Strings.Tools_SniffLF);

    public static readonly Avalonia.Data.Converters.IValueConverter DirectionLabel =
        new Avalonia.Data.Converters.FuncValueConverter<bool, string>(reader => reader ? Strings.Chameleon_Reader : Strings.Common_Card);

    public static readonly Avalonia.Data.Converters.IValueConverter CaptureLabel =
        new Avalonia.Data.Converters.FuncValueConverter<bool, string>(busy => busy ? Strings.Tools_Listening : Strings.Tools_Capture);
}

/// <summary>One sniffed authentication group (card, block, key type) and the key recovered from it.</summary>
public sealed class HfAuthGroupViewModel : ViewModelBase
{
    private readonly HfAuthGroup _group;

    public HfAuthGroupViewModel(HfAuthGroup group)
    {
        _group = group;
        RecoverCommand = ReactiveCommand.CreateFromTask(RecoverAsync,
            this.WhenAnyValue(x => x.IsRecovering, recovering => !recovering));
    }

    public string Title =>
        string.Format(Strings.Tools_AuthGroupHeader, _group.Block, _group.Sector, _group.KeyChar, _group.Uid);

    public string Exchanges => string.Join('\n', _group.Exchanges.Select((e, i) =>
        $"{i + 1}. nt {e.Nt:X8}  nr {e.NrEnc:X8}  ar {e.ArEnc:X8}" + (e.AtEnc is { } at ? $"  at {at:X8}" : "")));

    /// <summary>mfkey64 needs the card's answer; mfkey32 two runs.</summary>
    public bool CanRecover => _group.Exchanges.Any(e => e.AtEnc is not null) || _group.Exchanges.Count >= 2;

    public string Hint => CanRecover
        ? ""
        : Strings.Tools_OneAuthOnly;

    /// <summary>The <c>mfkey32v2</c> command for the first two exchanges, to run elsewhere.</summary>
    public string? Mfkey32Command => _group.Exchanges.Count >= 2
        ? $"mfkey32v2 {_group.Uid:X8} {E(0).Nt:X8} {E(0).NrEnc:X8} {E(0).ArEnc:X8} {E(1).Nt:X8} {E(1).NrEnc:X8} {E(1).ArEnc:X8}"
        : null;

    private HfAuthExchange E(int i) => _group.Exchanges[i];

    public ICommand RecoverCommand { get; }

    public bool IsRecovering
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public string? Key
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>Which attack found <see cref="Key"/>: mfkey32 or mfkey64.</summary>
    public string? Method
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public string? Result
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public async Task RecoverAsync()
    {
        if (IsRecovering || !CanRecover) return;
        IsRecovering = true;
        Result = Strings.Tools_Recovering;
        Key = null;
        try
        {
            var found = await Task.Run(() => Mfkey32Recovery.Recover(_group));
            Key = found is { } f ? string.Join(' ', f.Key.ToString("X12").Chunk(2).Select(c => new string(c))) : null;
            Method = found?.Method;
            Result = found is null ? Strings.Tools_NoUniqueKey : null;
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
