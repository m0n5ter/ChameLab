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

/// <summary>The side of the dump comparison a card is chosen for.</summary>
public enum CompareSide { None, A, B }

/// <summary>
/// The dump comparison tool: two cards, each a saved card or a dump file, compared block by block
/// (MIFARE Classic) or page by page (Ultralight / NTAG).
/// </summary>
public sealed partial class ToolsViewModel
{
    private CardLibrary _cards = null!;
    private DumpComparison? _comparison;
    private CompareSide _pickingSide;
    // Cards read from dump files: not in the library, so not followed by its changes.
    private readonly HashSet<CardSave> _compareFiles = new(ReferenceEqualityComparer.Instance);

    public ICommand OpenCompareCommand { get; private set; } = null!;
    public ICommand ChooseCompareACommand { get; private set; } = null!;
    public ICommand ChooseCompareBCommand { get; private set; } = null!;
    public ICommand CloseComparePickerCommand { get; private set; } = null!;
    public ICommand CompareFileCommand { get; private set; } = null!;
    public ICommand CompareClearCommand { get; private set; } = null!;

    private void InitCompare(CardLibrary cards)
    {
        _cards = cards;
        OpenCompareCommand = ReactiveCommand.Create(() => { Tool = SniffTool.Compare; });
        ChooseCompareACommand = ReactiveCommand.Create(() => OpenComparePicker(CompareSide.A));
        ChooseCompareBCommand = ReactiveCommand.Create(() => OpenComparePicker(CompareSide.B));
        CloseComparePickerCommand = ReactiveCommand.Create(CloseComparePicker);
        CompareFileCommand = ReactiveCommand.CreateFromTask(CompareFileAsync);
        CompareClearCommand = ReactiveCommand.Create(() =>
        {
            CompareA = null;
            CompareB = null;
            _compareFiles.Clear();
        });
        _cards.Changed += () => Dispatcher.UIThread.Post(OnCardsChanged);
    }

    public bool IsCompare => _tool == SniffTool.Compare;

    /// <summary>Side A: a saved card or a card read from a dump file.</summary>
    public CardSave? CompareA
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            RaiseSide();
        }
    }

    /// <summary>Side B: a saved card or a card read from a dump file.</summary>
    public CardSave? CompareB
    {
        get;
        private set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            RaiseSide();
        }
    }

    public CardItemViewModel? CompareItemA => CompareA is { } card ? new CardItemViewModel(card) : null;
    public CardItemViewModel? CompareItemB => CompareB is { } card ? new CardItemViewModel(card) : null;
    public bool HasCompareA => CompareA is not null;
    public bool HasCompareB => CompareB is not null;
    public bool HasCompareSide => CompareA is not null || CompareB is not null;

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

    public string? CompareSummary
    {
        get
        {
            if (_comparison is not { } c) return null;
            if (c.IsSame) return Strings.Compare_Identical;
            var pages = CompareA?.Tag.IsUltralight() == true;
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
            var classic = CompareA?.Tag.IsMifareClassic() == true;
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

    // ---- Choosing a card -------------------------------------------------

    /// <summary>The dialog choosing the card of one side is open.</summary>
    public bool IsComparePicking => _pickingSide != CompareSide.None;

    public string ComparePickerTitle => _pickingSide == CompareSide.B ? Strings.Compare_ChooseB : Strings.Compare_ChooseA;

    /// <summary>
    /// The saved cards with a dump the side can take (see <see cref="PickerKind"/>), but the other side's
    /// card. A new list each time the dialog opens, so its search starts over.
    /// </summary>
    public ObservableCollection<CardSave> ComparePickerCards
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = new();

    public string ComparePickerEmptyText => PickerKind() is null ? Strings.Compare_NoDumps : Strings.Compare_NoCards;

    /// <summary>Picking a card in the dialog puts it on the side being chosen.</summary>
    public CardSave? ComparePickerSelected
    {
        get => null;
        set
        {
            var side = _pickingSide;
            if (value is null || side == CompareSide.None) return;
            // The list is still committing the selection: emptying it now would break that.
            Dispatcher.UIThread.Post(() =>
            {
                if (_pickingSide != side) return;
                CloseComparePicker();
                SetSide(side, value);
            });
        }
    }

    private CardSave? OtherSide(CompareSide side) => side switch
    {
        CompareSide.A => CompareB,
        CompareSide.B => CompareA,
        _ => null,
    };

    /// <summary>A card can be compared: it has a dump of a memory the comparison reads.</summary>
    private static bool HasDump(CardSave card) =>
        (card.Tag.IsMifareClassic() || card.Tag.IsUltralight()) && card.Data.Any(d => d.Length > 0);

    /// <summary>
    /// The card the side being chosen must match: the other side's while only that one is chosen. With
    /// both or neither chosen, any dump fits (a card of another kind then clears the other side).
    /// </summary>
    private CardSave? PickerKind() =>
        (_pickingSide == CompareSide.A ? CompareA : CompareB) is null ? OtherSide(_pickingSide) : null;

    private bool Fits(CardSave card) =>
        HasDump(card) && OtherSide(_pickingSide)?.Id != card.Id
        && (PickerKind() is not { } kind || DumpComparison.CanCompare(card, kind));

    private void OpenComparePicker(CompareSide side)
    {
        _pickingSide = side;
        ComparePickerCards = new ObservableCollection<CardSave>();
        RebuildComparePicker();
        this.RaisePropertyChanged(nameof(ComparePickerTitle));
        this.RaisePropertyChanged(nameof(ComparePickerEmptyText));
        this.RaisePropertyChanged(nameof(IsComparePicking));
    }

    private void CloseComparePicker()
    {
        _pickingSide = CompareSide.None;
        ComparePickerCards.Clear();
        this.RaisePropertyChanged(nameof(IsComparePicking));
    }

    private void RebuildComparePicker()
    {
        ComparePickerCards.Clear();
        foreach (var card in _cards.Cards)
            if (Fits(card)) ComparePickerCards.Add(card);
    }

    /// <summary>Puts the card on the side; the other side goes if its kind of memory differs.</summary>
    private void SetSide(CompareSide side, CardSave card)
    {
        if (OtherSide(side) is { } other && !DumpComparison.CanCompare(card, other))
        {
            if (side == CompareSide.A) CompareB = null;
            else CompareA = null;
        }
        if (side == CompareSide.A) CompareA = card;
        else CompareB = card;
    }

    /// <summary>The saved cards changed: the sides follow their edits, the open dialog its list.</summary>
    private void OnCardsChanged()
    {
        CompareA = Resaved(CompareA);
        CompareB = Resaved(CompareB);
        if (IsComparePicking) RebuildComparePicker();
    }

    /// <summary>The saved card as it is now; a deleted card or one from a file stays as it was.</summary>
    private CardSave? Resaved(CardSave? card) =>
        card is null || _compareFiles.Contains(card) ? card : _cards.Cards.FirstOrDefault(c => c.Id == card.Id && HasDump(c)) ?? card;

    /// <summary>A dump file (any format the import reads) as the side being chosen.</summary>
    private Task CompareFileAsync() => RunAsync(async () =>
    {
        var side = _pickingSide;
        if (side == CompareSide.None) return;
        var picked = await StorageService.OpenAsync(Strings.Compare_OpenFileTitle, CardFormats.ImportExtensions);
        if (picked is not { } file) return;

        IReadOnlyList<CardSave> cards;
        try { cards = CardFormats.Import(file.Name, file.Data); }
        catch (Exception ex) { throw new FormatException(string.Format(Strings.Cards_UnsupportedFile, file.Name, ex.Message)); }
        // A card with the other side's kind of memory first; else, unless only the other side is chosen,
        // any, which clears the other side.
        var other = OtherSide(side);
        var kind = PickerKind();
        var dumps = cards.Where(HasDump).ToList();
        var card = dumps.FirstOrDefault(c => other is not null && DumpComparison.CanCompare(c, other))
            ?? (kind is null ? dumps.FirstOrDefault() : null)
            ?? throw new FormatException(string.Format(
                kind is null ? Strings.Compare_FileHasNoDump : Strings.Compare_FileHasNoMatch, file.Name));

        Dispatcher.UIThread.Post(() =>
        {
            if (_pickingSide != side) return;
            // Shown by the file's name.
            card.Name = file.Name;
            card.Folder = string.Empty;
            _compareFiles.Add(card);
            CloseComparePicker();
            SetSide(side, card);
        });
    });

    // ---- Comparing -------------------------------------------------------

    private void RaiseSide()
    {
        this.RaisePropertyChanged(nameof(CompareItemA));
        this.RaisePropertyChanged(nameof(CompareItemB));
        this.RaisePropertyChanged(nameof(HasCompareA));
        this.RaisePropertyChanged(nameof(HasCompareB));
        this.RaisePropertyChanged(nameof(HasCompareSide));
        _comparison = CompareA is { } a && CompareB is { } b && DumpComparison.CanCompare(a, b)
            ? DumpComparison.Compare(a, b)
            : null;
        RaiseComparison();
    }

    private void RaiseComparison()
    {
        this.RaisePropertyChanged(nameof(HasComparison));
        this.RaisePropertyChanged(nameof(CompareSummary));
        this.RaisePropertyChanged(nameof(CompareFields));
        this.RaisePropertyChanged(nameof(HasCompareFields));
        this.RaisePropertyChanged(nameof(CompareRows));
    }
}
