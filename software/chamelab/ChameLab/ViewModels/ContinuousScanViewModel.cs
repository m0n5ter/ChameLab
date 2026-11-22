using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Localization;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;
using ReactiveUI;

namespace ChameLab.ViewModels;

public sealed class ContinuousScanViewModel : ViewModelBase
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromMilliseconds(150);
    private const string NewHfColor = "#00FFFF";
    private const string NewLfColor = "#FFFF00";
    private readonly DeviceService _device;
    private readonly Func<CardSave, CardSave?> _findSaved;
    private readonly Action<string> _reportError;
    private readonly Action _found;
    private CancellationTokenSource? _cts;
    private Task? _scanTask;
    private bool _tagWasPresent;

    public ContinuousScanViewModel(DeviceService device, Func<CardSave, CardSave?> findSaved, Action<string> reportError)
        : this(device, findSaved, reportError, ScanFeedback.Found) { }

    internal ContinuousScanViewModel(DeviceService device, Func<CardSave, CardSave?> findSaved,
        Action<string> reportError, Action found)
    {
        _device = device;
        _findSaved = findSaved;
        _reportError = reportError;
        _found = found;
    }

    public ObservableCollection<CardItemViewModel> Cards { get; } = new();

    public CardBand Band
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public CardItemViewModel? Selected
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public bool IsRunning
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public bool HasCards => Cards.Count > 0;
    public string Title => string.Format(Strings.Cards_ContinuousScanTitle, Band == CardBand.Hf ? "HF" : "LF");
    public string Instruction => Strings.Cards_ContinuousScanInstruction;

    public async Task StartAsync(CardBand band)
    {
        await StopAsync();
        Cards.Clear();
        Selected = null;
        _tagWasPresent = false;
        this.RaisePropertyChanged(nameof(HasCards));
        Band = band;
        this.RaisePropertyChanged(nameof(Title));
        this.RaisePropertyChanged(nameof(Instruction));
        if (_device.Client is not { } client) return;

        var cts = new CancellationTokenSource();
        _cts = cts;
        IsRunning = true;
        _scanTask = ScanAsync(client, cts, Band);
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        var task = _scanTask;
        _cts = null;
        _scanTask = null;
        _tagWasPresent = false;
        IsRunning = false;
        if (cts is not null)
        {
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        if (task is not null) await task;
        cts?.Dispose();
    }

    internal async Task ScanAsync(ChameleonClient client, CancellationTokenSource cts, CardBand band)
    {
        try
        {
            await using var wakeLock = await AppServices.WakeLock.AcquireAsync();
            var readerMode = false;
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    if (!readerMode)
                    {
                        await client.SetDeviceModeAsync(DeviceMode.Reader, cts.Token);
                        readerMode = true;
                    }

                    var previous = Selected;
                    if (band == CardBand.Hf)
                    {
                        var tags = await client.Hf14aScanAsync(cts.Token);
                        foreach (var tag in tags) Observe(CardFormats.FromHf14a(tag));
                        if (tags.Count > 0 && (!_tagWasPresent || !ReferenceEquals(previous, Selected))) _found();
                        _tagWasPresent = tags.Count > 0;
                    }
                    else
                    {
                        var tag = await client.LfAutoScanAsync(cts.Token);
                        Observe(new CardSave
                        {
                            Name = $"{tag.Type.DisplayName()} {tag.IdHex}",
                            Tag = tag.Type,
                            Uid = tag.Id,
                        });
                        if (!_tagWasPresent || !ReferenceEquals(previous, Selected)) _found();
                        _tagWasPresent = true;
                    }
                }
                catch (ChameleonException ex) when (ex.Status == (band == CardBand.Hf ? Status.HfTagNo : Status.LfTagNoFound))
                {
                    _tagWasPresent = false;
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _tagWasPresent = false;
                    ReportScanError(ex, cts);
                }
                await Task.Delay(ScanInterval, cts.Token);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ReportScanError(ex, cts);
        }
        finally
        {
            if (ReferenceEquals(_cts, cts))
            {
                _cts = null;
                _scanTask = null;
                IsRunning = false;
            }
        }
    }

    private void ReportScanError(Exception ex, CancellationTokenSource cts)
    {
        if (!ReferenceEquals(_cts, cts)) return;
        Console.WriteLine($"[{nameof(ContinuousScanViewModel)}] {ex}");
        _reportError(ex.Message);
    }

    internal bool Observe(CardSave card)
    {
        card.Color = card.Sense switch
        {
            TagSenseType.Hf => NewHfColor,
            TagSenseType.Lf => NewLfColor,
            _ => CardSave.DefaultColor,
        };
        var saved = _findSaved(card);
        var existing = Cards.FirstOrDefault(row => SameCard(row.Card, card)
            || saved is not null && row.IsSavedMatch && row.Card.Id == saved.Id);
        if (existing is not null)
        {
            Selected = existing;
            return false;
        }

        var added = new CardItemViewModel(saved ?? card) { IsSavedMatch = saved is not null };
        Cards.Add(added);
        Selected = added;
        this.RaisePropertyChanged(nameof(HasCards));
        return true;
    }

    private static bool SameCard(CardSave a, CardSave b) => a.Sense == b.Sense
        && (a.Sense != TagSenseType.Lf || ChameleonClient.SlotTypeFor(a.Tag) == ChameleonClient.SlotTypeFor(b.Tag))
        && a.Uid.AsSpan().SequenceEqual(b.Uid);
}
