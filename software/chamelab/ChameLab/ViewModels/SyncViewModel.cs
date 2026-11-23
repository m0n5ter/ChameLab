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
public sealed class SyncMemberViewModel(SyncMember member, SyncStatus localStatus, int connectedCount, ICommand askRemove)
{
    public string Id => member.Id;
    public string Name => member.Name;
    public bool IsThis => member.IsThis;
    public bool CanRemove => !member.IsThis;
    public ICommand AskRemoveCommand => askRemove;

    /// <summary>Key for IconGeometryConverter matching the device's OS/platform.</summary>
    public string PlatformIconKey => member.Platform switch
    {
        "windows" => "deviceWindows",
        "linux" => "deviceLinux",
        "macos" => "deviceMacos",
        "android" => "deviceAndroid",
        "ios" => "deviceIos",
        "browser" => "web",
        _ => "device",
    };

    /// <summary>"This device", "Connected · local network", "Connected · internet", "Last seen 5 min ago", "Not seen yet".</summary>
    public string Status =>
        member.IsThis ? Strings.Sync_ThisDevice
        : member.IsOnline ? (member.Link == "lan" ? Strings.Sync_ConnectedLan : Strings.Sync_ConnectedRelay)
        : member.LastSeen is { } seen ? string.Format(Strings.Sync_LastSeen, Ago(seen))
        : Strings.Sync_NotSeenYet;

    public bool IsOnline => member.IsOnline && !member.IsThis;

    public bool IsStateOnline => member.IsThis ? (localStatus == SyncStatus.Online || connectedCount > 0) : member.IsOnline;
    public bool IsStateConnecting => member.IsThis && localStatus == SyncStatus.Connecting && connectedCount == 0;
    public bool IsStateOffline => !IsStateOnline && !IsStateConnecting;

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
    private string? _lastSyncedDeviceName;

    public SyncViewModel(SyncService sync, Action<string> error, Action<string> notice)
    {
        _sync = sync;
        _error = error;
        _notice = notice;
        _lastSyncedDeviceName = sync.DeviceName;
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
        AskRemoveCommand = ReactiveCommand.Create<SyncMemberViewModel>(AskRemove);
        ConfirmRemoveCommand = ReactiveCommand.Create(ConfirmRemove);
        CancelRemoveCommand = ReactiveCommand.Create(() => { MemberToRemove = null; });
        AskLeaveCommand = ReactiveCommand.Create(() => { MemberToRemove = null; IsConfirmingLeave = true; });
        ConfirmLeaveCommand = ReactiveCommand.CreateFromTask(ConfirmLeaveAsync);
        CancelLeaveCommand = ReactiveCommand.Create(() => { IsConfirmingLeave = false; });
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
        foreach (var member in _sync.Members)
            Members.Add(new SyncMemberViewModel(member, _sync.Status, _sync.ConnectedCount, AskRemoveCommand));
        Fill(DeletedCards, EntityTypes.Card);
        Fill(DeletedDictionaries, EntityTypes.Dictionary);
        if (_sync.DeviceName is { } name)
        {
            if (!string.Equals(name, _lastSyncedDeviceName, StringComparison.Ordinal))
            {
                _lastSyncedDeviceName = name;
                if (!string.Equals(name, DeviceName, StringComparison.Ordinal)) DeviceName = name;
            }
        }
        else
        {
            _lastSyncedDeviceName = null;
        }
        if (MemberToRemove is { } pending && _sync.Members.All(m => m.Id != pending.Id)) MemberToRemove = null;
        if (!IsEnabled && IsConfirmingLeave) IsConfirmingLeave = false;
        if (!IsEnabled && IsInviting) CancelInvite();
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
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(IsDialogOpen));
            this.RaisePropertyChanged(nameof(IsInviteLoading));
            this.RaisePropertyChanged(nameof(ShowInviteCodeSlot));
        }
    }

    public string? InviteQr
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(IsInviteLoading));
            this.RaisePropertyChanged(nameof(ShowInviteCodeSlot));
        }
    }

    public string? InviteCode
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(ShowInviteCode));
            this.RaisePropertyChanged(nameof(ShowInviteCodeSlot));
        }
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
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(IsInviteLoading));
            this.RaisePropertyChanged(nameof(ShowInviteCode));
            this.RaisePropertyChanged(nameof(ShowInviteCodeSlot));
        }
    }

    public bool IsInviteLoading => IsInviting && !IsDeciding && InviteQr is null;
    public bool ShowInviteCode => !IsDeciding && InviteCode is not null;
    public bool ShowInviteCodeSlot => !IsDeciding && (InviteCode is not null || IsInviteLoading);

    private async Task InviteAsync()
    {
        _inviteCts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var ct = _inviteCts.Token;
        MemberToRemove = null;
        IsConfirmingLeave = false;
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

    public ICommand AskRemoveCommand { get; }
    public ICommand ConfirmRemoveCommand { get; }
    public ICommand CancelRemoveCommand { get; }
    public ICommand AskLeaveCommand { get; }
    public ICommand ConfirmLeaveCommand { get; }
    public ICommand CancelLeaveCommand { get; }

    /// <summary>The peer device pending removal confirmation; null when closed.</summary>
    public SyncMemberViewModel? MemberToRemove
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(IsConfirmingRemove));
            this.RaisePropertyChanged(nameof(RemoveDevicePrompt));
            this.RaisePropertyChanged(nameof(IsConfirming));
            this.RaisePropertyChanged(nameof(IsDialogOpen));
        }
    }

    public bool IsConfirmingRemove => MemberToRemove is not null;

    public string RemoveDevicePrompt =>
        MemberToRemove is { } m ? string.Format(Strings.Sync_RemoveDevicePrompt, m.Name) : string.Empty;

    public bool IsConfirmingLeave
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(IsConfirming));
            this.RaisePropertyChanged(nameof(IsDialogOpen));
        }
    }

    public bool IsConfirming => IsConfirmingRemove || IsConfirmingLeave;

    public bool IsDialogOpen => IsInviting || IsConfirming;

    public void CommitDeviceName()
    {
        var sanitized = SyncService.SanitizeDeviceName(DeviceName);
        if (!IsEnabled)
        {
            var fallback = SyncService.SanitizeDeviceName(SyncService.DefaultDeviceName);
            DeviceName = sanitized.Length > 0 ? sanitized : (fallback.Length > 0 ? fallback : "ChameLab");
            return;
        }
        var current = _sync.DeviceName ?? SyncService.DefaultDeviceName;
        if (sanitized.Length == 0)
        {
            DeviceName = current;
            return;
        }
        if (!string.Equals(DeviceName, sanitized, StringComparison.Ordinal)) DeviceName = sanitized;
        if (string.Equals(sanitized, current, StringComparison.Ordinal)) return;
        _lastSyncedDeviceName = sanitized;
        _sync.Rename(sanitized);
    }

    public void RevertDeviceName()
    {
        if (IsEnabled) DeviceName = _sync.DeviceName ?? SyncService.DefaultDeviceName;
    }

    private void AskRemove(SyncMemberViewModel? member)
    {
        if (member is null || !member.CanRemove) return;
        IsConfirmingLeave = false;
        MemberToRemove = member;
    }

    private void ConfirmRemove()
    {
        var member = MemberToRemove;
        MemberToRemove = null;
        if (member is null || _sync.Members.All(m => m.Id != member.Id)) return;
        _sync.Remove(member.Id);
        _notice(Strings.Sync_DeviceRemoved);
    }

    private async Task ConfirmLeaveAsync()
    {
        IsConfirmingLeave = false;
        await LeaveAsync();
    }

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
