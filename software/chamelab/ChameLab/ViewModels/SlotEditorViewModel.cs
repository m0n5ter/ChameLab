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
/// Editor for one emulation slot: HF / LF tag type and nick, enable toggles,
/// clear / reset, and (for the active slot) the MIFARE Classic and Ultralight
/// emulator settings. All firmware calls act on the active slot, so the editor
/// activates its slot before reading or writing anything. Type, nick, enable
/// and colour edits are held until <see cref="SaveAsync"/> (see <see cref="IsDirty"/>).
/// </summary>
public sealed class SlotEditorViewModel : PageViewModelBase
{
    private readonly DeviceService _device;
    private readonly int _slot;
    private readonly Action _onChanged;

    private TagSpecificType _hfType;
    private TagSpecificType _lfType;
    private string _hfNick = string.Empty;
    private string _lfNick = string.Empty;
    private bool _hfEnabled;
    private bool _lfEnabled;
    private string? _hfColor;
    private string? _lfColor;

    /// <summary>What the slot holds on the device; the edits are compared against it.</summary>
    private sealed record Snapshot(
        TagSpecificType HfType, TagSpecificType LfType, string HfNick, string LfNick,
        bool HfEnabled, bool LfEnabled, string? HfColor, string? LfColor);

    private Snapshot _saved;

    private bool _showMfcSettings;
    private bool _showUlSettings;
    private bool _gen1a, _gen2, _blockAntiColl, _mfcDetection, _fieldOffReset;
    private EmulatorWriteMode _mfcWriteMode;
    private MifareClassicPrngType _prng;
    private bool _ulUidMagic, _ulDetection;
    private EmulatorWriteMode _ulWriteMode;

    public SlotEditorViewModel(DeviceService device, SlotInfo info, Action onChanged)
        : base($"Slot {info.Index}", "slots")
    {
        _device = device;
        _slot = info.Index;
        _onChanged = onChanged;

        _hfType = info.HfType;
        _lfType = info.LfType;
        _hfNick = info.HfNick;
        _lfNick = info.LfNick;
        _hfEnabled = info.HfEnabled;
        _lfEnabled = info.LfEnabled;
        _hfColor = info.HfColor is { } hf ? SlotColors.SnapToPalette(hf) : null;
        _lfColor = info.LfColor is { } lf ? SlotColors.SnapToPalette(lf) : null;
        _saved = Current;

        ApplyCommand = ReactiveCommand.CreateFromTask(SaveAsync);
        ClearHfCommand = ReactiveCommand.CreateFromTask(() => ClearAsync(TagSenseType.Hf));
        ClearLfCommand = ReactiveCommand.CreateFromTask(() => ClearAsync(TagSenseType.Lf));
        ResetDefaultCommand = ReactiveCommand.CreateFromTask(ResetDefaultAsync);
        ApplyEmulatorCommand = ReactiveCommand.CreateFromTask(ApplyEmulatorAsync);
        ResetHfColorCommand = ReactiveCommand.Create(() => SetColor(TagSenseType.Hf, null));
        ResetLfColorCommand = ReactiveCommand.Create(() => SetColor(TagSenseType.Lf, null));
    }

    public int SlotIndex => _slot;

    public static IReadOnlyList<TagSpecificType> HfTypes { get; } =
        new[] { TagSpecificType.Undefined }.Concat(TagTypeExtensions.HfTypes()).ToArray();

    public static IReadOnlyList<TagSpecificType> LfTypes { get; } =
        new[] { TagSpecificType.Undefined }.Concat(TagTypeExtensions.LfTypes()).ToArray();

    public static IReadOnlyList<EmulatorWriteMode> WriteModes { get; } = new[]
    {
        EmulatorWriteMode.Normal, EmulatorWriteMode.Denied,
        EmulatorWriteMode.Deceive, EmulatorWriteMode.Shadow,
    };

    public static IReadOnlyList<MifareClassicPrngType> PrngTypes { get; } = new[]
    {
        MifareClassicPrngType.Static, MifareClassicPrngType.Weak, MifareClassicPrngType.Hard,
    };

    public TagSpecificType HfType { get => _hfType; set { this.RaiseAndSetIfChanged(ref _hfType, value); RaiseDirty(); } }
    public TagSpecificType LfType { get => _lfType; set { this.RaiseAndSetIfChanged(ref _lfType, value); RaiseDirty(); } }
    public string HfNick { get => _hfNick; set { this.RaiseAndSetIfChanged(ref _hfNick, value); RaiseDirty(); } }
    public string LfNick { get => _lfNick; set { this.RaiseAndSetIfChanged(ref _lfNick, value); RaiseDirty(); } }
    public bool HfEnabled { get => _hfEnabled; set { this.RaiseAndSetIfChanged(ref _hfEnabled, value); RaiseDirty(); } }
    public bool LfEnabled { get => _lfEnabled; set { this.RaiseAndSetIfChanged(ref _lfEnabled, value); RaiseDirty(); } }

    private Snapshot Current => new(_hfType, _lfType, _hfNick, _lfNick, _hfEnabled, _lfEnabled, _hfColor, _lfColor);

    /// <summary>Type, nick, enable or colour differ from what the slot holds (drives the save bar).</summary>
    public bool IsDirty => Current != _saved;

    private void RaiseDirty() => this.RaisePropertyChanged(nameof(IsDirty));

    /// <summary>Puts the fields back to what the slot holds.</summary>
    public void Discard()
    {
        HfType = _saved.HfType; LfType = _saved.LfType;
        HfNick = _saved.HfNick; LfNick = _saved.LfNick;
        HfEnabled = _saved.HfEnabled; LfEnabled = _saved.LfEnabled;
        SetColor(TagSenseType.Hf, _saved.HfColor);
        SetColor(TagSenseType.Lf, _saved.LfColor);
    }

    // The swatch list deselects (and writes null) when the colour isn't in the palette, so null only comes from Reset.
    public string? HfColor { get => _hfColor; set { if (!string.IsNullOrWhiteSpace(value)) SetColor(TagSenseType.Hf, value); } }
    public string? LfColor { get => _lfColor; set { if (!string.IsNullOrWhiteSpace(value)) SetColor(TagSenseType.Lf, value); } }

    /// <summary>The colour the slot LED shows for each card: its own, or the firmware default.</summary>
    public string HfLedPreview => _hfColor ?? SlotColors.DefaultHf;
    public string LfLedPreview => _lfColor ?? SlotColors.DefaultLf;

    private void SetColor(TagSenseType sense, string? color)
    {
        if (sense == TagSenseType.Hf)
        {
            this.RaiseAndSetIfChanged(ref _hfColor, color, nameof(HfColor));
            this.RaisePropertyChanged(nameof(HfLedPreview));
        }
        else
        {
            this.RaiseAndSetIfChanged(ref _lfColor, color, nameof(LfColor));
            this.RaisePropertyChanged(nameof(LfLedPreview));
        }
        RaiseDirty();
    }

    public bool ShowMfcSettings { get => _showMfcSettings; private set => this.RaiseAndSetIfChanged(ref _showMfcSettings, value); }
    public bool ShowUlSettings { get => _showUlSettings; private set => this.RaiseAndSetIfChanged(ref _showUlSettings, value); }

    public bool Gen1a { get => _gen1a; set => this.RaiseAndSetIfChanged(ref _gen1a, value); }
    public bool Gen2 { get => _gen2; set => this.RaiseAndSetIfChanged(ref _gen2, value); }
    public bool BlockAntiColl { get => _blockAntiColl; set => this.RaiseAndSetIfChanged(ref _blockAntiColl, value); }
    public bool MfcDetection { get => _mfcDetection; set => this.RaiseAndSetIfChanged(ref _mfcDetection, value); }
    public bool FieldOffReset { get => _fieldOffReset; set => this.RaiseAndSetIfChanged(ref _fieldOffReset, value); }
    public EmulatorWriteMode MfcWriteMode { get => _mfcWriteMode; set => this.RaiseAndSetIfChanged(ref _mfcWriteMode, value); }
    public MifareClassicPrngType Prng { get => _prng; set => this.RaiseAndSetIfChanged(ref _prng, value); }

    public bool UlUidMagic { get => _ulUidMagic; set => this.RaiseAndSetIfChanged(ref _ulUidMagic, value); }
    public bool UlDetection { get => _ulDetection; set => this.RaiseAndSetIfChanged(ref _ulDetection, value); }
    public EmulatorWriteMode UlWriteMode { get => _ulWriteMode; set => this.RaiseAndSetIfChanged(ref _ulWriteMode, value); }

    public ICommand ApplyCommand { get; }
    public ICommand ClearHfCommand { get; }
    public ICommand ClearLfCommand { get; }
    public ICommand ResetDefaultCommand { get; }
    public ICommand ApplyEmulatorCommand { get; }
    public ICommand ResetHfColorCommand { get; }
    public ICommand ResetLfColorCommand { get; }

    public override Task OnActivatedAsync() => RunAsync(async () =>
    {
        var client = _device.Client;
        if (client is null) return;
        await client.SetActiveSlotAsync(_slot);
        await LoadEmulatorAsync(client);
    });

    private async Task LoadEmulatorAsync(ChameleonClient client)
    {
        var mfc = _hfType.IsMifareClassic() && _hfEnabled;
        var ul = _hfType.IsUltralight() && _hfEnabled;

        if (mfc)
        {
            try
            {
                var cfg = await client.GetMf1EmulatorConfigAsync();
                var prng = await client.GetMf1PrngTypeAsync();
                var fieldReset = false;
                try { fieldReset = await client.GetMf1FieldOffDoResetAsync(); } catch { }
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    Gen1a = cfg.Gen1a; Gen2 = cfg.Gen2; BlockAntiColl = cfg.BlockAntiColl;
                    MfcDetection = cfg.Detection; MfcWriteMode = cfg.WriteMode;
                    Prng = prng; FieldOffReset = fieldReset;
                });
            }
            catch { mfc = false; }
        }
        else if (ul)
        {
            try
            {
                var uid = await client.GetMf0UidMagicModeAsync();
                var wm = await client.GetMf0WriteModeAsync();
                var det = false;
                try { det = await client.GetMf0DetectionEnableAsync(); } catch { }
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    UlUidMagic = uid; UlWriteMode = wm; UlDetection = det;
                });
            }
            catch { ul = false; }
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            ShowMfcSettings = mfc;
            ShowUlSettings = ul;
        });
    }

    /// <returns>False when the slot could not be saved (the edits stay).</returns>
    public async Task<bool> SaveAsync()
    {
        var saved = false;
        await RunAsync(async () =>
        {
            var client = _device.Client;
            if (client is null) return;
            var edits = Current;
            await client.SetActiveSlotAsync(_slot);

            await ApplyFrequencyAsync(client, TagSenseType.Hf, edits.HfType, edits.HfEnabled, edits.HfNick, edits.HfColor);
            await ApplyFrequencyAsync(client, TagSenseType.Lf, edits.LfType, edits.LfEnabled, edits.LfNick, edits.LfColor);

            await client.SaveSlotConfigAsync();
            Avalonia.Threading.Dispatcher.UIThread.Post(() => { _saved = edits; RaiseDirty(); });
            await LoadEmulatorAsync(client);
            _onChanged();
            saved = true;
        }, "Slot saved to flash.");
        return saved;
    }

    /// <summary>Sets one frequency's tag type, default data, enable flag, nick and LED colour.</summary>
    private async Task ApplyFrequencyAsync(
        ChameleonClient client, TagSenseType sense, TagSpecificType type, bool enabled, string nick, string? color)
    {
        if (type == TagSpecificType.Undefined)
        {
            // No tag on this frequency: just make sure it is disabled.
            await client.SetSlotEnableAsync(_slot, sense, enabled: false);
            return;
        }

        await client.SetSlotTagTypeAsync(_slot, type);
        await client.SetSlotEnableAsync(_slot, sense, enabled);

        if (!string.IsNullOrWhiteSpace(nick))
            await client.SetSlotNickAsync(_slot, sense, nick.Trim());
        else
            try { await client.DeleteSlotNickAsync(_slot, sense); } catch { /* no nick to delete */ }

        try { await client.SetSlotColorAsync(_slot, sense, color); }
        catch (ChameleonException) { /* older firmware: no per-card LED colours */ }
    }

    private Task ClearAsync(TagSenseType sense) => RunAsync(async () =>
    {
        var client = _device.Client;
        if (client is null) return;
        await client.SetActiveSlotAsync(_slot);
        await client.DeleteSlotSenseTypeAsync(_slot, sense);
        await client.SaveSlotConfigAsync();
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _saved = sense == TagSenseType.Hf
                ? _saved with { HfType = TagSpecificType.Undefined, HfEnabled = false, HfNick = string.Empty, HfColor = null }
                : _saved with { LfType = TagSpecificType.Undefined, LfEnabled = false, LfNick = string.Empty, LfColor = null };
            if (sense == TagSenseType.Hf) { HfType = TagSpecificType.Undefined; HfEnabled = false; HfNick = string.Empty; }
            else { LfType = TagSpecificType.Undefined; LfEnabled = false; LfNick = string.Empty; }
            SetColor(sense, null); // the firmware drops the colour together with the card
        });
        _onChanged();
    }, "Frequency cleared.");

    private Task ResetDefaultAsync() => RunAsync(async () =>
    {
        var client = _device.Client;
        if (client is null) return;
        await client.SetActiveSlotAsync(_slot);
        // The types the slot holds, not unsaved edits of them.
        if (_saved.HfType != TagSpecificType.Undefined)
            await client.SetSlotDataDefaultAsync(_slot, _saved.HfType);
        if (_saved.LfType != TagSpecificType.Undefined)
            await client.SetSlotDataDefaultAsync(_slot, _saved.LfType);
        await client.SaveSlotConfigAsync();
        _onChanged();
    }, "Slot data reset to defaults.");

    private Task ApplyEmulatorAsync() => RunAsync(async () =>
    {
        var client = _device.Client;
        if (client is null) return;
        await client.SetActiveSlotAsync(_slot);

        if (ShowMfcSettings)
        {
            await client.SetMf1Gen1aModeAsync(Gen1a);
            await client.SetMf1Gen2ModeAsync(Gen2);
            await client.SetMf1BlockAntiCollModeAsync(BlockAntiColl);
            await client.SetMf1DetectionEnableAsync(MfcDetection);
            await client.SetMf1WriteModeAsync(MfcWriteMode);
            try { await client.SetMf1FieldOffDoResetAsync(FieldOffReset); } catch { }
            try { await client.SetMf1PrngTypeAsync(Prng); } catch { }
        }
        else if (ShowUlSettings)
        {
            await client.SetMf0UidMagicModeAsync(UlUidMagic);
            await client.SetMf0WriteModeAsync(UlWriteMode);
            await client.SetMf0DetectionEnableAsync(UlDetection);
        }

        await client.SaveSlotConfigAsync();
    }, "Emulator settings saved.");
}
