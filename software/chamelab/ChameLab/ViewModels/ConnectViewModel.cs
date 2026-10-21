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

namespace ChameLab.ViewModels;

public sealed class ConnectViewModel : PageViewModelBase
{
    private readonly DeviceService _device;
    private TransportCandidate? _selected;
    private string _manualEndpoint = "127.0.0.1:4321";

    public ConnectViewModel(DeviceService device) : base("Connect", "link")
    {
        _device = device;

        ScanCommand = ReactiveCommand.CreateFromTask(ScanAsync);
        ConnectCommand = ReactiveCommand.CreateFromTask(ConnectAsync,
            this.WhenAnyValue(x => x.Selected).Select(s => s is not null));
        ConnectManualCommand = ReactiveCommand.CreateFromTask(ConnectManualAsync,
            this.WhenAnyValue(x => x.ManualEndpoint).Select(e => !string.IsNullOrWhiteSpace(e)));
        ContinueOfflineCommand = ReactiveCommand.Create(() => OfflineRequested?.Invoke());
    }

    /// <summary>Raised when the user chooses to work without a device.</summary>
    public event Action? OfflineRequested;

    public ObservableCollection<TransportCandidate> Candidates { get; } = new();

    public TransportCandidate? Selected
    {
        get => _selected;
        set => this.RaiseAndSetIfChanged(ref _selected, value);
    }

    private bool _hasCandidates;

    public bool HasCandidates
    {
        get => _hasCandidates;
        private set => this.RaiseAndSetIfChanged(ref _hasCandidates, value);
    }

    public string ManualEndpoint
    {
        get => _manualEndpoint;
        set => this.RaiseAndSetIfChanged(ref _manualEndpoint, value);
    }

    public ICommand ScanCommand { get; }
    public ICommand ConnectCommand { get; }
    public ICommand ConnectManualCommand { get; }
    public ICommand ContinueOfflineCommand { get; }

    /// <summary>How often cheap scanners (USB serial) are re-run to catch a device being plugged in.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private DispatcherTimer? _pollTimer;
    private bool _autoScanning;

    /// <summary>
    /// Called while this page is shown and nothing is connected: scans right away, then keeps
    /// polling the cheap scanners so a device appears without pressing Rescan.
    /// </summary>
    public void StartWatching()
    {
        if (_pollTimer is not null) return;
        _ = AutoScanAsync(AutoScan.OnOpen);
        _pollTimer = new DispatcherTimer { Interval = PollInterval };
        _pollTimer.Tick += (_, _) => _ = AutoScanAsync(AutoScan.Poll);
        _pollTimer.Start();
    }

    public void StopWatching()
    {
        _pollTimer?.Stop();
        _pollTimer = null;
    }

    /// <summary>Silent background scan: no busy bar, no status toast, errors ignored.</summary>
    private async Task AutoScanAsync(AutoScan trigger)
    {
        if (_autoScanning || IsBusy) return;
        _autoScanning = true;
        try
        {
            var found = await Task.Run(() => _device.ScanAsync(trigger));
            Apply(found, trigger);
        }
        catch { /* next tick retries */ }
        finally { _autoScanning = false; }
    }

    private Task ScanAsync() => RunAsync(async () =>
    {
        var found = await _device.ScanAsync();
        Dispatcher.UIThread.Post(() => Apply(found, AutoScan.Never));
        SetStatus(found.Count == 0
            ? "No Chameleon found. Plug it in, or use a network bridge."
            : $"Found {found.Count} device(s).");
    });

    /// <summary>
    /// Replaces the candidates of the transport kinds that were just scanned, keeping the others
    /// (a quick serial poll mustn't drop BLE devices found earlier) and the current selection.
    /// </summary>
    private void Apply(IReadOnlyList<TransportCandidate> found, AutoScan trigger)
    {
        var scannedKinds = _device.Scanners.Where(s => s.AutoScan >= trigger).Select(s => s.Kind).ToHashSet();
        var next = Candidates.Where(c => !scannedKinds.Contains(c.Kind)).Concat(found).ToList();
        if (next.SequenceEqual(Candidates)) return;

        var keep = Selected;
        Candidates.Clear();
        foreach (var c in next) Candidates.Add(c);
        Selected = keep is not null && Candidates.Contains(keep) ? keep
                 : Candidates.Count > 0 ? Candidates[0] : null;
        HasCandidates = Candidates.Count > 0;
    }

    private Task ConnectAsync() => RunAsync(async () =>
    {
        if (Selected is null) { SetStatus("Select a device first.", true); return; }
        await _device.ConnectAsync(Selected);
    });

    private Task ConnectManualAsync() => RunAsync(async () =>
    {
        var candidate = TcpScanner.Candidate(ManualEndpoint);
        await _device.ConnectAsync(candidate);
    });
}
