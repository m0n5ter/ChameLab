using System;
using System.Windows.Input;
﻿using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using ChameLab.Services;
using ReactiveUI;

namespace ChameLab.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    private readonly DeviceService _device;
    private readonly ConnectViewModel _connect;
    private PageViewModelBase _currentPage;
    private bool _isConnected;
    private bool _isPaneOpen = true;
    private string _deviceSummary = "Not connected";
    private bool _isExitHintVisible;
    private DateTime _exitHintShownAt;

    /// <summary>How long a second Back press still counts as confirming the exit.</summary>
    private static readonly TimeSpan ExitConfirmWindow = TimeSpan.FromSeconds(2);

    public MainViewModel(DeviceService device, CardLibrary cards, DictionaryLibrary dictionaries,
        FirmwareUpdateService firmware)
    {
        _device = device;

        Firmware = new FirmwareUpdateViewModel(device, firmware);
        _connect = new ConnectViewModel(device);
        Chameleon = new ChameleonViewModel(device, cards, Firmware);
        Slots = new SlotsViewModel(device, cards);
        Cards = new CardsViewModel(device, cards, dictionaries);
        Dictionaries = new DictionariesViewModel(dictionaries, cards);
        Tools = new ToolsViewModel(device);
        AppUpdate = new AppUpdateViewModel();
        Settings = new SettingsViewModel(cards, dictionaries, AppUpdate);

        // Chameleon takes the place of Connect once a device is attached.
        _connectedPages = new PageViewModelBase[] { Chameleon, Slots, Cards, Dictionaries, Tools, Settings };
        // Without a device only the app-side pages make sense, behind Connect to attach one.
        _disconnectedPages = new PageViewModelBase[] { _connect, Cards, Dictionaries, Tools, Settings };
        NavPages = new ObservableCollection<PageViewModelBase>(_disconnectedPages);

        _currentPage = _connect;

        NavigateCommand = ReactiveCommand.CreateFromTask<PageViewModelBase>(NavigateAsync);
        DisconnectCommand = ReactiveCommand.CreateFromTask(DisconnectAsync);
        TogglePaneCommand = ReactiveCommand.Create(() => { IsPaneOpen = !IsPaneOpen; });
        AddCardCommand = ReactiveCommand.CreateFromTask(AddCardAsync,
            this.WhenAnyValue(x => x.CurrentPage, x => x.Cards.IsPickingSource,
                (page, picking) => !(ReferenceEquals(page, Cards) && picking)));

        Slots.OpenCardRequested += card => _ = OpenCardAsync(card.Id);
        Cards.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CardsViewModel.CurrentFolder)) RaiseNavSelection();
        };
        NavPages.CollectionChanged += (_, _) => SyncNavItems();
        Cards.Folders.CollectionChanged += (_, _) => SyncNavItems();
        SyncNavItems();
        _device.StateChanged += OnStateChanged;
        _connect.StartWatching();
        _ = AppUpdate.CheckInBackgroundAsync();
        // A firmware install looks for the bootloader itself: a Connect page scan running beside
        // it would cut its BLE scan short, so watching resumes only once the install is over.
        Firmware.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FirmwareUpdateViewModel.IsInstalling) && !Firmware.IsInstalling &&
                _device.State == ConnectionState.Disconnected)
                _connect.StartWatching();
        };
    }

    public ConnectViewModel Connect => _connect;
    /// <summary>Version check and install dialog; the dialog overlays the whole shell.</summary>
    public FirmwareUpdateViewModel Firmware { get; }
    /// <summary>Whether a newer ChameLab is out; shown in the top bar and in Settings.</summary>
    public AppUpdateViewModel AppUpdate { get; }
    /// <summary>The connected device: status, firmware and its settings.</summary>
    public ChameleonViewModel Chameleon { get; }
    public SlotsViewModel Slots { get; }
    public CardsViewModel Cards { get; }
    public DictionariesViewModel Dictionaries { get; }
    /// <summary>Tools: HF and LF sniffing. Captures also load from files, so it is listed without a device too.</summary>
    public ToolsViewModel Tools { get; }
    public SettingsViewModel Settings { get; }

    private readonly PageViewModelBase[] _connectedPages;
    private readonly PageViewModelBase[] _disconnectedPages;

    /// <summary>Pages listed in the navigation pane: the full set when connected, the app-side ones without a device.</summary>
    public ObservableCollection<PageViewModelBase> NavPages { get; }

    public PageViewModelBase CurrentPage
    {
        get => _currentPage;
        private set
        {
            this.RaiseAndSetIfChanged(ref _currentPage, value);
            this.RaisePropertyChanged(nameof(SelectedNavItem));
            this.RaisePropertyChanged(nameof(SelectedNavPage));
        }
    }

    /// <summary>The page shown, for the bottom tab bar of narrow layouts (the card folders stay in the pane).</summary>
    public PageViewModelBase? SelectedNavPage
    {
        get => _clearingNavSelection ? null : NavPages.Contains(_currentPage) ? _currentPage : null;
        set
        {
            if (value is not null) SelectedNavItem = value;
        }
    }

    /// <summary>The navigation list: the pages, with the card folders under Cards.</summary>
    public ObservableCollection<object> NavItems { get; } = new();

    /// <summary>Keeps <see cref="NavItems"/> in step with the pages and folders, in place (so a folder being renamed keeps its row).</summary>
    private void SyncNavItems()
    {
        var wanted = new List<object>();
        foreach (var page in NavPages)
        {
            wanted.Add(page);
            if (!ReferenceEquals(page, Cards)) continue;
            wanted.AddRange(Cards.Folders);
        }
        for (var i = NavItems.Count - 1; i >= 0; i--)
            if (!wanted.Contains(NavItems[i])) NavItems.RemoveAt(i);
        for (var i = 0; i < wanted.Count; i++)
        {
            var at = NavItems.IndexOf(wanted[i]);
            if (at < 0) NavItems.Insert(i, wanted[i]);
            else if (at != i) NavItems.Move(at, i);
        }
        if (!_swappingNavPages) RaiseNavSelection();
    }

    /// <summary>
    /// Two-way selection for the navigation list: the page shown, or the card folder shown. Selecting
    /// a page navigates to it (Cards shows the cards in no folder), a folder shows its cards.
    /// </summary>
    public object? SelectedNavItem
    {
        get => _clearingNavSelection ? null
            : ReferenceEquals(_currentPage, Cards) && Cards.CurrentFolder is { } folder ? folder
            : NavPages.Contains(_currentPage) ? _currentPage : null;
        set
        {
            switch (value)
            {
                case PageViewModelBase page:
                    if (!ReferenceEquals(page, _currentPage)) _ = NavigateAsync(page);
                    else if (ReferenceEquals(page, Cards) && Cards.CurrentFolder is not null) ShowFolder(null);
                    break;
                case CardFolderViewModel folder when !ReferenceEquals(folder, SelectedNavItem):
                    _ = OpenFolderAsync(folder);
                    break;
            }
        }
    }

    private bool _clearingNavSelection;

    /// <summary>
    /// Puts the list's highlight on the place shown, now and once more after layout: unsaved work may
    /// have kept the page where it was, and folders added or moving to a new place drop the highlight
    /// (while the list still holds the item as selected, so it is cleared before it is set again).
    /// </summary>
    private void RaiseNavSelection()
    {
        this.RaisePropertyChanged(nameof(SelectedNavItem));
        this.RaisePropertyChanged(nameof(SelectedNavPage));
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _clearingNavSelection = true;
            this.RaisePropertyChanged(nameof(SelectedNavItem));
            this.RaisePropertyChanged(nameof(SelectedNavPage));
            _clearingNavSelection = false;
            this.RaisePropertyChanged(nameof(SelectedNavItem));
            this.RaisePropertyChanged(nameof(SelectedNavPage));
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    private async Task OpenFolderAsync(CardFolderViewModel folder)
    {
        await NavigateAsync(Cards);
        ShowFolder(folder);
    }

    private void ShowFolder(CardFolderViewModel? folder)
    {
        Cards.ShowFolder(folder);
        RaiseNavSelection();
    }

    /// <summary>Set by the view when the window is too narrow to dock the pane (it overlays content).</summary>
    public bool IsCompact { get; set; }

    public bool IsConnected
    {
        get => _isConnected;
        private set => this.RaiseAndSetIfChanged(ref _isConnected, value);
    }

    public bool IsPaneOpen
    {
        get => _isPaneOpen;
        set => this.RaiseAndSetIfChanged(ref _isPaneOpen, value);
    }

    public string DeviceSummary
    {
        get => _deviceSummary;
        private set => this.RaiseAndSetIfChanged(ref _deviceSummary, value);
    }

    /// <summary>"Press Back again to exit", shown after a Back press on the start page.</summary>
    public bool IsExitHintVisible
    {
        get => _isExitHintVisible;
        private set => this.RaiseAndSetIfChanged(ref _isExitHintVisible, value);
    }

    /// <summary>The page Back returns to before leaving the app: where the current session starts.</summary>
    private PageViewModelBase StartPage => IsConnected ? Chameleon : _connect;

    /// <summary>
    /// Steps back inside the shell for the system Back button: closes the overlaid navigation
    /// pane, else returns to the start page. False when already there, so the app may close.
    /// </summary>
    public bool TryGoBack()
    {
        if (IsCompact && IsPaneOpen)
        {
            IsPaneOpen = false;
            return true;
        }
        if (ReferenceEquals(CurrentPage, StartPage)) return false;
        _ = NavigateAsync(StartPage);
        return true;
    }

    /// <summary>
    /// The first Back on the start page only shows a hint; a second one within a couple of
    /// seconds is let through and closes the app.
    /// </summary>
    public bool ConfirmExit()
    {
        if (IsExitHintVisible && DateTime.UtcNow - _exitHintShownAt < ExitConfirmWindow) return true;
        var shownAt = _exitHintShownAt = DateTime.UtcNow;
        IsExitHintVisible = true;
        Avalonia.Threading.DispatcherTimer.RunOnce(() =>
        {
            if (_exitHintShownAt == shownAt) IsExitHintVisible = false;
        }, ExitConfirmWindow);
        return false;
    }

    public ICommand NavigateCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand TogglePaneCommand { get; }
    /// <summary>The navigation pane's Add card: opens Cards (from any page) and starts a new card there.</summary>
    public ICommand AddCardCommand { get; }

    private async Task NavigateAsync(PageViewModelBase page)
    {
        if (page is null) return;
        var leaving = CurrentPage;
        CurrentPage = page;
        if (IsCompact) IsPaneOpen = false;
        if (!ReferenceEquals(leaving, page))
        {
            leaving.ClearMessages();
            await leaving.OnDeactivatedAsync();
        }
        await page.OnActivatedAsync();
    }

    private async Task OpenCardAsync(string id)
    {
        await NavigateAsync(Cards);
        Cards.OpenCard(id);
    }

    private async Task AddCardAsync()
    {
        await NavigateAsync(Cards);
        // Cards may have been left with the source buttons up: then they are simply shown again.
        if (Cards.AddCommand.CanExecute(null)) Cards.AddCommand.Execute(null);
    }

    private Task DisconnectAsync() => _device.DisconnectAsync();

    /// <summary>
    /// Swaps the listed pages. Our CollectionChanged handler runs before the tab bar's, so any selection
    /// raised mid-swap (even none) makes the ListBox read an index it has not caught up with: the
    /// selection is raised only once the swap is done.
    /// </summary>
    private void SetNavPages(IReadOnlyList<PageViewModelBase> pages)
    {
        _swappingNavPages = true;
        try
        {
            NavPages.Clear();
            foreach (var p in pages) NavPages.Add(p);
        }
        finally
        {
            _swappingNavPages = false;
        }
        RaiseNavSelection();
    }

    private bool _swappingNavPages;

    private void OnStateChanged(ConnectionState state)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            IsConnected = state == ConnectionState.Connected;
            if (state == ConnectionState.Connected)
            {
                _connect.StopWatching();
                SetNavPages(_connectedPages);
                var info = _device.DeviceInfo;
                DeviceSummary = info is null
                    ? "Connected"
                    : $"{info.ModelName} · fw {info.FirmwareVersion}";
                _ = Firmware.OnConnectedAsync();
                // Connecting from the Connect page goes on to the device; any other page stays.
                if (ReferenceEquals(CurrentPage, _connect)) await NavigateAsync(Chameleon);
            }
            else
            {
                DeviceSummary = "Not connected";
                SetNavPages(_disconnectedPages);
                if (state == ConnectionState.Disconnected && !Firmware.IsInstalling) _connect.StartWatching();
                // A device page has nothing left to show: back to Connect. App-side pages stay.
                if (!NavPages.Contains(CurrentPage)) await NavigateAsync(_connect);
            }
        });
    }
}
