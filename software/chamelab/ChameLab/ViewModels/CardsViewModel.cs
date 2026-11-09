using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using ChameLab.Behaviors;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;
using ReactiveUI;
using ChameLab.Localization;

namespace ChameLab.ViewModels;

public sealed record ScanResultRow(string Field, string Value);

/// <summary>What the right-hand pane of the Cards page shows.</summary>
public enum CardsPane { None, Add, Card }

/// <summary>Order of the card list; <see cref="Manual"/> is the library's own order, changed by dragging.</summary>
public enum CardSort { Manual, Name, NameDesc, Type, TypeDesc }

public static class CardSortExtensions
{
    public static string Label(this CardSort s) => s switch
    {
        CardSort.Name => Strings.Common_SortNameAz,
        CardSort.NameDesc => Strings.Common_SortNameZa,
        CardSort.Type => Strings.Cards_SortTypeUp,
        CardSort.TypeDesc => Strings.Cards_SortTypeDown,
        _ => Strings.Common_SortManual,
    };
}

/// <summary>Frequency filter of the card list.</summary>
public enum CardBand { All, Hf, Lf }

/// <summary>An entry of the card type filter; no tag means any type.</summary>
public sealed record CardTypeOption(TagSpecificType? Tag)
{
    public static readonly CardTypeOption Any = new((TagSpecificType?)null);
    public string Label => Tag?.DisplayName() ?? Strings.Cards_AllTypes;
}

/// <summary>Which action panel is open under a saved card.</summary>
public enum CardAction { None, Slots, Write, Delete }

/// <summary>
/// A saved dictionary in the Save keys menu, shown like its row in the dictionary list: choosing
/// it adds the open card's keys to it.
/// </summary>
public sealed record KeysTargetViewModel(DictionaryItemViewModel Item, ICommand SaveCommand);

/// <summary>
/// Card library in one place: the list of saved cards, adding a card (scan
/// HF/LF with a full MIFARE Classic or Ultralight / NTAG dump, import from a file, or manual entry)
/// and an editor for a saved card with duplicate / export / emulate-in-slot /
/// write-to-tag / delete actions.
/// </summary>
public sealed partial class CardsViewModel : PageViewModelBase, IReorderableList, IDropOutList, IKeyRecoveryContext
{
    private readonly DeviceService _device;
    private readonly CardLibrary _library;
    private readonly DictionaryLibrary _dictionaries;
    private CardItemViewModel? _selected;
    private CardEditorViewModel? _form;
    private CardsPane _pane;
    private CardAction _action;
    private string? _filter;
    private CardBand _band;
    private CardTypeOption _typeFilter = CardTypeOption.Any;
    private CardSort _sort = LoadSort();
    // The folder shown; null shows the cards in no folder.
    private string? _folder;
    private bool _isConnected;
    private IReadOnlyList<SlotInfo> _slotInfos = Array.Empty<SlotInfo>();

    // Add-a-card state
    private MifareDump? _classicDump;
    private MifareClassicPrngType? _cardPrng;
    private bool _darksideAvailable;

    public bool ShowDarkside => _classicDump is not null && _darksideAvailable && HasUnknownKeys;
    public bool ShowNested => _cardPrng == MifareClassicPrngType.Weak && HasKnownKeys && HasUnknownKeys;
    public bool ShowStaticNested => _cardPrng == MifareClassicPrngType.Static && HasKnownKeys && HasUnknownKeys;
    public bool ShowStaticEncryptedNested => _classicDump is not null && _cardPrng is not null && HasUnknownKeys;
    public bool ShowHardnested => _cardPrng == MifareClassicPrngType.Hard && HasKnownKeys && HasUnknownKeys;
    public bool CanRunHardnested => AppServices.Hardnested.UnavailableReason is null;
    public string? HardnestedWarning => ShowHardnested ? AppServices.Hardnested.UnavailableReason : null;
    private bool HasKnownKeys => DumpSectors.Any(s => s.KeyAState == KeyCellState.Found || s.KeyBState == KeyCellState.Found);
    private bool HasUnknownKeys => DumpSectors.Any(s => s.KeyAState != KeyCellState.Found || s.KeyBState != KeyCellState.Found);

    private void UpdateRecoveryMethods()
    {
        this.RaisePropertyChanged(nameof(ShowCheckRecovery));
        this.RaisePropertyChanged(nameof(ShowMfkey32));
        this.RaisePropertyChanged(nameof(ShowDarkside));
        this.RaisePropertyChanged(nameof(ShowNested));
        this.RaisePropertyChanged(nameof(ShowStaticNested));
        this.RaisePropertyChanged(nameof(ShowStaticEncryptedNested));
        this.RaisePropertyChanged(nameof(ShowHardnested));
        this.RaisePropertyChanged(nameof(HardnestedWarning));
    }

    private bool _canDump;
    private bool _isUltralightDump;
    private string? _dumpPassword;
    private string? _writePassword;
    private string? _writeLfPassword;
    private bool _hasDump;
    // The card being added is entered by hand: there is no physical card to read or recover keys from.
    private bool _isManual;
    private CancellationTokenSource? _dumpCts;
    private IReadOnlyList<MifareBlockState> _dumpBlockStates = Array.Empty<MifareBlockState>();
    private string? _writeProgressText;
    private CancellationTokenSource? _writeCts;
    private IReadOnlyList<MifareBlockState> _writeBlockStates = Array.Empty<MifareBlockState>();
    private byte[] _scannedAts = Array.Empty<byte>();
    // A dump result is being put into the form: its type change is not a user's change.
    private bool _applyingDump;
    // A dump stopped because its result no longer applies (type changed, add left): its result is dropped.
    private CancellationTokenSource? _discardedDump;

    // Page margins 56 + card list 280 + gap 18 + card editor 600 (see CardsView).
    public override double MinContentWidth => 954;

    public CardsViewModel(DeviceService device, CardLibrary library, DictionaryLibrary dictionaries)
        : base(() => Strings.Nav_Cards, "cards")
    {
        _device = device;
        _library = library;
        _dictionaries = dictionaries;
        WriteT55xxSettings = new(dictionaries);
        WriteT55xxSettings.WhenAnyValue(x => x.ReadyToStart, x => x.NewKeyError)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(WriteLfSettingsReady)));
        _isConnected = device.State == ConnectionState.Connected;
        WriteRecovery = new MifareKeyRecoveryViewModel(() => _device.Client, _dictionaries,
            () => _slotInfos, SourceWriteKeys, work => RunAsync(work), cancelWrite: CancelWrite,
            targetCard: ResolveRecoveryTargetCard);
        WriteRecovery.SetGeneration(SelectedGeneration);
        WriteRecovery.WhenAnyValue(x => x.AllTargetKeysRecovered, x => x.AllPlannedSectorsWritten,
            (keys, written) => (keys, written)).Subscribe(_ => RaiseWriteKeyAvailability());
        this.WhenAnyValue(x => x.IsConnected, x => x.IsBusy, (online, busy) => (online, busy))
            .Subscribe(state =>
            {
                WriteRecovery.SetAvailability(state.Item1, state.Item2);
                this.RaisePropertyChanged(nameof(CanChangeWriteOptions));
            });

        var hasForm = this.WhenAnyValue(x => x.Form).Select(f => f is not null);
        var cardOpen = this.WhenAnyValue(x => x.Selected).Select(s => s is not null);
        // Export / emulate / write: the open card (saved or new) as it stands in the editor.
        var actionable = this.WhenAnyValue(x => x.Pane, x => x.Form,
            (pane, form) => pane is CardsPane.Add or CardsPane.Card && form is not null);
        // Anything that talks to the device stays disabled without one.
        var connected = this.WhenAnyValue(x => x.IsConnected);
        var actionableOnline = actionable.CombineLatest(connected, (ok, online) => ok && online);

        // Off while the source buttons it brings up are already on screen.
        AddCommand = ReactiveCommand.Create(() => LeaveThen(BeginAdd),
            this.WhenAnyValue(x => x.IsPickingSource).Select(picking => !picking));
        CloseCommand = ReactiveCommand.Create(() => LeaveThen(ClosePane));
        SaveAndLeaveCommand = ReactiveCommand.Create(() => ResolveLeave(run: Save()));
        DiscardAndLeaveCommand = ReactiveCommand.Create(() => ResolveLeave(run: true));
        StayCommand = ReactiveCommand.Create(() => ResolveLeave(run: false));

        ScanHfCommand = ReactiveCommand.CreateFromTask(ScanHfAsync, connected);
        ScanLfCommand = ReactiveCommand.CreateFromTask(ScanLfAsync, connected);
        DumpCommand = ReactiveCommand.CreateFromTask(DumpAsync,
            this.WhenAnyValue(x => x.CanDump, x => x.IsConnected, x => x.DumpKeyCount, x => x.IsUltralightDump,
                    x => x.DumpPasswordError, x => x.IsBusy,
                (can, online, keys, ultralight, pwdError, busy) =>
                    can && online && !busy && (ultralight ? pwdError is null : keys > 0)));
        var recoveryIdle = this.WhenAnyValue(x => x.CanDump, x => x.IsConnected, x => x.IsBusy,
            (can, online, busy) => can && online && !busy);
        DarksideCommand = ReactiveCommand.CreateFromTask(() => RecoverKeysAsync(MifareRecoveryMethod.Darkside),
            recoveryIdle.CombineLatest(this.WhenAnyValue(x => x.ShowDarkside), (idle, show) => idle && show));
        NestedCommand = ReactiveCommand.CreateFromTask(() => RecoverKeysAsync(MifareRecoveryMethod.Nested),
            recoveryIdle.CombineLatest(this.WhenAnyValue(x => x.ShowNested), (idle, show) => idle && show));
        StaticNestedCommand = ReactiveCommand.CreateFromTask(() => RecoverKeysAsync(MifareRecoveryMethod.StaticNested),
            recoveryIdle.CombineLatest(this.WhenAnyValue(x => x.ShowStaticNested), (idle, show) => idle && show));
        StaticEncryptedNestedCommand = ReactiveCommand.CreateFromTask(() => RecoverKeysAsync(MifareRecoveryMethod.StaticEncryptedNested),
            recoveryIdle.CombineLatest(this.WhenAnyValue(x => x.ShowStaticEncryptedNested), (idle, show) => idle && show));
        HardnestedCommand = ReactiveCommand.CreateFromTask(() => RecoverKeysAsync(MifareRecoveryMethod.Hardnested),
            recoveryIdle.CombineLatest(this.WhenAnyValue(x => x.ShowHardnested),
                (idle, show) => idle && show && CanRunHardnested));
        CheckRecoveryCommand = ReactiveCommand.CreateFromTask(CheckRecoveryAsync, recoveryIdle);
        this.WhenAnyValue(x => x.IsBusy).Subscribe(_ => RaiseMfkey32Availability());
        Mfkey32Command = ReactiveCommand.CreateFromTask(() => _mfkey32Task = RunMfkey32Async(),
            this.WhenAnyValue(x => x.CanRunMfkey32));
        CancelDumpCommand = ReactiveCommand.Create(CancelDump);
        ImportCommand = ReactiveCommand.CreateFromTask(ImportAsync);
        ManualCommand = ReactiveCommand.Create(BeginManual);
        CancelAddCommand = ReactiveCommand.Create(CancelAdd);
        OpenScanMatchCommand = ReactiveCommand.Create<CardItemViewModel>(OpenScanMatch);

        SaveCommand = ReactiveCommand.Create(() => { Save(); }, hasForm);
        DiscardCommand = ReactiveCommand.Create(Discard);
        UpdateSlotsCommand = ReactiveCommand.Create(() => ResolveSlotUpdate(update: true), connected);
        SkipSlotUpdateCommand = ReactiveCommand.Create(() => ResolveSlotUpdate(update: false));

        DuplicateCommand = ReactiveCommand.Create(Duplicate, cardOpen);
        ExportCommand = ReactiveCommand.CreateFromTask(ExportAsync, actionable);
        ExportBinCommand = ReactiveCommand.CreateFromTask(() => ExportDumpAsync(bin: true), actionable);
        ExportEmlCommand = ReactiveCommand.CreateFromTask(() => ExportDumpAsync(bin: false), actionable);
        SaveKeysToNewDictionaryCommand = ReactiveCommand.Create(SaveKeysToNewDictionary, actionable);
        SaveKeysToFileCommand = ReactiveCommand.CreateFromTask(SaveKeysToFileAsync, actionable);
        SaveWriteT55xxKeyToNewDictionaryCommand = ReactiveCommand.Create(SaveWriteT55xxKeyToNewDictionary);
        SaveWriteT55xxKeyToFileCommand = ReactiveCommand.CreateFromTask(SaveWriteT55xxKeyToFileAsync);
        AssignCommand = ReactiveCommand.Create(() => Toggle(CardAction.Slots), actionableOnline);
        WriteCommand = ReactiveCommand.Create(() => Toggle(CardAction.Write),
            actionableOnline.CombineLatest(this.WhenAnyValue(x => x.CanWriteSelected), (ok, can) => ok && can));
        DeleteCommand = ReactiveCommand.Create(() => Toggle(CardAction.Delete), this.WhenAnyValue(x => x.Selected).Select(s => s is not null));
        CloseActionCommand = ReactiveCommand.Create(() => { Action = CardAction.None; });
        CancelWriteCommand = ReactiveCommand.Create(() =>
        {
            if (IsWriting) CancelWrite();
            else Action = CardAction.None;
        });
        ConfirmDeleteCommand = ReactiveCommand.Create(ConfirmDelete);
        LoadToSlotCommand = ReactiveCommand.CreateFromTask<SlotTargetViewModel>(LoadToSlotAsync, connected);
        RefreshSlotsCommand = ReactiveCommand.CreateFromTask(RefreshSlotsAsync, connected);
        WriteNowCommand = ReactiveCommand.CreateFromTask(WriteAsync,
            this.WhenAnyValue(x => x.IsConnected, x => x.WritePasswordError, x => x.WriteLfPasswordError,
                x => x.WriteLfSettingsReady, x => x.IsBusy, x => x.HasRequiredWriteKeys, x => x.IsWriteSuccessful,
                (online, ulError, lfError, lfReady, busy, keysReady, succeeded) =>
                    online && !busy && !succeeded && keysReady && ulError is null && lfError is null && lfReady));
        WriteAnotherCommand = ReactiveCommand.CreateFromTask(WriteAnotherAsync,
            this.WhenAnyValue(x => x.IsConnected, x => x.IsBusy, x => x.IsWriteSuccessful,
                (online, busy, succeeded) => online && !busy && succeeded));
        DetectCommand = ReactiveCommand.CreateFromTask(DetectAsync, connected);
        ResetFiltersCommand = ReactiveCommand.Create(ResetFilters, this.WhenAnyValue(x => x.HasFilters));
        NewFolderCommand = ReactiveCommand.Create(NewFolder);
        OpenNewFolderCommand = ReactiveCommand.Create(OpenNewFolder);
        RenameCurrentFolderCommand = ReactiveCommand.Create(BeginRenameCurrentFolder);
        ShowRootCommand = ReactiveCommand.Create(() => ShowFolder(null));

        // Library edits made on the UI thread rebuild synchronously so a new card can be selected right away.
        _library.Changed += () =>
        {
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) Rebuild();
            else Avalonia.Threading.Dispatcher.UIThread.Post(Rebuild);
        };
        _device.StateChanged += s => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            IsConnected = s == ConnectionState.Connected;
            if (IsConnected) _ = RefreshSlotsAsync();
            if (!IsConnected)
            {
                if (_mfkey32Running) CancelDump();
                _slotInfos = Array.Empty<SlotInfo>();
                RebuildTargets();
                if (_slotUpdate is not null) ResolveSlotUpdate(update: false);
                if (_action is CardAction.Slots or CardAction.Write) Action = CardAction.None;
            }
        });

        _dictionaries.Changed += () =>
        {
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) { RebuildKeySources(); RebuildKeysTargets(); }
            else Avalonia.Threading.Dispatcher.UIThread.Post(() => { RebuildKeySources(); RebuildKeysTargets(); });
        };

        Rebuild();
        RebuildKeySources();
        RebuildKeysTargets();
        if (_library.LoadError is { } err) ReportError(err);
    }

    // ---- List ----------------------------------------------------------

    public ObservableCollection<CardItemViewModel> Cards { get; } = new();

    public bool IsEmpty => _library.Cards.Count == 0;
    /// <summary>The search or filters hide every card of the folder (or of Cards) shown.</summary>
    public bool NoMatches => HasFilters && Cards.Count == 0 && _library.Cards.Any(InView);
    /// <summary>The open folder has no cards (Cards with every card in a folder shows the folder tiles instead).</summary>
    public bool IsFolderEmpty => !IsEmpty && _folder is not null && !_library.Cards.Any(InView);

    public string CountText
    {
        get
        {
            var count = _library.Cards.Count(InView);
            return Loc.Plural(Strings.Plural_Cards, count);
        }
    }

    /// <summary>The card belongs where the list is: the folder shown, or no folder for Cards.</summary>
    private bool InView(CardSave card) => card.Folder == (_folder ?? string.Empty);

    /// <summary>The page title: the open folder, or Cards for the cards in no folder.</summary>
    public string ListTitle => _folder ?? Strings.Nav_Cards;
    public bool CanUseFiles => StorageService.IsAvailable;

    public string? Filter
    {
        get => _filter;
        set { this.RaiseAndSetIfChanged(ref _filter, value); RaiseFilters(); Rebuild(); }
    }

    public CardBand Band
    {
        get => _band;
        set
        {
            if (_band == value) return;
            this.RaiseAndSetIfChanged(ref _band, value);
            this.RaisePropertyChanged(nameof(IsBandAll));
            this.RaisePropertyChanged(nameof(IsBandHf));
            this.RaisePropertyChanged(nameof(IsBandLf));
            RaiseFilters();
            Rebuild();
        }
    }

    // One per segment of the frequency switch; unchecking a segment is the other one's job.
    public bool IsBandAll { get => _band == CardBand.All; set { if (value) Band = CardBand.All; } }
    public bool IsBandHf { get => _band == CardBand.Hf; set { if (value) Band = CardBand.Hf; } }
    public bool IsBandLf { get => _band == CardBand.Lf; set { if (value) Band = CardBand.Lf; } }

    /// <summary>“All types” and the types of the saved cards on the chosen frequency.</summary>
    public ObservableCollection<CardTypeOption> TypeOptions { get; } = new() { CardTypeOption.Any };

    public CardTypeOption? TypeFilter
    {
        get => _typeFilter;
        set
        {
            // The combo drops its selection while the options are rebuilt; Rebuild restores it.
            if (_rebuilding || value is null || value == _typeFilter) return;
            _typeFilter = value;
            this.RaisePropertyChanged();
            RaiseFilters();
            Rebuild();
        }
    }

    public IReadOnlyList<CardSort> SortModes { get; } = Enum.GetValues<CardSort>();

    public CardSort Sort
    {
        get => _sort;
        set
        {
            if (_sort == value) return;
            this.RaiseAndSetIfChanged(ref _sort, value);
            this.RaisePropertyChanged(nameof(CanReorder));
            SaveSort(value);
            Rebuild();
        }
    }

    /// <summary>Cards can be dragged into place only in the library's own order.</summary>
    public bool CanReorder => _sort == CardSort.Manual;

    /// <summary>The list is narrowed by search, frequency or type.</summary>
    public bool HasFilters => !string.IsNullOrWhiteSpace(_filter) || _band != CardBand.All || _typeFilter.Tag is not null;

    public ICommand ResetFiltersCommand { get; }

    private void RaiseFilters() => this.RaisePropertyChanged(nameof(HasFilters));

    private void ResetFilters()
    {
        _filter = null;
        _band = CardBand.All;
        _typeFilter = CardTypeOption.Any;
        foreach (var name in new[] { nameof(Filter), nameof(Band), nameof(IsBandAll), nameof(IsBandHf), nameof(IsBandLf) })
            this.RaisePropertyChanged(name);
        RaiseFilters();
        Rebuild();
    }

    private bool MatchesBand(CardSave card) => _band switch
    {
        CardBand.Hf => card.Sense == TagSenseType.Hf,
        CardBand.Lf => card.Sense == TagSenseType.Lf,
        _ => true,
    };

    private const string SortSetting = "cardsSort";

    private static CardSort LoadSort()
    {
        var saved = AppSettings.GetString(SortSetting);
        return Enum.TryParse<CardSort>(saved, out var sort) ? sort : CardSort.Name;
    }

    private static void SaveSort(CardSort sort)
    {
        AppSettings.Set(SortSetting, sort.ToString());
    }

    IList IReorderableList.ReorderItems => Cards;
    string IReorderableList.ReorderKey(object item) => ((CardItemViewModel)item).Card.Id;
    void IReorderableList.OpenItem(object item) => Selected = (CardItemViewModel)item;

    /// <summary>Moves a card within the list while it is dragged; <see cref="CommitOrder"/> saves the result.</summary>
    public void MoveItem(int from, int to)
    {
        if (!CanReorder || from == to || from < 0 || to < 0 || from >= Cards.Count || to >= Cards.Count) return;
        _rebuilding = true;
        try { Cards.Move(from, to); }
        finally { _rebuilding = false; }
        // The list may have dropped its highlight while the item moved.
        this.RaisePropertyChanged(nameof(Selected));
    }

    /// <summary>Saves the dragged order of the shown cards to the library.</summary>
    public void CommitOrder()
    {
        if (CanReorder) _library.Reorder(Cards.Select(c => c.Card.Id).ToList());
    }

    /// <summary>The saved card open in the editor; selecting one leaves “add a card”.</summary>
    /// <remarks>Picking another card while the open one has unsaved work asks first.</remarks>
    public CardItemViewModel? Selected
    {
        get => _selected;
        set
        {
            // Detaching the view can clear the list selection. Only an actual card pick
            // changes the editor; closing it goes through CloseCommand instead.
            if (value is null) return;
            if (ReferenceEquals(value, _selected)) return;
            // Clearing the list during a rebuild resets the ListBox selection; Rebuild restores it itself.
            if (_rebuilding) return;
            if (HasUnsavedWork)
            {
                LeaveThen(() => SetSelected(value));
                // The list has already moved its highlight; put it back on the open card.
                Avalonia.Threading.Dispatcher.UIThread.Post(() => this.RaisePropertyChanged(nameof(Selected)));
                return;
            }
            SetSelected(value);
        }
    }

    /// <summary>Opens a saved card in the editor (asked from another page), clearing filters that hide it.</summary>
    public void OpenCard(string id) => LeaveThen(() =>
    {
        if (_library.Cards.All(c => c.Id != id)) return;
        if (Cards.All(c => c.Card.Id != id))
        {
            var folder = _library.Cards.First(c => c.Id == id).Folder;
            SetFolder(folder.Length == 0 ? null : folder);
            ResetFilters();
        }
        SetSelected(Cards.FirstOrDefault(c => c.Card.Id == id));
    });

    private void SetSelected(CardItemViewModel? value)
    {
        if (ReferenceEquals(value, _selected)) return;
        this.RaiseAndSetIfChanged(ref _selected, value, nameof(Selected));
        Action = CardAction.None;
        // Moving to another card or closing the editor answers the slot question with “no”.
        SetSlotUpdate(null);
        if (value is not null)
        {
            // Once saved, a card entered by hand is read and recovered like any other.
            _isManual = false;
            Form = new CardEditorViewModel(value.Card, restrictType: true);
            Pane = CardsPane.Card;
        }
        else if (_pane == CardsPane.Card)
        {
            Form = null;
            Pane = CardsPane.None;
        }
        RaiseCardInfo();
        RebuildTargets();
    }

    // ---- Leaving unsaved work -------------------------------------------

    private Action? _pendingLeave;

    /// <summary>The open card (saved and edited, or new) would be lost by navigating away.</summary>
    private bool HasUnsavedWork => _pane switch
    {
        CardsPane.Card => _form?.IsDirty == true,
        CardsPane.Add => _form is not null,
        _ => false,
    };

    /// <summary>Runs <paramref name="next"/> now, or holds it until the user saves or discards.</summary>
    private void LeaveThen(Action next)
    {
        if (!HasUnsavedWork) { next(); return; }
        _pendingLeave = next;
        this.RaisePropertyChanged(nameof(IsConfirmingLeave));
        this.RaisePropertyChanged(nameof(LeaveText));
        this.RaisePropertyChanged(nameof(IsAsking));
    }

    private void ResolveLeave(bool run)
    {
        var next = _pendingLeave;
        _pendingLeave = null;
        this.RaisePropertyChanged(nameof(IsConfirmingLeave));
        this.RaisePropertyChanged(nameof(IsAsking));
        if (!run) return;
        // Saving may have raised the slot-update question; leave once it is answered.
        if (_slotUpdate is not null) _afterSlotUpdate = next;
        else next?.Invoke();
    }

    /// <summary>Asking whether to save before switching card, closing or starting a new one.</summary>
    public bool IsConfirmingLeave => _pendingLeave is not null;

    /// <summary>A question waits for an answer; the editor is dimmed until then.</summary>
    public bool IsAsking => IsConfirmingLeave || IsConfirmingDelete || IsAskingSlotUpdate;

    public string LeaveText => _pane == CardsPane.Add
        ? Strings.Cards_SaveNewBeforeLeaving
        : string.Format(Strings.Common_SaveChangesTo, _selected?.Name);

    public ICommand SaveAndLeaveCommand { get; }
    public ICommand DiscardAndLeaveCommand { get; }
    public ICommand StayCommand { get; }

    public CardEditorViewModel? Form
    {
        get => _form;
        private set
        {
            if (_form is not null) _form.PropertyChanged -= OnFormPropertyChanged;
            // Another card (or the saved state of this one): an earlier save error is moot.
            if (!ReferenceEquals(_form, value)) ClearSaveError();
            this.RaiseAndSetIfChanged(ref _form, value);
            this.RaisePropertyChanged(nameof(RecoverySelectedSector));
            if (_form is not null) _form.PropertyChanged += OnFormPropertyChanged;
            if (!_applyingDump) InitializeDumpFromForm();
            this.RaisePropertyChanged(nameof(IsCardDirty));
            this.RaisePropertyChanged(nameof(ShowEditor));
            this.RaisePropertyChanged(nameof(ShowMemoryEditor));
            RaiseMemoryView();
            RaiseAddStage();
            RaiseCardInfo();
            RebuildTargets();
        }
    }

    private void OnFormPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CardEditorViewModel.SelectedSector))
            this.RaisePropertyChanged(nameof(RecoverySelectedSector));
        if (e.PropertyName == nameof(CardEditorViewModel.Memory))
            this.RaisePropertyChanged(nameof(ShowMemoryEditor));
        if (e.PropertyName is nameof(CardEditorViewModel.Name) or nameof(CardEditorViewModel.Tag)
            or nameof(CardEditorViewModel.Uid) or nameof(CardEditorViewModel.Sak)
            or nameof(CardEditorViewModel.Atqa) or nameof(CardEditorViewModel.Ats))
            RaiseMfkey32Availability();
        // The card actions follow its type and memory as they are edited.
        if (e.PropertyName is nameof(CardEditorViewModel.Tag) or nameof(CardEditorViewModel.HasData))
        {
            RaiseCardInfo();
            if (e.PropertyName == nameof(CardEditorViewModel.Tag))
            {
                RebuildTargets();
                InitializeDumpFromForm();
                RaiseMemoryView();
            }
        }
        // HasData is raised on every memory edit. MIFARE Classic picks up typed trailer keys from it;
        // for Ultralight only a change of it restarts the dump section, which would otherwise
        // rebuild (and scroll to) the page strip on each edit, e.g. of the password settings.
        if (!_applyingDump && ((e.PropertyName == nameof(CardEditorViewModel.HasData)
                && (!IsUltralightDump || _form?.HasData != _hasDump))
            || e.PropertyName is nameof(CardEditorViewModel.Uid) or nameof(CardEditorViewModel.Sak)
                or nameof(CardEditorViewModel.Atqa) or nameof(CardEditorViewModel.Ats)))
            InitializeDumpFromForm();
        if (e.PropertyName != nameof(CardEditorViewModel.IsDirty)) return;
        this.RaisePropertyChanged(nameof(IsCardDirty));
        // Editing again supersedes the question about the previous save.
        if (_form?.IsDirty == true && _slotUpdate is not null) SetSlotUpdate(null);
    }

    /// <summary>A saved card is open and has unsaved edits (drives the save bar).</summary>
    public bool IsCardDirty => _pane == CardsPane.Card && _form?.IsDirty == true;

    public CardsPane Pane
    {
        get => _pane;
        private set
        {
            this.RaiseAndSetIfChanged(ref _pane, value);
            this.RaisePropertyChanged(nameof(IsAdding));
            this.RaisePropertyChanged(nameof(ShowMfkey32));
            this.RaisePropertyChanged(nameof(IsCardOpen));
            this.RaisePropertyChanged(nameof(HasPane));
            this.RaisePropertyChanged(nameof(IsCardDirty));
            RaiseAddStage();
            // Open actions belong to the card that was open.
            Action = CardAction.None;
            RaiseCardInfo();
            RebuildTargets();
        }
    }

    /// <summary>A card (saved or new) is open: its color, name and actions stay above the scrolling editor.</summary>
    public bool ShowCardHeader => IsCardOpen || IsEditingNew;

    public bool IsAdding => _pane == CardsPane.Add;

    /// <summary>Adding, nothing scanned or imported yet: the source buttons are offered.</summary>
    public bool IsPickingSource => _pane == CardsPane.Add && _form is null;

    /// <summary>
    /// Adding, and the new card is open in the editor. Leaving it (another card, close)
    /// asks to save or discard it first.
    /// </summary>
    public bool IsEditingNew => _pane == CardsPane.Add && _form is not null;

    private void RaiseAddStage()
    {
        this.RaisePropertyChanged(nameof(IsPickingSource));
        this.RaisePropertyChanged(nameof(IsEditingNew));
        this.RaisePropertyChanged(nameof(ShowCardHeader));
    }

    public bool IsCardOpen => _pane == CardsPane.Card;

    /// <summary>The right-hand pane is in use; on phones it replaces the list.</summary>
    public bool HasPane => _pane != CardsPane.None;

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isConnected, value);
            RaiseDumpToolbar();
        }
    }

    public string? ProgressText
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>Current operation shown on the first progress line; null when idle.</summary>
    public string? DumpProgressText
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            DumpProgressDetail = null;
            RaiseDumpToolbar();
            if (value is null) SetDumpKeyProgress(0, 0);
        }
    }

    public bool IsDumpProgressSuccess
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public bool IsDumpProgressError
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>Target and counters shown on the second progress line.</summary>
    public string? DumpProgressDetail
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(ShowMfkey32CaptureCount));
            this.RaisePropertyChanged(nameof(ShowMfkey32FoundKey));
        }
    }

    private int _dumpKeyTried;
    private int _dumpKeyTotal;

    /// <summary>Candidate keys tried so far for the key a MIFARE Classic dump is searching.</summary>
    public int DumpKeyTried
    {
        get => _dumpKeyTried;
        private set => this.RaiseAndSetIfChanged(ref _dumpKeyTried, value);
    }

    /// <summary>Candidate keys in the search; 0 when no key search is under way.</summary>
    public int DumpKeyTotal
    {
        get => _dumpKeyTotal;
        private set => this.RaiseAndSetIfChanged(ref _dumpKeyTotal, value);
    }

    public bool HasDumpKeyProgress => _dumpKeyTotal > 0;

    public string DumpKeyProgressTip => string.Format(Strings.Common_KeysTriedOf, _dumpKeyTried, _dumpKeyTotal);

    private void SetDumpKeyProgress(int tried, int total)
    {
        DumpKeyTotal = total;
        DumpKeyTried = tried;
        this.RaisePropertyChanged(nameof(HasDumpKeyProgress));
        this.RaisePropertyChanged(nameof(DumpKeyProgressTip));
    }

    /// <summary>
    /// The MIFARE Classic dump sector grid: one entry per sector, each drawn as a key A / key B
    /// square pair by <see cref="Controls.SectorKeyGrid"/>. Initialized on scan and kept across methods.
    /// </summary>
    public ObservableCollection<DumpSectorRow> DumpSectors { get; } = new();

    public bool HasDumpSectors => DumpSectors.Count > 0;

    /// <summary>Builds empty (unknown) sectors for a card of <paramref name="blockCount"/> blocks.</summary>
    private void ResetDumpSectors(int blockCount)
    {
        DumpSectors.Clear();
        for (var s = 0; MifareClassic.SectorFirstBlock(s) < blockCount; s++)
            DumpSectors.Add(new DumpSectorRow(s));
        this.RaisePropertyChanged(nameof(HasDumpSectors));
    }

    /// <summary>The first Pending tick (covering the whole card) sizes the sector grid.</summary>
    private void OnDumpSectorBlock(MifareBlockProgress p)
    {
        if (p is { State: MifareBlockState.Pending, First: 0 } && DumpSectors.Count == 0) ResetDumpSectors(p.Count);
        if (p.State == MifareBlockState.Reading)
        {
            SetDumpKeyProgress(0, 0);
            var sector = MifareClassic.SectorOfBlock(p.First);
            DumpProgressText = p.Count == 1 ? Strings.Cards_ReadingCard : Strings.Cards_PreparingSector;
            DumpProgressDetail = string.Format(Strings.Cards_SectorBlock, sector, p.First);
        }
        RefreshDumpReadState();
    }

    private void RefreshDumpReadState()
    {
        if (_classicDump is not { } dump) return;
        foreach (var sector in DumpSectors) sector.UpdateReadState(dump);
    }

    private void OnDumpKey(MifareKeyProgress p)
    {
        if (p.Sector < 0 || p.Sector >= DumpSectors.Count) return;
        DumpSectors[p.Sector].ApplyKey(p);
        UpdateRecoveryMethods();
        var key = p.KeyType == MfKeyType.A ? "A" : "B";
        var detail = p.Phase switch
        {
            MifareKeyPhase.Found => string.Format(Strings.Cards_FoundAfterChecks, p.Tried),
            MifareKeyPhase.NotFound => string.Format(Strings.Cards_NotFoundChecked, p.Tried, p.Total),
            _ => string.Format(Strings.Cards_CheckingKey, Math.Min(p.Tried + 1, p.Total), p.Total),
        };
        DumpProgressText = p.Phase switch
        {
            MifareKeyPhase.Found => Strings.Common_DictKeyFound,
            MifareKeyPhase.NotFound => Strings.Common_DictKeyNotFound,
            _ => Strings.Common_CheckingDictKeys,
        };
        DumpProgressDetail = string.Format(Strings.Cards_SectorBlockKeyDetail, p.Sector, MifareClassic.SectorTrailerBlock(p.Sector), key, detail);
        IsDumpProgressSuccess = p.Phase == MifareKeyPhase.Found;
        IsDumpProgressError = p.Phase == MifareKeyPhase.NotFound;
        SetDumpKeyProgress(p.Tried, p.Total);
    }

    /// <summary>
    /// Read state of every Ultralight / NTAG page, drawn by the block strip; kept after the dump.
    /// Replaced with a new array on each change so the strip redraws.
    /// </summary>
    public IReadOnlyList<MifareBlockState> DumpBlockStates
    {
        get => _dumpBlockStates;
        private set
        {
            this.RaiseAndSetIfChanged(ref _dumpBlockStates, value);
            this.RaisePropertyChanged(nameof(HasDumpBlocks));
        }
    }

    public bool HasDumpBlocks => _dumpBlockStates.Count > 0;

    private void OnDumpBlocks(MifareBlockProgress p)
    {
        if (p is { State: MifareBlockState.Pending, First: 0 })
        {
            ResetDumpBlocks(p.Count);
            return;
        }
        SetDumpBlocks(p.First, p.Count, p.State);
    }

    private void SetDumpBlocks(int first, int count, MifareBlockState state, MifareBlockState? onlyFrom = null) =>
        DumpBlockStates = WithBlocks(_dumpBlockStates, first, count, state, onlyFrom);

    private void ResetDumpBlocks(int count) => DumpBlockStates = new MifareBlockState[count];

    public ICommand AddCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand DiscardCommand { get; }

    public override Task OnActivatedAsync()
    {
        WriteT55xxKeys.Refresh();
        IsConnected = _device.State == ConnectionState.Connected;
        return IsConnected ? RefreshSlotsAsync() : Task.CompletedTask;
    }

    private bool _rebuilding;

    private void Rebuild()
    {
        var selectedId = _selected?.Card.Id;
        _rebuilding = true;
        try
        {
            RebuildTypeOptions();
            var items = _library.Cards
                .Where(c => InView(c) && MatchesBand(c)
                            && (_typeFilter.Tag is not { } tag || c.Tag == tag))
                .Select(c => new CardItemViewModel(c))
                .Where(c => c.Matches(_filter));
            items = _sort switch
            {
                CardSort.Name => items.OrderBy(c => c.Name, NaturalStringComparer.Instance),
                CardSort.NameDesc => items.OrderByDescending(c => c.Name, NaturalStringComparer.Instance),
                // Types in their natural order (LF first, sizes ascending) or reversed; cards
                // without one last either way, names A–Z within a type.
                CardSort.Type => items
                    .OrderBy(c => c.Card.Tag == TagSpecificType.Undefined)
                    .ThenBy(c => c.Card.Tag)
                    .ThenBy(c => c.Name, NaturalStringComparer.Instance),
                CardSort.TypeDesc => items
                    .OrderBy(c => c.Card.Tag == TagSpecificType.Undefined)
                    .ThenByDescending(c => c.Card.Tag)
                    .ThenBy(c => c.Name, NaturalStringComparer.Instance),
                _ => items,
            };
            Cards.Clear();
            foreach (var card in items)
                Cards.Add(card);
        }
        finally { _rebuilding = false; }
        this.RaisePropertyChanged(nameof(TypeFilter));

        // Browsing folders or filtering the list does not leave the open card.
        // Clear the editor only if the card was actually removed from the library.
        var reselect = selectedId is null ? null
            : Cards.FirstOrDefault(c => c.Card.Id == selectedId)
                ?? (_library.Cards.Any(c => c.Id == selectedId) ? _selected : null);
        if (reselect is not null && _form is not null && _pane == CardsPane.Card)
        {
            // Same card, new list item: keep the editor (and any unsaved edits) as they are.
            _selected = reselect;
            this.RaisePropertyChanged(nameof(Selected));
            RaiseCardInfo();
        }
        else if (!ReferenceEquals(reselect, _selected))
        {
            SetSelected(reselect);
        }

        RebuildFolders();
        this.RaisePropertyChanged(nameof(IsEmpty));
        this.RaisePropertyChanged(nameof(NoMatches));
        this.RaisePropertyChanged(nameof(IsFolderEmpty));
        this.RaisePropertyChanged(nameof(CountText));
        this.RaisePropertyChanged(nameof(ListTitle));
    }

    // ---- Folders ---------------------------------------------------------

    /// <summary>The library's folders, A–Z, listed in the navigation pane.</summary>
    public ObservableCollection<CardFolderViewModel> Folders { get; } = new();

    /// <summary>The folder shown, or null when the list shows every card.</summary>
    public CardFolderViewModel? CurrentFolder => _folder is null ? null : Folders.FirstOrDefault(f => f.Name == _folder);

    public ICommand NewFolderCommand { get; }

    /// <summary>Back from a folder to Cards (the list's own back button, for when the navigation pane is hidden).</summary>
    public ICommand ShowRootCommand { get; }

    public bool IsInFolder => _folder is not null;

    public bool HasFolders => Folders.Count > 0;

    /// <summary>Cards (no folder open) lists the folders after its own cards.</summary>
    public bool ShowsFolderTiles => _folder is null && Folders.Count > 0;

    /// <summary>Where the open card can be moved: no folder (back to Cards), apart from the folders below it.</summary>
    public IReadOnlyList<CardFolderOption> RootFolderOptions { get; } = new[] { new CardFolderOption(string.Empty) };

    /// <summary>Where the open card can be moved: every folder.</summary>
    public ObservableCollection<CardFolderOption> FolderOptions { get; } = new();

    /// <summary>The open card's folder; picking another moves the card, and the list follows it there.</summary>
    public CardFolderOption? SelectedCardFolder
    {
        get => _selected is { } s ? new CardFolderOption(s.Card.Folder) : null;
        set
        {
            if (_rebuilding || value is null || _selected is not { } s || s.Card.Folder == value.Name) return;
            SetFolder(value.Name.Length == 0 ? null : value.Name);
            _library.MoveToFolder(s.Card, value.Name);
            Notify(value.Name.Length == 0 ? string.Format(Strings.Cards_MovedOutOfFolder, s.Name) : string.Format(Strings.Cards_MovedToFolder, s.Name, value.Name));
        }
    }

    /// <summary>Label of the open card's folder button.</summary>
    public string CardFolderLabel => _selected is { Card.Folder: { Length: > 0 } folder } ? folder : Strings.Cards_Folder;

    /// <summary>Shows the cards of a folder, or all of them for null.</summary>
    public void ShowFolder(CardFolderViewModel? folder)
    {
        SetFolder(folder?.Name);
        Rebuild();
    }

    private void SetFolder(string? name)
    {
        if (_folder == name) return;
        _folder = name;
        IsRenamingFolder = false;
        this.RaisePropertyChanged(nameof(CurrentFolder));
        this.RaisePropertyChanged(nameof(IsInFolder));
        this.RaisePropertyChanged(nameof(ShowsFolderTiles));
    }

    /// <summary>Syncs <see cref="Folders"/> with the library in place, so selection and an edit in progress survive.</summary>
    private void RebuildFolders()
    {
        var names = _library.Folders;
        if (_folder is not null && !names.Contains(_folder)) SetFolder(null);
        var counts = _library.Cards.GroupBy(c => c.Folder).ToDictionary(g => g.Key, g => g.Count());
        for (var i = Folders.Count - 1; i >= 0; i--)
            if (!names.Contains(Folders[i].Name)) Folders.RemoveAt(i);
        for (var i = 0; i < names.Count; i++)
        {
            var at = -1;
            for (var j = i; j < Folders.Count; j++)
                if (Folders[j].Name == names[i]) { at = j; break; }
            if (at < 0) Folders.Insert(i, new CardFolderViewModel(names[i], f => ShowFolder(f), BeginRename, DeleteFolder));
            else if (at != i) Folders.Move(at, i);
            Folders[i].Count = counts.GetValueOrDefault(names[i]);
        }

        var options = names.Select(n => new CardFolderOption(n, counts.GetValueOrDefault(n))).ToList();
        // Rebuilt when a name or a count changed (options compare by name alone).
        if (!options.Select(o => (o.Name, o.Count)).SequenceEqual(FolderOptions.Select(o => (o.Name, o.Count))))
        {
            _rebuilding = true;
            try
            {
                FolderOptions.Clear();
                foreach (var option in options) FolderOptions.Add(option);
            }
            finally { _rebuilding = false; }
        }
        this.RaisePropertyChanged(nameof(CurrentFolder));
        this.RaisePropertyChanged(nameof(HasFolders));
        this.RaisePropertyChanged(nameof(ShowsFolderTiles));
        this.RaisePropertyChanged(nameof(SelectedCardFolder));
        this.RaisePropertyChanged(nameof(CardFolderLabel));
    }

    /// <summary>The list's New folder: opens the new folder with its title being renamed, as the navigation
    /// pane (where <see cref="NewFolderCommand"/> renames it) may be hidden.</summary>
    public ICommand OpenNewFolderCommand { get; }

    private void NewFolder()
    {
        if (AddFolder() is { } folder) BeginRename(folder);
    }

    private void OpenNewFolder()
    {
        if (AddFolder() is not { } folder) return;
        ShowFolder(folder);
        BeginRenameCurrentFolder();
    }

    private CardFolderViewModel? AddFolder()
    {
        var name = Strings.Common_NewFolder;
        for (var n = 2; _library.FindFolder(name) is not null; n++) name = string.Format(Strings.Cards_NewFolderN, n);
        return _library.AddFolder(name) ? Folders.FirstOrDefault(f => f.Name == name) : null;
    }

    private void BeginRename(CardFolderViewModel folder)
    {
        foreach (var other in Folders.Where(f => f.IsEditing && f != folder)) other.IsEditing = false;
        folder.EditName = folder.Name;
        folder.IsEditing = true;
    }

    /// <summary>Ends an edit of a folder's name; a name another folder has is refused.</summary>
    public void CommitRename(CardFolderViewModel folder)
    {
        if (!folder.IsEditing) return;
        folder.IsEditing = false;
        RenameFolder(folder, folder.EditName);
    }

    /// <summary>Renames a folder; a name another folder has is refused.</summary>
    private void RenameFolder(CardFolderViewModel folder, string? newName)
    {
        var old = folder.Name;
        var name = CardSave.NormalizeFolder(newName);
        if (name.Length == 0 || name == old) return;
        if (_library.FindFolder(name) is { } taken && taken != old)
        {
            ReportError(string.Format(Strings.Cards_FolderExists, taken));
            return;
        }
        // Renamed in place first, so the row (and the selection on it) is kept.
        folder.Name = name;
        if (_folder == old) _folder = name;
        if (_library.RenameFolder(old, name)) return;
        folder.Name = old;
        if (_folder == name) _folder = old;
    }

    public static void CancelRename(CardFolderViewModel folder) => folder.IsEditing = false;

    // ---- The open folder's own header: rename in place, delete ------------

    /// <summary>The open folder's title is a field.</summary>
    public bool IsRenamingFolder
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public string? FolderRenameText
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public ICommand RenameCurrentFolderCommand { get; }

    private void BeginRenameCurrentFolder()
    {
        if (CurrentFolder is not { } folder) return;
        FolderRenameText = folder.Name;
        IsRenamingFolder = true;
    }

    public void CommitFolderRename()
    {
        if (!IsRenamingFolder) return;
        IsRenamingFolder = false;
        if (CurrentFolder is { } folder) RenameFolder(folder, FolderRenameText);
    }

    public void CancelFolderRename() => IsRenamingFolder = false;

    /// <summary>Deletes the open folder (confirmed in the view); Cards shows its cards then.</summary>
    public void DeleteCurrentFolder()
    {
        if (CurrentFolder is { } folder) DeleteFolder(folder);
    }

    /// <summary>Deletes a folder; its cards stay, in no folder.</summary>
    private void DeleteFolder(CardFolderViewModel folder)
    {
        if (_folder == folder.Name) SetFolder(null);
        var count = folder.Count;
        _library.RemoveFolder(folder.Name);
        Notify(count switch
        {
            0 => string.Format(Strings.Cards_DeletedFolder, folder.Name),
            _ => Loc.Plural(Strings.Plural_DeletedFolderCards, count, folder.Name),
        });
    }

    /// <summary>
    /// Moves a card to a folder (empty for none). Taking the open card out of the folder shown
    /// closes it, so unsaved edits are asked about first.
    /// </summary>
    private void MoveCard(CardSave card, string folder)
    {
        var name = card.Name.Length == 0 ? Strings.Common_NoName : card.Name;
        void Move()
        {
            _library.MoveToFolder(card, folder);
            Notify(folder.Length == 0 ? string.Format(Strings.Cards_MovedOutOfFolder, name) : string.Format(Strings.Cards_MovedToFolder, name, folder));
        }
        if (folder != (_folder ?? string.Empty) && _selected?.Card.Id == card.Id) LeaveThen(Move);
        else Move();
    }

    // Cards are dropped onto a folder in the navigation pane, or onto Cards to take them out of theirs.
    bool IDropOutList.CanDropOut(object item, object target) =>
        item is CardItemViewModel card && TargetFolder(target) is { } folder && card.Card.Folder != folder;

    void IDropOutList.DropOut(object item, object target)
    {
        if (item is CardItemViewModel card && TargetFolder(target) is { } folder) MoveCard(card.Card, folder);
    }

    private string? TargetFolder(object target) => target switch
    {
        CardFolderViewModel f => f.Name,
        CardsViewModel c when ReferenceEquals(c, this) => string.Empty,
        _ => null,
    };

    /// <summary>
    /// Offers the types of the saved cards on the chosen frequency. A chosen type no card
    /// has any more (or not on that frequency) falls back to all types.
    /// </summary>
    private void RebuildTypeOptions()
    {
        var tags = _library.Cards.Where(MatchesBand).Select(c => c.Tag)
            .Where(t => t != TagSpecificType.Undefined).Distinct().OrderBy(t => t).ToList();
        if (_typeFilter.Tag is { } chosen && !tags.Contains(chosen))
        {
            _typeFilter = CardTypeOption.Any;
            RaiseFilters();
        }
        // Updated in place: the combo would lose its selection if the chosen entry were replaced.
        var wanted = tags.Select(t => new CardTypeOption(t)).Prepend(CardTypeOption.Any).ToList();
        for (var i = TypeOptions.Count - 1; i >= 0; i--)
            if (!wanted.Contains(TypeOptions[i])) TypeOptions.RemoveAt(i);
        for (var i = 0; i < wanted.Count; i++)
            if (i >= TypeOptions.Count || TypeOptions[i] != wanted[i]) TypeOptions.Insert(i, wanted[i]);
    }

    private void SelectById(string id) => SetSelected(Cards.FirstOrDefault(c => c.Card.Id == id));

    private void ClosePane()
    {
        if (_pane == CardsPane.Card) { SetSelected(null); return; }
        Form = null;
        Pane = CardsPane.None;
    }

    // ---- Add a card ----------------------------------------------------

    public ICommand ScanHfCommand { get; }
    public ICommand ScanLfCommand { get; }
    public ICommand DumpCommand { get; }
    public ICommand CheckRecoveryCommand { get; }
    public ICommand DarksideCommand { get; }
    public ICommand NestedCommand { get; }
    public ICommand StaticNestedCommand { get; }
    public ICommand StaticEncryptedNestedCommand { get; }
    public ICommand HardnestedCommand { get; }
    public ICommand CancelDumpCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand ManualCommand { get; }
    public ICommand CancelAddCommand { get; }
    public ICommand OpenScanMatchCommand { get; }

    /// <summary>LF protocols that can be scanned for.</summary>
    public IReadOnlyList<TagSpecificType> LfProtocols { get; } = new[]
    {
        TagSpecificType.Em410x, TagSpecificType.HidProx, TagSpecificType.IoProx,
        TagSpecificType.Viking, TagSpecificType.Pac, TagSpecificType.Jablotron,
        TagSpecificType.FarpointePyramid,
    };

    public TagSpecificType LfProtocol
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = TagSpecificType.Em410x;

    /// <summary>Why the last scan produced no card (“No HF tag detected.”); null otherwise.</summary>
    public string? ScanMissText
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>
    /// The scanned MIFARE Classic card's PRNG (“Weak (predictable)”…), measured live and not
    /// stored with the card; shown in the dump section. Null when unknown.
    /// </summary>
    public string? Prng
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>Where the card being added came from (“MIFARE Classic 1K, UID …”, “Imported from x.nfc”, “Manual entry”).</summary>
    public string? FormSource
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>The open card supports memory reading, regardless of its source.</summary>
    public bool CanDump
    {
        get => _canDump;
        private set
        {
            this.RaiseAndSetIfChanged(ref _canDump, value);
            this.RaisePropertyChanged(nameof(ShowMemoryEditor));
            RaiseSaveLabel();
            RaiseDumpToolbar();
        }
    }

    /// <summary>
    /// The dump reads Ultralight / NTAG pages (no keys) rather than MIFARE Classic blocks;
    /// switches the dump section's wording and hides the key dictionaries.
    /// </summary>
    public bool IsUltralightDump
    {
        get => _isUltralightDump;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isUltralightDump, value);
            this.RaisePropertyChanged(nameof(DumpHint));
            this.RaisePropertyChanged(nameof(DumpUnit));
            this.RaisePropertyChanged(nameof(TryKeysLabel));
            this.RaisePropertyChanged(nameof(TryKeysTip));
            RaiseDumpToolbar();
        }
    }

    public string DumpHint => _isUltralightDump
        ? Strings.Cards_DumpUlHint
        : Strings.Cards_DumpClassicHint;

    /// <summary>
    /// Optional key for the protected pages of an Ultralight / NTAG dump: the 4-byte PWD
    /// (NTAG21x, Ultralight EV1) or the 16-byte 3DES key of an Ultralight C. Kept between scans,
    /// since a set of cards usually shares one password.
    /// </summary>
    public string? DumpPassword
    {
        get => _dumpPassword;
        set
        {
            this.RaiseAndSetIfChanged(ref _dumpPassword, value);
            this.RaisePropertyChanged(nameof(DumpPasswordError));
        }
    }

    /// <summary>Why <see cref="DumpPassword"/> can't be used; null when it is empty or valid.</summary>
    public string? DumpPasswordError =>
        string.IsNullOrWhiteSpace(_dumpPassword) ? null
        : !Hex.TryParse(_dumpPassword, out var key) ? Strings.Common_NotHex
        : key.Length is 4 or 16 ? null
        : Strings.Cards_UlPasswordLength;

    /// <summary>What one cell of the block strip is: a MIFARE Classic block or an Ultralight page.</summary>
    public string DumpUnit => _isUltralightDump ? Strings.Common_Page : Strings.Common_Block;

    /// <summary>A MIFARE Classic or Ultralight card was scanned but its memory has not been dumped yet.</summary>
    public bool NeedsDump => _canDump && !_hasDump;

    /// <summary>Every memory block or page was read; partial dumps can still be continued.</summary>
    public bool IsDumpComplete
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(ShowCheckRecovery));
            RaiseDumpToolbar();
        }
    }

    public bool IsDumpRunning => DumpProgressText is not null;
    public bool ShowDumpActions => CanDump && !_isManual && !IsDumpRunning && NeedsRecovery;
    public bool ShowDumpNotice => ShowDumpActions && !IsConnected;

    /// <summary>The Ultralight password goes with the Read button: gone once every page is read.</summary>
    public bool ShowDumpPassword => IsUltralightDump && ShowDumpActions;

    /// <summary>The hint over the dump button: until the first dump, or for Ultralight while Read is offered.</summary>
    public bool ShowDumpHint => IsUltralightDump ? ShowDumpPassword : NeedsDump && !_isManual;

    /// <summary>Blocks are missing, or a read trailer lacks key A or key B (held as zeros).</summary>
    private bool NeedsRecovery => !IsDumpComplete || HasMissingKeys;

    private bool HasMissingKeys => _classicDump is { } d && Enumerable.Range(0, d.SectorCount)
        .Any(s => (d.KeyAUnknown[s] && d.KeyA[s] is null) || (d.KeyBUnknown[s] && d.KeyB[s] is null));
    public string DumpNotice => Strings.Cards_DumpNotice;

    private void RaiseDumpToolbar()
    {
        this.RaisePropertyChanged(nameof(ShowRecoveryTools));
        this.RaisePropertyChanged(nameof(IsDumpRunning));
        this.RaisePropertyChanged(nameof(ShowDumpActions));
        this.RaisePropertyChanged(nameof(ShowDumpNotice));
        this.RaisePropertyChanged(nameof(ShowDumpPassword));
        this.RaisePropertyChanged(nameof(ShowDumpHint));
        this.RaisePropertyChanged(nameof(DumpNotice));
    }

    /// <summary>Makes it explicit that saving before a dump keeps only the card's identity.</summary>
    public string AddSaveLabel => NeedsDump ? Strings.Cards_SaveUidOnly : Strings.Common_Save;

    private void RaiseSaveLabel()
    {
        this.RaisePropertyChanged(nameof(NeedsDump));
        this.RaisePropertyChanged(nameof(ShowDumpHint));
        this.RaisePropertyChanged(nameof(AddSaveLabel));
    }

    private void BeginAdd()
    {
        if (_selected is not null) SetSelected(null);
        ResetAddState();
        Form = null;
        Pane = CardsPane.Add;
    }

    /// <summary>Drops the new card and goes back to choosing a source; the list unlocks.</summary>
    private void CancelAdd()
    {
        // The writer or slot list opened for the new card goes with it.
        Action = CardAction.None;
        ResetAddState();
        Form = null;
    }

    private void ResetAddState()
    {
        ScanMissText = null;
        Prng = null;
        FormSource = null;
        _hasDump = false;
        _isManual = false;
        // Leaving the add pane stops a dump still in flight.
        ClearDump();
        CanDump = false;
        IsUltralightDump = false;
        ProgressText = null;
        _scannedAts = Array.Empty<byte>();
        SetScanMatches(null);
    }

    /// <summary>
    /// Wraps a dump's UI update so it is skipped once <see cref="ClearDump"/> dropped that dump: its ticks
    /// still queued on the dispatcher must not repaint (or re-show the progress of) the reset section.
    /// </summary>
    private Action<T> IfKept<T>(CancellationTokenSource cts, Action<T> apply) =>
        v => { if (!ReferenceEquals(_discardedDump, cts)) apply(v); };

    private Action IfKept(CancellationTokenSource cts, Action apply) =>
        () => { if (!ReferenceEquals(_discardedDump, cts)) apply(); };

    /// <summary>Stops a running dump (dropping its result) and empties the grid, strip and summary.</summary>
    private void ClearDump()
    {
        IsMfkey32Done = false;
        Mfkey32FoundKey = null;
        Mfkey32CapturedCount = null;
        if (_dumpCts is { } cts)
        {
            _discardedDump = cts;
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }
        _classicDump = null;
        Prng = null;
        IsDumpComplete = false;
        _cardPrng = null;
        _darksideAvailable = false;
        ResetDumpBlocks(0);
        DumpSectors.Clear();
        UpdateRecoveryMethods();
        this.RaisePropertyChanged(nameof(HasDumpSectors));
        DumpStats.Clear();
        HasDumpStats = false;
        DumpProgressText = null;
    }

    /// <summary>Resume from the editor's memory regardless of how the card was added.</summary>
    private void InitializeDumpFromForm()
    {
        if (_applyingDump) return;
        ClearDump();
        var tag = Form?.Tag ?? TagSpecificType.Undefined;
        IsUltralightDump = tag.IsUltralight();
        CanDump = tag.IsMifareClassic() || IsUltralightDump;
        _hasDump = Form?.HasData == true;
        if (tag.IsMifareClassic()) ResetDumpSectors(tag.MifareClassicBlocks());
        try
        {
            if (CanDump && Form is { } form)
            {
                // A card entered by hand shows its memory while its identity is still being typed.
                var card = _isManual ? form.Draft() : form.Build();
                _scannedAts = card.Ats;
                if (tag.IsMifareClassic())
                {
                    // Saved trailer keys are candidates until authenticated on the physical card.
                    _classicDump = card.ToMifareDump();
                    RefreshDumpReadState();
                    if (_isManual) ApplyTrailerKeys(_classicDump);
                    IsDumpComplete = _classicDump.RecoveredBlocks == _classicDump.BlockCount;
                }
                else
                {
                    var count = tag.UltralightPages();
                    IsDumpComplete = count > 0 && card.Data.Count >= count
                        && card.Data.Take(count).All(b => b.Length == 4);
                    // Every page shows from the scan on: those in the editor as read, the rest not tried.
                    DumpBlockStates = Enumerable.Range(0, count)
                        .Select(p => p < card.Data.Count && card.Data[p].Length == 4 ? MifareBlockState.Read : MifareBlockState.Pending)
                        .ToArray();
                }
            }
        }
        catch (FormatException) { /* The manual identity is still being entered. */ }
        RebuildKeySources();
        UpdateRecoveryMethods();
        RaiseSaveLabel();
    }

    /// <summary>A card entered by hand has no physical card to authenticate against: its trailer keys are its keys.</summary>
    private void ApplyTrailerKeys(MifareDump dump)
    {
        for (var sector = 0; sector < Math.Min(dump.SectorCount, DumpSectors.Count); sector++)
        {
            if (dump.GetBlock(MifareClassic.SectorTrailerBlock(sector)) is not { Length: 16 } trailer) continue;
            dump.KeyA[sector] = trailer[..6];
            dump.KeyB[sector] = trailer[10..];
            DumpSectors[sector].ApplyKey(new MifareKeyProgress(sector, MfKeyType.A, MifareKeyPhase.Found, 0, 0, dump.KeyA[sector]));
            DumpSectors[sector].ApplyKey(new MifareKeyProgress(sector, MfKeyType.B, MifareKeyPhase.Found, 0, 0, dump.KeyB[sector]));
        }
    }

    public bool ShowCheckRecovery => _classicDump is not null && _cardPrng is null && NeedsRecovery && HasUnknownKeys;

    private Task CheckRecoveryAsync() => RunAsync(async () =>
    {
        if (_device.Client is not { } client) return;
        using var cts = new CancellationTokenSource();
        _dumpCts = cts;
        DumpProgressText = Strings.Common_CheckingRecoveryMethods;
        try { await ProbeRecoveryAsync(client, cts.Token); }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_dumpCts, cts)) _dumpCts = null;
            Avalonia.Threading.Dispatcher.UIThread.Post(IfKept(cts, FinishClassicOperation));
        }
    });

    private async Task ProbeRecoveryAsync(ChameleonClient client, CancellationToken ct)
    {
        if (_classicDump is not { } dump) return;
        await client.SetDeviceModeAsync(DeviceMode.Reader, ct);
        var tags = await client.Hf14aScanAsync(ct);
        if (tags.Count == 0 || !dump.Uid.AsSpan().SequenceEqual(tags[0].Uid))
            throw new InvalidOperationException(Strings.Cards_PlaceSelectedCard);
        var prng = await client.DetectNtLevelAsync(ct);
        var darkside = false;
        if (prng != MifareClassicPrngType.Static)
        {
            try { darkside = await client.CheckDarksideAsync(ct) == DarksideResult.Vulnerable; }
            catch (ChameleonException) { }
        }
        for (var sector = 0; sector < dump.SectorCount; sector++)
        {
            var trailer = dump.GetBlock(MifareClassic.SectorTrailerBlock(sector));
            if (trailer is not { Length: 16 }) continue;
            foreach (var kt in new[] { MfKeyType.A, MfKeyType.B })
            {
                var keys = kt == MfKeyType.A ? dump.KeyA : dump.KeyB;
                if (keys[sector] is not null || (kt == MfKeyType.A ? dump.KeyAUnknown : dump.KeyBUnknown)[sector]) continue;
                var key = kt == MfKeyType.A ? trailer[..6] : trailer[10..];
                if (await client.Mf1AuthAsync(MifareClassic.SectorFirstBlock(sector), kt, key, ct))
                    keys[sector] = key;
            }
        }
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!ReferenceEquals(dump, _classicDump)) return;
            Prng = prng.Label();
            _cardPrng = prng;
            _darksideAvailable = darkside;
            for (var sector = 0; sector < dump.SectorCount; sector++)
                foreach (var kt in new[] { MfKeyType.A, MfKeyType.B })
                    if ((kt == MfKeyType.A ? dump.KeyA : dump.KeyB)[sector] is { } key)
                        DumpSectors[sector].ApplyKey(new MifareKeyProgress(sector, kt, MifareKeyPhase.Found, 0, 0, key));
            UpdateRecoveryMethods();
        });
    }

    // ---- Scanned card already saved ---------------------------------------

    /// <summary>
    /// The first saved card matching the scanned tag, in library order: once its memory is dumped,
    /// match memory; before that, frequency and UID / ID (and protocol, for LF; EM410x bit rates aside).
    /// </summary>
    public ObservableCollection<CardItemViewModel> ScanMatches { get; } = new();

    public bool HasScanMatches => ScanMatches.Count > 0;

    private bool _scanMatchesByDump;

    public string ScanMatchText => _scanMatchesByDump
        ? Strings.Cards_DumpAlreadySaved
        : Strings.Cards_TagAlreadySaved;

    private void SetScanMatches(CardSave? scanned)
    {
        ScanMatches.Clear();
        _scanMatchesByDump = scanned is not null && scanned.Data.Any(d => d.Length > 0);
        if (scanned is { Uid.Length: > 0 } || _scanMatchesByDump)
            foreach (var card in _library.Cards.Where(c => c.Sense == scanned!.Sense
                         && (_scanMatchesByDump
                             ? SameMemory(c.Data, scanned.Data)
                             : (c.Sense != TagSenseType.Lf || SameLfProtocol(c.Tag, scanned.Tag))
                               && c.Uid.AsSpan().SequenceEqual(scanned.Uid))).Take(1))
                ScanMatches.Add(new CardItemViewModel(card));
        this.RaisePropertyChanged(nameof(HasScanMatches));
        this.RaisePropertyChanged(nameof(ScanMatchText));
    }

    /// <summary>
    /// A scan reports the EM410x bit rate (EM410X / 64…) while a card saved from a slot has the
    /// plain EM410X type the slot emulates it with; both are the same tag.
    /// </summary>
    private static bool SameLfProtocol(TagSpecificType a, TagSpecificType b) =>
        ChameleonClient.SlotTypeFor(a) == ChameleonClient.SlotTypeFor(b);

    /// <summary>
    /// Same size and the same content in every block / page read in both; blocks left unread
    /// (empty) on either side are not compared, but at least one must be.
    /// </summary>
    private static bool SameMemory(IReadOnlyList<byte[]> a, IReadOnlyList<byte[]> b)
    {
        if (a.Count != b.Count) return false;
        var compared = false;
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].Length == 0 || b[i].Length == 0) continue;
            if (!a[i].AsSpan().SequenceEqual(b[i])) return false;
            compared = true;
        }
        return compared;
    }

    /// <summary>
    /// Opens the saved copy of the scanned card. The scan is dropped, unless its memory was
    /// dumped: then the usual “save before leaving?” question comes first.
    /// </summary>
    private void OpenScanMatch(CardItemViewModel match)
    {
        if (!_hasDump)
        {
            ResetAddState();
            Form = null;
        }
        OpenCard(match.Card.Id);
    }

    private void BeginManual()
    {
        ResetAddState();
        _isManual = true;
        // Starts as a MIFARE Classic 1K with its usual SAK / ATQA; changing the type adapts them.
        // Its memory is that of a factory-blank card of the type, all of it known.
        var card = new CardSave { Name = Strings.Cards_NewCard, Tag = TagSpecificType.Mifare1024 };
        if (card.Tag.DefaultSakAtqa(sevenByteUid: false) is { } id) { card.Sak = id.Sak; card.Atqa = id.Atqa; }
        Form = new CardEditorViewModel(card, blankMemory: true);
        FormSource = Strings.Cards_ManualEntry;
    }

    private Task ScanHfAsync() => RunAsync(async () =>
    {
        var client = _device.Client;
        if (client is null) return;
        await client.SetDeviceModeAsync(DeviceMode.Reader);
        var tags = await client.Hf14aScanAsync();
        if (tags.Count > 0) ScanFeedback.Found();
        else ScanFeedback.Missed();

        // The SAK only says “Ultralight family”; GET_VERSION tells the exact model.
        TagSpecificType? ultralight = null;
        if (tags.Count > 0 && CardFormats.GuessHfType(tags[0].Sak, tags[0].Atqa).IsUltralight())
        {
            try { ultralight = await client.IdentifyUltralightAsync(); }
            catch (ChameleonException) { /* keep the guess */ }
        }

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            ResetAddState();
            if (tags.Count == 0)
            {
                ScanMissText = Strings.Cards_NoHfTag;
                Form = null;
                return;
            }
            var t = tags[0];
            var card = CardFormats.FromHf14a(t);
            if (ultralight is { } exact) card.Tag = exact;
            _scannedAts = card.Ats;
            Form = new CardEditorViewModel(card, sameFrequency: true);
            SetScanMatches(card);
            FormSource = string.Format(Strings.Cards_SourceTypeUid, card.Tag.DisplayName(), t.UidHex);
        });

        Notify(Loc.Plural(Strings.Plural_FoundHfTags, tags.Count));
    });

    private Task ScanLfAsync() => RunAsync(async () =>
    {
        var client = _device.Client;
        if (client is null) return;
        await client.SetDeviceModeAsync(DeviceMode.Reader);

        if (LfProtocol == TagSpecificType.Em410x)
        {
            var tag = await WithMissFeedback(client.Em410xScanAsync());
            ScanFeedback.Found();
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                ResetAddState();
                FormSource = string.Format(Strings.Cards_SourceTag, tag.Type.DisplayName());
                var card = CardFormats.FromEm410x(tag);
                Form = new CardEditorViewModel(card, sameFrequency: true);
                SetScanMatches(card);
            });
            Notify(Strings.Cards_Em410xRead);
            return;
        }

        var result = await WithMissFeedback(client.LfScanAsync(LfProtocol));
        ScanFeedback.Found();
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            ResetAddState();
            FormSource = string.Format(Strings.Cards_SourceTag, result.Type.DisplayName());
            var card = new CardSave
            {
                Name = $"{result.Type.DisplayName()} {result.IdHex}",
                Tag = result.Type,
                Uid = result.Id,
            };
            Form = new CardEditorViewModel(card, sameFrequency: true);
            SetScanMatches(card);
        });
        Notify(string.Format(Strings.Cards_LfTagRead, LfProtocol.DisplayName()));
    });

    // Buzzes when a scan or dump fails outright (no tag, card lost); a cancel stays quiet.
    private static async Task<T> WithMissFeedback<T>(Task<T> scan)
    {
        try { return await scan; }
        catch (Exception e) when (e is not OperationCanceledException) { ScanFeedback.Missed(); throw; }
    }

    private Task DumpAsync() => IsUltralightDump ? DumpUltralightAsync() : RunAsync(async () =>
    {
        var client = _device.Client;
        if (client is null) return;

        if (_classicDump is null && Form is { } form) _classicDump = form.Build().ToMifareDump();
        using var cts = new CancellationTokenSource();
        _dumpCts = cts;
        var keys = BuildKeyDictionary();
        try
        {
            // A saved full dump still needs its physical card's keys authenticated, and so do
            // sectors whose blocks are all there but whose key A or B is missing.
            // The normal continuation reader deliberately skips blocks already present.
            if ((IsDumpComplete || HasMissingKeys) && _classicDump is { } complete && HasUnknownKeys)
            {
                DumpProgressText = Strings.Common_CheckingDictKeys;
                var progress = new PostedProgress<MifareKeyProgress>(IfKept<MifareKeyProgress>(cts, OnDumpKey));
                await client.FindMifareKeysAsync(complete, keys, progress, cts.Token);
            }
            await MifareDumpBodyAsync(client, keys, DumpFormType(), cts, finish: false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        finally
        {
            if (_classicDump is { } dump) RememberCardKeys(dump);
            if (ReferenceEquals(_dumpCts, cts)) _dumpCts = null;
            Avalonia.Threading.Dispatcher.UIThread.Post(IfKept(cts, FinishClassicOperation));
        }
    });

    private Task RecoverKeysAsync(MifareRecoveryMethod method) => RunAsync(async () =>
    {
        var client = _device.Client;
        if (client is null || _classicDump is not { } dump) return;
        using var cts = new CancellationTokenSource();
        _dumpCts = cts;
        DumpProgressText = Strings.Common_StartingRecovery;
        try
        {
            var recovery = new MifareKeyRecovery(client, AppServices.Hardnested, _dictionaries.KnownKeys);
            var progress = new PostedProgress<KeyRecoveryProgress>(IfKept<KeyRecoveryProgress>(cts, OnRecoverProgress));
            try { await recovery.RecoverAsync(dump, method, progress, cts.Token); }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
            if (ReferenceEquals(_discardedDump, cts)) return;
            // Read missing blocks with the keys we now know; another dictionary pass is explicit via Dump.
            await MifareDumpBodyAsync(client, Array.Empty<byte[]>(), dump.Type, cts, read: !cts.IsCancellationRequested);
        }
        finally
        {
            RememberCardKeys(dump);
            if (ReferenceEquals(_dumpCts, cts)) _dumpCts = null;
            Avalonia.Threading.Dispatcher.UIThread.Post(IfKept(cts, FinishClassicOperation));
        }
    });

    private void FinishClassicOperation()
    {
        foreach (var sector in DumpSectors)
            foreach (var kt in new[] { MfKeyType.A, MfKeyType.B })
                if ((kt == MfKeyType.A ? sector.KeyAState : sector.KeyBState) == KeyCellState.Searching)
                    sector.ApplyKey(new MifareKeyProgress(sector.Sector, kt, MifareKeyPhase.NotFound, 0, 0, null));
        DumpProgressText = null;
        UpdateRecoveryMethods();
    }

    /// <summary>The MIFARE Classic type the form is set to, or null when it is not a Classic card.</summary>
    private MifareClassicType? DumpFormType() =>
        Form?.Tag is { } tag && tag.IsMifareClassic() ? MifareClassic.FromSlotTagType(tag) : null;

    /// <summary>Maps a recovery tick onto the dump's per-sector key grid and status line.</summary>
    private void OnRecoverProgress(KeyRecoveryProgress p)
    {
        DumpProgressText = p.Status;
        if (p.Sector < 0 || p.Sector >= DumpSectors.Count) return;
        DumpProgressDetail = string.Format(Strings.Cards_SectorBlockKey, p.Sector, MifareClassic.SectorTrailerBlock(p.Sector), (p.KeyType == MfKeyType.A ? "A" : "B"));
        if (p.Detail is { } detail) DumpProgressDetail += $" · {detail}";
        IsDumpProgressSuccess = p.Mark == KeyMark.Found;
        IsDumpProgressError = p.Mark == KeyMark.None;
        var phase = p.Mark switch
        {
            KeyMark.Found => MifareKeyPhase.Found,
            KeyMark.Checking => MifareKeyPhase.Searching,
            _ => MifareKeyPhase.NotFound,
        };
        DumpSectors[p.Sector].ApplyKey(new MifareKeyProgress(p.Sector, p.KeyType, phase, 0, 0, p.Key));
        SetDumpKeyProgress(0, 0);
        UpdateRecoveryMethods();
    }

    /// <summary>
    /// Shared dump body for dictionary and attack runs: continues reading the card with
    /// <paramref name="keys"/> and fills the grid, stats and card form. The caller owns
    /// <paramref name="cts"/> (and has set <see cref="_dumpCts"/>).
    /// </summary>
    private async Task MifareDumpBodyAsync(
        ChameleonClient client, IReadOnlyList<byte[]> keys, MifareClassicType? type, CancellationTokenSource cts, bool read = true,
        bool finish = true)
    {
        // Posted (not Progress<T>) so ticks reach the UI in order: a "found" key must not be
        // overtaken by the "searching" tick before it.
        // Sector-done ticks are not shown: the key ticks name the sector and the grid shows the rest.
        var progress = new PostedProgress<MifareProgress>(_ => { });
        var blockProgress = new PostedProgress<MifareBlockProgress>(IfKept<MifareBlockProgress>(cts, OnDumpSectorBlock));
        var keyProgress = new PostedProgress<MifareKeyProgress>(IfKept<MifareKeyProgress>(cts, OnDumpKey));
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            DumpStats.Clear();
            HasDumpStats = false;
            DumpProgressText = _mfkey32Running ? Strings.Cards_ReadingCard : Strings.Common_Starting;
        });

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var learnedDump = _classicDump;
        try
        {
            // A stop keeps the partial dump: the call returns what was read, so a canceled dump
            // is handled the same as a completed one and can still be saved.
            var dump = read
                ? await WithMissFeedback(client.ReadMifareClassicAsync(keys, progress, cts.Token, blockProgress, keyProgress, type, _classicDump))
                : _classicDump!;
            learnedDump = dump;
            dump.FillRecoveredKeys();
            clock.Stop();
            var stopped = cts.IsCancellationRequested;
            if (!stopped) ScanFeedback.Found();
            var stats = BuildDumpStats(dump, clock.Elapsed);
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(_discardedDump, cts)) return;
                _classicDump = dump;
                RefreshDumpReadState();
                IsDumpComplete = dump.RecoveredBlocks == dump.BlockCount;
                UpdateRecoveryMethods();
                DumpStats.Clear();
                foreach (var row in stats) DumpStats.Add(row);
                HasDumpStats = true;
                var card = CardFormats.FromMifareDump(dump);
                card.Ats = _scannedAts;
                _applyingDump = true;
                if (Form is { } form) form.ReplaceContent(card);
                else Form = new CardEditorViewModel(card, sameFrequency: true);
                _applyingDump = false;
                SetScanMatches(card);
                _hasDump = true;
                RaiseSaveLabel();
            });
            Notify(stopped
                ? string.Format(Strings.Cards_DumpStoppedKept, ReadSectorCount(dump), dump.SectorCount, dump.UidHex)
                : string.Format(Strings.Cards_ReadSectorsFrom, ReadSectorCount(dump), dump.SectorCount, dump.UidHex));
        }
        finally
        {
            if (learnedDump is not null) RememberCardKeys(learnedDump);
            if (finish)
            {
                if (ReferenceEquals(_dumpCts, cts)) _dumpCts = null;
                Avalonia.Threading.Dispatcher.UIThread.Post(IfKept(cts, FinishClassicOperation));
            }
        }
    }

    private Task DumpUltralightAsync() => RunAsync(async () =>
    {
        var client = _device.Client;
        if (client is null) return;

        using var cts = new CancellationTokenSource();
        _dumpCts = cts;
        var progress = new Progress<MifareProgress>(IfKept<MifareProgress>(cts, p =>
            DumpProgressText = string.Format(Strings.Cards_PageProgress, p.Done, p.Total)));
        var pageProgress = new Progress<MifareBlockProgress>(IfKept<MifareBlockProgress>(cts, OnDumpBlocks));
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            DumpStats.Clear();
            HasDumpStats = false;
            DumpProgressText = Strings.Common_Starting;
        });
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var key = Hex.TryParse(DumpPassword, out var k) && k.Length > 0 ? k : null;
            var previous = Form?.Build();
            if (previous is not null)
            {
                await client.SetDeviceModeAsync(DeviceMode.Reader, cts.Token);
                var tags = await client.Hf14aScanAsync(cts.Token);
                if (tags.Count == 0 || !previous.Uid.AsSpan().SequenceEqual(tags[0].Uid))
                    throw new InvalidOperationException(Strings.Cards_PlaceSelectedCard);
            }
            var result = await WithMissFeedback(client.DumpUltralightAsync(key, progress, cts.Token, pageProgress,
                _dictionaries.KnownKeys.PasswordForUid));
            if (result.Pwd is { } accepted) RememberCardPassword(result.Card.Uid, accepted, result.Card.Tag);
            // Pages an earlier read got stay read.
            var kept = new List<int>();
            if (previous is not null && previous.Tag == result.Card.Tag)
            {
                for (var page = 0; page < Math.Min(previous.Data.Count, result.Card.Data.Count); page++)
                    if (result.Card.Data[page].Length != 4 && previous.Data[page].Length == 4)
                    {
                        result.Card.Data[page] = previous.Data[page];
                        kept.Add(page);
                    }
                result = result with { PagesRead = result.Card.Data.Count(p => p.Length == 4) };
            }
            clock.Stop();
            ScanFeedback.Found();
            var stats = BuildUltralightStats(result, clock.Elapsed);
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(_discardedDump, cts)) return;
                DumpStats.Clear();
                foreach (var row in stats) DumpStats.Add(row);
                HasDumpStats = true;
                foreach (var page in kept) SetDumpBlocks(page, 1, MifareBlockState.Read);
                _applyingDump = true;
                if (Form is { } form) form.ReplaceContent(result.Card);
                else Form = new CardEditorViewModel(result.Card, sameFrequency: true);
                _applyingDump = false;
                FormSource = string.Format(Strings.Cards_SourceTypeUid, result.Card.Tag.DisplayName(), Hex.Compact(result.Card.Uid));
                IsDumpComplete = result.PagesRead == result.PageCount;
                SetScanMatches(result.Card);
                _hasDump = true;
                RaiseSaveLabel();
            });
            Notify(string.Format(Strings.Cards_ReadPagesFrom, result.PagesRead, result.PageCount, Hex.Compact(result.Card.Uid)));
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Notify(Strings.Cards_DumpCanceled);
            Avalonia.Threading.Dispatcher.UIThread.Post(IfKept(cts, () =>
                SetDumpBlocks(0, _dumpBlockStates.Count, MifareBlockState.Pending, onlyFrom: MifareBlockState.Reading)));
        }
        finally
        {
            if (ReferenceEquals(_dumpCts, cts)) _dumpCts = null;
            Avalonia.Threading.Dispatcher.UIThread.Post(IfKept(cts, () => DumpProgressText = null));
        }
    });

    private static List<ScanResultRow> BuildUltralightStats(UltralightDumpResult r, TimeSpan elapsed)
    {
        var extra = r.Card.Extra;
        var pct = r.PageCount == 0 ? 0 : 100.0 * r.PagesRead / r.PageCount;
        var rows = new List<ScanResultRow>
        {
            new(Strings.Cards_Model, r.Card.Tag.DisplayName()),
            new(Strings.Cards_PagesRead, $"{r.PagesRead} / {r.PageCount} ({pct:0}%)"),
        };
        if (r.KeyAccepted is { } accepted)
            rows.Add(new(r.Card.Tag == TagSpecificType.Mf0Icu2 ? Strings.Cards_3DesKey : Strings.Cards_Password, accepted
                ? (r.KnownKeyUsed ? string.Format(Strings.Cards_RememberedAccepted, Hex.Spaced(r.Pwd ?? [])) : "accepted")
                    + (r.Pack is { } pack ? $" · PACK {Hex.Spaced(pack)}" : "")
                : r.KnownKeyUsed ? Strings.Cards_RememberedRejected
                : Strings.Cards_PasswordRejected));
        else if (r.KeyNotNeeded)
            rows.Add(new(Strings.Cards_Password, Strings.Cards_PasswordNotSent));
        if (r.LockedFrom is { } locked)
            rows.Add(r.CardLost
                ? new(Strings.Cards_Stopped, string.Format(Strings.Cards_StoppedAtPage, locked))
                : new(Strings.Cards_Protected, locked == r.PageCount - 1 ? string.Format(Strings.Cards_PageN, locked) : string.Format(Strings.Cards_PagesRange, locked, r.PageCount - 1)));
        if (extra.UltralightSignature.Length > 0) rows.Add(new(Strings.Cards_Signature, Hex.Compact(extra.UltralightSignature)));
        if (extra.UltralightCounters.Count > 0)
            rows.Add(new(extra.UltralightCounters.Count == 1 ? Strings.Cards_Counter : Strings.Cards_Counters, string.Join(", ", extra.UltralightCounters)));
        rows.Add(new(Strings.Cards_Time, $"{elapsed.TotalSeconds:0.0} s"));
        return rows;
    }

    /// <summary>Stops a running dump; whatever was read so far is kept and can be saved.</summary>
    private void CancelDump()
    {
        if (IsMfkey32Done)
        {
            IsMfkey32Done = false;
            Mfkey32FoundKey = null;
            DumpProgressText = null;
            return;
        }
        if (_dumpCts is not { } cts) return;
        try { cts.Cancel(); } catch (ObjectDisposedException) { /* already finished */ }
        DumpProgressText = Strings.Common_Stopping;
    }

    // ---- Dump statistics -------------------------------------------------

    /// <summary>Summary of the last dump; per-sector keys are already on the sector grid.</summary>
    public ObservableCollection<ScanResultRow> DumpStats { get; } = new();

    public bool HasDumpStats
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    private static int ReadSectorCount(MifareDump dump) =>
        Enumerable.Range(0, dump.SectorCount).Count(s =>
            Enumerable.Range(MifareClassic.SectorFirstBlock(s), MifareClassic.BlocksInSector(s))
                .All(b => dump.GetBlock(b) is not null));

    private static List<ScanResultRow> BuildDumpStats(MifareDump dump, TimeSpan elapsed)
    {
        // A sector counts once every one of its blocks was read, as in the memory summary.
        var read = ReadSectorCount(dump);
        return new List<ScanResultRow>
        {
            new(Strings.Cards_SectorsRead, $"{read} / {dump.SectorCount}"),
            new(Strings.Cards_Time, $"{elapsed.TotalSeconds:0.0} s"),
        };
    }

    // ---- Dump key sources ------------------------------------------------

    /// <summary>Built-in keys plus every saved dictionary, each with a check box for the next dump.</summary>
    public ObservableCollection<DumpKeySourceViewModel> DumpKeySources { get; } = new();

    private void RememberCardKeys(MifareDump dump)
    {
        try { _dictionaries.KnownKeys.Remember(dump); }
        catch (Exception ex) { ReportError(string.Format(Strings.Cards_CouldNotSaveKnownKeys, ex.Message)); }
    }

    private void RememberCardPassword(byte[] uid, byte[] pwd, TagSpecificType? tag = null)
    {
        try { _dictionaries.KnownKeys.RememberPassword(uid, pwd, tag); }
        catch (Exception ex) { ReportError(string.Format(Strings.Cards_CouldNotSavePassword, ex.Message)); }
    }

    private void RememberT55xxPassword(T55xxPasswordResult result)
    {
        try { _dictionaries.KnownKeys.RememberT55xxPassword(result.Card.Id, result.NewKey, result.Card.Type); }
        catch (Exception ex) { ReportError(string.Format(Strings.Cards_CouldNotSavePassword, ex.Message)); }
    }

    /// <summary>Unique keys across the checked sources: what a dump will try per sector and key type.</summary>
    public int DumpKeyCount
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(DumpKeyCountText));
        }
    }

    public string DumpKeyCountText => DumpKeyCount switch
    {
        0 => Strings.Cards_NoKeysSelected,
        var n => Loc.Plural(Strings.Plural_UniqueKeys, n),
    };

    /// <summary>Rebuilds the source list from the dictionary library, keeping earlier unchecks.</summary>
    private void RebuildKeySources()
    {
        DumpKeySources.Clear();
        DumpKeySources.Add(NewKeySource(KnownCardKeys.Id, KnownCardKeys.Name,
            _classicDump is { } dump ? _dictionaries.KnownKeys.ForUid(dump.Uid) : Array.Empty<byte[]>()));
        DumpKeySources.Add(NewKeySource(DumpKeySourceViewModel.BuiltInId, Strings.Common_BuiltInKeys, MifareClassic.DefaultKeys));
        // MIFARE Classic keys are 6 bytes: those of every dictionary that has some.
        foreach (var d in _dictionaries.Dictionaries.Where(d => d.HasKeysOfLength(6)))
            DumpKeySources.Add(NewKeySource(d.Id, string.IsNullOrWhiteSpace(d.Name) ? Strings.Common_Untitled : d.Name, d.KeysOfLength(6), d.Color));
        WriteRecovery.SetKeySources(DumpKeySources);
        UpdateDumpKeyCount();
    }

    private DumpKeySourceViewModel NewKeySource(string id, string name, IReadOnlyList<byte[]> keys, string? color = null)
    {
        var source = new DumpKeySourceViewModel(id, name, keys, KeySourceSelection.IsSelected(id), color);
        source.WhenAnyValue(x => x.IsSelected).Skip(1).Subscribe(on =>
        {
            if (!DumpKeySources.Contains(source)) return;
            KeySourceSelection.SetSelected(id, on);
            UpdateDumpKeyCount();
        });
        return source;
    }

    private void UpdateDumpKeyCount()
    {
        DumpKeyCount = BuildKeyDictionary().Count;
        WriteRecovery.Refresh();
    }

    /// <summary>Keys of the checked sources in list order (built-in first), de-duplicated.</summary>
    private IReadOnlyList<byte[]> BuildKeyDictionary()
    {
        var seen = new HashSet<string>();
        var keys = new List<byte[]>();
        foreach (var source in DumpKeySources)
        {
            if (!source.IsSelected) continue;
            foreach (var k in source.IsKnownCardKeys
                ? (_classicDump is { } dump ? _dictionaries.KnownKeys.ForUid(dump.Uid) : Array.Empty<byte[]>())
                : source.Keys)
                if (seen.Add(Convert.ToHexString(k))) keys.Add(k);
        }
        if (_classicDump is { } saved)
            for (var sector = 0; sector < saved.SectorCount; sector++)
                foreach (var key in saved.TrailerKeys(sector))
                    if (seen.Add(Convert.ToHexString(key))) keys.Add(key);
        return keys;
    }

    private Task ImportAsync() => RunAsync(async () =>
    {
        var picked = await StorageService.OpenAsync(Strings.Cards_ImportCardTitle, CardFormats.ImportExtensions);
        if (picked is not { } file) return;

        // A Chameleon Ultra GUI library (settings export, folder export or shared_preferences.json) keeps its folders.
        if (ChameleonGuiImport.TryRead(file.Data) is { } gui)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                var (added, refiled) = _library.Merge(gui.Cards, gui.Folders);
                if (gui.Cards.Count > 0) SelectById(gui.Cards[^1].Id);
                Notify(Loc.Plural(Strings.Plural_ImportedCards, added + refiled));
            });
            return;
        }

        IReadOnlyList<CardSave> cards;
        try { cards = CardFormats.Import(file.Name, file.Data); }
        catch (Exception ex) { throw new FormatException(string.Format(Strings.Cards_UnsupportedFile, file.Name, ex.Message)); }
        if (cards.Count == 0) throw new FormatException(string.Format(Strings.Cards_FileHasNoCards, file.Name));

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (cards.Count == 1)
            {
                // A single card is reviewed (name, color, identity) before it is saved.
                ResetAddState();
                Form = new CardEditorViewModel(cards[0]);
                FormSource = string.Format(Strings.Common_ImportedFrom, file.Name);
                return;
            }
            // A whole library export goes straight in (the open folder takes cards that have none).
            if (_folder is not null)
                foreach (var card in cards.Where(c => c.Folder.Length == 0)) card.Folder = _folder;
            _library.AddRange(cards);
            SelectById(cards[^1].Id);
            Notify(Loc.Plural(Strings.Plural_ImportedCards, cards.Count));
        });
    });

    // ---- Save ----------------------------------------------------------

    /// <returns>False when the card could not be saved (it stays open).</returns>
    private bool Save()
    {
        if (Form is not { } form) return false;
        ClearSaveError();
        CardSave card;
        try { card = form.Build(); }
        catch (FormatException ex) { ReportSaveError(ex.Message); return false; }

        var isNew = _pane == CardsPane.Add;
        var previous = isNew ? null : _selected?.Card;
        if (isNew && card.Folder.Length == 0 && _folder is not null) card.Folder = _folder;
        try
        {
            if (isNew) _library.Add(card); else _library.Update(card);
        }
        catch (Exception ex) { ReportSaveError(string.Format(Strings.Common_CouldNotSave, ex.Message)); return false; }

        if (isNew)
        {
            Pane = CardsPane.None;
            Form = null;
            SelectById(card.Id);
            return true;
        }

        // Fresh editor so the saved state becomes the new baseline.
        Form = new CardEditorViewModel(card, restrictType: true);
        RaiseCardInfo();
        RebuildTargets();
        if (previous is not null) OfferSlotUpdate(previous, card);
        return true;
    }

    // ---- Updating slots that emulate an edited card ----------------------

    private sealed record SlotUpdate(CardSave Card, IReadOnlyList<int> Slots);

    private SlotUpdate? _slotUpdate;
    private Action? _afterSlotUpdate;

    /// <summary>
    /// After an edit that changes what the device emulates, asks whether to re-upload
    /// the card to the slots still holding its previous version. A new color alone
    /// is just the slot LED, so it goes to those slots right away.
    /// </summary>
    private void OfferSlotUpdate(CardSave previous, CardSave saved)
    {
        if (!IsConnected) return;
        var slots = _slotInfos.Where(s => ChameleonClient.SlotHoldsCard(s, previous)).Select(s => s.Index).ToList();
        if (slots.Count == 0) return;
        if (ChangesEmulation(previous, saved))
            SetSlotUpdate(new SlotUpdate(saved, slots));
        else if (previous.Color != saved.Color)
            _ = UpdateSlotColorsAsync(saved, slots);
    }

    private Task UpdateSlotColorsAsync(CardSave card, IReadOnlyList<int> slots) => RunAsync(async () =>
    {
        var client = _device.Client;
        if (client is null) return;
        try
        {
            foreach (var slot in slots)
                await client.SetSlotColorAsync(slot, card.Sense, card.Color);
        }
        catch (ChameleonException) { return; /* older firmware: no per-card LED colors */ }
        await client.SaveSlotConfigAsync();
        var infos = await client.GetAllSlotsAsync();
        Avalonia.Threading.Dispatcher.UIThread.Post(() => { _slotInfos = infos; RebuildTargets(); });
    });

    /// <summary>Everything but the color needs a full upload (the name becomes the slot nick).</summary>
    private static bool ChangesEmulation(CardSave a, CardSave b)
    {
        static string Payload(CardSave c)
        {
            var json = c.ToJson();
            json.Remove("color");
            return json.ToJsonString();
        }
        return Payload(a) != Payload(b);
    }

    private void SetSlotUpdate(SlotUpdate? update)
    {
        _slotUpdate = update;
        if (update is null) _afterSlotUpdate = null;
        this.RaisePropertyChanged(nameof(IsAskingSlotUpdate));
        this.RaisePropertyChanged(nameof(SlotUpdateText));
        this.RaisePropertyChanged(nameof(IsAsking));
    }

    /// <summary>Asking whether to push the saved card to the slots that emulate it.</summary>
    public bool IsAskingSlotUpdate => _slotUpdate is not null;

    public string SlotUpdateText => _slotUpdate is not { } u
        ? string.Empty
        : u.Slots.Count == 1
            ? string.Format(Strings.Cards_LoadedInSlot, u.Card.Name, u.Slots[0])
            : string.Format(Strings.Cards_LoadedInSlots, u.Card.Name, string.Join(", ", u.Slots));

    public ICommand UpdateSlotsCommand { get; }
    public ICommand SkipSlotUpdateCommand { get; }

    /// <summary>Closes the question and carries on with a navigation that was waiting for it.</summary>
    private void ResolveSlotUpdate(bool update)
    {
        var pending = _slotUpdate;
        var next = _afterSlotUpdate;
        SetSlotUpdate(null);
        if (update && pending is not null) _ = UpdateSlotsAsync(pending);
        next?.Invoke();
    }

    private Task UpdateSlotsAsync(SlotUpdate update) => RunAsync(async () =>
    {
        var client = _device.Client;
        if (client is null) return;
        // Loading a card activates its slot; put the user's active slot back afterwards.
        var active = _slotInfos.FirstOrDefault(s => s.IsActive)?.Index;

        try
        {
            foreach (var slot in update.Slots)
            {
                var progress = new Progress<MifareProgress>(p =>
                    ProgressText = string.Format(Strings.Cards_UpdatingSlot, slot, p.Done, p.Total));
                await client.LoadCardToSlotAsync(update.Card, slot, progress);
            }
            if (active is { } a && a != update.Slots[^1])
            {
                await client.SetActiveSlotAsync(a);
                await client.SaveSlotConfigAsync();
            }
        }
        finally
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => ProgressText = null);
        }

        var slots = await client.GetAllSlotsAsync();
        Avalonia.Threading.Dispatcher.UIThread.Post(() => { _slotInfos = slots; RebuildTargets(); });
        Notify(update.Slots.Count == 1
            ? string.Format(Strings.Cards_UpdatedSlot, update.Slots[0], update.Card.Name)
            : string.Format(Strings.Cards_UpdatedSlots, string.Join(", ", update.Slots), update.Card.Name));
    });

    private void Discard()
    {
        if (_selected is { } s) Form = new CardEditorViewModel(s.Card, restrictType: true);
    }

    // ---- Saved card actions ---------------------------------------------

    public ICommand DuplicateCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand ExportBinCommand { get; }
    public ICommand ExportEmlCommand { get; }
    public ICommand AssignCommand { get; }
    public ICommand WriteCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand CloseActionCommand { get; }
    public ICommand ConfirmDeleteCommand { get; }
    public ICommand LoadToSlotCommand { get; }
    public ICommand RefreshSlotsCommand { get; }
    public ICommand WriteNowCommand { get; }
    public ICommand WriteAnotherCommand { get; }
    public ICommand CancelWriteCommand { get; }
    public ICommand DetectCommand { get; }

    public ObservableCollection<SlotTargetViewModel> SlotTargets { get; } = new();

    public CardAction Action
    {
        get => _action;
        private set
        {
            // Closing the writer (Cancel, another action, a disconnect) stops a write in flight.
            if (value != CardAction.Write)
            {
                CancelWrite();
                ClearWriteSession();
                WriteRecovery.Reset();
            }
            this.RaiseAndSetIfChanged(ref _action, value);
            this.RaisePropertyChanged(nameof(IsSlotsOpen));
            this.RaisePropertyChanged(nameof(IsWriteOpen));
            this.RaisePropertyChanged(nameof(IsConfirmingDelete));
            this.RaisePropertyChanged(nameof(IsAsking));
            this.RaisePropertyChanged(nameof(ShowEditor));
            this.RaisePropertyChanged(nameof(ShowMemoryEditor));
        }
    }

    public bool IsSlotsOpen => _action == CardAction.Slots;
    public bool IsWriteOpen => _action == CardAction.Write;
    public bool IsConfirmingDelete => _action == CardAction.Delete;

    /// <summary>
    /// The list stays usable (leaving unsaved work asks first), except while a write runs:
    /// picking another card would stop it halfway through the tag.
    /// </summary>
    public bool ListLocked => IsWriting;

    /// <summary>The card editor is shown unless the writer view has taken over the pane.</summary>
    public bool ShowEditor => _form is not null && !IsWriteOpen;

    /// <summary>The memory panel is shown only for an open card with readable or editable memory.</summary>
    public bool ShowMemoryEditor => ShowEditor && (CanDump || _form?.Memory is not null);

    private bool _isNdefView;

    /// <summary>The memory of an Ultralight / NTAG card is shown as NDEF records rather than hex; kept between cards.</summary>
    public bool IsNdefView
    {
        get => _isNdefView;
        set
        {
            this.RaiseAndSetIfChanged(ref _isNdefView, value);
            RaiseMemoryView();
        }
    }

    /// <summary>The other segment of the view switch.</summary>
    public bool IsHexView
    {
        get => !_isNdefView;
        set => IsNdefView = !value;
    }

    public bool CanShowNdef => _form?.IsUltralight == true;
    public bool ShowNdefView => _isNdefView && CanShowNdef;
    public bool ShowHexView => !ShowNdefView;

    private void RaiseMemoryView()
    {
        this.RaisePropertyChanged(nameof(IsHexView));
        this.RaisePropertyChanged(nameof(CanShowNdef));
        this.RaisePropertyChanged(nameof(ShowNdefView));
        this.RaisePropertyChanged(nameof(ShowHexView));
    }

    private void Toggle(CardAction action)
    {
        Action = _action == action ? CardAction.None : action;
        if (_action == CardAction.Write)
        {
            WriteT55xxKeys.Refresh();
            ClearWriteT55xxResult();
            WriteBlockStates = Array.Empty<MifareBlockState>();
            ResetWriteRecovery();
            // A clone of a protected card gets its settings rewritten; otherwise the target keeps its own.
            WriteProtection = _form is { Protection: { } p } form ? new WriteProtectionViewModel(p, form.Tag) : null;
            RewriteProtection = _form?.IsProtected == true;
        }
    }

    /// <summary>Short progress line shown beside the Stop button while a write runs; null otherwise.</summary>
    public string? WriteProgressText
    {
        get => _writeProgressText;
        private set
        {
            this.RaiseAndSetIfChanged(ref _writeProgressText, value);
            this.RaisePropertyChanged(nameof(IsWriting));
            this.RaisePropertyChanged(nameof(ListLocked));
            this.RaisePropertyChanged(nameof(IsT55xxWriting));
            this.RaisePropertyChanged(nameof(WriteKeyHint));
            this.RaisePropertyChanged(nameof(CancelWriteLabel));
            this.RaisePropertyChanged(nameof(CancelWriteTip));
        }
    }

    public bool IsWriting => _writeProgressText is not null;
    public bool IsT55xxWriting => IsT55xxWrite && IsWriting;

    public string CancelWriteLabel => IsWriting ? Strings.Common_Cancel : Strings.Common_Close;

    public string CancelWriteTip => IsWriting
        ? Strings.Cards_StopWriteTip
        : Strings.Cards_CloseWriter;

    /// <summary>
    /// Write state of every card block (or Ultralight page), drawn by the writer's block strip;
    /// kept after the write. Replaced with a new array on each change so the strip redraws.
    /// </summary>
    public IReadOnlyList<MifareBlockState> WriteBlockStates
    {
        get => _writeBlockStates;
        private set
        {
            this.RaiseAndSetIfChanged(ref _writeBlockStates, value);
            this.RaisePropertyChanged(nameof(HasWriteBlocks));
        }
    }

    public bool HasWriteBlocks => IsUltralightWrite && _writeBlockStates.Count > 0;

    /// <summary>What one cell of the writer's strip is.</summary>
    public string WriteUnit => IsUltralightWrite ? Strings.Common_Page : Strings.Common_Block;

    private void OnWriteBlocks(MifareBlockProgress p)
    {
        if (IsMifareWrite) WriteRecovery.ApplyWriteProgress(p);
        if (p is { State: MifareBlockState.Pending, First: 0 })
        {
            WriteBlockStates = new MifareBlockState[p.Count];
            return;
        }
        WriteBlockStates = WithBlocks(_writeBlockStates, p.First, p.Count, p.State);
    }

    private static MifareBlockState[] WithBlocks(
        IReadOnlyList<MifareBlockState> states, int first, int count, MifareBlockState state, MifareBlockState? onlyFrom = null)
    {
        var next = states.ToArray();
        for (var b = first; b < first + count && b < next.Length; b++)
            if (onlyFrom is null || next[b] == onlyFrom) next[b] = state;
        return next;
    }

    /// <summary>Stops a running write; blocks already written stay on the card.</summary>
    private void CancelWrite()
    {
        if (_writeCts is not { } cts) return;
        try { cts.Cancel(); } catch (ObjectDisposedException) { /* already finished */ }
        WriteProgressText = Strings.Common_Stopping;
    }

    /// <summary>Tag type the card actions work on: the open card's, as edited.</summary>
    private TagSpecificType? ActionTag => _pane is CardsPane.Card or CardsPane.Add ? _form?.Tag : null;

    /// <summary>
    /// The card to duplicate, export, emulate or write: built from the form, unsaved edits included
    /// (throws with a user-facing message when the form is incomplete).
    /// </summary>
    private CardSave? ActionCard() => _pane is CardsPane.Card or CardsPane.Add ? _form?.Build() : null;

    public bool CanExportDump => _pane is CardsPane.Card or CardsPane.Add
        && _form is { IsMifareClassic: true, HasData: true };

    public bool CanWriteSelected => ActionTag?.CanWriteToTag() == true;

    /// <summary>The writer targets a MIFARE Classic card, so the Gen1/2/3 choice applies.</summary>
    public bool IsMifareWrite => ActionTag?.IsMifareClassic() == true;

    public IReadOnlyList<MagicGeneration> MagicGenerations { get; } =
        new[] { MagicGeneration.Gen1a, MagicGeneration.Gen2, MagicGeneration.Gen3 };

    // The magic card kind chosen or detected last; a set of blanks is usually one kind.
    private const string GenerationSetting = "cardsWriteGeneration";

    private const string Gen2Block0Setting = "cardsWriteGen2Block0";

    public bool IsGen2Write => IsMifareWrite && SelectedGeneration == MagicGeneration.Gen2;

    public bool WriteGen2Block0
    {
        get;
        set
        {
            if (field == value) return;
            this.RaiseAndSetIfChanged(ref field, value);
            AppSettings.Set(Gen2Block0Setting, value);
            this.RaisePropertyChanged(nameof(WriteInstructions));
            this.RaisePropertyChanged(nameof(WriteWarning));
            RefreshWritePreview();
        }
    } = AppSettings.Get(Gen2Block0Setting) is JsonValue savedBlock0
        && savedBlock0.TryGetValue<bool>(out var enabledBlock0) && enabledBlock0;

    /// <summary>The writer targets an Ultralight / NTAG card: only its user pages are written.</summary>
    public bool IsUltralightWrite => ActionTag?.IsUltralight() == true;

    /// <summary>The writer targets an LF (T55xx) tag, so the unlock password applies.</summary>
    public bool IsLfWrite => ActionTag is { } t && t.SenseType() == TagSenseType.Lf && t.CanWriteToTag();

    /// <summary>Optional 4-byte PWD of the target Ultralight EV1 / NTAG card when its pages are write-protected.</summary>
    public string? WritePassword
    {
        get => _writePassword;
        set
        {
            this.RaiseAndSetIfChanged(ref _writePassword, value);
            this.RaisePropertyChanged(nameof(WritePasswordError));
        }
    }

    /// <summary>Why <see cref="WritePassword"/> can't be used; null when it is empty or valid.</summary>
    public string? WritePasswordError =>
        !IsUltralightWrite || string.IsNullOrWhiteSpace(_writePassword) ? null
        : !Hex.TryParse(_writePassword, out var key) ? Strings.Common_NotHex
        : key.Length == 4 ? null
        : Strings.Common_Password4Bytes;

    /// <summary>Password settings (Ultralight EV1 / NTAG21x) can be put on the target card.</summary>
    public bool CanWriteProtection => IsUltralightWrite && _writeProtection is not null;

    private WriteProtectionViewModel? _writeProtection;

    /// <summary>
    /// Password settings for the target, set up from the card's own each time the writer opens;
    /// null when the card's configuration pages were not read.
    /// </summary>
    public WriteProtectionViewModel? WriteProtection
    {
        get => _writeProtection;
        private set
        {
            this.RaiseAndSetIfChanged(ref _writeProtection, value);
            this.RaisePropertyChanged(nameof(CanWriteProtection));
        }
    }

    private bool _rewriteProtection;

    /// <summary>Write <see cref="WriteProtection"/> onto the target; otherwise it keeps its password settings as they are.</summary>
    public bool RewriteProtection
    {
        get => _rewriteProtection;
        set
        {
            this.RaiseAndSetIfChanged(ref _rewriteProtection, value);
            this.RaisePropertyChanged(nameof(KeepProtection));
        }
    }

    /// <summary>The other segment of the choice: the target keeps its password settings.</summary>
    public bool KeepProtection
    {
        get => !_rewriteProtection;
        set => RewriteProtection = !value;
    }

    private bool _writeToEm4305;

    /// <summary>An EM410x ID at RF/64 can also be cloned onto an EM4205 / EM4305 instead of a T55xx.</summary>
    public bool CanWriteEm4305 => IsLfWrite && ActionTag is TagSpecificType.Em410x or TagSpecificType.Em410x64;

    /// <summary>The LF write targets an EM4205 / EM4305 tag (the alternative to <see cref="WriteToT55xx"/>).</summary>
    public bool WriteToEm4305
    {
        get => _writeToEm4305;
        set
        {
            this.RaiseAndSetIfChanged(ref _writeToEm4305, value);
            RaiseLfChip();
        }
    }

    /// <summary>The other segment of the chip choice: the LF write targets a T55xx tag.</summary>
    public bool WriteToT55xx
    {
        get => !_writeToEm4305;
        set => WriteToEm4305 = !value;
    }

    /// <summary>The LF write actually goes to an EM4305: chosen, and possible for this card.</summary>
    public bool IsEm4305Write => CanWriteEm4305 && _writeToEm4305;

    /// <summary>A T55xx is written, so its new-password option applies.</summary>
    public bool IsT55xxWrite => IsLfWrite && !IsEm4305Write;

    public T55xxPasswordSettingsViewModel WriteT55xxSettings { get; }
    public T55xxKeySelectionViewModel WriteT55xxKeys => WriteT55xxSettings.Keys;
    public bool WriteLfSettingsReady => !IsT55xxWrite ||
        WriteT55xxSettings.ReadyToStart && WriteT55xxSettings.NewKeyError is null;
    public double WriteT55xxProgress { get; private set => this.RaiseAndSetIfChanged(ref field, value); }

    /// <summary>The target tag's UID before the write, as hex.</summary>
    public string? WriteT55xxUid { get; private set => this.RaiseAndSetIfChanged(ref field, value); }

    /// <summary>The target's matching password, as hex (or a note when it had none).</summary>
    public string? WriteT55xxFoundKey { get; private set => this.RaiseAndSetIfChanged(ref field, value); }

    /// <summary>The password the written tag now has, as hex (shown green).</summary>
    public string? WriteT55xxSetKey { get; private set => this.RaiseAndSetIfChanged(ref field, value); }

    /// <summary>The recovered password, as bytes; null when the target had none or nothing was found.</summary>
    private byte[]? _writeT55xxFoundKeyBytes;

    private void ClearWriteT55xxResult()
    {
        WriteT55xxUid = null;
        WriteT55xxFoundKey = null;
        WriteT55xxSetKey = null;
        _writeT55xxFoundKeyBytes = null;
        this.RaisePropertyChanged(nameof(CanSaveWriteT55xxKey));
    }

    private Task _writeTask = Task.CompletedTask;

    public string WriteLfPasswordTip => IsEm4305Write ? Strings.Cards_Em4305PasswordTip : Strings.Cards_Password4BytesOfAnAlready;

    private void RaiseLfChip()
    {
        this.RaisePropertyChanged(nameof(WriteToT55xx));
        this.RaisePropertyChanged(nameof(CanWriteEm4305));
        this.RaisePropertyChanged(nameof(IsEm4305Write));
        this.RaisePropertyChanged(nameof(IsT55xxWrite));
        this.RaisePropertyChanged(nameof(IsT55xxWriting));
        this.RaisePropertyChanged(nameof(WriteLfPasswordTip));
        this.RaisePropertyChanged(nameof(WriteLfPasswordError));
        this.RaisePropertyChanged(nameof(WriteLfSettingsReady));
        this.RaisePropertyChanged(nameof(WriteKind));
        this.RaisePropertyChanged(nameof(WriteInstructions));
    }

    /// <summary>
    /// Optional 4-byte login password of a write-protected EM4305 tag.
    /// </summary>
    public string? WriteLfPassword
    {
        get => _writeLfPassword;
        set
        {
            this.RaiseAndSetIfChanged(ref _writeLfPassword, value);
            this.RaisePropertyChanged(nameof(WriteLfPasswordError));
        }
    }

    /// <summary>Why <see cref="WriteLfPassword"/> can't be used; null when it is empty or valid.</summary>
    public string? WriteLfPasswordError =>
        !IsEm4305Write || string.IsNullOrWhiteSpace(_writeLfPassword) ? null
        : !Hex.TryParse(_writeLfPassword, out var key) ? Strings.Common_NotHex
        : key.Length == 4 ? null
        : Strings.Common_Password4Bytes;

    /// <summary>Which magic generation the MIFARE write targets.</summary>
    public MagicGeneration SelectedGeneration
    {
        get;
        set
        {
            if (field == value) return;
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(WriteInstructions));
            this.RaisePropertyChanged(nameof(WriteWarning));
            this.RaisePropertyChanged(nameof(ShowWriteSectors));
            this.RaisePropertyChanged(nameof(IsGen2Write));
            ResetWriteRecovery();
            RaiseWriteKeyAvailability();
            AppSettings.Set(GenerationSetting, value.ToString());
        }
    } = Enum.TryParse<MagicGeneration>(AppSettings.GetString(GenerationSetting), out var gen) ? gen : MagicGeneration.Gen2;

    public string WriteKind => ActionTag switch
    {
        null => string.Empty,
        { } t when t.IsMifareClassic() => Strings.Cards_WriteToMagic,
        { } t when t.IsUltralight() => Strings.Cards_WriteToUl,
        { } t when t.SenseType() == TagSenseType.Lf && t.CanWriteToTag() =>
            IsEm4305Write ? Strings.Cards_WriteToEm4305 : Strings.Cards_WriteToT55xx,
        _ => Strings.Cards_CannotWriteType,
    };

    public string WriteInstructions => ActionTag switch
    {
        { } t when t.IsMifareClassic() => SelectedGeneration switch
        {
            MagicGeneration.Gen1a =>
                Strings.Cards_WriteHintGen1a,
            MagicGeneration.Gen3 =>
                Strings.Cards_WriteHintGen3,
            _ =>
                WriteGen2Block0
                    ? Strings.Cards_WriteHintGen2
                    : Strings.Cards_WriteHintGen2KeepUid,
        },
        { } t when t.IsUltralight() =>
            Strings.Cards_WriteHintUl,
        { } t when t.SenseType() == TagSenseType.Lf =>
            IsEm4305Write ? Strings.Cards_WriteHintEm4305 : Strings.Cards_WriteHintT55xx,
        _ => string.Empty,
    };

    public string? WriteWarning
    {
        get
        {
            var gen2 = !IsGen2Write ? null
                : WriteGen2Block0
                    ? Strings.Cards_WarnBlock0
                    : Strings.Cards_WarnAccessBits;
            var keys = IsMifareWrite ? _form?.UnknownKeyWarning : null;
            return gen2 is null ? keys : keys is null ? gen2 : $"{gen2}\n{keys}";
        }
    }

    private void RaiseCardInfo()
    {
        RaiseWriteKeyAvailability();
        this.RaisePropertyChanged(nameof(SelectedCardFolder));
        this.RaisePropertyChanged(nameof(CardFolderLabel));
        this.RaisePropertyChanged(nameof(CanExportDump));
        this.RaisePropertyChanged(nameof(CanSaveKeys));
        this.RaisePropertyChanged(nameof(CanWriteSelected));
        this.RaisePropertyChanged(nameof(IsMifareWrite));
        this.RaisePropertyChanged(nameof(IsGen2Write));
        this.RaisePropertyChanged(nameof(HasWriteBlocks));
        this.RaisePropertyChanged(nameof(ShowWriteSectors));
        this.RaisePropertyChanged(nameof(IsUltralightWrite));
        this.RaisePropertyChanged(nameof(IsLfWrite));
        RaiseLfChip();
        this.RaisePropertyChanged(nameof(WriteUnit));
        this.RaisePropertyChanged(nameof(WritePasswordError));
        this.RaisePropertyChanged(nameof(CanWriteProtection));
        this.RaisePropertyChanged(nameof(WriteLfPasswordError));
        this.RaisePropertyChanged(nameof(WriteLfSettingsReady));
        this.RaisePropertyChanged(nameof(WriteKind));
        this.RaisePropertyChanged(nameof(WriteInstructions));
        this.RaisePropertyChanged(nameof(WriteWarning));
    }

    private Task DetectAsync() => RunAsync(async () =>
    {
        var client = _device.Client;
        if (client is null) return;
        var gen = await client.DetectMagicAsync();
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (gen is null)
            {
                ReportError(Strings.Cards_NoCardOnReader);
                return;
            }
            SelectedGeneration = gen.Value;
            Notify(gen.Value switch
            {
                MagicGeneration.Gen1a => Strings.Cards_DetectedGen1a,
                MagicGeneration.Gen3 => Strings.Cards_DetectedGen3,
                _ => Strings.Cards_AssumingGen2,
            });
        });
    });

    private void Duplicate()
    {
        CardSave? card;
        try { card = ActionCard(); }
        catch (FormatException ex) { ReportError(ex.Message); return; }
        if (card is null) return;
        // The copy takes the unsaved edits; the original stays as last saved.
        var copy = card.Clone();
        copy.Name = card.Name + Strings.Common_CopySuffix;
        _library.Add(copy);
        SelectById(copy.Id);
    }

    private void ConfirmDelete()
    {
        if (Selected is not { } s) return;
        SetSelected(null);
        _library.Remove(s.Card);
    }

    private Task ExportAsync() => RunAsync(async () =>
    {
        if (ActionCard() is not { } card) return;
        var name = await StorageService.SaveAsync(Strings.Cards_ExportCardTitle, $"{SafeFileName(card.Name)}.json", CardFormats.ExportJson(card), "json");
        if (name is not null) Notify(string.Format(Strings.Common_SavedFile, name));
    });

    /// <summary>Exports a MIFARE Classic card's memory as a raw .bin or Proxmark .eml dump.</summary>
    private Task ExportDumpAsync(bool bin) => RunAsync(async () =>
    {
        if (!CanExportDump || ActionCard() is not { } card) return;
        var dump = card.ToMifareDump();
        var safe = SafeFileName(card.Name);
        var name = bin
            ? await StorageService.SaveAsync(Strings.Cards_ExportBinTitle, $"{safe}.bin", dump.ToBin(), "bin")
            : await StorageService.SaveAsync(Strings.Cards_ExportEmlTitle, $"{safe}.eml",
                System.Text.Encoding.UTF8.GetBytes(dump.ToEml()), "eml");
        if (name is not null) Notify(string.Format(Strings.Common_SavedFile, name));
    });

    // ---- Save keys -------------------------------------------------------

    /// <summary>The open card's keys (see <see cref="CardEditorViewModel.Keys"/>); empty when none is open.</summary>
    private IReadOnlyList<byte[]> ActionKeys() =>
        _pane is CardsPane.Card or CardsPane.Add && _form is { } form ? form.Keys() : Array.Empty<byte[]>();

    /// <summary>The open card has keys to save.</summary>
    public bool CanSaveKeys => ActionKeys().Count > 0;

    /// <summary>The saved dictionaries, each a choice of where to add the card's keys.</summary>
    public ObservableCollection<KeysTargetViewModel> KeysTargets { get; } = new();

    public bool HasKeysTargets => KeysTargets.Count > 0;

    public ICommand SaveKeysToNewDictionaryCommand { get; }
    public ICommand SaveKeysToFileCommand { get; }

    private static string KeysText(IEnumerable<byte[]> keys) =>
        string.Concat(keys.Select(k => Convert.ToHexString(k) + "\n"));

    /// <summary>The open card's keys, one hex key per line, for the clipboard; null when it has none.</summary>
    public string? CardKeysText() => ActionKeys() is { Count: > 0 } keys ? KeysText(keys) : null;

    /// <summary>Confirms that the view put <paramref name="count"/> keys on the clipboard.</summary>
    public void NotifyKeysCopied(int count) => Notify(string.Format(Strings.Cards_KeysCopied, Loc.Plural(Strings.Plural_Keys, count)));

    private string DictionaryName(KeyDictionary dict) =>
        string.IsNullOrWhiteSpace(dict.Name) ? Strings.Common_Untitled : dict.Name;

    private void RebuildKeysTargets()
    {
        KeysTargets.Clear();
        WriteT55xxKeysTargets.Clear();
        foreach (var d in _dictionaries.Dictionaries)
        {
            var dict = d;
            KeysTargets.Add(new KeysTargetViewModel(new DictionaryItemViewModel(dict),
                ReactiveCommand.Create(() => AddKeysTo(dict, ActionKeys()))));
            WriteT55xxKeysTargets.Add(new KeysTargetViewModel(new DictionaryItemViewModel(dict),
                ReactiveCommand.Create(() => AddKeysTo(dict, WriteT55xxActionKeys()))));
        }
        this.RaisePropertyChanged(nameof(HasKeysTargets));
        this.RaisePropertyChanged(nameof(HasWriteT55xxKeysTargets));
    }

    private void AddKeysTo(KeyDictionary dict, IReadOnlyList<byte[]> keys)
    {
        if (keys.Count == 0) return;
        var have = dict.Keys.Select(Convert.ToHexString).ToHashSet();
        var added = keys.Where(k => have.Add(Convert.ToHexString(k))).ToList();
        if (added.Count == 0)
        {
            Notify(string.Format(Strings.Cards_KeysAlreadyInDictionary, DictionaryName(dict)));
            return;
        }
        try
        {
            dict.Keys.AddRange(added);
            _dictionaries.Update(dict);
        }
        catch (Exception ex) { ReportError(ex.Message); return; }
        Notify(string.Format(Strings.Cards_KeysAddedTo, Loc.Plural(Strings.Plural_Keys, added.Count), DictionaryName(dict)));
    }

    private void SaveKeysToNewDictionary()
    {
        var keys = ActionKeys();
        if (keys.Count == 0 || _form is not { } form) return;
        var name = FreeDictionaryName(string.IsNullOrWhiteSpace(form.Name) ? Strings.Common_Untitled : form.Name.Trim());
        var dict = new KeyDictionary { Name = name, Color = form.Color, Keys = keys.Select(k => k.ToArray()).ToList() };
        try { _dictionaries.Add(dict); }
        catch (Exception ex) { ReportError(ex.Message); return; }
        Notify(string.Format(Strings.Cards_KeysSavedToNewDictionary, Loc.Plural(Strings.Plural_Keys, keys.Count), name));
    }

    /// <summary>The name, or with " (2)", " (3)"… after it when a dictionary already has it.</summary>
    private string FreeDictionaryName(string name)
    {
        var taken = _dictionaries.Dictionaries.Select(d => d.Name.Trim())
            .ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        var free = name;
        for (var n = 2; taken.Contains(free); n++) free = $"{name} ({n})";
        return free;
    }

    private Task SaveKeysToFileAsync() => RunAsync(async () =>
    {
        var keys = ActionKeys();
        if (keys.Count == 0 || _form is not { } form) return;
        var data = System.Text.Encoding.UTF8.GetBytes(KeysText(keys));
        var name = await StorageService.SaveAsync(Strings.Cards_ExportKeysTitle, $"{SafeFileName(form.Name)}.dic", data, "dic");
        if (name is not null) Notify(string.Format(Strings.Common_SavedFile, name));
    });

    // ---- Save the password recovered while writing a T55xx (as Tools / T55xx does) ----

    /// <summary>The saved dictionaries, each a choice of where to add the recovered password.</summary>
    public ObservableCollection<KeysTargetViewModel> WriteT55xxKeysTargets { get; } = new();

    public bool HasWriteT55xxKeysTargets => WriteT55xxKeysTargets.Count > 0;

    /// <summary>A recovered password is available to save.</summary>
    public bool CanSaveWriteT55xxKey => _writeT55xxFoundKeyBytes is not null;

    public ICommand SaveWriteT55xxKeyToNewDictionaryCommand { get; }
    public ICommand SaveWriteT55xxKeyToFileCommand { get; }

    private IReadOnlyList<byte[]> WriteT55xxActionKeys() =>
        _writeT55xxFoundKeyBytes is { } k ? new[] { k } : Array.Empty<byte[]>();

    /// <summary>The recovered password, as a hex line for the clipboard; null when there is none.</summary>
    public string? WriteT55xxFoundKeyText() => WriteT55xxActionKeys() is { Count: > 0 } keys ? KeysText(keys) : null;

    private void SaveWriteT55xxKeyToNewDictionary()
    {
        var keys = WriteT55xxActionKeys();
        if (keys.Count == 0) return;
        var name = FreeDictionaryName(Strings.Tools_T55xxPassword);
        var dict = new KeyDictionary { Name = name, Keys = keys.Select(k => k.ToArray()).ToList() };
        try { _dictionaries.Add(dict); }
        catch (Exception ex) { ReportError(ex.Message); return; }
        Notify(string.Format(Strings.Cards_KeysSavedToNewDictionary, Loc.Plural(Strings.Plural_Keys, keys.Count), name));
    }

    private Task SaveWriteT55xxKeyToFileAsync() => RunAsync(async () =>
    {
        var keys = WriteT55xxActionKeys();
        if (keys.Count == 0) return;
        var data = System.Text.Encoding.UTF8.GetBytes(KeysText(keys));
        var name = await StorageService.SaveAsync(Strings.Cards_ExportKeysTitle, "t55xx-key.dic", data, "dic");
        if (name is not null) Notify(string.Format(Strings.Common_SavedFile, name));
    });

    private static string SafeFileName(string name) =>
        string.Concat(name.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private Task RefreshSlotsAsync() => RunAsync(async () =>
    {
        var client = _device.Client;
        if (client is null) return;
        var slots = await client.GetAllSlotsAsync();
        Avalonia.Threading.Dispatcher.UIThread.Post(() => { _slotInfos = slots; RebuildTargets(); });
    });

    private void RebuildTargets()
    {
        RaiseMfkey32Availability();
        SlotTargets.Clear();
        if (ActionTag is not { } tag) return;
        var sense = tag.SenseType() == TagSenseType.Lf ? TagSenseType.Lf : TagSenseType.Hf;
        foreach (var info in _slotInfos) SlotTargets.Add(new SlotTargetViewModel(info, sense));
    }

    private Task LoadToSlotAsync(SlotTargetViewModel target) => RunAsync(async () =>
    {
        var client = _device.Client;
        if (client is null || ActionCard() is not { } card) return;

        Action = CardAction.None;

        var progress = new Progress<MifareProgress>(p =>
            ProgressText = string.Format(Strings.Common_UploadingProgress, p.Done, p.Total));
        try
        {
            await client.LoadCardToSlotAsync(card, target.Index, progress);
        }
        finally
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => ProgressText = null);
        }

        var slots = await client.GetAllSlotsAsync();
        Avalonia.Threading.Dispatcher.UIThread.Post(() => { _slotInfos = slots; RebuildTargets(); });
        Notify(string.Format(Strings.Cards_NowEmulated, card.Name, target.Index));
    });

    private Task WriteAsync() => _writeTask = RunAsync(async () =>
    {
        var client = _device.Client;
        if (client is null || ActionCard() is not { } card) return;
        if (!HasRequiredWriteKeys)
        {
            ReportError(Strings.Cards_RecoverGen2KeysFirst);
            return;
        }

        var unit = card.Tag.IsUltralight() ? "page" : "block";
        var version = _writeVersion;
        var resume = _writeSession is not null;
        if (!resume) _writeTargetHadDefaultKeys = _assumeDefaultWriteKeys || HasOnlyDefaultKeys(WriteRecovery.Target);
        var session = card.Tag.IsMifareClassic()
            ? _writeSession ??= new MifareWriteSession(card.ToMifareDump(), SelectedGeneration, WriteGen2Block0, WriteRecovery.Target)
            : null;
        var succeeded = false;
        using var cts = new CancellationTokenSource();
        _writeCts = cts;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long lastProgress = -200;
        var progress = new PostedProgress<MifareProgress>(p =>
        {
            if (version != _writeVersion || cts.IsCancellationRequested) return;
            // Counts change at most ~7 times a second; completion is always delivered.
            if (clock.ElapsedMilliseconds - lastProgress < 150 && p.Done != p.Total) return;
            lastProgress = clock.ElapsedMilliseconds;
            WriteProgressText = $"{WriteUnit} {p.Done}/{p.Total}";
            if (session is not null) WriteRecovery.ReportWriteCount(p);
        });
        var blockProgress = new PostedProgress<MifareBlockProgress>(p =>
        {
            if (version == _writeVersion) OnWriteBlocks(p);
        });
        var keyProgress = new PostedProgress<MifareKeyProgress>(p =>
        {
            if (version == _writeVersion) WriteRecovery.ApplyKey(p);
        });
        var writeKeys = _assumeDefaultWriteKeys
            ? [MifareClassic.DefaultKeys[0], .. WriteRecovery.BuildKeyDictionary()]
            : WriteRecovery.BuildKeyDictionary();
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (version != _writeVersion) return;
            if (!resume) WriteBlockStates = Array.Empty<MifareBlockState>();
            if (session is not null)
                WriteRecovery.PrepareWrite(card.Tag, session.Source, IncludeWriteBlock0, resume);
            if (IsT55xxWrite)
            {
                ClearWriteT55xxResult();
                WriteT55xxProgress = 0;
            }
            WriteProgressText = resume ? Strings.Common_CheckingTargetUid : Strings.Common_Starting;
            this.RaisePropertyChanged(nameof(CanChangeWriteOptions));
        });
        try
        {
            if (card.Tag.SenseType() == TagSenseType.Lf)
            {
                var hasCurrent = Hex.TryParse(WriteLfPassword, out var lfKey) && lfKey.Length == 4;
                if (IsEm4305Write)
                {
                    await client.WriteEm410xToEm4x05Async(card.Uid, hasCurrent ? lfKey : null, cts.Token);
                    succeeded = true;
                    Notify(string.Format(Strings.Cards_WroteEm4305, card.Name));
                    return;
                }
                var oldKeys = WriteT55xxSettings.BuildOldKeys();
                var newKey = WriteT55xxSettings.BuildNewKey();
                var result = await T55xxPasswordRecovery.WriteAsync(client, card.Tag, card.Uid, oldKeys, newKey,
                    (done, total) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        if (version != _writeVersion) return;
                        WriteT55xxProgress = total == 0 ? 0 : 100.0 * done / total;
                        if (!cts.IsCancellationRequested)
                            WriteProgressText = string.Format(Strings.Tools_T55xxProgress, done, total);
                    }), original => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        if (version == _writeVersion) WriteT55xxUid = Hex.Spaced(original.Id);
                    }), key => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        if (version != _writeVersion) return;
                        _writeT55xxFoundKeyBytes = key;
                        WriteT55xxFoundKey = key is null ? Strings.Tools_T55xxUnprotected : Hex.Spaced(key);
                        this.RaisePropertyChanged(nameof(CanSaveWriteT55xxKey));
                    }), original => _dictionaries.KnownKeys.T55xxPasswordForId(original.Id), cts.Token);
                if (result is null) throw new InvalidOperationException(Strings.Tools_T55xxNoMatch);
                RememberT55xxPassword(result);
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (version != _writeVersion) return;
                    WriteT55xxProgress = 100;
                    WriteT55xxSetKey = Hex.Spaced(newKey);
                });
                succeeded = true;
                Notify(string.Format(Strings.Cards_WroteT55xx, card.Name));
                return;
            }

            IReadOnlyList<int> failed;
            if (card.Tag.IsUltralight())
            {
                var pwd = Hex.TryParse(WritePassword, out var k) && k.Length > 0 ? k : null;
                var protection = RewriteProtection ? WriteProtection?.Settings : null;
                failed = await client.WriteUltralightUserPagesAsync(card, pwd, progress, cts.Token, blockProgress, protection,
                    _dictionaries.KnownKeys.PasswordForUid, (uid, pwd) => RememberCardPassword(uid, pwd, card.Tag));
            }
            else if (session is not null)
            {
                failed = await client.WriteMifareDumpToCardAsync(card.ToMifareDump(), writeKeys,
                    SelectedGeneration, progress, cts.Token, blockProgress, keyProgress,
                    session.TargetKeys, WriteGen2Block0, session);
            }
            else return;

            succeeded = failed.Count == 0;
            if (succeeded) Notify(string.Format(Strings.Cards_WroteCard, card.Name));
            else ReportError(Loc.Plural(card.Tag.IsUltralight() ? Strings.Plural_WriteFailedPages : Strings.Plural_WriteFailedBlocks,
                failed.Count, card.Name, string.Join(", ", failed)));
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Notify(IsT55xxWrite ? Strings.Tools_T55xxCancelled : session is not null
                ? Strings.Cards_WriteStoppedResume
                : card.Tag.IsUltralight() ? Strings.Cards_WriteStoppedPages : Strings.Cards_WriteStoppedBlocks);
        }
        finally
        {
            if (ReferenceEquals(_writeCts, cts)) _writeCts = null;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (version != _writeVersion) return;
                WriteBlockStates = WithBlocks(_writeBlockStates, 0, _writeBlockStates.Count,
                    MifareBlockState.Pending, onlyFrom: MifareBlockState.Reading);
                WriteProgressText = null;
                if (session is not null) WriteRecovery.FinishWrite(session);
                IsWriteSuccessful = succeeded;
            });
        }
    });

    /// <summary>
    /// Reports on the UI thread in the order reported: unlike <see cref="Progress{T}"/>, whose ticks can
    /// overtake an earlier <c>Dispatcher.UIThread.Post</c> (or each other when made off the UI thread).
    /// </summary>
    private sealed class PostedProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => Avalonia.Threading.Dispatcher.UIThread.Post(() => report(value));
    }
}
