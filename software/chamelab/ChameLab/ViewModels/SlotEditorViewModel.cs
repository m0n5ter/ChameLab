using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows.Input;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;
using ReactiveUI;
using ChameLab.Localization;

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
    private readonly Func<string>? _saveFolder;
    internal Action<Action> PostToUi { get; set; } = a => Avalonia.Threading.Dispatcher.UIThread.Post(a);

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

    private bool _savedSlotEnabled, _isSlotEnabled, _hadAnyCard, _hasOtherEnabledSlot;
    private SlotSideViewModel? _pickerSide;
    private bool _supportsLfCycle, _supportsCycleMode;
    // The cycle the LF cards were loaded with while multi-LF is supported, else null.
    private LfCardCycle? _shownCycle;
    private bool _savedCycleWhilePresent;
    private int _savedCycleBursts = 1;
    private decimal _savedCycleGapSeconds;

    public LfSlotListViewModel LfCards { get; }
    public bool SupportsMultiLf => _supportsLfCycle;

    /// <summary>How the cards are switched: shown for a list on firmware that has the modes.</summary>
    public bool ShowLfCycle => _supportsLfCycle && _supportsCycleMode && LfCards.Cards.Count > 1;
    public bool ShowCycleTiming => ShowLfCycle && CycleWhilePresent;

    /// <summary>The next card on each appearance of the reader's field (the default).</summary>
    public bool CyclePerField
    {
        get => !CycleWhilePresent;
        set { if (value) CycleWhilePresent = false; }
    }

    /// <summary>The cards in turn while the field is present, with the bursts and silence below.</summary>
    public bool CycleWhilePresent
    {
        get;
        set
        {
            if (field == value) return;
            SetField(ref field, value, nameof(CycleWhilePresent));
            this.RaisePropertyChanged(nameof(CyclePerField));
            this.RaisePropertyChanged(nameof(ShowCycleTiming));
        }
    }

    public int CycleBursts
    {
        get;
        set => SetField(ref field, Math.Clamp(value, 1, LfCardCycle.MaxBursts), nameof(CycleBursts));
    } = 1;

    public decimal CycleGapSeconds
    {
        get;
        set => SetField(ref field, Math.Clamp(decimal.Round(value, 1), 0m, LfCardCycle.MaxGapSteps / 10m), nameof(CycleGapSeconds));
    }

    private bool IsCycleModeDirty => CycleWhilePresent != _savedCycleWhilePresent ||
                                     (CycleWhilePresent && (CycleBursts != _savedCycleBursts || CycleGapSeconds != _savedCycleGapSeconds));

    private bool IsCycleDirty => SupportsMultiLf && (LfCards.IsDirty || (ShowLfCycle && IsCycleModeDirty));

    internal void SetLfCapabilities(IReadOnlyList<ushort> capabilities)
    {
        _supportsLfCycle = capabilities.Contains((ushort)Command.LfSetCycle) && capabilities.Contains((ushort)Command.LfGetCycle);
        _supportsCycleMode = capabilities.Contains((ushort)Command.LfSetCycleMode) && capabilities.Contains((ushort)Command.LfGetCycleMode);
        this.RaisePropertyChanged(nameof(SupportsMultiLf));
        RaiseCycleShown();
        RaiseDirty();
    }

    private void RaiseCycleShown()
    {
        this.RaisePropertyChanged(nameof(ShowLfCycle));
        this.RaisePropertyChanged(nameof(ShowCycleTiming));
    }

    /// <summary>The list with the mode being edited.</summary>
    private LfCardCycle EditedCycle() => LfCards.Cycle() with
    {
        Mode = CycleWhilePresent ? LfCycleMode.WhilePresent : LfCycleMode.PerField,
        Bursts = CycleBursts,
        GapSteps = (int)(CycleGapSeconds * 10m),
    };

    public SlotEditorViewModel(DeviceService device, CardLibrary library, SlotInfo info,
        Action onChanged, Action<CardSave> openCard, DictionaryLibrary? dictionaries = null,
        bool hasOtherEnabledSlot = true, Func<string>? saveFolder = null)
        : base(() => string.Format(Strings.Slots_SlotN, info.Index), "slots")
    {
        _device = device;
        _library = library;
        _slot = info.Index;
        _onChanged = onChanged;
        _saveFolder = saveFolder;
        var keyDictionaries = dictionaries ?? AppServices.Dictionaries;
        MfcCapture = new EmulatorCaptureViewModel(() => _device.Client, _slot, true, keyDictionaries);
        UlCapture = new EmulatorCaptureViewModel(() => _device.Client, _slot, false, keyDictionaries);
        MfcCapture.PropertyChanged += OnCapturePropertyChanged;
        UlCapture.PropertyChanged += OnCapturePropertyChanged;

        var colors = device.DeviceInfo?.SupportsSlotColors == true;
        Hf = new SlotSideViewModel(TagSenseType.Hf, info, library, colors, OnHfChanged, OpenPicker, SaveToLibraryAsync, openCard);
        Lf = new SlotSideViewModel(TagSenseType.Lf, info, library, colors, OnLfChanged, OpenPicker, SaveToLibraryAsync, openCard);
        Hf.Partner = Lf;
        Lf.Partner = Hf;
        LfCards = new LfSlotListViewModel(Lf, library, OnLfListChanged, openCard, saveFolder);
        _hasOtherEnabledSlot = hasOtherEnabledSlot;
        _savedSlotEnabled = _isSlotEnabled = (info.HfType != TagSpecificType.Undefined && info.HfEnabled)
            || (info.LfType != TagSpecificType.Undefined && info.LfEnabled);
        _hadAnyCard = HasAnyCard;
        // Lay the LF cards out from what the slot list read, so they don't change shape once the device answers.
        if (device.Client?.Capabilities is { } capabilities) SetLfCapabilities(capabilities);
        LoadCycle(LfCycleCard.CanCycle(info.LfType) ? info.LfCycle ?? new LfCardCycle([]) : new LfCardCycle([]));
        SetEmulator(Emulator.Defaults);

        ClosePickerCommand = ReactiveCommand.Create(ClosePicker);
    }

    public int SlotIndex => _slot;

    public SlotSideViewModel Hf { get; }
    public SlotSideViewModel Lf { get; }
    public EmulatorCaptureViewModel MfcCapture { get; }
    public EmulatorCaptureViewModel UlCapture { get; }

    public override bool HasActivity => base.HasActivity || MfcCapture.IsBusy || UlCapture.IsBusy;

    private void OnCapturePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IsBusy)) this.RaisePropertyChanged(nameof(HasActivity));
    }

    public static IReadOnlyList<EmulatorWriteMode> WriteModes { get; } = new[]
    {
        EmulatorWriteMode.Normal, EmulatorWriteMode.Denied,
        EmulatorWriteMode.Deceive, EmulatorWriteMode.Shadow,
    };

    public static IReadOnlyList<MifareClassicPrngType> PrngTypes { get; } = new[]
    {
        MifareClassicPrngType.Static, MifareClassicPrngType.Weak, MifareClassicPrngType.Hard,
    };

    public bool HasAnyCard => Hf.HasCard || Lf.HasCard;
    public bool CanToggleSlotEnabled => HasAnyCard && (!IsSlotEnabled || _hasOtherEnabledSlot);

    public void SetHasOtherEnabledSlot(bool value)
    {
        if (_hasOtherEnabledSlot == value) return;
        _hasOtherEnabledSlot = value;
        this.RaisePropertyChanged(nameof(CanToggleSlotEnabled));
    }

    public bool IsSlotEnabled
    {
        get => _isSlotEnabled;
        set
        {
            if (_isSlotEnabled == value) return;
            this.RaiseAndSetIfChanged(ref _isSlotEnabled, value);
            this.RaisePropertyChanged(nameof(CanToggleSlotEnabled));
            RaiseDirty();
        }
    }

    /// <summary>Anything differs from what the slot holds (drives the save bar).</summary>
    public bool IsDirty => Hf.IsDirty || Lf.IsDirty || IsEmulatorDirty || IsCycleDirty || IsSlotEnabled != _savedSlotEnabled;

    private bool IsEmulatorDirty => ShownKind != EmulatorKind.None && CurrentEmulator != _savedEmulator;

    private void RaiseDirty() => this.RaisePropertyChanged(nameof(IsDirty));

    /// <summary>Puts every field back to what the slot holds.</summary>
    public void Discard()
    {
        ClearSaveError();
        Hf.Discard();
        Lf.Discard();
        LfCards.Discard();
        RestoreCycleMode();
        SetEmulator(_savedEmulator);
        _hadAnyCard = HasAnyCard;
        IsSlotEnabled = _savedSlotEnabled;
        this.RaisePropertyChanged(nameof(CanToggleSlotEnabled));
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

    private const string MfcExpandedSetting = "slotsMfcSettingsExpanded";
    private const string UlExpandedSetting = "slotsUlSettingsExpanded";

    public bool MfcSettingsExpanded
    {
        get;
        set
        {
            if (field == value) return;
            this.RaiseAndSetIfChanged(ref field, value);
            AppSettings.Set(MfcExpandedSetting, value);
        }
    } = AppSettings.Get(MfcExpandedSetting) is JsonValue savedMfc
        && savedMfc.TryGetValue<bool>(out var expandedMfc) && expandedMfc;

    public bool UlSettingsExpanded
    {
        get;
        set
        {
            if (field == value) return;
            this.RaiseAndSetIfChanged(ref field, value);
            AppSettings.Set(UlExpandedSetting, value);
        }
    } = AppSettings.Get(UlExpandedSetting) is JsonValue savedUl
        && savedUl.TryGetValue<bool>(out var expandedUl) && expandedUl;

    /// <summary>The emulator settings the HF card's type has.</summary>
    private EmulatorKind TypeKind =>
        Hf.Type.IsMifareClassic() ? EmulatorKind.Mfc
        : Hf.Type.IsUltralight() ? EmulatorKind.Ul
        : EmulatorKind.None;

    /// <summary>The settings shown (and saved) for the HF card.</summary>
    private EmulatorKind ShownKind => TypeKind;

    public bool ShowMfcSettings => ShownKind == EmulatorKind.Mfc;
    public bool ShowUlSettings => ShownKind == EmulatorKind.Ul;

    private void UpdateSlotEnabledForCardChange()
    {
        var hasAny = HasAnyCard;
        if (!_hadAnyCard && hasAny)
            IsSlotEnabled = true;
        else if (!hasAny)
            IsSlotEnabled = false;
        _hadAnyCard = hasAny;
        this.RaisePropertyChanged(nameof(CanToggleSlotEnabled));
    }

    private void OnLfChanged()
    {
        Hf.RaiseLed();
        LfCards?.RefreshPrimary();
        UpdateSlotEnabledForCardChange();
        OnLfListChanged();
    }

    private void OnLfListChanged()
    {
        RaiseCycleShown();
        RaiseDirty();
    }

    private void OnHfChanged()
    {
        Lf.RaiseLed();
        UpdateSlotEnabledForCardChange();
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
        var capture = TypeKind switch
        {
            EmulatorKind.Mfc => MfcCapture,
            EmulatorKind.Ul => UlCapture,
            _ => null,
        };
        await client.WithSlotLockAsync(async () =>
        {
            await client.SetActiveSlotAsync(_slot);
            await LoadCardAsync(client, Hf);
            await LoadCardAsync(client, Lf);
            var capabilities = await client.GetCapabilitiesAsync();
            var supportsCycle = capabilities.Contains((ushort)Command.LfSetCycle) && capabilities.Contains((ushort)Command.LfGetCycle);
            var cycle = supportsCycle && LfCycleCard.CanCycle(Lf.SavedType)
                ? await client.GetLfCycleAsync() : new LfCardCycle([]);
            PostToUi(() =>
            {
                SetLfCapabilities(capabilities);
                // Reloading the same cycle would only rebuild the rows (and drop edits made meanwhile).
                if (_shownCycle is null || !_shownCycle.SameAs(cycle)) LoadCycle(cycle);
            });
            await LoadEmulatorAsync(client);
        });
        // Refresh acquires the slot lock itself; run it after loading the slot has released it.
        if (capture is not null) await capture.RefreshAsync();
    });

    private void LoadCycle(LfCardCycle cycle)
    {
        _shownCycle = _supportsLfCycle ? cycle : null;
        LfCards.Load(_supportsLfCycle, cycle);
        _savedCycleWhilePresent = cycle.Mode == LfCycleMode.WhilePresent;
        _savedCycleBursts = cycle.Bursts;
        _savedCycleGapSeconds = cycle.GapSteps / 10m;
        RestoreCycleMode();
        RaiseCycleShown();
    }

    private void RestoreCycleMode()
    {
        CycleWhilePresent = _savedCycleWhilePresent;
        CycleBursts = _savedCycleBursts;
        CycleGapSeconds = _savedCycleGapSeconds;
    }

    // The saved card is matched by the full card content the slot emulates, not just its name or UID.
    private async Task LoadCardAsync(ChameleonClient client, SlotSideViewModel side)
    {
        var type = side.SavedType;
        if (type == TagSpecificType.Undefined) return;
        var info = side.IsHf
            ? new SlotInfo { Index = _slot, HfType = type, HfNick = side.SavedNick, HfColor = side.SavedColor }
            : new SlotInfo { Index = _slot, LfType = type, LfNick = side.SavedNick, LfColor = side.SavedColor };
        CardSave card;
        try { card = await client.ReadActiveSlotCardAsync(info, side.Sense); }
        catch (Exception ex) when (ex is ChameleonException or NotSupportedException) { return; /* matched by name */ }
        PostToUi(() =>
        {
            side.CardRead(card);
            // The LF list was laid out before the UID was known; its saved first card has it now.
            if (side == Lf) LfCards.PrimaryUidRead(type, card.Uid);
        });
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

        UpdateSavedCollection(kind, e);
        PostToUi(() =>
        {
            _savedEmulator = e;
            _savedKind = _fieldsKind = kind;
            SetEmulator(e);
            RaiseDirty();
        });
    }

    // ---- Saving --------------------------------------------------------

    private void UpdateSavedCollection(EmulatorKind kind, Emulator emulator)
    {
        MfcCapture.CollectionEnabled = kind == EmulatorKind.Mfc && emulator.MfcDetection;
        UlCapture.CollectionEnabled = kind == EmulatorKind.Ul && emulator.UlDetection;
    }

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
            var cycle = _supportsLfCycle && LfCycleCard.CanCycle(Lf.Type)
                ? EditedCycle() : null;
            var progress = new Progress<MifareProgress>(p => Notify(string.Format(Strings.Common_UploadingProgress, p.Done, p.Total)));

            await client.WithSlotLockAsync(async () =>
            {
                await client.SetActiveSlotAsync(_slot);
                await Hf.WriteAsync(client, _slot, progress);
                await Lf.WriteAsync(client, _slot, progress);
                if (cycle is not null)
                {
                    await client.SetActiveSlotAsync(_slot);
                    await client.SetLfCycleAsync(cycle);
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
                if (Hf.HasCard) await client.SetSlotEnableAsync(_slot, TagSenseType.Hf, IsSlotEnabled);
                if (Lf.HasCard) await client.SetSlotEnableAsync(_slot, TagSenseType.Lf, IsSlotEnabled);
                await client.SaveSlotConfigAsync();
            });

            UpdateSavedCollection(kind, emulator);
            if (kind == EmulatorKind.Mfc && !emulator.MfcDetection) MfcCapture.Clear();
            if (kind == EmulatorKind.Ul && !emulator.UlDetection) UlCapture.Clear();

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                Hf.MarkSaved();
                Lf.MarkSaved();
                LfCards.MarkSaved();
                _savedSlotEnabled = IsSlotEnabled;
                _hadAnyCard = HasAnyCard;
                if (cycle is not null && cycle.Extras.Length > 0 && _supportsCycleMode)
                {
                    _savedCycleWhilePresent = cycle.Mode == LfCycleMode.WhilePresent;
                    _savedCycleBursts = cycle.Bursts;
                    _savedCycleGapSeconds = cycle.GapSteps / 10m;
                }
                _savedEmulator = emulator;
                _savedKind = _fieldsKind = TypeKind;
                RaiseDirty();
            });
            _onChanged();
            saved = true;
        }, Strings.Slots_SlotSaved);
        return saved;
    }

    /// <summary>Copies the card a frequency holds on the device into Saved cards.</summary>
    private Task SaveToLibraryAsync(SlotSideViewModel side) => RunAsync(async () =>
    {
        var client = _device.Client;
        if (client is null || side.SavedType == TagSpecificType.Undefined) return;
        var folder = _saveFolder?.Invoke();
        var info = side.IsHf
            ? new SlotInfo { Index = _slot, HfType = side.SavedType, HfNick = side.SavedNick, HfColor = side.SavedColor }
            : new SlotInfo { Index = _slot, LfType = side.SavedType, LfNick = side.SavedNick, LfColor = side.SavedColor };
        var card = await client.WithSlotLockAsync(() => client.ReadCardFromSlotAsync(info, side.Sense));
        card.Name = _library.FreeName(card.Name);
        card.Folder = string.IsNullOrEmpty(folder) ? string.Empty : _library.FindFolder(folder) ?? string.Empty;

        // A slot without a nick is matched to the card by the name the card got.
        var nick = ChameleonClient.SlotNickFor(card);
        if (nick != side.SavedNick)
        {
            await client.SetSlotNickAsync(_slot, side.Sense, nick);
            await client.SaveSlotConfigAsync();
            _onChanged();
        }
        PostToUi(() =>
        {
            _library.Add(card);
            side.Renamed(nick, card);
        });
        Notify(string.Format(Strings.Slots_SavedToCards, card.Name));
    });

    // ---- Choosing a saved card -----------------------------------------

    /// <summary>The dialog listing the saved cards of one frequency is open.</summary>
    public bool IsPicking => _pickerSide is not null;

    public string PickerTitle => SupportsMultiLf && _pickerSide == Lf && LfCards.HasCards
        ? Strings.Slots_AddSavedLf : string.Format(Strings.Slots_ChooseSavedSide, _pickerSide?.Label);

    /// <summary>
    /// The saved cards the frequency can take (the picker searches them itself). A new list each time
    /// the picker opens, so its search starts over; a library change updates it in place.
    /// </summary>
    public ObservableCollection<CardSave> PickerCards
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = new();

    public string PickerEmptyText => string.Format(Strings.Slots_NoSavedSide, _pickerSide?.Label);

    /// <summary>Picking a card in the dialog puts it into the frequency.</summary>
    public CardSave? PickerSelected
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
                PickSavedCard(side, value);
            });
        }
    }

    public ICommand ClosePickerCommand { get; }

    internal void PickSavedCard(SlotSideViewModel side, CardSave card)
    {
        if (side == Lf && SupportsMultiLf) LfCards.Add(card);
        else side.Pick(card);
    }

    private void OpenPicker(SlotSideViewModel side)
    {
        if (side == Lf && SupportsMultiLf && !LfCards.CanAdd) return;
        _pickerSide = side;
        PickerCards = new ObservableCollection<CardSave>();
        this.RaisePropertyChanged(nameof(PickerTitle));
        this.RaisePropertyChanged(nameof(PickerEmptyText));
        RebuildPicker();
        this.RaisePropertyChanged(nameof(IsPicking));
    }

    private void ClosePicker()
    {
        _pickerSide = null;
        PickerCards.Clear();
        this.RaisePropertyChanged(nameof(IsPicking));
    }

    private void RebuildPicker()
    {
        PickerCards.Clear();
        if (_pickerSide is not { } side) return;
        foreach (var card in _library.Cards.Where(c => c.Sense == side.Sense))
            if (side != Lf || !SupportsMultiLf || LfCards.CanChoose(card)) PickerCards.Add(card);
    }
}
