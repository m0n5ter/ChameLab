using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using ChameLab.Localization;
using ChameLab.Models;
using ChameLab.Services;
using ChameLab.Sync;
using ReactiveUI;

namespace ChameLab.ViewModels;

/// <summary>One entry of the open card's history: when, from which device, what changed.</summary>
public sealed record CardHistoryItemViewModel(string When, string Device, string What, ICommand RestoreCommand);

/// <summary>Device sync on the Cards page: sending a card to another device, a card's history, receiving cards.</summary>
public sealed partial class CardsViewModel
{
    private CancellationTokenSource? _sendCts;
    private TaskCompletionSource<bool>? _sendConfirm;
    private CancellationTokenSource? _receiveCts;

    public ICommand SendCommand { get; private set; } = null!;
    public ICommand ConfirmSendCommand { get; private set; } = null!;
    public ICommand HistoryCommand { get; private set; } = null!;
    public ICommand ShowReceiveCommand { get; private set; } = null!;
    public ICommand ReceiveCommand { get; private set; } = null!;
    public ICommand CancelReceiveCommand { get; private set; } = null!;

    private void InitSync()
    {
        SendCommand = ReactiveCommand.Create(() => Toggle(CardAction.Send));
        ConfirmSendCommand = ReactiveCommand.Create(() => _sendConfirm?.TrySetResult(true));
        HistoryCommand = ReactiveCommand.Create(() => Toggle(CardAction.History));
        ShowReceiveCommand = ReactiveCommand.Create(() => { IsReceiveOpen = !IsReceiveOpen; });
        ReceiveCommand = ReactiveCommand.CreateFromTask(() => ReceiveAsync(ReceiveCode),
            this.WhenAnyValue(x => x.ReceiveCode, x => x.IsReceiving, (code, busy) => !busy && !string.IsNullOrWhiteSpace(code)));
        CancelReceiveCommand = ReactiveCommand.Create(() => _receiveCts?.Cancel());
        AppServices.Sync.Changed += () => this.RaisePropertyChanged(nameof(CanShowHistory));
    }

    public bool IsSendOpen => _action == CardAction.Send;
    public bool IsHistoryOpen => _action == CardAction.History;

    /// <summary>History is kept by sync, for saved cards.</summary>
    public bool CanShowHistory => AppServices.Sync.IsEnabled && _pane == CardsPane.Card;

    private void OnSyncActionChanged()
    {
        this.RaisePropertyChanged(nameof(IsSendOpen));
        this.RaisePropertyChanged(nameof(IsHistoryOpen));
        if (_action == CardAction.Send)
        {
            if (ActionCard() is { } card) _ = SendAsync(card);
        }
        else
        {
            _sendCts?.Cancel();
        }
        if (_action == CardAction.History) LoadHistory();
    }

    // ---- Sending -----------------------------------------------------------------

    public string? SendQr
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(SendCopyText));
        }
    }

    public string? SendCode
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(SendCopyText));
        }
    }

    public string? SendCopyText => SendCode ?? SendQr;

    public string? SendDigits
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public string? SendStatus
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>The receiver typed the code: the digits on both screens must match before the card goes.</summary>
    public bool IsSendConfirming
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    private async Task SendAsync(CardSave card)
    {
        _sendCts?.Cancel();
        var cts = _sendCts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        SendQr = null;
        SendCode = null;
        SendDigits = null;
        IsSendConfirming = false;
        SendStatus = Strings.Sync_Connecting;
        try
        {
            await using var offer = await SyncService.OfferCardsAsync([card], cts.Token);
            SendQr = offer.Qr;
            SendCode = offer.TypedCode;
            SendStatus = Strings.Sync_SendWaiting;
            var (name, digits) = await offer.WaitAsync(cts.Token);
            if (digits is not null)
            {
                _sendConfirm = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                SendDigits = digits;
                SendStatus = string.Format(Strings.Sync_CompareDigitsSend, name);
                IsSendConfirming = true;
                await _sendConfirm.Task.WaitAsync(cts.Token);
                IsSendConfirming = false;
            }
            SendStatus = Strings.Sync_Sending;
            await offer.SendAsync(cts.Token);
            if (_action == CardAction.Send) Action = CardAction.None;
            Notify(string.Format(Strings.Sync_CardSent, name));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is System.IO.IOException or System.Net.WebSockets.WebSocketException
                                       or System.Security.Cryptography.CryptographicException or InvalidOperationException)
        {
            if (_action == CardAction.Send) Action = CardAction.None;
            ReportError(ex.Message);
        }
        finally
        {
            IsSendConfirming = false;
            SendDigits = null;
        }
    }

    // ---- History -----------------------------------------------------------------

    public ObservableCollection<CardHistoryItemViewModel> HistoryItems { get; } = new();

    public bool HasHistory => HistoryItems.Count > 0;

    private void LoadHistory()
    {
        HistoryItems.Clear();
        if (_selected?.Card.Id is not { } id) return;
        var items = AppServices.Sync.History(EntityTypes.Card, id);
        // The newest entry is how the card is now: restoring it would change nothing.
        foreach (var item in items.Skip(1))
        {
            var time = item.Time;
            HistoryItems.Add(new CardHistoryItemViewModel(
                time.WallTime.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
                item.Device,
                item.Deleted ? Strings.Sync_HistoryDeleted : string.Join(", ", item.Fields.Select(FieldName).Distinct()),
                ReactiveCommand.Create(() => Restore(id, time))));
        }
        this.RaisePropertyChanged(nameof(HasHistory));
    }

    private static string FieldName(string field) => field switch
    {
        "name" => Strings.Sync_FieldName,
        "color" => Strings.Sync_FieldColor,
        "folder" => Strings.Sync_FieldFolder,
        "data" or "extra" => Strings.Sync_FieldData,
        _ => Strings.Sync_FieldIdentity,
    };

    private void Restore(string id, Hlc time)
    {
        AppServices.Sync.Restore(EntityTypes.Card, id, time);
        Action = CardAction.None;
        Notify(Strings.Sync_Restored);
    }

    // ---- Receiving -----------------------------------------------------------------

    public bool IsReceiveOpen
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public string? ReceiveCode
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public bool IsReceiving
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public string? ReceiveStatus
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public string? ReceiveDigits
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>Receives the cards another device offers (its code typed, or its QR scanned) into the folder shown.</summary>
    private async Task ReceiveAsync(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return;
        var cts = _receiveCts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        IsReceiving = true;
        ReceiveDigits = null;
        ReceiveStatus = Strings.Sync_Connecting;
        try
        {
            var cards = await SyncService.ReceiveCardsAsync(code,
                digits => Dispatcher.UIThread.Post(() =>
                {
                    IsReceiveOpen = true;
                    ReceiveDigits = digits;
                    ReceiveStatus = Strings.Sync_CompareDigitsReceive;
                }),
                cts.Token);
            var folder = CurrentFolder?.Path ?? string.Empty;
            foreach (var card in cards) card.Folder = folder;
            _library.EnsureUniqueNames(cards);
            _library.AddRange(cards);
            IsReceiveOpen = false;
            ReceiveCode = null;
            if (cards.Count == 1) OpenCard(cards[0].Id);
            Notify(string.Format(Strings.Sync_CardsReceived, Loc.Plural(Strings.Plural_Cards, cards.Count)));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is FormatException or System.IO.IOException or System.Net.WebSockets.WebSocketException
                                       or System.Security.Cryptography.CryptographicException or InvalidOperationException)
        {
            ReportError(ex.Message);
        }
        finally
        {
            IsReceiving = false;
            ReceiveStatus = null;
            ReceiveDigits = null;
        }
    }

    /// <summary>A scanned QR code another device shows to send cards: receive them.</summary>
    private bool TryReceiveScanned(string text)
    {
        if (!text.TrimStart().StartsWith(PairingCode.QrPrefix, StringComparison.OrdinalIgnoreCase)) return false;
        Dispatcher.UIThread.Post(() => _ = ReceiveAsync(text));
        return true;
    }
}
