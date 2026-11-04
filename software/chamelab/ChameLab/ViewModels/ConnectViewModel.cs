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

    public ConnectViewModel(DeviceService device) : base(() => Strings.Connect_Connect, "link")
    {
        _device = device;

        ScanCommand = ReactiveCommand.CreateFromTask(ScanAsync);
        PickCommand = ReactiveCommand.CreateFromTask(PickAsync);
        ConnectCommand = ReactiveCommand.CreateFromTask(ConnectAsync,
            this.WhenAnyValue(x => x.Selected).Select(s => s is not null));
        ConnectManualCommand = ReactiveCommand.CreateFromTask(ConnectManualAsync,
            this.WhenAnyValue(x => x.ManualEndpoint).Select(e => !string.IsNullOrWhiteSpace(e)));
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

    public ICommand ScanCommand { get; }
    public ICommand PickCommand { get; }
    public ICommand ConnectCommand { get; }
    public ICommand ConnectManualCommand { get; }

    /// <summary>How often cheap scanners (USB serial) are re-run to catch a device being plugged in.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private DispatcherTimer? _pollTimer;
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
}
