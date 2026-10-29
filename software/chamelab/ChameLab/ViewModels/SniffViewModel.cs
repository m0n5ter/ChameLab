using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;
using ReactiveUI;

namespace ChameLab.ViewModels;

/// <summary>
/// LF sniffing: records the 125 kHz antenna signal (<c>LF_SNIFF</c>), shows it as a plot, sums up its levels,
/// guesses its modulation and decodes it as Manchester. Captures save to and load from raw <c>.bin</c> files
/// (one byte per sample, the same as the CLI's <c>lf sniff --out</c>), so a saved one can be studied without
/// a device.
/// </summary>
public sealed class SniffViewModel : PageViewModelBase
{
    /// <summary>How long the firmware may wait for samples; its 4000-sample buffer fills in 32 ms.</summary>
    private const int CaptureTimeoutMs = 2000;

    private readonly DeviceService _device;
    private byte[]? _samples;
    private string? _source;
    private LfSniffSummary? _summary;
    private LfModulationResult? _modulation;
    private LfManchesterDecode? _decode;
    private int _clockDivisor = 64;
    private bool _invert;
    private double _zoom = 1;
    private bool? _isSupported;

    public SniffViewModel(DeviceService device) : base("Sniff", "sniff")
    {
        _device = device;
        var idle = this.WhenAnyValue(x => x.IsBusy, busy => !busy);
        var canCapture = this.WhenAnyValue(x => x.IsBusy, x => x.IsSupported,
            (busy, supported) => !busy && supported != false);
        var hasCapture = this.WhenAnyValue(x => x.IsBusy, x => x.HasCapture, (busy, has) => !busy && has);
        CaptureCommand = ReactiveCommand.CreateFromTask(CaptureAsync, canCapture);
        LoadCommand = ReactiveCommand.CreateFromTask(LoadAsync, idle);
        SaveCommand = ReactiveCommand.CreateFromTask(SaveAsync, hasCapture);
        _device.StateChanged += _ => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            this.RaisePropertyChanged(nameof(IsConnected));
            IsSupported = null;
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
        get => _isSupported;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isSupported, value);
            this.RaisePropertyChanged(nameof(IsUnsupported));
        }
    }

    public bool IsUnsupported => IsSupported == false;

    public override async Task OnActivatedAsync()
    {
        if (!IsConnected || IsSupported is not null || _device.Client is not { } client) return;
        try
        {
            var capabilities = await client.GetCapabilitiesAsync();
            // Firmware without the capability list returns none: then just try.
            IsSupported = capabilities.Count == 0 ? null : capabilities.Contains((ushort)Command.LfSniff);
        }
        catch { IsSupported = null; }
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
            this.RaisePropertyChanged(nameof(ClockDivisor));
            this.RaisePropertyChanged(nameof(HasCapture));
            this.RaisePropertyChanged(nameof(Summary));
            this.RaisePropertyChanged(nameof(SamplesText));
            this.RaisePropertyChanged(nameof(RangeText));
            this.RaisePropertyChanged(nameof(GapsText));
            this.RaisePropertyChanged(nameof(ModulationText));
            this.RaisePropertyChanged(nameof(ClockText));
            this.RaisePropertyChanged(nameof(HexDump));
            this.RaisePropertyChanged(nameof(HexDumpCaption));
            this.RaisePropertyChanged(nameof(HexText));
            Redecode();
        }
    }

    public bool HasCapture => _samples is { Length: > 0 };

    /// <summary>Where the capture shown came from: the device and when, or the file it was loaded from.</summary>
    public string? Source
    {
        get => _source;
        private set => this.RaiseAndSetIfChanged(ref _source, value);
    }

    private Task CaptureAsync() => RunAsync(async () =>
    {
        var client = _device.Client ?? throw new InvalidOperationException("Connect a Chameleon to capture.");
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
        if (samples.Length == 0) throw new InvalidOperationException("No LF samples were captured.");
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Samples = samples;
            Source = $"Captured at {DateTime.Now:HH:mm:ss}";
        });
        ScanFeedback.Found();
    });

    private Task LoadAsync() => RunAsync(async () =>
    {
        if (await StorageService.OpenAsync("Load LF capture", "bin") is not { } file) return;
        if (file.Data.Length == 0) throw new InvalidOperationException($"{file.Name} is empty.");
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Samples = file.Data;
            Source = file.Name;
        });
    });

    private Task SaveAsync() => RunAsync(async () =>
    {
        if (_samples is not { } samples) return;
        var name = await StorageService.SaveAsync("Save LF capture",
            $"lf-sniff-{DateTime.Now:yyyyMMdd-HHmmss}.bin", samples, "bin");
        if (name is not null) SetStatus($"Capture saved: {name}.");
    });

    // ---- Summary -------------------------------------------------------

    public LfSniffSummary? Summary => _summary;

    public string SamplesText => _summary is { } s
        ? $"{s.SampleCount} samples · {s.DurationUs / 1000.0:0.0} ms"
        : "";

    public string RangeText => _summary is { } s
        ? $"0x{s.Min:X2}–0x{s.Max:X2} · mean 0x{s.Mean:X2}"
        : "";

    public string GapsText => _summary is { } s
        ? s.GapCount == 0
            ? $"None below 0x{s.GapThreshold:X2}: a steady field"
            : $"{s.GapCount} samples below 0x{s.GapThreshold:X2}"
        : "";

    public string ModulationText => _modulation is { } m ? m.Kind.Label() : "";

    public string ClockText => _modulation is { ClockDivisor: { } clock, RunSamples: { } run }
        ? $"RF/{clock} · shortest run {run} samples ({run * LfSniff.SampleUs} µs)"
        : "—";

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
    }

    public bool HasBits => _decode is { Bits.Count: > 0 };

    public string DecodeText => _decode switch
    {
        null => "",
        { Bits.Count: 0 } => "No bits decoded. Try another clock or Invert.",
        { Errors: 0 } d => $"{d.Bits.Count} bits",
        var d => $"{d.Bits.Count} bits · {d.Errors} {(d.Errors == 1 ? "break" : "breaks")} in the code",
    };

    /// <summary>The decoded bits, 32 to a line.</summary>
    public string Bits => _decode is { } d ? string.Join('\n', d.BitString.Chunk(32).Select(c => new string(c))) : "";

    public string BitsHex => _decode?.Hex ?? "";

    // ---- Hex -----------------------------------------------------------

    private const int HexDumpBytes = 512;

    public string HexDump => _samples is { } s ? LfSniff.HexDump(s, HexDumpBytes) : "";

    public string HexDumpCaption => _samples is { Length: > HexDumpBytes } s
        ? $"The first {HexDumpBytes} of {s.Length} samples. Save the capture to keep them all."
        : "All samples.";

    /// <summary>Every sample as hex, for the clipboard.</summary>
    public string HexText => _samples is { } s ? Convert.ToHexString(s) : "";
}
