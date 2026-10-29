using System;
using System.Collections;
using System.Collections.Generic;
using System.Windows.Input;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using ChameLab.Behaviors;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;
using ReactiveUI;

namespace ChameLab.ViewModels;

/// <summary>
/// The device's eight emulation slots, laid out like the Cards page: the list of slots on the
/// left and an editor for the open one on the right (the saved card and LED color of
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
    private bool _isSinglePane;
    private bool _autoOpened;
    private string? _progressText;

    // Page margins 56 + list 280 + gap 18 + details 420 (see SlotsView).
    public override double MinContentWidth => 774;

    public SlotsViewModel(DeviceService device, CardLibrary cards) : base("Slots", "slots")
    {
        _device = device;
        _cards = cards;
        _isConnected = device.State == ConnectionState.Connected;

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

    private void SetSelected(SlotItemViewModel? value, bool auto = false)
    {
        if (ReferenceEquals(value, _selected)) return;
        _autoOpened = auto && value is not null;
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
            else OpenActiveIfIdle();
        });
    });

    // Nothing open yet: start on the slot the device has active. Only beside the list; on a
    // narrow screen the details would cover it, so the list comes first there.
    private void OpenActiveIfIdle()
    {
        if (_selected is null && !_isSinglePane && Slots.FirstOrDefault(s => s.IsActive) is { } active)
            SetSelected(active, auto: true);
    }

    /// <summary>The list and the details show one at a time (set by the view from its layout).</summary>
    public bool IsSinglePane
    {
        get => _isSinglePane;
        set
        {
            if (value == _isSinglePane) return;
            _isSinglePane = value;
            // A slot the page opened by itself would now hide the list: close it, unless it is being edited.
            if (value && _autoOpened && _editor?.IsDirty != true) SetSelected(null);
            else if (!value) OpenActiveIfIdle();
        }
    }

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
            // The editor's messages show on this page (its save errors bind to the save bar directly).
            case nameof(Notice) when sender is SlotEditorViewModel { Notice: { } notice }:
                Notify(notice);
                break;
            case nameof(Error) when sender is SlotEditorViewModel editor:
                if (editor.Error is { } error) ReportError(error); else ClearError();
                break;
        }
    }

    /// <summary>The right-hand pane is in use; on phones it replaces the list.</summary>
    public bool HasPane => _editor is not null;

    /// <summary>The open slot has unsaved edits (drives the save bar).</summary>
    public bool IsDirty => _editor?.IsDirty == true;

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

    /// <summary>Asking whether to save before switching slot, closing or swapping.</summary>
    public bool IsConfirmingLeave => _pendingLeave is not null;

    public string LeaveText => $"Save changes to slot {_selected?.Index}?";

    /// <summary>A question or the card picker waits for an answer; the editor is dimmed until then.</summary>
    public bool IsAsking => IsConfirmingLeave || _editor?.IsPicking == true;

    public ICommand SaveAndLeaveCommand { get; }
    public ICommand DiscardAndLeaveCommand { get; }
    public ICommand StayCommand { get; }

    // ---- Actions ---------------------------------------------------------

    /// <summary>
    /// Swaps what two slots hold: both cards of each (with nick, LED color and enable flag) and the
    /// HF emulator settings. Both are read first, so a failed write can put the first slot back.
    /// The active slot stays the same number; an open slot reopens on its new content.
    /// </summary>
    private async Task SwapAsync(SlotItemViewModel a, SlotItemViewModel b)
    {
        await SwapSlotsAsync(a, b);
        // Show what the slots hold now, whether or not the swap went through. Posted after the swap
        // has posted IsBusy = false, or the reload would see the page still busy and skip.
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = LoadAsync(reopen: true));
    }

    private Task SwapSlotsAsync(SlotItemViewModel a, SlotItemViewModel b) => RunAsync(async () =>
    {
        var client = _device.Client;
        if (client is null) return;
        var (ia, ib) = (a.Index, b.Index);
        try
        {
            // An editor opening a slot meanwhile would switch the active slot under the writes.
            await client.WithSlotLockAsync(async () =>
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
            });
            Notify($"Swapped slots {ia} and {ib}.");
        }
        finally { SetProgress(null); }
    });

    private void SetProgress(string? text) => Avalonia.Threading.Dispatcher.UIThread.Post(() => ProgressText = text);

    // Reported in line and posted like SetProgress, so no late report can land after the final SetProgress(null)
    // (a Progress<T> made off the UI thread reports on the thread pool, in no particular order).
    private IProgress<MifareProgress> Progress(string label) =>
        new InlineProgress(p => SetProgress($"{label}: {p.Done}/{p.Total}…"));

    private sealed class InlineProgress(Action<MifareProgress> report) : IProgress<MifareProgress>
    {
        public void Report(MifareProgress value) => report(value);
    }
}
