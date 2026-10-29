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
/// Editor for one emulation slot: the HF and LF card (a saved card picked from the library,
/// or cleared), their LED colors, and the MIFARE Classic / Ultralight
/// emulator settings of the HF card. All firmware calls act on the active slot, so the editor
/// activates its slot before reading or writing anything. Every edit is held until
/// <see cref="SaveAsync"/> writes the slot to the device (see <see cref="IsDirty"/>).
/// </summary>
public sealed class SlotEditorViewModel : PageViewModelBase
{
    private readonly DeviceService _device;
    private readonly CardLibrary _library;
    private readonly int _slot;
    private readonly Action _onChanged;

    /// <summary>The HF card's emulator settings; compared against what the slot holds.</summary>
    private sealed record Emulator(
        bool Gen1a, bool Gen2, bool BlockAntiColl, bool MfcDetection, bool FieldOffReset,
        EmulatorWriteMode MfcWriteMode, MifareClassicPrngType Prng,
        bool UlUidMagic, bool UlDetection, EmulatorWriteMode UlWriteMode)
    {
        // What the firmware gives a fresh card (see nfc_mf1.c / nfc_mf0_ntag.c).
        public static readonly Emulator Defaults = new(
            false, false, false, false, false, EmulatorWriteMode.Normal, MifareClassicPrngType.Weak,
            false, false, EmulatorWriteMode.Normal);
    }

    /// <summary>Which emulator settings an HF type has.</summary>
    private enum EmulatorKind { None, Mfc, Ul }

    private Emulator _savedEmulator = Emulator.Defaults;
    // The kind of HF card the saved settings were read from, and of the one the fields are for.
    private EmulatorKind _savedKind;
    private EmulatorKind _fieldsKind;

    private bool _gen1a, _gen2, _blockAntiColl, _mfcDetection, _fieldOffReset;
    private EmulatorWriteMode _mfcWriteMode;
    private MifareClassicPrngType _prng;
    private bool _ulUidMagic, _ulDetection;
    private EmulatorWriteMode _ulWriteMode;

    private SlotSideViewModel? _pickerSide;
    private string? _pickerFilter;
    private bool _supportsLfCycle;
    private bool _supportsCycleGap;
    private decimal _cycleGapSeconds, _savedCycleGapSeconds;
    private int _cycleBursts = 1, _savedCycleBursts = 1;

    public LfSlotListViewModel LfCards { get; }
    public bool ShowLfCycle => _supportsLfCycle && LfCards.Cards.Count > 1;
    public bool ShowCycleGap => _supportsCycleGap;
    public decimal CycleGapSeconds
    {
        get => _cycleGapSeconds;
        set => SetField(ref _cycleGapSeconds, Math.Clamp(decimal.Round(value, 1), 0m, 3m), nameof(CycleGapSeconds));
    }
    public int CycleBursts { get => _cycleBursts; set => SetField(ref _cycleBursts, value, nameof(CycleBursts)); }
    private bool IsCycleDirty => LfCards.IsDirty || (ShowLfCycle &&
        (CycleBursts != _savedCycleBursts || CycleGapSeconds != _savedCycleGapSeconds));

    public SlotEditorViewModel(DeviceService device, CardLibrary library, SlotInfo info,
        Action onChanged, Action<CardSave> openCard)
        : base($"Slot {info.Index}", "slots")
    {
        _device = device;
        _library = library;
        _slot = info.Index;
        _onChanged = onChanged;

        var colors = device.DeviceInfo?.SupportsSlotColors == true;
        Hf = new SlotSideViewModel(TagSenseType.Hf, info, library, colors, OnHfChanged, OpenPicker, SaveToLibraryAsync, openCard);
        Lf = new SlotSideViewModel(TagSenseType.Lf, info, library, colors, OnLfChanged, OpenPicker, SaveToLibraryAsync, openCard);
        Hf.Partner = Lf;
        Lf.Partner = Hf;
        LfCards = new LfSlotListViewModel(Lf, library, OnLfListChanged, openCard);
        SetEmulator(Emulator.Defaults);

        ClosePickerCommand = ReactiveCommand.Create(ClosePicker);
    }

    public int SlotIndex => _slot;

    public SlotSideViewModel Hf { get; }
    public SlotSideViewModel Lf { get; }

    public static IReadOnlyList<EmulatorWriteMode> WriteModes { get; } = new[]
    {
        EmulatorWriteMode.Normal, EmulatorWriteMode.Denied,
        EmulatorWriteMode.Deceive, EmulatorWriteMode.Shadow,
    };

    public static IReadOnlyList<MifareClassicPrngType> PrngTypes { get; } = new[]
    {
        MifareClassicPrngType.Static, MifareClassicPrngType.Weak, MifareClassicPrngType.Hard,
    };

    /// <summary>Anything differs from what the slot holds (drives the save bar).</summary>
    public bool IsDirty => Hf.IsDirty || Lf.IsDirty || IsEmulatorDirty || IsCycleDirty;

    private bool IsEmulatorDirty => ShownKind != EmulatorKind.None && CurrentEmulator != _savedEmulator;

    private void RaiseDirty() => this.RaisePropertyChanged(nameof(IsDirty));

    /// <summary>Puts every field back to what the slot holds.</summary>
    public void Discard()
    {
        ClearSaveError();
        Hf.Discard();
        Lf.Discard();
        LfCards.Discard();
        CycleBursts = _savedCycleBursts;
        CycleGapSeconds = _savedCycleGapSeconds;
        SetEmulator(_savedEmulator);
    }

    /// <summary>The library changed: the sides re-match their cards, the open picker its list.</summary>
    public void OnLibraryChanged()
    {
        Hf.OnLibraryChanged();
        Lf.OnLibraryChanged();
        LfCards.OnLibraryChanged();
        if (IsPicking) RebuildPicker();
    }

    // ---- Emulator settings (HF) ----------------------------------------

    /// <summary>The emulator settings the HF card's type has.</summary>
    private EmulatorKind TypeKind =>
        Hf.Type.IsMifareClassic() ? EmulatorKind.Mfc
        : Hf.Type.IsUltralight() ? EmulatorKind.Ul
        : EmulatorKind.None;

    /// <summary>The settings shown (and saved) for the HF card.</summary>
    private EmulatorKind ShownKind => TypeKind;

    public bool ShowMfcSettings => ShownKind == EmulatorKind.Mfc;
    public bool ShowUlSettings => ShownKind == EmulatorKind.Ul;

    private void OnLfChanged()
    {
        Hf.RaiseLed();
        LfCards?.RefreshPrimary();
        OnLfListChanged();
    }

    private void OnLfListChanged()
    {
        this.RaisePropertyChanged(nameof(ShowLfCycle));
        RaiseDirty();
    }

    private void OnHfChanged()
    {
        Lf.RaiseLed();
        // Another kind of card starts from the firmware's defaults; the saved kind gets the slot's settings back.
        var kind = TypeKind;
        if (kind != _fieldsKind)
        {
            _fieldsKind = kind;
            SetEmulator(kind == _savedKind ? _savedEmulator : Emulator.Defaults);
        }
        this.RaisePropertyChanged(nameof(ShowMfcSettings));
        this.RaisePropertyChanged(nameof(ShowUlSettings));
        RaiseDirty();
    }

    private Emulator CurrentEmulator => new(
        _gen1a, _gen2, _blockAntiColl, _mfcDetection, _fieldOffReset, _mfcWriteMode, _prng,
        _ulUidMagic, _ulDetection, _ulWriteMode);

    private void SetEmulator(Emulator e)
    {
        Gen1a = e.Gen1a; Gen2 = e.Gen2; BlockAntiColl = e.BlockAntiColl; MfcDetection = e.MfcDetection;
        FieldOffReset = e.FieldOffReset; MfcWriteMode = e.MfcWriteMode; Prng = e.Prng;
        UlUidMagic = e.UlUidMagic; UlDetection = e.UlDetection; UlWriteMode = e.UlWriteMode;
    }

    private void SetField<T>(ref T field, T value, string name)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        this.RaiseAndSetIfChanged(ref field, value, name);
        RaiseDirty();
    }

    public bool Gen1a { get => _gen1a; set => SetField(ref _gen1a, value, nameof(Gen1a)); }
    public bool Gen2 { get => _gen2; set => SetField(ref _gen2, value, nameof(Gen2)); }
    public bool BlockAntiColl { get => _blockAntiColl; set => SetField(ref _blockAntiColl, value, nameof(BlockAntiColl)); }
    public bool MfcDetection { get => _mfcDetection; set => SetField(ref _mfcDetection, value, nameof(MfcDetection)); }
    public bool FieldOffReset { get => _fieldOffReset; set => SetField(ref _fieldOffReset, value, nameof(FieldOffReset)); }
    public EmulatorWriteMode MfcWriteMode { get => _mfcWriteMode; set => SetField(ref _mfcWriteMode, value, nameof(MfcWriteMode)); }
    public MifareClassicPrngType Prng { get => _prng; set => SetField(ref _prng, value, nameof(Prng)); }

    public bool UlUidMagic { get => _ulUidMagic; set => SetField(ref _ulUidMagic, value, nameof(UlUidMagic)); }
    public bool UlDetection { get => _ulDetection; set => SetField(ref _ulDetection, value, nameof(UlDetection)); }
    public EmulatorWriteMode UlWriteMode { get => _ulWriteMode; set => SetField(ref _ulWriteMode, value, nameof(UlWriteMode)); }

    public override Task OnActivatedAsync() => RunAsync(async () =>
    {
        var client = _device.Client;
        if (client is null) return;
        await client.WithSlotLockAsync(async () =>
        {
            await client.SetActiveSlotAsync(_slot);
            await LoadUidAsync(client, Hf);
            await LoadUidAsync(client, Lf);
            var capabilities = await client.GetCapabilitiesAsync();
            _supportsLfCycle = capabilities.Contains((ushort)Command.Em410xSetCycle);
            _supportsCycleGap = capabilities.Contains((ushort)Command.Em410xSetCycleGap) && capabilities.Contains((ushort)Command.Em410xGetCycleGap);
            var cycle = _supportsLfCycle && Lf.SavedType == TagSpecificType.Em410x
                ? await client.GetEm410xCycleAsync() : new LfCardCycle(1, []);
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _savedCycleBursts = cycle.Bursts;
                LfCards.Load(_supportsLfCycle, cycle);
                CycleBursts = _savedCycleBursts;
                CycleGapSeconds = _savedCycleGapSeconds = cycle.GapSteps / 10m;
                this.RaisePropertyChanged(nameof(ShowCycleGap));
                this.RaisePropertyChanged(nameof(ShowLfCycle));
            });
            await LoadEmulatorAsync(client);
        });
    });

    // The saved card is matched by the UID the slot emulates, not just its name.
    private static async Task LoadUidAsync(ChameleonClient client, SlotSideViewModel side)
    {
        var type = side.SavedType;
        if (type == TagSpecificType.Undefined) return;
        byte[] uid;
        try { uid = await client.ReadSlotUidAsync(type); }
        catch (ChameleonException) { return; /* matched by name */ }
        Avalonia.Threading.Dispatcher.UIThread.Post(() => side.UidRead(type, uid));
    }

    private async Task LoadEmulatorAsync(ChameleonClient client)
    {
        // Read whether or not the card is enabled, so enabling it shows what the slot holds.
        var kind = TypeKind;
        var e = Emulator.Defaults;
        try
        {
            if (kind == EmulatorKind.Mfc)
            {
                var cfg = await client.GetMf1EmulatorConfigAsync();
                var prng = e.Prng;
                var fieldReset = false;
                try { prng = await client.GetMf1PrngTypeAsync(); } catch (ChameleonException) { }
                try { fieldReset = await client.GetMf1FieldOffDoResetAsync(); } catch (ChameleonException) { }
                e = e with
                {
                    Gen1a = cfg.Gen1a, Gen2 = cfg.Gen2, BlockAntiColl = cfg.BlockAntiColl,
                    MfcDetection = cfg.Detection, MfcWriteMode = cfg.WriteMode, Prng = prng, FieldOffReset = fieldReset,
                };
            }
            else if (kind == EmulatorKind.Ul)
            {
                var uid = await client.GetMf0UidMagicModeAsync();
                var wm = await client.GetMf0WriteModeAsync();
                var det = false;
                try { det = await client.GetMf0DetectionEnableAsync(); } catch (ChameleonException) { }
                e = e with { UlUidMagic = uid, UlWriteMode = wm, UlDetection = det };
            }
        }
        catch (ChameleonException) { /* the firmware defaults are shown */ }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _savedEmulator = e;
            _savedKind = _fieldsKind = kind;
            SetEmulator(e);
            RaiseDirty();
        });
    }

    // ---- Saving --------------------------------------------------------

    /// <returns>False when the slot could not be saved (the edits stay).</returns>
    public async Task<bool> SaveAsync()
    {
        var saved = false;
        await RunSaveAsync(async () =>
        {
            var client = _device.Client;
            if (client is null) return;
            var kind = ShownKind;
            var emulator = CurrentEmulator;
            // Validate before writing either frequency, so invalid input leaves the device untouched.
            var cycle = _supportsLfCycle && Lf.Type == TagSpecificType.Em410x
                ? LfCards.Cycle(CycleBursts, (int)(CycleGapSeconds * 10m)) : null;
            var progress = new Progress<MifareProgress>(p => Notify($"Uploading {p.Done}/{p.Total}…"));

            await client.WithSlotLockAsync(async () =>
            {
                await client.SetActiveSlotAsync(_slot);
                await Hf.WriteAsync(client, _slot, progress);
                await Lf.WriteAsync(client, _slot, progress);
                if (cycle is not null)
                {
                    await client.SetActiveSlotAsync(_slot);
                    await client.SetEm410xCycleAsync(cycle);
                }
                if (kind != EmulatorKind.None)
                {
                    await client.SetActiveSlotAsync(_slot);
                    await client.ApplyEmulatorSettingsAsync(new SlotEmulatorSettings(
                        kind == EmulatorKind.Mfc
                            ? new Mf1EmulatorSettings(emulator.Gen1a, emulator.Gen2, emulator.BlockAntiColl,
                                emulator.MfcDetection, emulator.MfcWriteMode, emulator.Prng, emulator.FieldOffReset)
                            : null,
                        kind == EmulatorKind.Ul
                            ? new Mf0EmulatorSettings(emulator.UlUidMagic, emulator.UlWriteMode, emulator.UlDetection)
                            : null));
                }
                await client.SaveSlotConfigAsync();
            });

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                Hf.MarkSaved();
                Lf.MarkSaved();
                if (cycle is null)
                {
                    CycleBursts = 1;
                    CycleGapSeconds = 0;
                }
                LfCards.MarkSaved();
                _savedCycleBursts = CycleBursts;
                _savedCycleGapSeconds = CycleGapSeconds;
                _savedEmulator = emulator;
                _savedKind = _fieldsKind = TypeKind;
                RaiseDirty();
            });
            _onChanged();
            saved = true;
        }, "Slot saved.");
        return saved;
    }

    /// <summary>Copies the card a frequency holds on the device into Saved cards.</summary>
    private Task SaveToLibraryAsync(SlotSideViewModel side) => RunAsync(async () =>
    {
        var client = _device.Client;
        if (client is null || side.SavedType == TagSpecificType.Undefined) return;
        var info = side.IsHf
            ? new SlotInfo { Index = _slot, HfType = side.SavedType, HfNick = side.SavedNick, HfColor = side.SavedColor }
            : new SlotInfo { Index = _slot, LfType = side.SavedType, LfNick = side.SavedNick, LfColor = side.SavedColor };
        var card = await client.WithSlotLockAsync(() => client.ReadCardFromSlotAsync(info, side.Sense));

        // A slot without a nick is matched to the card by the name the card got.
        var nick = ChameleonClient.SlotNickFor(card);
        if (nick != side.SavedNick)
        {
            await client.SetSlotNickAsync(_slot, side.Sense, nick);
            await client.SaveSlotConfigAsync();
            _onChanged();
        }
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            side.Renamed(nick, card.Uid);
            _library.Add(card);
        });
        Notify($"Saved “{card.Name}” to Saved cards.");
    });

    // ---- Choosing a saved card -----------------------------------------

    /// <summary>The dialog listing the saved cards of one frequency is open.</summary>
    public bool IsPicking => _pickerSide is not null;

    public string PickerTitle => _pickerSide == Lf && LfCards.HasCards
        ? "Add a saved LF card" : $"Choose a saved {_pickerSide?.Label} card";

    public string? PickerFilter
    {
        get => _pickerFilter;
        set
        {
            this.RaiseAndSetIfChanged(ref _pickerFilter, value);
            RebuildPicker();
        }
    }

    public ObservableCollection<CardItemViewModel> PickerCards { get; } = new();

    public bool PickerHasNoCards => IsPicking && PickerCards.Count == 0;

    public string PickerEmptyText => _library.Cards.Any(c => c.Sense == _pickerSide?.Sense)
        ? "No cards match the search."
        : $"No {_pickerSide?.Label} cards saved yet.";

    /// <summary>Picking a card in the dialog puts it into the frequency.</summary>
    public CardItemViewModel? PickerSelected
    {
        get => null;
        set
        {
            if (value is null || _pickerSide is not { } side) return;
            // The list is still committing the selection: emptying it now would break that.
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(_pickerSide, side)) return;
                ClosePicker();
                if (side == Lf) LfCards.Add(value.Card);
                else side.Pick(value.Card);
            });
        }
    }

    public ICommand ClosePickerCommand { get; }

    private void OpenPicker(SlotSideViewModel side)
    {
        if (side == Lf && !LfCards.CanAdd) return;
        _pickerSide = side;
        _pickerFilter = null;
        this.RaisePropertyChanged(nameof(PickerFilter));
        this.RaisePropertyChanged(nameof(PickerTitle));
        RebuildPicker();
        this.RaisePropertyChanged(nameof(IsPicking));
    }

    private void ClosePicker()
    {
        _pickerSide = null;
        PickerCards.Clear();
        this.RaisePropertyChanged(nameof(IsPicking));
        this.RaisePropertyChanged(nameof(PickerHasNoCards));
    }

    private void RebuildPicker()
    {
        PickerCards.Clear();
        if (_pickerSide is not { } side) return;
        foreach (var card in _library.Cards.Where(c => c.Sense == side.Sense).Select(c => new CardItemViewModel(c)))
            if ((side != Lf || LfCards.CanChoose(card.Card))
                && card.Matches(_pickerFilter)) PickerCards.Add(card);
        this.RaisePropertyChanged(nameof(PickerHasNoCards));
        this.RaisePropertyChanged(nameof(PickerEmptyText));
    }
}
