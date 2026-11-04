using System;
using System.Collections.Generic;
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
/// The Tools page: a choice of tools, each opening its own view with a Back button (like Add card). HF sniffing
/// is in ToolsViewModel.Hf.cs. LF sniffing records the 125 kHz antenna signal (<c>LF_SNIFF</c>), shows it as a plot, sums up its levels,
/// guesses its modulation and decodes it as Manchester. Captures save to and load from raw <c>.bin</c> files
/// (one byte per sample, the same as the CLI's <c>lf sniff --out</c>), so a saved one can be studied without
/// a device.
/// </summary>
public sealed partial class ToolsViewModel : PageViewModelBase
{
    /// <summary>How long the firmware may wait for samples; its 4000-sample buffer fills in 32 ms.</summary>
    private const int CaptureTimeoutMs = 2000;

    private readonly DeviceService _device;
    private byte[]? _samples;
    private LfSniffSummary? _summary;
    private LfModulationResult? _modulation;
    private LfManchesterDecode? _decode;
    private int _clockDivisor = 64;
    private bool _invert;
    private double _zoom = 1;
    private int _cursorIndex = -1;
    private SniffTool _tool;

    public ToolsViewModel(DeviceService device) : base(() => Strings.Tools_Tools, "tools")
    {
        _device = device;
        var idle = this.WhenAnyValue(x => x.IsBusy, busy => !busy);
        var canCapture = this.WhenAnyValue(x => x.IsBusy, x => x.IsSupported,
            (busy, supported) => !busy && supported != false);
        var hasCapture = this.WhenAnyValue(x => x.IsBusy, x => x.HasCapture, (busy, has) => !busy && has);
        CaptureCommand = ReactiveCommand.CreateFromTask(CaptureAsync, canCapture);
        LoadCommand = ReactiveCommand.CreateFromTask(LoadAsync, idle);
        SaveCommand = ReactiveCommand.CreateFromTask(SaveAsync, hasCapture);
        InitHf(idle);
        OpenHfCommand = ReactiveCommand.Create(() => { Tool = SniffTool.Hf; });
        OpenLfCommand = ReactiveCommand.Create(() => { Tool = SniffTool.Lf; });
        BackCommand = ReactiveCommand.Create(() => { Tool = SniffTool.None; }, idle);
        _device.StateChanged += _ => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            this.RaisePropertyChanged(nameof(IsConnected));
            IsSupported = null;
            IsHfSupported = null;
        });
    }

    public ICommand CaptureCommand { get; }
    public ICommand LoadCommand { get; }
    public ICommand SaveCommand { get; }

    public bool CanUseFiles => StorageService.IsAvailable;
    public bool IsConnected => _device.State == ConnectionState.Connected;

    /// <summary>Whether the firmware has <c>LF_SNIFF</c>; null until asked (or without a device).</summary>
    public bool? IsSupported
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(IsUnsupported));
        }
    }

    public bool IsUnsupported => IsSupported == false;

    /// <summary>The tool shown; <see cref="SniffTool.None"/> for the list of tools.</summary>
    public SniffTool Tool
    {
        get => _tool;
        private set
        {
            this.RaiseAndSetIfChanged(ref _tool, value);
            this.RaisePropertyChanged(nameof(IsHome));
            this.RaisePropertyChanged(nameof(IsHf));
            this.RaisePropertyChanged(nameof(IsLf));
        }
    }

    public bool IsHome => _tool == SniffTool.None;
    public bool IsHf => _tool == SniffTool.Hf;
    public bool IsLf => _tool == SniffTool.Lf;

    public ICommand OpenHfCommand { get; }
    public ICommand OpenLfCommand { get; }
    /// <summary>Back from a tool to the list; what it captured stays for the next visit.</summary>
    public ICommand BackCommand { get; }

    public override async Task OnActivatedAsync()
    {
        if (!IsConnected || IsSupported is not null && IsHfSupported is not null || _device.Client is not { } client) return;
        try
        {
            var capabilities = await client.GetCapabilitiesAsync();
            // Firmware without the capability list returns none: then just try.
            if (capabilities.Count == 0) return;
            IsSupported = capabilities.Contains((ushort)Command.LfSniff);
            IsHfSupported = capabilities.Contains((ushort)Command.Hf14aSniff);
        }
        catch { /* unknown: just try */ }
    }

    // ---- Capture -------------------------------------------------------

    public byte[]? Samples
    {
        get => _samples;
        private set
        {
            this.RaiseAndSetIfChanged(ref _samples, value);
            _summary = value is null ? null : LfSniff.Summarize(value);
            _modulation = value is null ? null : LfSniff.DetectModulation(value, _summary);
            // Decode at the bit rate the capture appears to use.
            if (_modulation?.ClockDivisor is { } clock) _clockDivisor = clock;
            Zoom = 1;
            CursorIndex = -1;
            this.RaisePropertyChanged(nameof(ClockDivisor));
            this.RaisePropertyChanged(nameof(HasCapture));
            this.RaisePropertyChanged(nameof(Summary));
            this.RaisePropertyChanged(nameof(SamplesText));
            this.RaisePropertyChanged(nameof(RangeText));
            this.RaisePropertyChanged(nameof(GapsText));
            this.RaisePropertyChanged(nameof(ModulationText));
            this.RaisePropertyChanged(nameof(ClockText));
            SamplesBuffer = value is null ? null : SampleRows(value);
            this.RaisePropertyChanged(nameof(SamplesBuffer));
            Redecode();
        }
    }

    public bool HasCapture => _samples is { Length: > 0 };

    /// <summary>Where the capture shown came from: the device and when, or the file it was loaded from.</summary>
    public string? Source
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    private Task CaptureAsync() => RunAsync(async () =>
    {
        var client = _device.Client ?? throw new InvalidOperationException(Strings.Tools_ConnectToCapture);
        var mode = await client.GetDeviceModeAsync();
        byte[] samples;
        try
        {
            if (mode != DeviceMode.Reader) await client.SetDeviceModeAsync(DeviceMode.Reader);
            samples = await client.LfSniffAsync(CaptureTimeoutMs);
        }
        finally
        {
            if (mode != DeviceMode.Reader)
                try { await client.SetDeviceModeAsync(mode); } catch { /* the capture's outcome matters more */ }
        }
        if (samples.Length == 0) throw new InvalidOperationException(Strings.Tools_NoLfSamples);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Samples = samples;
            Source = string.Format(Strings.Tools_CapturedAt, DateTime.Now);
        });
        ScanFeedback.Found();
    });

    private Task LoadAsync() => RunAsync(async () =>
    {
        if (await StorageService.OpenAsync(Strings.Tools_LoadLfTitle, "bin") is not { } file) return;
        if (file.Data.Length == 0) throw new InvalidOperationException(string.Format(Strings.Tools_FileEmpty, file.Name));
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Samples = file.Data;
            Source = file.Name;
        });
    });

    private Task SaveAsync() => RunAsync(async () =>
    {
        if (_samples is not { } samples) return;
        var name = await StorageService.SaveAsync(Strings.Tools_SaveLfTitle,
            $"lf-sniff-{DateTime.Now:yyyyMMdd-HHmmss}.bin", samples, "bin");
        if (name is not null) Notify(string.Format(Strings.Tools_CaptureSaved, name));
    });

    // ---- Summary -------------------------------------------------------

    public LfSniffSummary? Summary => _summary;

    public string SamplesText => _summary is { } s
        ? string.Format(Strings.Tools_SamplesDuration, s.SampleCount, s.DurationUs / 1000.0)
        : "";

    public string RangeText => _summary is { } s
        ? string.Format(Strings.Tools_RangeMean, s.Min, s.Max, s.Mean)
        : "";

    public string GapsText => _summary is { } s
        ? s.GapCount == 0
            ? string.Format(Strings.Tools_NoGaps, s.GapThreshold)
            : string.Format(Strings.Tools_GapSamples, s.GapCount, s.GapThreshold)
        : "";

    public string ModulationText => _modulation is { } m ? m.Kind.Label() : "";

    public string ClockText => _modulation is { ClockDivisor: { } clock, RunSamples: { } run }
        ? string.Format(Strings.Tools_ClockRun, clock, run, run * LfSniff.SampleUs)
        : "-";

    // ---- Waveform ------------------------------------------------------

    public double Zoom
    {
        get => _zoom;
        set
        {
            this.RaiseAndSetIfChanged(ref _zoom, Math.Clamp(value, 1, Controls.LfWaveform.MaxZoom));
            this.RaisePropertyChanged(nameof(ZoomText));
        }
    }

    public string ZoomText => $"{_zoom:0.#}×";

    /// <summary>Sample under the waveform's cursor; -1 for none.</summary>
    public int CursorIndex
    {
        get => _cursorIndex;
        set
        {
            this.RaiseAndSetIfChanged(ref _cursorIndex, value);
            this.RaisePropertyChanged(nameof(CursorText));
        }
    }

    /// <summary>Where the cursor is and what the signal reads there.</summary>
    public string CursorText => _samples is { } s && _cursorIndex >= 0 && _cursorIndex < s.Length && _summary is { } sum
        ? string.Format(Strings.Tools_CursorSample, _cursorIndex, _cursorIndex * LfSniff.SampleUs / 1000.0, s[_cursorIndex]) +
          (s[_cursorIndex] < sum.GapThreshold ? Strings.Tools_Gap : s[_cursorIndex] > sum.Threshold ? Strings.Tools_High : Strings.Tools_Low)
        : WaveformHint;

    /// <summary>How to zoom and pan, for the input the device has.</summary>
    public static string WaveformHint { get; } = OperatingSystem.IsAndroid() || OperatingSystem.IsIOS()
        ? Strings.Tools_WaveHintTouch
        : Strings.Tools_WaveHintMouse;

    // ---- Decode --------------------------------------------------------

    public IReadOnlyList<int> ClockDivisors => LfSniff.ClockDivisors;

    public int ClockDivisor
    {
        get => _clockDivisor;
        set
        {
            if (value <= 0 || value == _clockDivisor) return;
            this.RaiseAndSetIfChanged(ref _clockDivisor, value);
            Redecode();
        }
    }

    public bool Invert
    {
        get => _invert;
        set
        {
            if (value == _invert) return;
            this.RaiseAndSetIfChanged(ref _invert, value);
            Redecode();
        }
    }

    private void Redecode()
    {
        _decode = _samples is { Length: > 0 } s ? LfSniff.DecodeManchester(s, _clockDivisor, _invert, _summary) : null;
        this.RaisePropertyChanged(nameof(HasBits));
        this.RaisePropertyChanged(nameof(DecodeText));
        this.RaisePropertyChanged(nameof(Bits));
        this.RaisePropertyChanged(nameof(BitsHex));
        this.RaisePropertyChanged(nameof(BitsPlain));
    }

    public bool HasBits => _decode is { Bits.Count: > 0 };

    public string DecodeText => _decode switch
    {
        null => "",
        { Bits.Count: 0 } => Strings.Tools_NoBits,
        { Errors: 0 } d => Loc.Plural(Strings.Plural_Bits, d.Bits.Count),
        var d => string.Format(Strings.Tools_BitsBreaks, Loc.Plural(Strings.Plural_Bits, d.Bits.Count), Loc.Plural(Strings.Plural_Breaks, d.Errors)),
    };

    /// <summary>The decoded bits in groups of eight, wrapping with the width.</summary>
    public string Bits => _decode is { } d ? string.Join(' ', d.BitString.Chunk(8).Select(c => new string(c))) : "";

    /// <summary>The decoded bits in one run, for the clipboard.</summary>
    public string BitsPlain => _decode?.BitString ?? "";

    public string BitsHex => _decode?.Hex ?? "";

    // ---- Hex -----------------------------------------------------------

    /// <summary>Every sample in a read-only hex view, 16 to a row labelled by its offset.</summary>
    public Controls.HexBuffer? SamplesBuffer { get; private set; }

    private static Controls.HexBuffer SampleRows(byte[] samples) =>
        new(samples.Chunk(16).Select(c => c.Length == 16 ? c : [.. c, .. new byte[16 - c.Length]]).ToList(), 16)
        {
            IsReadOnly = true,
            RowLabel = row => (row * 16).ToString("X4"),
        };
}

public enum SniffTool { None, Hf, Lf }
