using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using ChameLab.Localization;
using ChameLab.Services;
using ChameLab.Sync;
using ReactiveUI;

namespace ChameLab.ViewModels;

/// <summary>A device in the sync devices list.</summary>
public sealed class SyncMemberViewModel(SyncMember member, ICommand remove)
{
    public string Id => member.Id;
    public string Name => member.Name;
    public bool IsThis => member.IsThis;
    public bool CanRemove => !member.IsThis;
    public ICommand RemoveCommand => remove;

    /// <summary>"This device", "Connected · local network", "Connected · internet", "Last seen 5 min ago", "Not seen yet".</summary>
    public string Status =>
        member.IsThis ? Strings.Sync_ThisDevice
        : member.IsOnline ? (member.Link == "lan" ? Strings.Sync_ConnectedLan : Strings.Sync_ConnectedRelay)
        : member.LastSeen is { } seen ? string.Format(Strings.Sync_LastSeen, Ago(seen))
        : Strings.Sync_NotSeenYet;

    public bool IsOnline => member.IsOnline && !member.IsThis;

    private static string Ago(DateTimeOffset time)
    {
        var span = DateTimeOffset.UtcNow - time;
        if (span < TimeSpan.FromMinutes(1)) return Strings.Sync_JustNow;
        if (span < TimeSpan.FromHours(1)) return string.Format(Strings.Sync_MinutesAgo, (int)span.TotalMinutes);
        if (span < TimeSpan.FromDays(1)) return string.Format(Strings.Sync_HoursAgo, (int)span.TotalHours);
        return time.ToLocalTime().ToString("d", CultureInfo.CurrentCulture);
    }
}

/// <summary>A deleted card or dictionary in the sync card, with a command to bring it back.</summary>
public sealed record SyncDeletedViewModel(string Name, string When, ICommand RestoreCommand);

/// <summary>
/// The Device sync card in Settings: starting or joining a group, adding and removing devices,
/// the status, deleted items and (desktop) the background node.
/// </summary>
public sealed class SyncViewModel : ViewModelBase
{
    private readonly SyncService _sync;
    private readonly Action<string> _error;
    private readonly Action<string> _notice;
    private CancellationTokenSource? _joinCts;
    private CancellationTokenSource? _inviteCts;
    private SyncService.Invitation? _invitation;
    private TaskCompletionSource<bool>? _decision;

    public SyncViewModel(SyncService sync, Action<string> error, Action<string> notice)
    {
        _sync = sync;
        _error = error;
        _notice = notice;
        DeviceName = sync.DeviceName ?? SyncService.DefaultDeviceName;
        CreateCommand = ReactiveCommand.Create(Create);
        ShowJoinCommand = ReactiveCommand.Create(() => { IsJoinOpen = !IsJoinOpen; });
        JoinCommand = ReactiveCommand.CreateFromTask(() => JoinAsync(JoinCode),
            this.WhenAnyValue(x => x.JoinCode, x => x.IsJoining, (code, joining) => !joining && !string.IsNullOrWhiteSpace(code)));
        ScanJoinCommand = ReactiveCommand.CreateFromTask(ScanJoinAsync, this.WhenAnyValue(x => x.IsJoining, j => !j));
        CancelJoinCommand = ReactiveCommand.Create(() => _joinCts?.Cancel());
        AddDeviceCommand = ReactiveCommand.CreateFromTask(InviteAsync, this.WhenAnyValue(x => x.IsInviting, i => !i));
        CancelInviteCommand = ReactiveCommand.Create(CancelInvite);
        AcceptCommand = ReactiveCommand.Create(() => _decision?.TrySetResult(true));
        DeclineCommand = ReactiveCommand.Create(() => _decision?.TrySetResult(false));
        RenameCommand = ReactiveCommand.Create(() => _sync.Rename(DeviceName));
        LeaveCommand = ReactiveCommand.CreateFromTask(LeaveAsync);
        _sync.Changed += Refresh;
        Refresh();
    }

    public bool IsEnabled => _sync.IsEnabled;
    public bool IsOff => !_sync.IsEnabled;

    /// <summary>The background node: only the desktop app runs one.</summary>
    public static bool IsDesktop => !OperatingSystem.IsAndroid() && !OperatingSystem.IsIOS() && !OperatingSystem.IsBrowser();

    public string StatusText => _sync.Status switch
    {
        SyncStatus.Paused => Strings.Sync_StatusPaused,
        SyncStatus.Connecting => Strings.Sync_StatusConnecting,
        SyncStatus.Online => _sync.ConnectedCount == 0
            ? Strings.Sync_StatusOnlineAlone
            : Loc.Plural(Strings.Plural_SyncDevicesConnected, _sync.ConnectedCount),
        SyncStatus.Offline => _sync.ConnectedCount == 0
            ? Strings.Sync_StatusOffline
            : Loc.Plural(Strings.Plural_SyncDevicesConnected, _sync.ConnectedCount),
        _ => string.Empty,
    };

    public bool IsOnline => _sync.Status == SyncStatus.Online || _sync.ConnectedCount > 0;

    public ObservableCollection<SyncMemberViewModel> Members { get; } = new();
    public ObservableCollection<SyncDeletedViewModel> DeletedCards { get; } = new();
    public ObservableCollection<SyncDeletedViewModel> DeletedDictionaries { get; } = new();

    public bool HasDeleted => DeletedCards.Count > 0 || DeletedDictionaries.Count > 0;

    public string DeviceName
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    private void Refresh() => Dispatcher.UIThread.Post(() =>
    {
        Members.Clear();
        var remove = ReactiveCommand.Create<string>(id =>
        {
            _sync.Remove(id);
            _notice(Strings.Sync_DeviceRemoved);
        });
        foreach (var member in _sync.Members) Members.Add(new SyncMemberViewModel(member, remove));
        Fill(DeletedCards, EntityTypes.Card);
        Fill(DeletedDictionaries, EntityTypes.Dictionary);
        if (_sync.DeviceName is { } name && !string.Equals(name, DeviceName, StringComparison.Ordinal)) DeviceName = name;
        this.RaisePropertyChanged(nameof(IsEnabled));
        this.RaisePropertyChanged(nameof(IsOff));
        this.RaisePropertyChanged(nameof(StatusText));
        this.RaisePropertyChanged(nameof(IsOnline));
        this.RaisePropertyChanged(nameof(HasDeleted));
    });

    private void Fill(ObservableCollection<SyncDeletedViewModel> list, string type)
    {
        list.Clear();
        foreach (var item in _sync.Deleted(type).Take(20))
        {
            var id = item.Id;
            list.Add(new SyncDeletedViewModel(
                string.IsNullOrWhiteSpace(item.Name) ? Strings.Common_Untitled : item.Name,
                item.Deleted.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
                ReactiveCommand.Create(() => _sync.Undelete(type, id))));
        }
    }

    // ---- Starting a group ----------------------------------------------------

    public ICommand CreateCommand { get; }

    private void Create()
    {
        try
        {
            _sync.CreateGroup(DeviceName);
            _notice(Strings.Sync_GroupCreated);
        }
        catch (InvalidOperationException ex) { _error(ex.Message); }
        Refresh();
    }

    // ---- Joining -------------------------------------------------------------

    public ICommand ShowJoinCommand { get; }
    public ICommand JoinCommand { get; }
    public ICommand ScanJoinCommand { get; }
    public ICommand CancelJoinCommand { get; }

    public bool IsJoinOpen
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public string? JoinCode
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public bool IsJoining
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>What joining is doing now, or the digits to compare.</summary>
    public string? JoinStatus
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public bool HasQrCamera => AppServices.QrCamera is not null;

    private async Task ScanJoinAsync()
    {
        if (AppServices.QrCamera is not { } camera) return;
        string? scanned = null;
        try
        {
            await camera.ScanAsync(Strings.Sync_ScanTheOtherDevice, text =>
            {
                if (PairingCode.Parse(text) is null) return new QrScanStep(false, Strings.Sync_NotASyncQr);
                scanned = text;
                return new QrScanStep(true, Strings.Sync_Connecting);
            });
        }
        catch (QrCameraUnavailableException ex)
        {
            _error(ex.Message);
            return;
        }
        if (scanned is not null) await JoinAsync(scanned);
    }

    private async Task JoinAsync(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return;
        _joinCts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        IsJoining = true;
        JoinStatus = Strings.Sync_Connecting;
        try
        {
            await _sync.JoinAsync(code, DeviceName,
                digits => Dispatcher.UIThread.Post(() => JoinStatus = string.Format(Strings.Sync_CompareDigitsJoin, digits)),
                _joinCts.Token);
            IsJoinOpen = false;
            JoinCode = null;
            _notice(Strings.Sync_Joined);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is FormatException or System.IO.IOException or InvalidOperationException
                                       or System.Security.Cryptography.CryptographicException
                                       or System.Net.WebSockets.WebSocketException or TimeoutException)
        {
            _error(ex.Message);
        }
        finally
        {
            IsJoining = false;
            JoinStatus = null;
            Refresh();
        }
    }

    // ---- Adding a device -----------------------------------------------------

    public ICommand AddDeviceCommand { get; }
    public ICommand CancelInviteCommand { get; }
    public ICommand AcceptCommand { get; }
    public ICommand DeclineCommand { get; }

    public bool IsInviting
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public string? InviteQr
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public string? InviteCode
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public string? InviteStatus
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>A device asked to join and waits for Add or Decline.</summary>
    public bool IsDeciding
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    private async Task InviteAsync()
    {
        _inviteCts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var ct = _inviteCts.Token;
        IsInviting = true;
        InviteStatus = Strings.Sync_Connecting;
        try
        {
            await using var invitation = await _sync.InviteAsync(ct);
            _invitation = invitation;
            InviteQr = invitation.Qr;
            InviteCode = invitation.TypedCode;
            InviteStatus = Strings.Sync_InviteWaiting;
            var request = await invitation.WaitAsync(ct);
            _decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            InviteStatus = request.Digits is { } digits
                ? string.Format(Strings.Sync_CompareDigitsInvite, request.Name, digits)
                : string.Format(Strings.Sync_DeviceWantsToJoin, request.Name);
            IsDeciding = true;
            var accept = await _decision.Task.WaitAsync(ct);
            IsDeciding = false;
            if (accept)
            {
                await invitation.AcceptAsync(ct);
                _notice(string.Format(Strings.Sync_DeviceAdded, request.Name));
            }
            else
            {
                await invitation.RejectAsync();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is System.IO.IOException or InvalidOperationException
                                       or System.Security.Cryptography.CryptographicException
                                       or System.Net.WebSockets.WebSocketException)
        {
            _error(ex.Message);
        }
        finally
        {
            _invitation = null;
            IsInviting = false;
            IsDeciding = false;
            InviteQr = null;
            InviteCode = null;
            InviteStatus = null;
            Refresh();
        }
    }

    private void CancelInvite()
    {
        _decision?.TrySetResult(false);
        _inviteCts?.Cancel();
    }

    // ---- This device -------------------------------------------------------------

    public ICommand RenameCommand { get; }
    public ICommand LeaveCommand { get; }

    private async Task LeaveAsync()
    {
        await _sync.LeaveAsync();
        _notice(Strings.Sync_Left);
        Refresh();
    }

    /// <summary>Keep syncing with the window closed (tray icon); desktop only.</summary>
    public bool RunInBackground
    {
        get => SyncService.RunInBackground;
        set
        {
            if (value == SyncService.RunInBackground) return;
            SyncService.RunInBackground = value;
            this.RaisePropertyChanged();
            BackgroundChanged?.Invoke();
        }
    }

    /// <summary>Start ChameLab (in the background) when the user signs in; desktop only.</summary>
    public bool StartWithSystem
    {
        get => Autostart.IsEnabled;
        set
        {
            if (value == Autostart.IsEnabled) return;
            try { Autostart.IsEnabled = value; }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.IO.IOException or System.Security.SecurityException)
            {
                _error(ex.Message);
            }
            this.RaisePropertyChanged();
        }
    }

    /// <summary>The app reacts (shows or hides the tray icon).</summary>
    public static event Action? BackgroundChanged;
}
