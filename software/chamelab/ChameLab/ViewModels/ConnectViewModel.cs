using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using System.Reactive.Linq;
using System.Threading.Tasks;
using ChameLab.Services;
using ChameLab.Transport;
using ReactiveUI;
using ChameLab.Localization;

namespace ChameLab.ViewModels;

public sealed class ConnectViewModel : PageViewModelBase
{
    private readonly DeviceService _device;
    private readonly DeviceSharingService _sharing;

    public ConnectViewModel(DeviceService device, DeviceSharingService sharing) : base(() => Strings.Connect_Connect, "link")
    {
        _device = device;
        _sharing = sharing;
        _sharing.Changed += () => Dispatcher.UIThread.Post(OnSharingChanged);

        // While a device is shared, the share holds its port: nothing connects locally then.
        ScanCommand = ReactiveCommand.CreateFromTask(ScanAsync);
        PickCommand = ReactiveCommand.CreateFromTask(PickAsync);
        ConnectCommand = ReactiveCommand.CreateFromTask(ConnectAsync,
            this.WhenAnyValue(x => x.Selected, x => x.IsSharing, (s, sharing) => s is not null && !sharing));
        ConnectManualCommand = ReactiveCommand.CreateFromTask(ConnectManualAsync,
            this.WhenAnyValue(x => x.ManualEndpoint, x => x.IsSharing,
                (e, sharing) => !string.IsNullOrWhiteSpace(e) && !sharing));
        ConnectSharedCommand = ReactiveCommand.CreateFromTask(ConnectSharedAsync,
            this.WhenAnyValue(x => x.SharedCode, x => x.IsSharing,
                (code, sharing) => !string.IsNullOrWhiteSpace(code) && !sharing));
        ShareCommand = ReactiveCommand.CreateFromTask(ShareAsync,
            this.WhenAnyValue(x => x.Selected, x => x.IsSharing,
                (s, sharing) => s is { Kind: not TransportKind.Remote } && !sharing));
        StopSharingCommand = ReactiveCommand.CreateFromTask(StopSharingAsync,
            this.WhenAnyValue(x => x.IsSharing));
    }

    public ObservableCollection<TransportCandidate> Candidates { get; } = new();

    public TransportCandidate? Selected
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public bool HasCandidates
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    private bool HasUsb => _device.Scanners.Any(s => s.Kind == TransportKind.Serial);
    private bool HasBle => _device.Scanners.Any(s => s.Kind == TransportKind.Ble);

    /// <summary>A scanner whose new devices come from a browser chooser, behind the Pair over Bluetooth button.</summary>
    private ITransportPicker? Picker => _device.Scanners.OfType<ITransportPicker>().FirstOrDefault();

    public bool CanPick => Picker is not null;

    /// <summary>The transport the empty state pictures: Bluetooth where it's the only way in, else USB.</summary>
    public TransportKind PrimaryKind => HasBle && !HasUsb ? TransportKind.Ble : TransportKind.Serial;

    // The device wakes by itself when plugged in over USB; over BLE it only advertises while awake.
    public string IntroHint => (HasUsb, HasBle) switch
    {
        (true, true) => Strings.Connect_IntroUsbBle,
        (false, true) => Strings.Connect_IntroBle,
        _ => Strings.Connect_IntroUsb,
    };

    // A web page may only open a serial port or a Bluetooth device the user picked in the
    // browser's chooser, which opens on Rescan (USB) or Pair over Bluetooth; after that the device shows
    // up on its own like on desktop.
    public string ScanHint => OperatingSystem.IsBrowser()
        ? CanPick
            ? Strings.Connect_WebPickOnceBle
            : Strings.Connect_WebPickOnce
        : HasBle && !HasUsb
            ? Strings.Connect_NearbyAuto
            : Strings.Connect_PluggedAuto;

    public string EmptyHint => OperatingSystem.IsBrowser()
        ? CanPick
            ? Strings.Connect_WebIntroBle
            : Strings.Connect_WebIntro
        : (HasUsb, HasBle) switch
        {
            (true, true) => Strings.Connect_HeroUsbBle,
            (false, true) => Strings.Connect_HeroBle,
            _ => Strings.Connect_HeroUsb,
        };

    public string ManualEndpoint
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "127.0.0.1:4321";

    /// <summary>The code someone gave to connect to the device they share.</summary>
    public string SharedCode
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    /// <summary>
    /// Relays use self-signed certificates that the app pins; a web page can't, so the browser
    /// build can't reach them.
    /// </summary>
    public bool CanJoinShared => !OperatingSystem.IsBrowser();

    /// <summary>The join tile, hidden like the other tiles while this app shares its own device.</summary>
    public bool ShowJoinShared => CanJoinShared && !IsSharing;

    /// <summary>The Share tile; while sharing, the sharing card takes its place.</summary>
    public bool ShowShareTile => CanShare && !IsSharing;

    /// <summary>Sharing needs a device on this computer; only the desktop app offers it.</summary>
    public bool CanShare => !OperatingSystem.IsBrowser() && !OperatingSystem.IsAndroid() && !OperatingSystem.IsIOS();

    public bool IsSharing
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(ShowJoinShared));
            this.RaisePropertyChanged(nameof(ShowShareTile));
        }
    }

    /// <summary>The code to pass on, once the relay has handed it out.</summary>
    public string? ShareCode
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>The Chameleon being shared, shown on the sharing card.</summary>
    public TransportCandidate? SharedDevice
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>What the other side sent to the device, e.g. "12.4 KB".</summary>
    public string TrafficToDevice
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = FormatBytes(0);

    /// <summary>What the device sent back.</summary>
    public string TrafficFromDevice
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = FormatBytes(0);

    public bool IsPeerConnected
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public string? ShareStatus
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public ICommand ConnectSharedCommand { get; }
    public ICommand ShareCommand { get; }
    public ICommand StopSharingCommand { get; }

    public ICommand ScanCommand { get; }
    public ICommand PickCommand { get; }
    public ICommand ConnectCommand { get; }
    public ICommand ConnectManualCommand { get; }

    /// <summary>How often cheap scanners (USB serial) are re-run to catch a device being plugged in.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private DispatcherTimer? _pollTimer;
    private DispatcherTimer? _trafficTimer;
    private bool _autoScanning;

    /// <summary>
    /// Called while this page is shown and nothing is connected: scans right away, then keeps
    /// polling the cheap scanners and runs the watchers so a device appears without pressing Rescan.
    /// </summary>
    public void StartWatching()
    {
        if (_pollTimer is not null) return;
        _ = AutoScanAsync(AutoScan.OnOpen);
        _pollTimer = new DispatcherTimer { Interval = PollInterval };
        _pollTimer.Tick += (_, _) => _ = AutoScanAsync(AutoScan.Poll);
        _pollTimer.Start();
        StartWatchers();
    }

    public void StopWatching()
    {
        _pollTimer?.Stop();
        _pollTimer = null;
        StopWatchers();
    }

    private void StartWatchers()
    {
        foreach (var scanner in _device.Scanners)
        {
            if (scanner is not ITransportWatcher watcher) continue;
            var kinds = new HashSet<TransportKind> { scanner.Kind };
            watcher.StartWatching(found => Dispatcher.UIThread.Post(() =>
            {
                if (_pollTimer is not null) Apply(found, kinds);
            }));
        }
    }

    private void StopWatchers()
    {
        foreach (var watcher in _device.Scanners.OfType<ITransportWatcher>())
            watcher.StopWatching();
    }

    /// <summary>Silent background scan: no busy bar, no notice, errors ignored.</summary>
    private async Task AutoScanAsync(AutoScan trigger)
    {
        if (_autoScanning || IsBusy) return;
        _autoScanning = true;
        try
        {
            var found = await Task.Run(() => _device.ScanAsync(trigger));
            Apply(found, ScannedKinds(trigger));
        }
        catch { /* next tick retries */ }
        finally { _autoScanning = false; }
    }

    private Task ScanAsync() => RunAsync(async () =>
    {
        var found = await _device.ScanAsync();
        Dispatcher.UIThread.Post(() => Apply(found, ScannedKinds(AutoScan.Never)));
        Notify(found.Count == 0
            ? Strings.Connect_NoneFound
            : Loc.Plural(Strings.Plural_FoundDevices, found.Count));
    });

    /// <summary>Opens the browser's Bluetooth chooser and selects the picked device, ready to connect.</summary>
    private Task PickAsync() => RunAsync(async () =>
    {
        if (Picker is not { } picker) return;
        var picked = await picker.PickAsync();
        if (picked is null) return;
        var scanner = (ITransportScanner)picker;
        var known = await scanner.ScanAsync(AutoScan.Poll);
        Dispatcher.UIThread.Post(() =>
        {
            Apply(known, new HashSet<TransportKind> { scanner.Kind });
            Selected = Candidates.FirstOrDefault(c => c.Kind == picked.Kind && c.Id == picked.Id) ?? Selected;
        });
        Notify(string.Format(Strings.Connect_SelectedPressConnect, picked.DisplayName));
    });

    private HashSet<TransportKind> ScannedKinds(AutoScan trigger) =>
        _device.Scanners.Where(s => DeviceService.RunsOn(s, trigger)).Select(s => s.Kind).ToHashSet();

    /// <summary>
    /// Replaces the candidates of the transport kinds that were just scanned, keeping the others
    /// (a quick serial poll mustn't drop BLE devices found earlier) and the current selection.
    /// </summary>
    private void Apply(IReadOnlyList<TransportCandidate> found, HashSet<TransportKind> scannedKinds)
    {
        var next = Candidates.Where(c => !scannedKinds.Contains(c.Kind)).Concat(found).ToList();
        if (next.SequenceEqual(Candidates)) return;

        // Match by id: a rescanned device comes back as a new record (e.g. with a fresh RSSI).
        var keep = Selected;
        Candidates.Clear();
        foreach (var c in next) Candidates.Add(c);
        Selected = Candidates.FirstOrDefault(c => keep is not null && c.Kind == keep.Kind && c.Id == keep.Id)
                   ?? (Candidates.Count > 0 ? Candidates[0] : null);
        HasCandidates = Candidates.Count > 0;
    }

    private Task ConnectAsync() => RunAsync(async () =>
    {
        if (Selected is null) { ReportError(Strings.Connect_SelectFirst); return; }
        // A BLE connection attempt made while the adapter is scanning may stall or fail.
        StopWatchers();
        try { await _device.ConnectAsync(Selected); }
        finally
        {
            if (_pollTimer is not null && _device.State != ConnectionState.Connected) StartWatchers();
        }
    });

    private Task ConnectManualAsync() => RunAsync(async () =>
    {
        var candidate = TcpScanner.Candidate(ManualEndpoint);
        await _device.ConnectAsync(candidate);
    });

    private Task ConnectSharedAsync() => RunAsync(async () =>
    {
        var candidate = RelayScanner.Candidate(SharedCode);
        await _device.ConnectAsync(candidate);
    });

    private Task ShareAsync() => RunAsync(async () =>
    {
        if (Selected is null) { ReportError(Strings.Connect_SelectFirst); return; }
        await _sharing.StartAsync(Selected);
    });

    private Task StopSharingAsync() => RunAsync(() => _sharing.StopAsync());

    private void OnSharingChanged()
    {
        var state = _sharing.State;
        IsSharing = state != SharingState.Idle;
        ShareCode = _sharing.Code;
        if (_sharing.Candidate is { } shared) SharedDevice = shared;
        UpdateTraffic();
        if (IsSharing && _trafficTimer is null)
        {
            _trafficTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _trafficTimer.Tick += (_, _) => UpdateTraffic();
            _trafficTimer.Start();
        }
        else if (!IsSharing && _trafficTimer is not null)
        {
            _trafficTimer.Stop();
            _trafficTimer = null;
        }
        IsPeerConnected = state == SharingState.PeerConnected;
        ShareStatus = state switch
        {
            SharingState.Starting => Strings.Share_Starting,
            SharingState.WaitingForPeer => Strings.Share_Waiting,
            SharingState.PeerConnected => _sharing.PeerName is { } peer
                ? string.Format(Strings.Share_PeerConnectedName, peer)
                : Strings.Share_PeerConnected,
            _ => null,
        };
        if (state == SharingState.Idle && _sharing.Error is { } error) ReportError(error);
    }

    private void UpdateTraffic()
    {
        TrafficToDevice = FormatBytes(_sharing.BytesToDevice);
        TrafficFromDevice = FormatBytes(_sharing.BytesFromDevice);
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => Loc.Plural(Strings.Plural_Bytes, (int)bytes),
        < 1024 * 1024 => string.Format(Strings.Common_SizeKilobytes, (bytes / 1024.0).ToString("0.0")),
        _ => string.Format(Strings.Common_SizeMegabytes, (bytes / (1024.0 * 1024)).ToString("0.0")),
    };
}
