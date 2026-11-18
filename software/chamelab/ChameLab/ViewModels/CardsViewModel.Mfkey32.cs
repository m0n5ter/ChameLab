using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using ChameLab.Models;
using ChameLab.Services;
using ReactiveUI;
using ChameLab.Localization;

namespace ChameLab.ViewModels;

public sealed partial class CardsViewModel
{
    private bool _mfkey32Running;
    private Task? _mfkey32Task;
    private TaskCompletionSource<SlotTargetViewModel>? _mfkey32SlotChoice;

    public bool IsMfkey32WaitingForSlot => _mfkey32SlotChoice is not null;
    public ICommand Mfkey32SelectSlotCommand { get; }

    public int? Mfkey32CapturedCount
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(ShowMfkey32CaptureCount));
            this.RaisePropertyChanged(nameof(Mfkey32CaptureText));
        }
    }

    public bool ShowMfkey32CaptureCount => DumpProgressDetail is null && Mfkey32FoundKey is null && Mfkey32CapturedCount is not null;
    public string Mfkey32CaptureText => string.Format(Strings.Cards_CapturedAuths, Mfkey32CapturedCount);

    public string? Mfkey32FoundKey
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(ShowMfkey32FoundKey));
            this.RaisePropertyChanged(nameof(ShowMfkey32CaptureCount));
        }
    }

    public bool ShowMfkey32FoundKey => DumpProgressDetail is null && Mfkey32FoundKey is not null;
    public bool IsMfkey32Progress => _mfkey32Running || IsMfkey32Done;

    public ICommand Mfkey32Command { get; }

    public bool CanRunMfkey32 => Mfkey32UnavailableReason is null;
    public string Mfkey32Tip => Mfkey32UnavailableReason ?? Strings.Cards_Mfkey32Tip;
    private string? Mfkey32UnavailableReason
    {
        get
        {
            if (!IsConnected) return Strings.Common_ConnectFirst;
            if (IsBusy) return Strings.Common_WaitForOperation;
            if (Form is not { IsMifareClassic: true } form) return Strings.Cards_Mfkey32NeedsClassic;
            try
            {
                form.Build();
                return null;
            }
            catch (FormatException) { return Strings.Cards_EnterIdentityFirst; }
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
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(DumpStopLabel));
            this.RaisePropertyChanged(nameof(DumpStopTip));
            this.RaisePropertyChanged(nameof(IsMfkey32Progress));
        }
    }

    public string DumpStopLabel => IsMfkey32Done ? Strings.Common_Done : Strings.Common_Stop;
    public string DumpStopTip => IsMfkey32Done ? Strings.Cards_CloseMfkey32Result : Strings.Cards_StopKeepMemory;

    private Task RunMfkey32Async() => RunAwakeAsync(async () =>
    {
        if (_device.Client is not { } client || Form is not { } form) return;
        var card = form.Build();
        _classicDump ??= card.ToMifareDump();
        using var cts = new CancellationTokenSource();
        _dumpCts = cts;
        _mfkey32Running = true;
        IsMfkey32Done = false;
        Mfkey32FoundKey = null;
        DumpProgressText = Strings.Cards_PreparingEmulation;
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
            }, cts.Token, loadCardIntoSlot: async ct =>
            {
                var slots = await client.GetAllSlotsAsync(ct);
                var choice = new TaskCompletionSource<SlotTargetViewModel>(TaskCreationOptions.RunContinuationsAsynchronously);
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(IfKept(cts, () =>
                {
                    _slotInfos = slots;
                    RebuildTargets();
                    _mfkey32SlotChoice = choice;
                    this.RaisePropertyChanged(nameof(IsMfkey32WaitingForSlot));
                }));
                try
                {
                    var target = await choice.Task.WaitAsync(ct);
                    ct.ThrowIfCancellationRequested();
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(IfKept(cts, () =>
                    {
                        _mfkey32SlotChoice = null;
                        this.RaisePropertyChanged(nameof(IsMfkey32WaitingForSlot));
                    }));
                    var uploadProgress = new PostedProgress<MifareProgress>(IfKept<MifareProgress>(cts, p =>
                    {
                        if (!ct.IsCancellationRequested)
                            DumpProgressText = string.Format(Strings.Common_UploadingProgress, p.Done, p.Total);
                    }));
                    await client.WithSlotLockAsync(() => client.LoadCardToSlotAsync(card, target.Index, uploadProgress, ct), ct);
                    slots = await client.GetAllSlotsAsync(ct);
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(IfKept(cts, () =>
                    {
                        _slotInfos = slots;
                        RebuildTargets();
                    }));
                }
                finally
                {
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (!ReferenceEquals(_mfkey32SlotChoice, choice)) return;
                        _mfkey32SlotChoice = null;
                        this.RaisePropertyChanged(nameof(IsMfkey32WaitingForSlot));
                    });
                }
            });
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(IfKept(cts, () =>
            {
                if (cts.IsCancellationRequested) return;
                IsMfkey32Done = true;
                DumpProgressText = string.Format(Strings.Cards_ReadSectorsProgress, ReadSectorCount(_classicDump!), _classicDump!.SectorCount);
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
        await _writeTask;
        if (!_mfkey32Running) return;
        CancelDump();
        if (_mfkey32Task is { } task) await task;
    }
}
