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
    private bool _isOffline;
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
        Dashboard = new DashboardViewModel(device, Firmware);
        Slots = new SlotsViewModel(device, cards);
        Cards = new CardsViewModel(device, cards, dictionaries);
        Dictionaries = new DictionariesViewModel(dictionaries);
        Tools = new ToolsViewModel(device, dictionaries);
        AppUpdate = new AppUpdateViewModel();
        Settings = new SettingsViewModel(device, cards, dictionaries, AppUpdate);

        _connectedPages = new PageViewModelBase[] { Dashboard, Slots, Cards, Dictionaries, Tools, Settings };
        // Without a device only the app-side pages make sense; Connect stays reachable to attach one later.
        _offlinePages = new PageViewModelBase[] { _connect, Cards, Dictionaries, Settings };
        NavPages = new ObservableCollection<PageViewModelBase>(_connectedPages);

        _currentPage = _connect;

        NavigateCommand = ReactiveCommand.CreateFromTask<PageViewModelBase>(NavigateAsync);
        DisconnectCommand = ReactiveCommand.CreateFromTask(DisconnectAsync);
        TogglePaneCommand = ReactiveCommand.Create(() => { IsPaneOpen = !IsPaneOpen; });

        Slots.OpenCardRequested += card => _ = OpenCardAsync(card.Id);
        Cards.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CardsViewModel.CurrentFolder)) RaiseNavSelection();
        };
        NavPages.CollectionChanged += (_, _) => SyncNavItems();
        Cards.Folders.CollectionChanged += (_, _) => SyncNavItems();
        SyncNavItems();
        _device.StateChanged += OnStateChanged;
        _connect.OfflineRequested += () => _ = EnterOfflineAsync();
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
    public DashboardViewModel Dashboard { get; }
    public SlotsViewModel Slots { get; }
    public CardsViewModel Cards { get; }
    public DictionariesViewModel Dictionaries { get; }
    public ToolsViewModel Tools { get; }
    public SettingsViewModel Settings { get; }

    private readonly PageViewModelBase[] _connectedPages;
    private readonly PageViewModelBase[] _offlinePages;

    /// <summary>Pages listed in the navigation pane: the full set when connected, a subset offline.</summary>
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
        RaiseNavSelection();
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
                    if (ReferenceEquals(page, Cards) && Cards.CurrentFolder is not null) ShowFolder(null);
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
        private set
        {
            this.RaiseAndSetIfChanged(ref _isConnected, value);
            this.RaisePropertyChanged(nameof(HasNavigation));
            this.RaisePropertyChanged(nameof(IsPaneOpen));
        }
    }

    /// <summary>Working without a device: saved cards, dictionaries and app settings only.</summary>
    public bool IsOffline
    {
        get => _isOffline;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isOffline, value);
            this.RaisePropertyChanged(nameof(HasNavigation));
            this.RaisePropertyChanged(nameof(IsPaneOpen));
        }
    }

    /// <summary>The navigation pane is shown when connected or in offline mode.</summary>
    public bool HasNavigation => _isConnected || _isOffline;

    /// <summary>Stays closed while there is nothing to navigate, so no empty strip shows beside Connect.</summary>
    public bool IsPaneOpen
    {
        get => _isPaneOpen && HasNavigation;
        set
        {
            if (_isPaneOpen == value) return;
            _isPaneOpen = value;
            this.RaisePropertyChanged();
        }
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
    private PageViewModelBase StartPage => IsConnected ? Dashboard : IsOffline ? Cards : _connect;

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

    private async Task NavigateAsync(PageViewModelBase page)
    {
        if (page is null) return;
        var leaving = CurrentPage;
        CurrentPage = page;
        if (IsCompact) IsPaneOpen = false;
        if (!ReferenceEquals(leaving, page)) await leaving.OnDeactivatedAsync();
        await page.OnActivatedAsync();
    }

    private async Task OpenCardAsync(string id)
    {
        await NavigateAsync(Cards);
        Cards.OpenCard(id);
    }

    private Task DisconnectAsync() => _device.DisconnectAsync();

    private void SetNavPages(IReadOnlyList<PageViewModelBase> pages)
    {
        NavPages.Clear();
        foreach (var p in pages) NavPages.Add(p);
    }

    private async Task EnterOfflineAsync()
    {
        if (IsConnected) return;
        if (!IsOffline)
        {
            IsOffline = true;
            SetNavPages(_offlinePages);
            DeviceSummary = "Offline mode";
        }
        if (!IsCompact) IsPaneOpen = true;
        await NavigateAsync(Cards);
    }

    private void OnStateChanged(ConnectionState state)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            IsConnected = state == ConnectionState.Connected;
            // Let the page being left undo anything it set up on the device before the mode switches.
            await CurrentPage.OnDeactivatedAsync();
            if (state == ConnectionState.Connected)
            {
                _connect.StopWatching();
                IsOffline = false;
                SetNavPages(_connectedPages);
                var info = _device.DeviceInfo;
                DeviceSummary = info is null
                    ? "Connected"
                    : $"{info.ModelName} · fw {info.FirmwareVersion}";
                CurrentPage = Dashboard;
                if (!IsCompact) IsPaneOpen = true;
                _ = Firmware.OnConnectedAsync();
                await Dashboard.OnActivatedAsync();
            }
            else
            {
                DeviceSummary = IsOffline ? "Offline mode" : "Not connected";
                if (!IsOffline) SetNavPages(_connectedPages);
                CurrentPage = _connect;
                if (state == ConnectionState.Disconnected && !Firmware.IsInstalling) _connect.StartWatching();
            }
        });
    }
}
