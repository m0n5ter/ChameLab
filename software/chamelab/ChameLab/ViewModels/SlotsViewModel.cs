using System;
using System.Collections;
using System.Collections.Generic;
using System.Windows.Input;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using ChameLab.Behaviors;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;
using ReactiveUI;

namespace ChameLab.ViewModels;

/// <summary>
/// The device's eight emulation slots, laid out like the Cards page: the list of slots on the
/// left and an editor for the open one on the right (the saved card, LED colour and enable flag of
/// each frequency plus the HF emulator settings, saved from the bar below). Opening a slot makes
/// it the active one, since the firmware reads and writes only the active slot. Dropping a slot
/// onto another swaps what the two hold.
/// </summary>
public sealed class SlotsViewModel : PageViewModelBase, IReorderableList, IDropOntoList
{
    private readonly DeviceService _device;
    private readonly CardLibrary _cards;
    private SlotItemViewModel? _selected;
    private SlotEditorViewModel? _editor;
    private bool _isConnected;
    private bool _rebuilding;
    private string? _progressText;

    // Page margins 56 + list 280 + gap 18 + details 420 (see SlotsView).
    public override double MinContentWidth => 774;

    public SlotsViewModel(DeviceService device, CardLibrary cards) : base("Slots", "slots")
    {
        _device = device;
        _cards = cards;
        _isConnected = device.State == ConnectionState.Connected;

        var open = this.WhenAnyValue(x => x.Selected).Select(s => s is not null);
        var connected = this.WhenAnyValue(x => x.IsConnected);

        RefreshCommand = ReactiveCommand.Create(() => LeaveThen(() => _ = LoadAsync(reopen: true)), connected);
        SaveCommand = ReactiveCommand.CreateFromTask(SaveAsync, connected);
        CloseCommand = ReactiveCommand.Create(() => LeaveThen(() => SetSelected(null)));
        SaveSlotCommand = ReactiveCommand.CreateFromTask(async () => { if (_editor is { } e) await e.SaveAsync(); });
        DiscardCommand = ReactiveCommand.Create(() => _editor?.Discard());
        SaveAndLeaveCommand = ReactiveCommand.CreateFromTask(async () =>
            ResolveLeave(run: _editor is not { } e || await e.SaveAsync()));
        DiscardAndLeaveCommand = ReactiveCommand.Create(() => ResolveLeave(run: true));
        StayCommand = ReactiveCommand.Create(() => ResolveLeave(run: false));

        _cards.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(() => _editor?.OnLibraryChanged());
        _device.StateChanged += s => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            IsConnected = s == ConnectionState.Connected;
            if (IsConnected) return;
            // Nothing to show or edit without the device.
            _pendingLeave = null;
            SetSelected(null);
            Slots.Clear();
            RaiseListState();
            this.RaisePropertyChanged(nameof(IsConfirmingLeave));
            this.RaisePropertyChanged(nameof(IsAsking));
        });
    }

    // ---- List ----------------------------------------------------------

    public ObservableCollection<SlotItemViewModel> Slots { get; } = new();

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isConnected, value);
            this.RaisePropertyChanged(nameof(IsEmpty));
        }
    }

    /// <summary>No slots to show (no device, or not read yet).</summary>
    public bool IsEmpty => Slots.Count == 0;

    public string CountText
    {
        get
        {
            if (_progressText is not null) return _progressText;
            if (Slots.Count == 0) return IsConnected ? "Reading the slots…" : "No device connected";
            var used = Slots.Count(s => !s.IsEmpty);
            var active = Slots.FirstOrDefault(s => s.IsActive);
            var usedText = used == 1 ? "1 slot in use" : $"{used} slots in use";
            return active is null ? usedText : $"{usedText} · slot {active.Index} active";
        }
    }

    private void RaiseListState()
    {
        this.RaisePropertyChanged(nameof(IsEmpty));
        this.RaisePropertyChanged(nameof(CountText));
    }

    // The slots keep the device's order: a slot dragged onto another swaps contents with it
    // (IDropOntoList) instead of moving.
    bool IReorderableList.CanReorder => IsConnected && !IsBusy && !IsAsking;
    IList IReorderableList.ReorderItems => Slots;
    string IReorderableList.ReorderKey(object item) => ((SlotItemViewModel)item).Index.ToString();
    void IReorderableList.OpenItem(object item) => Selected = (SlotItemViewModel)item;
    void IReorderableList.MoveItem(int from, int to) { }
    void IReorderableList.CommitOrder() { }

    // Swapping two empty slots would change nothing.
    bool IDropOntoList.CanDropOnto(object item, object target) =>
        !(((SlotItemViewModel)item).IsEmpty && ((SlotItemViewModel)target).IsEmpty);

    void IDropOntoList.DropOnto(object item, object target)
    {
        var (a, b) = ((SlotItemViewModel)item, (SlotItemViewModel)target);
        // Unsaved edits of the open slot would be overwritten by the swap: save or drop them first.
        LeaveThen(() => _ = SwapAsync(a, b));
    }

    /// <summary>What a long operation (a swap) is doing; shown under the title instead of the count.</summary>
    private string? ProgressText
    {
        get => _progressText;
        set
        {
            _progressText = value;
            this.RaisePropertyChanged(nameof(CountText));
        }
    }

    /// <summary>The slot open in the editor; picking another one with unsaved edits asks first.</summary>
    public SlotItemViewModel? Selected
    {
        get => _selected;
        set
        {
            if (ReferenceEquals(value, _selected)) return;
            // Clearing the list during a reload resets the ListBox selection; the reload restores it itself.
            if (_rebuilding) return;
            if (_editor?.IsDirty == true)
            {
                LeaveThen(() => SetSelected(value));
                // The list has already moved its highlight; put it back on the open slot.
                Avalonia.Threading.Dispatcher.UIThread.Post(() => this.RaisePropertyChanged(nameof(Selected)));
                return;
            }
            SetSelected(value);
        }
    }

    private void SetSelected(SlotItemViewModel? value)
    {
        if (ReferenceEquals(value, _selected)) return;
        this.RaiseAndSetIfChanged(ref _selected, value, nameof(Selected));
        if (value is null) Editor = null;
        else OpenEditor(value);
    }

    /// <summary>A fresh editor on what the slot holds; activates the slot on the device.</summary>
    private void OpenEditor(SlotItemViewModel slot)
    {
        var editor = new SlotEditorViewModel(_device, _cards, slot.Info, OnSlotChanged, OpenCard);
        Editor = editor;
        foreach (var s in Slots) s.IsActive = s.Index == slot.Index;
        this.RaisePropertyChanged(nameof(CountText));
        _ = editor.OnActivatedAsync();
    }

    public override Task OnActivatedAsync() => LoadAsync(reopen: true);

    /// <summary>
    /// Reads the slots, updating the rows in place so the selection stays. With
    /// <paramref name="reopen"/> the open slot's editor starts over from what was read.
    /// </summary>
    private Task LoadAsync(bool reopen) => RunAsync(async () =>
    {
        var client = _device.Client;
        if (client is null) return;
        var slots = await client.GetAllSlotsAsync();
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (Slots.Count == slots.Count && Slots.Select(s => s.Index).SequenceEqual(slots.Select(s => s.Index)))
            {
                for (var i = 0; i < slots.Count; i++) Slots[i].Update(slots[i]);
            }
            else
            {
                var openIndex = _selected?.Index;
                _rebuilding = true;
                try
                {
                    Slots.Clear();
                    foreach (var s in slots) Slots.Add(new SlotItemViewModel(s));
                }
                finally { _rebuilding = false; }
                _selected = Slots.FirstOrDefault(s => s.Index == openIndex);
                this.RaisePropertyChanged(nameof(Selected));
                if (_selected is null) Editor = null;
            }
            RaiseListState();
            if (reopen && _selected is { } open) OpenEditor(open);
        });
    });

    // The editor saved something: show it in the list, keep the editor as it is.
    private void OnSlotChanged() => _ = LoadAsync(reopen: false);

    /// <summary>Asks for a saved card to be opened in the Cards page (MainViewModel goes there).</summary>
    public event Action<CardSave>? OpenCardRequested;

    // Leaving for the card editor asks first about unsaved slot edits.
    private void OpenCard(CardSave card) => LeaveThen(() => OpenCardRequested?.Invoke(card));

    // ---- Editor ----------------------------------------------------------

    public SlotEditorViewModel? Editor
    {
        get => _editor;
        private set
        {
            if (_editor is not null) _editor.PropertyChanged -= OnEditorPropertyChanged;
            this.RaiseAndSetIfChanged(ref _editor, value);
            if (_editor is not null) _editor.PropertyChanged += OnEditorPropertyChanged;
            this.RaisePropertyChanged(nameof(HasPane));
            this.RaisePropertyChanged(nameof(IsDirty));
            this.RaisePropertyChanged(nameof(IsAsking));
        }
    }

    private void OnEditorPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SlotEditorViewModel.IsDirty):
                this.RaisePropertyChanged(nameof(IsDirty));
                break;
            case nameof(SlotEditorViewModel.IsPicking):
                this.RaisePropertyChanged(nameof(IsAsking));
                break;
            // The editor's messages show in this page's banner (read once its error flag is set too).
            case nameof(Status) when sender is SlotEditorViewModel editor:
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (editor.Status is { } message) SetStatus(message, editor.StatusIsError);
                });
                break;
        }
    }

    /// <summary>The right-hand pane is in use; on phones it replaces the list.</summary>
    public bool HasPane => _editor is not null;

    /// <summary>The open slot has unsaved edits (drives the save bar).</summary>
    public bool IsDirty => _editor?.IsDirty == true;

    public ICommand RefreshCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand SaveSlotCommand { get; }
    public ICommand DiscardCommand { get; }

    // ---- Leaving unsaved edits -------------------------------------------

    private Action? _pendingLeave;

    /// <summary>Runs <paramref name="next"/> now, or holds it until the user saves or discards.</summary>
    private void LeaveThen(Action next)
    {
        if (_editor?.IsDirty != true) { next(); return; }
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
        if (run) next?.Invoke();
    }

    /// <summary>Asking whether to save before switching slot, closing or reloading.</summary>
    public bool IsConfirmingLeave => _pendingLeave is not null;

    public string LeaveText => $"Save changes to slot {_selected?.Index}?";

    /// <summary>A question or the card picker waits for an answer; the editor is dimmed until then.</summary>
    public bool IsAsking => IsConfirmingLeave || _editor?.IsPicking == true;

    public ICommand SaveAndLeaveCommand { get; }
    public ICommand DiscardAndLeaveCommand { get; }
    public ICommand StayCommand { get; }

    // ---- Actions ---------------------------------------------------------

    /// <summary>
    /// Swaps what two slots hold: both cards of each (with nick, LED colour and enable flag) and the
    /// HF emulator settings. Both are read first, so a failed write can put the first slot back.
    /// The active slot stays the same number; an open slot reopens on its new content.
    /// </summary>
    private Task SwapAsync(SlotItemViewModel a, SlotItemViewModel b) => RunAsync(async () =>
    {
        var client = _device.Client;
        if (client is null) return;
        var (ia, ib) = (a.Index, b.Index);
        try
        {
            var active = await client.GetActiveSlotAsync();
            SetProgress($"Reading slot {ia}…");
            var first = await client.ReadSlotContentAsync(a.Info, Progress($"Reading slot {ia}"));
            SetProgress($"Reading slot {ib}…");
            var second = await client.ReadSlotContentAsync(b.Info, Progress($"Reading slot {ib}"));

            SetProgress($"Writing slot {ib}…");
            await client.WriteSlotContentAsync(ib, first, Progress($"Writing slot {ib}"));
            try
            {
                SetProgress($"Writing slot {ia}…");
                await client.WriteSlotContentAsync(ia, second, Progress($"Writing slot {ia}"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Slot b now holds a's content and a is half written: put b back as it was.
                try { await client.WriteSlotContentAsync(ib, second); }
                catch { /* the error below still tells what happened */ }
                throw new InvalidOperationException(
                    $"Could not write slot {ia} ({ex.Message}). Slot {ib} was restored; check slot {ia}.");
            }

            if (active is >= 1 and <= 8)
            {
                await client.SetActiveSlotAsync(active);
                await client.SaveSlotConfigAsync();
            }
            SetStatus($"Swapped slots {ia} and {ib}.");
        }
        finally
        {
            SetProgress(null);
            // Show what the slots hold now, whether or not the swap went through.
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = LoadAsync(reopen: true));
        }
    });

    private void SetProgress(string? text) => Avalonia.Threading.Dispatcher.UIThread.Post(() => ProgressText = text);

    private IProgress<MifareProgress> Progress(string label) =>
        new Progress<MifareProgress>(p => ProgressText = $"{label}: {p.Done}/{p.Total}…");

    private Task SaveAsync() => RunAsync(async () =>
    {
        var client = _device.Client;
        if (client is null) return;
        await client.SaveSlotConfigAsync();
    }, "Slot configuration saved to flash.");
}
