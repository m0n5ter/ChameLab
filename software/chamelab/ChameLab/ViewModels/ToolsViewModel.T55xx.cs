using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using ChameLab.Localization;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;
using ReactiveUI;

namespace ChameLab.ViewModels;

public sealed partial class ToolsViewModel
{
    public T55xxKeySelectionViewModel T55xxKeys { get; private set; } = null!;
    private CancellationTokenSource? _t55xxCts;
    private Task _t55xxRun = Task.CompletedTask;

    private void InitT55xx(DictionaryLibrary dictionaries, IObservable<bool> idle)
    {
        T55xxKeys = new(dictionaries);
        T55xxKeys.WhenAnyValue(x => x.Count).Subscribe(_ =>
        {
            this.RaisePropertyChanged(nameof(T55xxKeyCount));
            this.RaisePropertyChanged(nameof(T55xxKeyCountText));
        });
        OpenT55xxCommand = ReactiveCommand.Create(() => { Tool = SniffTool.T55xx; }, idle);
        TryT55xxCommand = ReactiveCommand.CreateFromTask(StartT55xxAsync,
            this.WhenAnyValue(x => x.IsBusy, x => x.IsConnected, x => x.IsT55xxSupported,
                x => x.T55xxKeyCount, x => x.T55xxNewKey, x => x.IsT55xxRunning,
                (busy, connected, supported, count, key, running) =>
                    !busy && !running && connected && supported != false && count > 0 && ValidT55xxKey(key)));
        CancelT55xxCommand = ReactiveCommand.Create(() => _t55xxCts?.Cancel(),
            this.WhenAnyValue(x => x.IsT55xxRunning));
    }

    public ICommand OpenT55xxCommand { get; private set; } = null!;
    public ICommand TryT55xxCommand { get; private set; } = null!;
    public ICommand CancelT55xxCommand { get; private set; } = null!;
    public ObservableCollection<DumpKeySourceViewModel> T55xxKeySources => T55xxKeys.Sources;

    public bool? IsT55xxSupported
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(IsT55xxUnsupported));
        }
    }
    public bool IsT55xxUnsupported => IsT55xxSupported == false;
    public bool IsT55xxRunning { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    public string T55xxNewKey
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(T55xxNewKeyError));
        }
    } = "";
    public string? T55xxNewKeyError => ValidT55xxKey(T55xxNewKey) ? null : Strings.Common_Password4Bytes;
    private static bool ValidT55xxKey(string? text) => string.IsNullOrWhiteSpace(text) ||
        Hex.TryParse(text, out var key) && key.Length == 4;

    public int T55xxKeyCount => T55xxKeys.Count;
    public string T55xxKeyCountText => T55xxKeyCount == 0 ? Strings.Cards_NoKeysSelected :
        Loc.Plural(Strings.Plural_UniqueKeys, T55xxKeyCount);
    public double T55xxProgress { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    public string? T55xxProgressText { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    public string? T55xxResult { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    public string? T55xxFoundKey { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    public string? T55xxOriginalId { get; private set => this.RaiseAndSetIfChanged(ref field, value); }

    private void RebuildT55xxKeySources() => T55xxKeys.Refresh();

    private IReadOnlyList<byte[]> SelectedT55xxKeys() => T55xxKeys.BuildKeys();

    private Task StartT55xxAsync()
    {
        if (IsBusy || IsT55xxRunning) return Task.CompletedTask;
        _t55xxRun = RunAsync(async () =>
        {
            var client = _device.Client ?? throw new InvalidOperationException(Strings.Common_ConnectFirst);
            var keys = SelectedT55xxKeys();
            if (!ValidT55xxKey(T55xxNewKey)) throw new FormatException(Strings.Common_Password4Bytes);
            var target = string.IsNullOrWhiteSpace(T55xxNewKey) ? ChameleonClient.DefaultT55xxKey : Hex.Parse(T55xxNewKey);
            using var cts = new CancellationTokenSource();
            _t55xxCts = cts;
            IsT55xxRunning = true;
            T55xxResult = null;
            T55xxFoundKey = null;
            T55xxOriginalId = null;
            T55xxProgress = 0;
            T55xxProgressText = string.Format(Strings.Tools_T55xxProgress, 0, keys.Count);
            try
            {
                var result = await T55xxPasswordRecovery.RecoverAsync(client, keys, target, (done, total) =>
                    Dispatcher.UIThread.Post(() =>
                    {
                        T55xxProgress = total == 0 ? 0 : 100.0 * done / total;
                        T55xxProgressText = string.Format(Strings.Tools_T55xxProgress, done, total);
                    }), original => Dispatcher.UIThread.Post(() =>
                        T55xxOriginalId = string.Format(Strings.Tools_T55xxOriginalId, original.Type.DisplayName(), Convert.ToHexString(original.Id))),
                    key => Dispatcher.UIThread.Post(() => T55xxFoundKey = key is null ? Strings.Tools_T55xxUnprotected :
                        string.Format(Strings.Tools_T55xxFoundKey, Convert.ToHexString(key))), cts.Token);
                Dispatcher.UIThread.Post(() =>
                {
                    T55xxResult = result is null ? Strings.Tools_T55xxNoMatch :
                        string.Format(Strings.Tools_T55xxComplete, Convert.ToHexString(target));
                    if (result is not null) T55xxProgress = 100;
                });
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                T55xxResult = Strings.Tools_T55xxCancelled;
            }
            finally
            {
                _t55xxCts = null;
                IsT55xxRunning = false;
            }
        });
        return _t55xxRun;
    }

    public override async Task OnDeactivatedAsync()
    {
        _t55xxCts?.Cancel();
        await _t55xxRun;
    }
}
