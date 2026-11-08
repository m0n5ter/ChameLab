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
                x => x.T55xxKeyCount, x => x.IsT55xxRunning, x => x.T55xxNewKeyError,
                (busy, connected, supported, count, running, keyError) =>
                    !busy && !running && connected && supported != false && count > 0 && keyError is null));
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

    /// <summary>The user wants to set a new key after a match; otherwise the default is used.</summary>
    public bool T55xxSetNewKey
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(T55xxNewKeyError));
        }
    }

    public string T55xxNewKey
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(T55xxNewKeyError));
        }
    } = "";

    /// <summary>The default key used when the user doesn't set one, as hex (shown in place of the input).</summary>
    public string T55xxDefaultKeyText { get; } = Hex.Spaced(ChameleonClient.DefaultT55xxKey);

    // The key is only required, and only validated, when the user chose to set one.
    public string? T55xxNewKeyError => !T55xxSetNewKey || ValidT55xxKey(T55xxNewKey) ? null : Strings.Common_Password4Bytes;
    private static bool ValidT55xxKey(string? text) => string.IsNullOrWhiteSpace(text) ||
        Hex.TryParse(text, out var key) && key.Length == 4;

    public int T55xxKeyCount => T55xxKeys.Count;
    public string T55xxKeyCountText => T55xxKeyCount == 0 ? Strings.Cards_NoKeysSelected :
        Loc.Plural(Strings.Plural_UniqueKeys, T55xxKeyCount);
    public double T55xxProgress { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    public string? T55xxProgressText { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    public string? T55xxResult { get; private set => this.RaiseAndSetIfChanged(ref field, value); }

    /// <summary>The tag's original UID, as hex.</summary>
    public string? T55xxUid { get; private set => this.RaiseAndSetIfChanged(ref field, value); }

    /// <summary>The matching key, as hex (or a note when the tag has no password).</summary>
    public string? T55xxFoundKey { get; private set => this.RaiseAndSetIfChanged(ref field, value); }

    /// <summary>The new key that was set, as hex (shown green).</summary>
    public string? T55xxSetKey { get; private set => this.RaiseAndSetIfChanged(ref field, value); }

    private void RebuildT55xxKeySources() => T55xxKeys.Refresh();

    private IReadOnlyList<byte[]> SelectedT55xxKeys() => T55xxKeys.BuildKeys();

    private Task StartT55xxAsync()
    {
        if (IsBusy || IsT55xxRunning) return Task.CompletedTask;
        _t55xxRun = RunAsync(async () =>
        {
            var client = _device.Client ?? throw new InvalidOperationException(Strings.Common_ConnectFirst);
            var keys = SelectedT55xxKeys();
            // A new key is only taken when the user chose to set one; otherwise the default is used.
            var custom = T55xxSetNewKey && !string.IsNullOrWhiteSpace(T55xxNewKey);
            if (custom && !ValidT55xxKey(T55xxNewKey)) throw new FormatException(Strings.Common_Password4Bytes);
            var target = custom ? Hex.Parse(T55xxNewKey) : ChameleonClient.DefaultT55xxKey;
            using var cts = new CancellationTokenSource();
            _t55xxCts = cts;
            IsT55xxRunning = true;
            T55xxResult = null;
            T55xxFoundKey = null;
            T55xxUid = null;
            T55xxSetKey = null;
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
                        T55xxUid = Hex.Spaced(original.Id)),
                    key => Dispatcher.UIThread.Post(() => T55xxFoundKey = key is null ? Strings.Tools_T55xxUnprotected :
                        Hex.Spaced(key)), cts.Token);
                Dispatcher.UIThread.Post(() =>
                {
                    T55xxResult = result is null ? Strings.Tools_T55xxNoMatch : Strings.Tools_T55xxComplete;
                    if (result is not null)
                    {
                        T55xxSetKey = Hex.Spaced(target);
                        T55xxProgress = 100;
                    }
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
