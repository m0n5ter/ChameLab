using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using ChameLab.Localization;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;
using ReactiveUI;

namespace ChameLab.ViewModels;

/// <summary>An identity field that differs, as shown above the blocks.</summary>
public sealed record CompareFieldRow(string Label, string A, string B);

/// <summary>One block (or page) of the comparison: both sides, with the differing bytes marked by the view.</summary>
public sealed record CompareBlockRow(string Label, string? Sector, byte[]? A, byte[]? B, bool Differs)
{
    public bool HasA => A is not null;
    public bool HasB => B is not null;
}

/// <summary>Comparing the open card's memory with another saved card or a dump file.</summary>
public sealed partial class CardsViewModel
{
    private DumpComparison? _comparison;
    private CardSave? _compareSelf;

    public ICommand CompareCommand { get; private set; } = null!;
    public ICommand CompareFileCommand { get; private set; } = null!;

    private void InitCompare()
    {
        CompareCommand = ReactiveCommand.Create(() => Toggle(CardAction.Compare));
        CompareFileCommand = ReactiveCommand.CreateFromTask(CompareFileAsync);
    }

    public bool IsCompareOpen => _action == CardAction.Compare;

    /// <summary>The open card has memory to compare: a MIFARE Classic or Ultralight / NTAG card.</summary>
    public bool CanCompareDump => _pane is CardsPane.Card or CardsPane.Add
        && _form is { } form && (form.IsMifareClassic || form.IsUltralight);

    /// <summary>
    /// Saved cards with the same kind of memory, and a file opened for this comparison (first, named
    /// after the file). A new list each time the panel opens, so the picker's search starts over.
    /// </summary>
    public ObservableCollection<CardSave> CompareTargets
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = new();

    public CardSave? CompareTarget
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            RefreshComparison();
        }
    }

    /// <summary>Only the blocks that differ are listed (the default); otherwise every block.</summary>
    public bool CompareOnlyDifferences
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            RaiseComparison();
        }
    } = true;

    public bool HasComparison => _comparison is not null;

    /// <summary>The open card's name (side A).</summary>
    public string CompareNameA => _compareSelf is { Name: { Length: > 0 } name } ? name : Strings.Common_NoName;

    /// <summary>The other card's name (side B).</summary>
    public string? CompareNameB => CompareTarget is { } target ? new CardItemViewModel(target).Name : null;

    public string? CompareSummary
    {
        get
        {
            if (_comparison is not { } c) return null;
            if (c.IsSame) return Strings.Compare_Identical;
            var pages = _compareSelf?.Tag.IsUltralight() == true;
            return string.Format(pages ? Strings.Compare_PagesDiffer : Strings.Compare_BlocksDiffer,
                c.DifferentBlocks, c.Blocks.Count, c.DifferentBytes);
        }
    }

    public IReadOnlyList<CompareFieldRow> CompareFields => _comparison?.Fields.Select(f => new CompareFieldRow(
        f.Field switch
        {
            DumpField.Type => Strings.Cards_Type,
            DumpField.Uid => "UID",
            DumpField.Sak => "SAK",
            DumpField.Atqa => "ATQA",
            _ => "ATS",
        }, f.A, f.B)).ToList() ?? (IReadOnlyList<CompareFieldRow>)Array.Empty<CompareFieldRow>();

    public bool HasCompareFields => _comparison?.Fields.Count > 0;

    public IReadOnlyList<CompareBlockRow> CompareRows
    {
        get
        {
            if (_comparison is not { } c) return Array.Empty<CompareBlockRow>();
            var classic = _compareSelf?.Tag.IsMifareClassic() == true;
            var unit = classic ? Strings.Common_Block : Strings.Common_Page;
            return c.Blocks
                .Where(b => !CompareOnlyDifferences || b.Differs)
                .Select(b => new CompareBlockRow(
                    $"{unit} {b.Index}",
                    // The sector by its first block, so a run of blocks reads as one sector.
                    classic && (MifareClassic.SectorFirstBlock(MifareClassic.SectorOfBlock(b.Index)) == b.Index
                        || CompareOnlyDifferences)
                        ? string.Format(Strings.Common_SectorN, MifareClassic.SectorOfBlock(b.Index))
                        : null,
                    b.A, b.B, b.Differs))
                .ToList();
        }
    }

    /// <summary>Builds the list of cards to compare with when the panel opens; forgets the comparison when it closes.</summary>
    private void OnCompareActionChanged()
    {
        CompareTarget = null;
        var targets = new ObservableCollection<CardSave>();
        if (_action == CardAction.Compare && _form?.Draft() is { } self)
            foreach (var card in _library.Cards)
                if (card.Id != self.Id && card.Data.Any(d => d.Length > 0) && DumpComparison.CanCompare(self, card))
                    targets.Add(card);
        CompareTargets = targets;
    }

    /// <summary>Compares again, e.g. after an edit of the open card's memory or identity.</summary>
    private void RefreshComparison()
    {
        _compareSelf = IsCompareOpen ? _form?.Draft() : null;
        _comparison = _compareSelf is { } self && CompareTarget is { } other && DumpComparison.CanCompare(self, other)
            ? DumpComparison.Compare(self, other)
            : null;
        RaiseComparison();
    }

    private void RaiseComparison()
    {
        this.RaisePropertyChanged(nameof(HasComparison));
        this.RaisePropertyChanged(nameof(CompareNameA));
        this.RaisePropertyChanged(nameof(CompareNameB));
        this.RaisePropertyChanged(nameof(CompareSummary));
        this.RaisePropertyChanged(nameof(CompareFields));
        this.RaisePropertyChanged(nameof(HasCompareFields));
        this.RaisePropertyChanged(nameof(CompareRows));
    }

    /// <summary>A dump file (any format the import reads) as the other side.</summary>
    private Task CompareFileAsync() => RunAsync(async () =>
    {
        var picked = await StorageService.OpenAsync(Strings.Compare_OpenFileTitle, CardFormats.ImportExtensions);
        if (picked is not { } file || _form?.Draft() is not { } self) return;

        IReadOnlyList<CardSave> cards;
        try { cards = CardFormats.Import(file.Name, file.Data); }
        catch (Exception ex) { throw new FormatException(string.Format(Strings.Cards_UnsupportedFile, file.Name, ex.Message)); }
        var card = cards.FirstOrDefault(c => c.Data.Any(d => d.Length > 0) && DumpComparison.CanCompare(self, c))
            ?? throw new FormatException(string.Format(Strings.Compare_FileHasNoMatch, file.Name));

        Dispatcher.UIThread.Post(() =>
        {
            if (!IsCompareOpen) return;
            // Listed by the file's name, above the saved cards (in no folder).
            card.Name = file.Name;
            card.Folder = string.Empty;
            CompareTargets.Insert(0, card);
            CompareTarget = card;
        });
    });
}
