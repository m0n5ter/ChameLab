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
        CardSort.Name => "Name A–Z",
        CardSort.NameDesc => "Name Z–A",
        CardSort.Type => "Type ↑",
        CardSort.TypeDesc => "Type ↓",
        _ => "Manual",
    };
}

/// <summary>Frequency filter of the card list.</summary>
public enum CardBand { All, Hf, Lf }

/// <summary>An entry of the card type filter; no tag means any type.</summary>
public sealed record CardTypeOption(TagSpecificType? Tag)
{
    public static readonly CardTypeOption Any = new((TagSpecificType?)null);
    public string Label => Tag?.DisplayName() ?? "All types";
}

/// <summary>Which action panel is open under a saved card.</summary>
public enum CardAction { None, Slots, Write, Delete }

/// <summary>
/// Card library in one place: the list of saved cards, adding a card (scan
/// HF/LF with a full MIFARE Classic or Ultralight / NTAG dump, import from a file, or manual entry)
/// and an editor for a saved card with duplicate / export / emulate-in-slot /
/// write-to-tag / delete actions.
/// </summary>
public sealed class CardsViewModel : PageViewModelBase, IReorderableList, IDropOutList
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
    private string? _progressText;
    private bool _isConnected;
    private IReadOnlyList<SlotInfo> _slotInfos = Array.Empty<SlotInfo>();

    // Add-a-card state
    private string? _scanMissText;
    private string? _prng;
    private MifareDump? _classicDump;
    private MifareClassicPrngType? _cardPrng;
    private bool _darksideAvailable;

    public bool ShowDarkside => _classicDump is not null && _darksideAvailable && HasUnknownKeys;
    public bool ShowNested => _cardPrng == MifareClassicPrngType.Weak && HasKnownKeys && HasUnknownKeys;
    public bool ShowStaticNested => _cardPrng == MifareClassicPrngType.Static && HasKnownKeys && HasUnknownKeys;
    private bool HasKnownKeys => DumpSectors.Any(s => s.KeyAState == KeyCellState.Found || s.KeyBState == KeyCellState.Found);
    private bool HasUnknownKeys => DumpSectors.Any(s => s.KeyAState != KeyCellState.Found || s.KeyBState != KeyCellState.Found);

    private void UpdateRecoveryMethods()
    {
        this.RaisePropertyChanged(nameof(ShowDarkside));
        this.RaisePropertyChanged(nameof(ShowNested));
        this.RaisePropertyChanged(nameof(ShowStaticNested));
    }
    private string? _formSource;
    private TagSpecificType _lfProtocol = TagSpecificType.Em410x;
    private bool _canDump;
    private bool _isUltralightDump;
    private string? _dumpPassword;
    private string? _writePassword;
    private string? _writeLfPassword;
    private string? _writeLfNewKey;
    private bool _hasDump;
    private bool _isDumpComplete;
    private string? _dumpProgressText;
    private CancellationTokenSource? _dumpCts;
    private int _dumpKeyCount;
    private bool _hasDumpStats;
    private IReadOnlyList<MifareBlockState> _dumpBlockStates = Array.Empty<MifareBlockState>();
    private string? _writeProgressText;
    private CancellationTokenSource? _writeCts;
    private IReadOnlyList<MifareBlockState> _writeBlockStates = Array.Empty<MifareBlockState>();
    private readonly HashSet<string> _uncheckedKeySources = LoadUncheckedKeySources();
    private byte[] _scannedAts = Array.Empty<byte>();
    // The new card came from an HF scan, so its memory can be dumped (as whatever type it is set to).
    private bool _isHfScan;
    // A dump result is being put into the form: its type change is not a user's change.
    private bool _applyingDump;
    // A dump stopped because its result no longer applies (type changed, add left): its result is dropped.
    private CancellationTokenSource? _discardedDump;

    // Page margins 56 + card list 280 + gap 18 + card editor 600 (see CardsView).
    public override double MinContentWidth => 954;

    public CardsViewModel(DeviceService device, CardLibrary library, DictionaryLibrary dictionaries)
        : base("Cards", "cards")
    {
        _device = device;
        _library = library;
        _dictionaries = dictionaries;
        _isConnected = device.State == ConnectionState.Connected;

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
                x => x.WriteLfNewKeyError,
                (online, ulError, lfError, lfNewError) => online && ulError is null && lfError is null && lfNewError is null));
        DetectCommand = ReactiveCommand.CreateFromTask(DetectAsync, connected);
        ResetFiltersCommand = ReactiveCommand.Create(ResetFilters, this.WhenAnyValue(x => x.HasFilters));
        NewFolderCommand = ReactiveCommand.Create(NewFolder);
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
            if (!IsConnected)
            {
                _slotInfos = Array.Empty<SlotInfo>();
                RebuildTargets();
                if (_slotUpdate is not null) ResolveSlotUpdate(update: false);
                if (_action is CardAction.Slots or CardAction.Write) Action = CardAction.None;
            }
        });

        _dictionaries.Changed += () =>
        {
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) RebuildKeySources();
            else Avalonia.Threading.Dispatcher.UIThread.Post(RebuildKeySources);
        };

        Rebuild();
        RebuildKeySources();
        if (_library.LoadError is { } err) SetStatus(err, isError: true);
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
            return count == 1 ? "1 card" : $"{count} cards";
        }
    }

    /// <summary>The card belongs where the list is: the folder shown, or no folder for Cards.</summary>
    private bool InView(CardSave card) => card.Folder == (_folder ?? string.Empty);

    /// <summary>The page title: the open folder, or Cards for the cards in no folder.</summary>
    public string ListTitle => _folder ?? "Cards";
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

    private System.Action? _pendingLeave;

    /// <summary>The open card (saved and edited, or new) would be lost by navigating away.</summary>
    private bool HasUnsavedWork => _pane switch
    {
        CardsPane.Card => _form?.IsDirty == true,
        CardsPane.Add => _form is not null,
        _ => false,
    };

    /// <summary>Runs <paramref name="next"/> now, or holds it until the user saves or discards.</summary>
    private void LeaveThen(System.Action next)
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
        ? "Save the new card before leaving?"
        : $"Save changes to “{_selected?.Name}”?";

    public ICommand SaveAndLeaveCommand { get; }
    public ICommand DiscardAndLeaveCommand { get; }
    public ICommand StayCommand { get; }

    public CardEditorViewModel? Form
    {
        get => _form;
        private set
        {
            if (_form is not null) _form.PropertyChanged -= OnFormPropertyChanged;
            this.RaiseAndSetIfChanged(ref _form, value);
            if (_form is not null) _form.PropertyChanged += OnFormPropertyChanged;
            this.RaisePropertyChanged(nameof(IsCardDirty));
            this.RaisePropertyChanged(nameof(ShowEditor));
            this.RaisePropertyChanged(nameof(ShowMemoryEditor));
            RaiseAddStage();
            RaiseCardInfo();
            RebuildTargets();
        }
    }

    private void OnFormPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CardEditorViewModel.CanCreateMemory))
            this.RaisePropertyChanged(nameof(ShowMemoryEditor));
        // The card actions follow its type and memory as they are edited.
        if (e.PropertyName is nameof(CardEditorViewModel.Tag) or nameof(CardEditorViewModel.HasData))
        {
            RaiseCardInfo();
            if (e.PropertyName == nameof(CardEditorViewModel.Tag))
            {
                RebuildTargets();
                OnAddTagChanged();
            }
        }
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
    /// Adding, and the new card is open in the editor. The list is locked until
    /// the card is saved or the add is canceled.
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
        private set => this.RaiseAndSetIfChanged(ref _isConnected, value);
    }

    public string? ProgressText
    {
        get => _progressText;
        private set => this.RaiseAndSetIfChanged(ref _progressText, value);
    }

    /// <summary>Short progress line shown under the Dump button while a dump runs; null otherwise.</summary>
    public string? DumpProgressText
    {
        get => _dumpProgressText;
        private set
        {
            this.RaiseAndSetIfChanged(ref _dumpProgressText, value);
            if (value is null) SetDumpKeyProgress(0, 0);
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

    public string DumpKeyProgressTip => $"{_dumpKeyTried} of {_dumpKeyTotal} keys tried";

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
        if (p.State == MifareBlockState.Pending && p.First == 0 && DumpSectors.Count == 0) ResetDumpSectors(p.Count);
    }

    private void OnDumpKey(MifareKeyProgress p)
    {
        if (p.Sector < 0 || p.Sector >= DumpSectors.Count) return;
        DumpSectors[p.Sector].ApplyKey(p);
        UpdateRecoveryMethods();
        // The text names the sector and key (numbered like the grid's tooltip); the bar shows the search.
        DumpProgressText = $"Sector {p.Sector} · key {(p.KeyType == MfKeyType.A ? "A" : "B")}";
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
        if (p.State == MifareBlockState.Pending && p.First == 0)
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
                CardSort.Name => items.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase),
                CardSort.NameDesc => items.OrderByDescending(c => c.Name, StringComparer.CurrentCultureIgnoreCase),
                // Types in their natural order (LF first, sizes ascending) or reversed; cards
                // without one last either way, names A–Z within a type.
                CardSort.Type => items
                    .OrderBy(c => c.Card.Tag == TagSpecificType.Undefined)
                    .ThenBy(c => c.Card.Tag)
                    .ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase),
                CardSort.TypeDesc => items
                    .OrderBy(c => c.Card.Tag == TagSpecificType.Undefined)
                    .ThenByDescending(c => c.Card.Tag)
                    .ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase),
                _ => items,
            };
            Cards.Clear();
            foreach (var card in items)
                Cards.Add(card);
        }
        finally { _rebuilding = false; }
        this.RaisePropertyChanged(nameof(TypeFilter));

        var reselect = selectedId is null ? null : Cards.FirstOrDefault(c => c.Card.Id == selectedId);
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
            SetStatus(value.Name.Length == 0 ? $"Moved “{s.Name}” out of its folder." : $"Moved “{s.Name}” to {value.Name}.");
        }
    }

    /// <summary>Label of the open card's folder button.</summary>
    public string CardFolderLabel => _selected is { Card.Folder: { Length: > 0 } folder } ? folder : "Folder";

    /// <summary>Shows the cards of a folder, or all of them for null.</summary>
    public void ShowFolder(CardFolderViewModel? folder) => LeaveThen(() =>
    {
        SetFolder(folder?.Name);
        Rebuild();
    });

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

    private void NewFolder()
    {
        var name = "New folder";
        for (var n = 2; _library.FindFolder(name) is not null; n++) name = $"New folder {n}";
        if (!_library.AddFolder(name)) return;
        if (Folders.FirstOrDefault(f => f.Name == name) is { } folder) BeginRename(folder);
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
            SetStatus($"There is already a folder named “{taken}”.", isError: true);
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

    private bool _isRenamingFolder;
    private string? _folderRenameText;

    /// <summary>The open folder's title is a field.</summary>
    public bool IsRenamingFolder
    {
        get => _isRenamingFolder;
        private set => this.RaiseAndSetIfChanged(ref _isRenamingFolder, value);
    }

    public string? FolderRenameText
    {
        get => _folderRenameText;
        set => this.RaiseAndSetIfChanged(ref _folderRenameText, value);
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
        SetStatus(count switch
        {
            0 => $"Deleted the folder {folder.Name}.",
            1 => $"Deleted the folder {folder.Name}; its card is still in Cards.",
            _ => $"Deleted the folder {folder.Name}; its {count} cards are still in Cards.",
        });
    }

    /// <summary>
    /// Moves a card to a folder (empty for none). Taking the open card out of the folder shown
    /// closes it, so unsaved edits are asked about first.
    /// </summary>
    private void MoveCard(CardSave card, string folder)
    {
        var name = card.Name.Length == 0 ? "No name" : card.Name;
        void Move()
        {
            _library.MoveToFolder(card, folder);
            SetStatus(folder.Length == 0 ? $"Moved “{name}” out of its folder." : $"Moved “{name}” to {folder}.");
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
    public ICommand DarksideCommand { get; }
    public ICommand NestedCommand { get; }
    public ICommand StaticNestedCommand { get; }
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
        get => _lfProtocol;
        set => this.RaiseAndSetIfChanged(ref _lfProtocol, value);
    }

    /// <summary>Why the last scan produced no card (“No HF tag detected.”); null otherwise.</summary>
    public string? ScanMissText
    {
        get => _scanMissText;
        private set => this.RaiseAndSetIfChanged(ref _scanMissText, value);
    }

    /// <summary>
    /// The scanned MIFARE Classic card's PRNG (“Weak (predictable)”…), measured live and not
    /// stored with the card; shown in the dump section. Null when unknown.
    /// </summary>
    public string? Prng
    {
        get => _prng;
        private set => this.RaiseAndSetIfChanged(ref _prng, value);
    }

    /// <summary>Where the card being added came from (“MIFARE Classic 1K — UID …”, “Imported from x.nfc”, “Manual entry”).</summary>
    public string? FormSource
    {
        get => _formSource;
        private set => this.RaiseAndSetIfChanged(ref _formSource, value);
    }

    /// <summary>The scanned tag is MIFARE Classic or Ultralight / NTAG, so its memory can be dumped.</summary>
    public bool CanDump
    {
        get => _canDump;
        private set { this.RaiseAndSetIfChanged(ref _canDump, value); RaiseSaveLabel(); }
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
            this.RaisePropertyChanged(nameof(DumpLockedText));
        }
    }

    public string DumpHint => _isUltralightDump
        ? "Keep the card on the reader and read every page, the signature and the counters. Protected pages need the card's password."
        : "Keep the card on the reader and read every block with the selected key dictionaries.";

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
        : !Hex.TryParse(_dumpPassword, out var key) ? "Not a hex value."
        : key.Length is 4 or 16 ? null
        : "4 bytes for NTAG / Ultralight EV1, 16 bytes for Ultralight C.";

    /// <summary>What one cell of the block strip is: a MIFARE Classic block or an Ultralight page.</summary>
    public string DumpUnit => _isUltralightDump ? "Page" : "Block";

    /// <summary>Legend of the strip's red cells.</summary>
    public string DumpLockedText => _isUltralightDump ? "Protected" : "No key";

    /// <summary>A MIFARE Classic or Ultralight card was scanned but its memory has not been dumped yet.</summary>
    public bool NeedsDump => _canDump && !_hasDump;

    /// <summary>Every memory block or page was read; partial dumps can still be continued.</summary>
    public bool IsDumpComplete
    {
        get => _isDumpComplete;
        private set => this.RaiseAndSetIfChanged(ref _isDumpComplete, value);
    }

    /// <summary>Makes it explicit that saving before a dump keeps only the card's identity.</summary>
    public string AddSaveLabel => NeedsDump ? "Save UID only" : "Save card";

    private void RaiseSaveLabel()
    {
        this.RaisePropertyChanged(nameof(NeedsDump));
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
        ResetAddState();
        Form = null;
    }

    private void ResetAddState()
    {
        ScanMissText = null;
        Prng = null;
        FormSource = null;
        _hasDump = false;
        _isHfScan = false;
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
        if (_dumpCts is { } cts)
        {
            _discardedDump = cts;
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }
        _classicDump = null;
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

    /// <summary>
    /// The new card's type was changed by hand. A dump read as the old type means nothing for the
    /// new one (1K and NTAG, even 1K and 4K, lay memory out differently), so it is dropped — grid,
    /// summary and the memory it filled in — and the dump section is set up afresh for the new type.
    /// </summary>
    private void OnAddTagChanged()
    {
        if (_pane != CardsPane.Add || !_isHfScan || _applyingDump || _form is not { } form) return;
        ClearDump();
        if (_hasDump)
        {
            form.ClearMemory();
            _hasDump = false;
        }
        ShowDumpFor(form.Tag);
    }

    /// <summary>
    /// Sets the dump section up for <paramref name="tag"/>: which dump it runs (Classic blocks or
    /// Ultralight pages), and for MIFARE Classic an empty sector grid sized for that type.
    /// </summary>
    private void ShowDumpFor(TagSpecificType tag)
    {
        IsUltralightDump = tag.IsUltralight();
        CanDump = tag.IsMifareClassic() || IsUltralightDump;
        if (tag.IsMifareClassic())
        {
            ResetDumpSectors(tag.MifareClassicBlocks());
            if (Form is { } form)
            {
                var card = form.Build();
                _classicDump = new MifareDump(MifareClassic.FromSlotTagType(tag), card.Uid, card.Atqa, card.Sak);
            }
        }
        RebuildKeySources();
        UpdateRecoveryMethods();
        RaiseSaveLabel();
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
        ? "A saved card has the same memory as this dump. Open it, or keep going to save the dump again."
        : "This tag is already in your library. Open the saved card, or keep going to save it again.";

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
        // Starts as a MIFARE Classic 1K with its usual SAK / ATQA; changing the type adapts them.
        var card = new CardSave { Name = "New card", Tag = TagSpecificType.Mifare1024 };
        if (card.Tag.DefaultSakAtqa(sevenByteUid: false) is { } id) { card.Sak = id.Sak; card.Atqa = id.Atqa; }
        Form = new CardEditorViewModel(card);
        FormSource = "Manual entry";
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
                ScanMissText = "No HF (13.56 MHz) tag detected.";
                Form = null;
                return;
            }
            var t = tags[0];
            var card = CardFormats.FromHf14a(t);
            if (ultralight is { } exact) card.Tag = exact;
            _scannedAts = card.Ats;
            _isHfScan = true;
            Form = new CardEditorViewModel(card, sameFrequency: true);
            SetScanMatches(card);
            ShowDumpFor(card.Tag);
            FormSource = $"{card.Tag.DisplayName()} — UID {t.UidHex}";
        });

        var scannedDump = _classicDump;

        // Probe MIFARE Classic specifics (support + PRNG) without failing the scan.
        if (tags.Count > 0 && !CardFormats.GuessHfType(tags[0].Sak, tags[0].Atqa).IsUltralight())
        {
            string? prng = null;
            MifareClassicPrngType? cardPrng = null;
            var darkside = false;
            try
            {
                if (await client.Mf1IsSupportedAsync())
                {
                    var p = await client.DetectNtLevelAsync();
                    cardPrng = p;
                    prng = p.Label();
                    if (p != MifareClassicPrngType.Static)
                    {
                        try { darkside = await client.CheckDarksideAsync() == DarksideResult.Vulnerable; }
                        catch (ChameleonException) { }
                    }
                }
            }
            catch { /* not a Classic card, or older firmware */ }

            if (prng is not null)
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    // Lands on the editor this scan opened; another scan or a cancel since then drops it.
                    if (scannedDump is null || !ReferenceEquals(scannedDump, _classicDump)) return;
                    Prng = prng;
                    _cardPrng = cardPrng;
                    _darksideAvailable = darkside;
                    UpdateRecoveryMethods();
                });
        }

        SetStatus($"Found {tags.Count} HF tag(s).");
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
                FormSource = $"{tag.Type.DisplayName()} tag";
                var card = CardFormats.FromEm410x(tag);
                Form = new CardEditorViewModel(card, sameFrequency: true);
                SetScanMatches(card);
            });
            SetStatus("EM410x tag read.");
            return;
        }

        var result = await WithMissFeedback(client.LfScanAsync(LfProtocol));
        ScanFeedback.Found();
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            ResetAddState();
            FormSource = $"{result.Type.DisplayName()} tag";
            var card = new CardSave
            {
                Name = $"{result.Type.DisplayName()} {result.IdHex}",
                Tag = result.Type,
                Uid = result.Id,
            };
            Form = new CardEditorViewModel(card, sameFrequency: true);
            SetScanMatches(card);
        });
        SetStatus($"{LfProtocol.DisplayName()} tag read.");
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

        using var cts = new CancellationTokenSource();
        _dumpCts = cts;
        var keys = BuildKeyDictionary();
        await MifareDumpBodyAsync(client, keys, DumpFormType(), cts);
    });

    private Task RecoverKeysAsync(MifareRecoveryMethod method) => RunAsync(async () =>
    {
        var client = _device.Client;
        if (client is null || _classicDump is not { } dump) return;
        using var cts = new CancellationTokenSource();
        _dumpCts = cts;
        DumpProgressText = "Starting recovery…";
        try
        {
            var recovery = new MifareKeyRecovery(client);
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
        Protocol.ChameleonClient client, IReadOnlyList<byte[]> keys, MifareClassicType? type, CancellationTokenSource cts, bool read = true)
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
            DumpProgressText = "Starting…";
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
            SetStatus(stopped
                ? $"Dump stopped — kept {dump.RecoveredBlocks}/{dump.BlockCount} blocks from {dump.UidHex}."
                : $"Read {dump.RecoveredBlocks}/{dump.BlockCount} blocks from {dump.UidHex}.");
        }
        finally
        {
            if (learnedDump is not null) RememberCardKeys(learnedDump);
            if (ReferenceEquals(_dumpCts, cts)) _dumpCts = null;
            Avalonia.Threading.Dispatcher.UIThread.Post(IfKept(cts, FinishClassicOperation));
        }
    }

    private Task DumpUltralightAsync() => RunAsync(async () =>
    {
        var client = _device.Client;
        if (client is null) return;

        using var cts = new CancellationTokenSource();
        _dumpCts = cts;
        var progress = new Progress<MifareProgress>(IfKept<MifareProgress>(cts, p =>
            DumpProgressText = $"Page {p.Done}/{p.Total}"));
        var pageProgress = new Progress<MifareBlockProgress>(IfKept<MifareBlockProgress>(cts, OnDumpBlocks));
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            DumpStats.Clear();
            HasDumpStats = false;
            DumpProgressText = "Starting…";
        });
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var key = Hex.TryParse(DumpPassword, out var k) && k.Length > 0 ? k : null;
            var result = await WithMissFeedback(client.DumpUltralightAsync(key, progress, cts.Token, pageProgress));
            clock.Stop();
            ScanFeedback.Found();
            var stats = BuildUltralightStats(result, clock.Elapsed);
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(_discardedDump, cts)) return;
                DumpStats.Clear();
                foreach (var row in stats) DumpStats.Add(row);
                HasDumpStats = true;
                _applyingDump = true;
                if (Form is { } form) form.ReplaceContent(result.Card);
                else Form = new CardEditorViewModel(result.Card, sameFrequency: true);
                _applyingDump = false;
                FormSource = $"{result.Card.Tag.DisplayName()} — UID {Hex.Compact(result.Card.Uid)}";
                IsDumpComplete = result.PagesRead == result.PageCount;
                SetScanMatches(result.Card);
                _hasDump = true;
                RaiseSaveLabel();
            });
            SetStatus($"Read {result.PagesRead}/{result.PageCount} pages from {Hex.Compact(result.Card.Uid)}.");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            SetStatus("Dump canceled.");
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
            new("Model", r.Card.Tag.DisplayName()),
            new("Pages read", $"{r.PagesRead} / {r.PageCount} ({pct:0}%)"),
        };
        if (r.KeyAccepted is { } accepted)
            rows.Add(new(r.Card.Tag == TagSpecificType.Mf0Icu2 ? "3DES key" : "Password", accepted
                ? r.Pack is { } pack ? $"accepted · PACK {Hex.Spaced(pack)}" : "accepted"
                : "not accepted — protected pages were left unread"));
        if (r.LockedFrom is { } locked)
            rows.Add(new("Protected", locked == r.PageCount - 1 ? $"page {locked}" : $"pages {locked}–{r.PageCount - 1}"));
        if (extra.UltralightSignature.Length > 0) rows.Add(new("Signature", Hex.Compact(extra.UltralightSignature)));
        if (extra.UltralightCounters.Count > 0)
            rows.Add(new(extra.UltralightCounters.Count == 1 ? "Counter" : "Counters", string.Join(", ", extra.UltralightCounters)));
        rows.Add(new("Time", $"{elapsed.TotalSeconds:0.0} s"));
        return rows;
    }

    /// <summary>Stops a running dump; whatever was read so far is kept and can be saved.</summary>
    private void CancelDump()
    {
        if (_dumpCts is not { } cts) return;
        try { cts.Cancel(); } catch (ObjectDisposedException) { /* already finished */ }
        DumpProgressText = "Stopping…";
    }

    // ---- Dump statistics -------------------------------------------------

    /// <summary>Summary of the last dump; per-sector keys are already on the sector grid.</summary>
    public ObservableCollection<ScanResultRow> DumpStats { get; } = new();

    public bool HasDumpStats
    {
        get => _hasDumpStats;
        private set => this.RaiseAndSetIfChanged(ref _hasDumpStats, value);
    }

    private static List<ScanResultRow> BuildDumpStats(MifareDump dump, TimeSpan elapsed)
    {
        // A sector counts once every one of its blocks was read, as in the memory summary.
        var read = Enumerable.Range(0, dump.SectorCount).Count(s =>
            Enumerable.Range(MifareClassic.SectorFirstBlock(s), MifareClassic.BlocksInSector(s))
                .All(b => dump.GetBlock(b) is not null));
        return new List<ScanResultRow>
        {
            new("Sectors read", $"{read} / {dump.SectorCount}"),
            new("Time", $"{elapsed.TotalSeconds:0.0} s"),
        };
    }

    // ---- Dump key sources ------------------------------------------------

    /// <summary>Built-in keys plus every saved dictionary, each with a check box for the next dump.</summary>
    public ObservableCollection<DumpKeySourceViewModel> DumpKeySources { get; } = new();

    private void RememberCardKeys(MifareDump dump)
    {
        try { _dictionaries.KnownKeys.Remember(dump.Uid, dump.KeyA.Concat(dump.KeyB).OfType<byte[]>()); }
        catch (Exception ex) { SetStatus($"Could not save known card keys: {ex.Message}", isError: true); }
    }

    /// <summary>Unique keys across the checked sources: what a dump will try per sector and key type.</summary>
    public int DumpKeyCount
    {
        get => _dumpKeyCount;
        private set
        {
            this.RaiseAndSetIfChanged(ref _dumpKeyCount, value);
            this.RaisePropertyChanged(nameof(DumpKeyCountText));
        }
    }

    public string DumpKeyCountText => DumpKeyCount switch
    {
        0 => "No keys selected",
        1 => "1 unique key",
        var n => $"{n} unique keys",
    };

    /// <summary>Rebuilds the source list from the dictionary library, keeping earlier unchecks.</summary>
    private void RebuildKeySources()
    {
        DumpKeySources.Clear();
        DumpKeySources.Add(NewKeySource(KnownCardKeys.Id, KnownCardKeys.Name,
            _classicDump is { } dump ? _dictionaries.KnownKeys.ForUid(dump.Uid) : Array.Empty<byte[]>()));
        DumpKeySources.Add(NewKeySource(DumpKeySourceViewModel.BuiltInId, "Built-in keys", MifareClassic.DefaultKeys));
        foreach (var d in _dictionaries.Dictionaries)
            DumpKeySources.Add(NewKeySource(d.Id, string.IsNullOrWhiteSpace(d.Name) ? "Untitled" : d.Name, d.Keys));
        UpdateDumpKeyCount();
    }

    private DumpKeySourceViewModel NewKeySource(string id, string name, IReadOnlyList<byte[]> keys)
    {
        var source = new DumpKeySourceViewModel(id, name, keys, !_uncheckedKeySources.Contains(id));
        source.WhenAnyValue(x => x.IsSelected).Skip(1).Subscribe(on =>
        {
            if (on) _uncheckedKeySources.Remove(id); else _uncheckedKeySources.Add(id);
            SaveUncheckedKeySources();
            UpdateDumpKeyCount();
        });
        return source;
    }

    // Unchecked rather than checked sources are kept, so a dictionary added later starts checked.
    private const string UncheckedKeySourcesSetting = "dumpUncheckedDictionaries";

    private static HashSet<string> LoadUncheckedKeySources() =>
        AppSettings.Get(UncheckedKeySourcesSetting) is JsonArray ids
            ? ids.Select(n => n is JsonValue v && v.TryGetValue<string>(out var id) ? id : null).OfType<string>().ToHashSet()
            : new HashSet<string>();

    /// <summary>Saves the unchecked sources, dropping dictionaries that no longer exist.</summary>
    private void SaveUncheckedKeySources()
    {
        var present = DumpKeySources.Select(s => s.Id).ToHashSet();
        _uncheckedKeySources.IntersectWith(present);
        AppSettings.Set(UncheckedKeySourcesSetting,
            new JsonArray(_uncheckedKeySources.Order().Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()));
    }

    private void UpdateDumpKeyCount() => DumpKeyCount = BuildKeyDictionary().Count;

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
        return keys;
    }

    private Task ImportAsync() => RunAsync(async () =>
    {
        var picked = await StorageService.OpenAsync("Import card", CardFormats.ImportExtensions);
        if (picked is not { } file) return;

        IReadOnlyList<CardSave> cards;
        try { cards = CardFormats.Import(file.Name, file.Data); }
        catch (Exception ex) { throw new FormatException($"{file.Name}: unsupported or damaged file ({ex.Message})."); }
        if (cards.Count == 0) throw new FormatException($"{file.Name} contains no cards.");

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (cards.Count == 1)
            {
                // A single card is reviewed (name, color, identity) before it is saved.
                ResetAddState();
                Form = new CardEditorViewModel(cards[0]);
                FormSource = $"Imported from {file.Name}";
                return;
            }
            // A whole library export goes straight in (the open folder takes cards that have none).
            if (_folder is not null)
                foreach (var card in cards.Where(c => c.Folder.Length == 0)) card.Folder = _folder;
            _library.AddRange(cards);
            SelectById(cards[^1].Id);
            SetStatus($"Imported {cards.Count} cards.");
        });
    });

    // ---- Save ----------------------------------------------------------

    /// <returns>False when the card could not be saved (it stays open).</returns>
    private bool Save()
    {
        if (Form is not { } form) return false;
        CardSave card;
        try { card = form.Build(); }
        catch (FormatException ex) { SetStatus(ex.Message, isError: true); return false; }

        var isNew = _pane == CardsPane.Add;
        var previous = isNew ? null : _selected?.Card;
        if (isNew && card.Folder.Length == 0 && _folder is not null) card.Folder = _folder;
        try
        {
            if (isNew) _library.Add(card); else _library.Update(card);
        }
        catch (Exception ex) { SetStatus($"Could not save: {ex.Message}", isError: true); return false; }

        if (isNew)
        {
            Pane = CardsPane.None;
            Form = null;
            SelectById(card.Id);
            SetStatus($"Saved “{card.Name}”.");
            return true;
        }

        // Fresh editor so the saved state becomes the new baseline.
        Form = new CardEditorViewModel(card, restrictType: true);
        RaiseCardInfo();
        RebuildTargets();
        SetStatus($"Saved “{card.Name}”.");
        if (previous is not null) OfferSlotUpdate(previous, card);
        return true;
    }

    // ---- Updating slots that emulate an edited card ----------------------

    private sealed record SlotUpdate(CardSave Card, IReadOnlyList<int> Slots);

    private SlotUpdate? _slotUpdate;
    private System.Action? _afterSlotUpdate;

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
            ? $"“{u.Card.Name}” is loaded in slot {u.Slots[0]}. Update the device?"
            : $"“{u.Card.Name}” is loaded in slots {string.Join(", ", u.Slots)}. Update the device?";

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
                    ProgressText = $"Updating slot {slot}: {p.Done}/{p.Total}…");
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
        SetStatus(update.Slots.Count == 1
            ? $"Updated slot {update.Slots[0]} with “{update.Card.Name}”."
            : $"Updated slots {string.Join(", ", update.Slots)} with “{update.Card.Name}”.");
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
    public ICommand CancelWriteCommand { get; }
    public ICommand DetectCommand { get; }

    public ObservableCollection<SlotTargetViewModel> SlotTargets { get; } = new();

    public CardAction Action
    {
        get => _action;
        private set
        {
            // Closing the writer (Cancel, another action, a disconnect) stops a write in flight.
            if (value != CardAction.Write) CancelWrite();
            this.RaiseAndSetIfChanged(ref _action, value);
            this.RaisePropertyChanged(nameof(IsSlotsOpen));
            this.RaisePropertyChanged(nameof(IsWriteOpen));
            this.RaisePropertyChanged(nameof(IsConfirmingDelete));
            this.RaisePropertyChanged(nameof(IsAsking));
            this.RaisePropertyChanged(nameof(ListLocked));
            this.RaisePropertyChanged(nameof(ShowEditor));
            this.RaisePropertyChanged(nameof(ShowMemoryEditor));
        }
    }

    public bool IsSlotsOpen => _action == CardAction.Slots;
    public bool IsWriteOpen => _action == CardAction.Write;
    public bool IsConfirmingDelete => _action == CardAction.Delete;

    /// <summary>The list and the editor are locked while the writer view is open (as when adding a card).</summary>
    public bool ListLocked => IsEditingNew || IsWriteOpen;

    /// <summary>The card editor is shown unless the writer view has taken over the pane.</summary>
    public bool ShowEditor => _form is not null && !IsWriteOpen;

    /// <summary>The "create memory" block is shown only while editing a card that still needs memory.</summary>
    public bool ShowMemoryEditor => ShowEditor && _form?.CanCreateMemory == true;

    private void Toggle(CardAction action)
    {
        Action = _action == action ? CardAction.None : action;
        if (_action == CardAction.Write) WriteBlockStates = Array.Empty<MifareBlockState>();
    }

    /// <summary>Short progress line shown beside the Stop button while a write runs; null otherwise.</summary>
    public string? WriteProgressText
    {
        get => _writeProgressText;
        private set
        {
            this.RaiseAndSetIfChanged(ref _writeProgressText, value);
            this.RaisePropertyChanged(nameof(IsWriting));
            this.RaisePropertyChanged(nameof(CancelWriteLabel));
            this.RaisePropertyChanged(nameof(CancelWriteTip));
        }
    }

    public bool IsWriting => _writeProgressText is not null;

    public string CancelWriteLabel => IsWriting ? "Cancel" : "Close";

    public string CancelWriteTip => IsWriting
        ? "Stop the write; blocks already written stay on the card"
        : "Close the writer";

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

    public bool HasWriteBlocks => _writeBlockStates.Count > 0;

    /// <summary>What one cell of the writer's strip is.</summary>
    public string WriteUnit => IsUltralightWrite ? "Page" : "Block";

    private void OnWriteBlocks(MifareBlockProgress p)
    {
        if (p.State == MifareBlockState.Pending && p.First == 0)
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
        WriteProgressText = "Stopping…";
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
    private MagicGeneration _generation =
        Enum.TryParse<MagicGeneration>(AppSettings.GetString(GenerationSetting), out var gen) ? gen : MagicGeneration.Gen2;

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
        : !Hex.TryParse(_writePassword, out var key) ? "Not a hex value."
        : key.Length == 4 ? null
        : "The password is 4 bytes.";

    /// <summary>
    /// Optional 4-byte password of an already-locked T55xx tag, tried before the firmware's
    /// defaults to unlock it. Left empty for a blank tag.
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
        !IsLfWrite || string.IsNullOrWhiteSpace(_writeLfPassword) ? null
        : !Hex.TryParse(_writeLfPassword, out var key) ? "Not a hex value."
        : key.Length == 4 ? null
        : "The password is 4 bytes.";

    /// <summary>
    /// Optional 4-byte password the T55xx tag is locked with after the write (the firmware always
    /// sets the PWD bit). Empty uses the firmware default (0x20206666).
    /// </summary>
    public string? WriteLfNewKey
    {
        get => _writeLfNewKey;
        set
        {
            this.RaiseAndSetIfChanged(ref _writeLfNewKey, value);
            this.RaisePropertyChanged(nameof(WriteLfNewKeyError));
        }
    }

    /// <summary>Why <see cref="WriteLfNewKey"/> can't be used; null when it is empty or valid.</summary>
    public string? WriteLfNewKeyError =>
        !IsLfWrite || string.IsNullOrWhiteSpace(_writeLfNewKey) ? null
        : !Hex.TryParse(_writeLfNewKey, out var key) ? "Not a hex value."
        : key.Length == 4 ? null
        : "The password is 4 bytes.";

    /// <summary>Which magic generation the MIFARE write targets.</summary>
    public MagicGeneration SelectedGeneration
    {
        get => _generation;
        set
        {
            if (_generation == value) return;
            this.RaiseAndSetIfChanged(ref _generation, value);
            this.RaisePropertyChanged(nameof(WriteWarning));
            AppSettings.Set(GenerationSetting, value.ToString());
        }
    }

    public string WriteKind => ActionTag switch
    {
        null => string.Empty,
        { } t when t.IsMifareClassic() => "Write to a magic card",
        { } t when t.IsUltralight() => "Write pages to an Ultralight / NTAG card",
        { } t when t.SenseType() == TagSenseType.Lf && t.CanWriteToTag() => "Write to T55xx tag",
        _ => "This card type cannot be written yet",
    };

    public string WriteWarning => ActionTag switch
    {
        { } t when t.IsMifareClassic() => SelectedGeneration switch
        {
            MagicGeneration.Gen1a =>
                "Place a Gen1a (UID) magic card on the reader. Every block, block 0 included, is written raw with no keys.",
            MagicGeneration.Gen3 =>
                "Place a Gen3 (APDU) magic card on the reader. Data blocks are written with the card's own keys or the saved dictionaries; block 0 is set with the Gen3 backdoor command.",
            _ =>
                "Place a Gen2 / CUID magic card on the reader. Every block, block 0 included, is written with the card's own keys or the saved dictionaries.",
        },
        { } t when t.IsUltralight() =>
            "Place an Ultralight / NTAG card of the same size or larger on the reader. The user pages are written; the UID, lock bytes, capability container and configuration (password, AUTH0) are left as they are, so the card keeps its own UID.",
        { } t when t.SenseType() == TagSenseType.Lf =>
            "Place a blank T55xx tag on the reader. Writing the wrong data can brick a tag — double-check the card.",
        _ => string.Empty,
    };

    private void RaiseCardInfo()
    {
        this.RaisePropertyChanged(nameof(SelectedCardFolder));
        this.RaisePropertyChanged(nameof(CardFolderLabel));
        this.RaisePropertyChanged(nameof(CanExportDump));
        this.RaisePropertyChanged(nameof(CanWriteSelected));
        this.RaisePropertyChanged(nameof(IsMifareWrite));
        this.RaisePropertyChanged(nameof(IsUltralightWrite));
        this.RaisePropertyChanged(nameof(IsLfWrite));
        this.RaisePropertyChanged(nameof(WriteUnit));
        this.RaisePropertyChanged(nameof(WritePasswordError));
        this.RaisePropertyChanged(nameof(WriteLfPasswordError));
        this.RaisePropertyChanged(nameof(WriteLfNewKeyError));
        this.RaisePropertyChanged(nameof(WriteKind));
        this.RaisePropertyChanged(nameof(WriteWarning));
    }

    private Task DetectAsync() => RunAsync(async () =>
    {
        var client = _device.Client;
        if (client is null) return;
        ProgressText = "Detecting the card…";
        try
        {
            var gen = await client.DetectMagicAsync();
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (gen is null)
                {
                    SetStatus("No card found on the reader.", isError: true);
                    return;
                }
                SelectedGeneration = gen.Value;
                SetStatus(gen.Value switch
                {
                    MagicGeneration.Gen1a => "Detected a Gen1a (UID) magic card.",
                    MagicGeneration.Gen3 => "Detected a Gen3 (APDU) magic card.",
                    _ => "No Gen1a or Gen3 magic card detected; assuming Gen2 / CUID.",
                });
            });
        }
        finally
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => ProgressText = null);
        }
    });

    private void Duplicate()
    {
        CardSave? card;
        try { card = ActionCard(); }
        catch (FormatException ex) { SetStatus(ex.Message, isError: true); return; }
        if (card is null) return;
        // The copy takes the unsaved edits; the original stays as last saved.
        var copy = card.Clone();
        copy.Name = card.Name + " (copy)";
        _library.Add(copy);
        SelectById(copy.Id);
        SetStatus($"Duplicated as “{copy.Name}”.");
    }

    private void ConfirmDelete()
    {
        if (Selected is not { } s) return;
        SetSelected(null);
        _library.Remove(s.Card);
        SetStatus($"Deleted “{s.Name}”.");
    }

    private Task ExportAsync() => RunAsync(async () =>
    {
        if (ActionCard() is not { } card) return;
        var name = await StorageService.SaveAsync("Export card", $"{SafeFileName(card.Name)}.json", CardFormats.ExportJson(card), "json");
        if (name is not null) SetStatus($"Saved {name}.");
    });

    /// <summary>Exports a MIFARE Classic card's memory as a raw .bin or Proxmark .eml dump.</summary>
    private Task ExportDumpAsync(bool bin) => RunAsync(async () =>
    {
        if (!CanExportDump || ActionCard() is not { } card) return;
        var dump = card.ToMifareDump();
        var safe = SafeFileName(card.Name);
        var name = bin
            ? await StorageService.SaveAsync("Export dump as .bin", $"{safe}.bin", dump.ToBin(), "bin")
            : await StorageService.SaveAsync("Export dump as .eml", $"{safe}.eml",
                System.Text.Encoding.UTF8.GetBytes(dump.ToEml()), "eml");
        if (name is not null) SetStatus($"Saved {name}.");
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
        SlotTargets.Clear();
        if (ActionTag is not { } tag) return;
        var sense = tag.SenseType() == TagSenseType.Lf ? TagSenseType.Lf : TagSenseType.Hf;
        foreach (var info in _slotInfos) SlotTargets.Add(new SlotTargetViewModel(info, sense));
    }

    private Task LoadToSlotAsync(SlotTargetViewModel target) => RunAsync(async () =>
    {
        var client = _device.Client;
        if (client is null || ActionCard() is not { } card) return;

        var progress = new Progress<MifareProgress>(p =>
            ProgressText = $"Uploading {p.Done}/{p.Total}…");
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
        SetStatus($"“{card.Name}” is now emulated in slot {target.Index}.");
    });

    private Task WriteAsync() => RunAsync(async () =>
    {
        var client = _device.Client;
        if (client is null || ActionCard() is not { } card) return;

        var unit = card.Tag.IsUltralight() ? "page" : "block";
        // Posted like the reset below and the cleanup in finally, so the ticks land in order between them.
        var progress = new PostedProgress<MifareProgress>(p =>
        {
            // A late tick must not overwrite “Stopping…”.
            if (_writeCts is { IsCancellationRequested: false }) WriteProgressText = $"{WriteUnit} {p.Done}/{p.Total}";
        });
        var blockProgress = new PostedProgress<MifareBlockProgress>(OnWriteBlocks);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            WriteBlockStates = Array.Empty<MifareBlockState>();
            WriteProgressText = "Starting…";
        });

        using var cts = new CancellationTokenSource();
        _writeCts = cts;
        try
        {
            if (card.Tag.SenseType() == TagSenseType.Lf)
            {
                // The current password unlocks the tag; it is tried before the firmware's defaults.
                var hasCurrent = Hex.TryParse(WriteLfPassword, out var lfKey) && lfKey.Length == 4;
                IReadOnlyList<byte[]>? oldKeys = hasCurrent ? [lfKey, .. ChameleonClient.DefaultT55xxOldKeys] : null;
                // The password the tag is locked with afterwards. An empty New password keeps the
                // current one, so the tag is not silently re-locked with the firmware default; with
                // no current password either (a blank tag) it falls back to the firmware default.
                var newKey = Hex.TryParse(WriteLfNewKey, out var nk) && nk.Length == 4 ? nk
                    : hasCurrent ? lfKey
                    : null;
                await client.WriteLfToT55xxAsync(card.Tag, card.Uid, newKey: newKey, oldKeys: oldKeys, ct: cts.Token);
                SetStatus($"Wrote “{card.Name}” to the T55xx tag.");
                return;
            }

            IReadOnlyList<int> failed;
            if (card.Tag.IsUltralight())
            {
                var pwd = Hex.TryParse(WritePassword, out var k) && k.Length > 0 ? k : null;
                failed = await client.WriteUltralightUserPagesAsync(card, pwd, progress, cts.Token, blockProgress);
            }
            else if (card.Tag.IsMifareClassic())
            {
                failed = await client.WriteMifareDumpToCardAsync(card.ToMifareDump(), _dictionaries.AllKeys(),
                    SelectedGeneration, progress, cts.Token, blockProgress);
            }
            else return;

            if (failed.Count == 0)
                SetStatus($"Wrote “{card.Name}” to the card.");
            else
                SetStatus($"Wrote “{card.Name}”, but {failed.Count} {unit}(s) failed: {string.Join(", ", failed)}.", isError: true);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            SetStatus($"Write stopped. The {unit}s already written stay on the card.");
        }
        finally
        {
            _writeCts = null;
            // The block being written when the write stopped (or the link dropped) was not confirmed.
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                WriteBlockStates = WithBlocks(_writeBlockStates, 0, _writeBlockStates.Count,
                    MifareBlockState.Pending, onlyFrom: MifareBlockState.Reading);
                WriteProgressText = null;
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
