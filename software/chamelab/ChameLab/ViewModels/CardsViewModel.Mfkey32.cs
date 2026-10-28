using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;
using ReactiveUI;

namespace ChameLab.ViewModels;

public sealed partial class CardsViewModel
{
    private bool _mfkey32Running;
    private bool _isMfkey32Done;
    private Task? _mfkey32Task;
    private string? _mfkey32FoundKey;
    private int? _mfkey32CapturedCount;

    public int? Mfkey32CapturedCount
    {
        get => _mfkey32CapturedCount;
        private set
        {
            this.RaiseAndSetIfChanged(ref _mfkey32CapturedCount, value);
            this.RaisePropertyChanged(nameof(ShowMfkey32CaptureCount));
            this.RaisePropertyChanged(nameof(Mfkey32CaptureText));
        }
    }
    public bool ShowMfkey32CaptureCount => DumpProgressDetail is null && Mfkey32FoundKey is null && Mfkey32CapturedCount is not null;
    public string Mfkey32CaptureText => $"Captured {Mfkey32CapturedCount} authentications";
    public string? Mfkey32FoundKey
    {
        get => _mfkey32FoundKey;
        private set
        {
            this.RaiseAndSetIfChanged(ref _mfkey32FoundKey, value);
            this.RaisePropertyChanged(nameof(ShowMfkey32FoundKey));
            this.RaisePropertyChanged(nameof(ShowMfkey32CaptureCount));
        }
    }
    public bool ShowMfkey32FoundKey => DumpProgressDetail is null && Mfkey32FoundKey is not null;
    public bool IsMfkey32Progress => _mfkey32Running || IsMfkey32Done;

    public ICommand Mfkey32Command { get; }

    public bool CanRunMfkey32 => Mfkey32UnavailableReason is null;
    public string Mfkey32Tip => Mfkey32UnavailableReason ?? "Recover card keys from a reader using mfkey32";
    private string? Mfkey32UnavailableReason
    {
        get
        {
            if (!IsConnected) return "Connect a Chameleon first.";
            if (IsBusy) return "Wait for the current operation to finish.";
            if (Form is not { IsMifareClassic: true } form) return "mfkey32 requires a MIFARE Classic card.";
            try
            {
                return Mfkey32Wizard.FindSlot(_slotInfos, form.Build()) is null
                    ? "Load this card into a slot before starting mfkey32." : null;
            }
            catch (FormatException) { return "Enter a valid card identity first."; }
        }
    }

    private void RaiseMfkey32Availability()
    {
        WriteRecovery.Refresh();
        this.RaisePropertyChanged(nameof(CanRunMfkey32));
        this.RaisePropertyChanged(nameof(Mfkey32Tip));
    }

    public bool IsMfkey32Done
    {
        get => _isMfkey32Done;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isMfkey32Done, value);
            this.RaisePropertyChanged(nameof(DumpStopLabel));
            this.RaisePropertyChanged(nameof(DumpStopTip));
            this.RaisePropertyChanged(nameof(IsMfkey32Progress));
        }
    }

    public string DumpStopLabel => IsMfkey32Done ? "Done" : "Stop";
    public string DumpStopTip => IsMfkey32Done ? "Close the mfkey32 result" : "Stop and keep the memory read so far (Esc)";

    private Task RunMfkey32Async() => RunAsync(async () =>
    {
        if (_device.Client is not { } client || Form is not { } form) return;
        var card = form.Build();
        _classicDump ??= card.ToMifareDump();
        using var cts = new CancellationTokenSource();
        _dumpCts = cts;
        _mfkey32Running = true;
        IsMfkey32Done = false;
        Mfkey32FoundKey = null;
        DumpProgressText = "Preparing emulation…";
        try
        {
            var progress = new PostedProgress<Mfkey32Progress>(IfKept<Mfkey32Progress>(cts, p =>
            {
                if (cts.IsCancellationRequested) return;
                DumpProgressText = p.Status;
                Mfkey32CapturedCount = p.CapturedCount;
                if (p.KeyFound is not null)
                {
                    Mfkey32FoundKey = p.KeyFound;
                    RebuildKeySources();
                }
            }));
            await new Mfkey32Wizard(client, _dictionaries.KnownKeys).RunAsync(card, progress, async keys =>
            {
                cts.Token.ThrowIfCancellationRequested();
                // The reader verifies the full UID before it applies any keys or memory.
                await MifareDumpBodyAsync(client, keys, _classicDump!.Type, cts, finish: false);
            }, cts.Token);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(IfKept(cts, () =>
            {
                if (cts.IsCancellationRequested) return;
                IsMfkey32Done = true;
                DumpProgressText = $"Read {ReadSectorCount(_classicDump!)}/{_classicDump!.SectorCount} sectors.";
            }));
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        finally
        {
            _mfkey32Running = false;
            if (ReferenceEquals(_dumpCts, cts)) _dumpCts = null;
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                this.RaisePropertyChanged(nameof(IsMfkey32Progress));
                IfKept(cts, () =>
                {
                    Mfkey32CapturedCount = null;
                    if (!IsMfkey32Done)
                    {
                        Mfkey32FoundKey = null;
                        FinishClassicOperation();
                    }
                })();
            });
        }
    });

    public override async Task OnDeactivatedAsync()
    {
        WriteRecovery.Cancel();
        if (WriteRecovery.RunningTask is { IsCompleted: false } recovery) await recovery;
        CancelWrite();
        if (!_mfkey32Running) return;
        CancelDump();
        if (_mfkey32Task is { } task) await task;
    }
}
