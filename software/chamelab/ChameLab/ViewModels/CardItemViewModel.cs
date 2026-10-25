using System;
using System.Linq;
using Avalonia.Media;
using ChameLab.Converters;
using ChameLab.Models;
using ChameLab.Protocol;

namespace ChameLab.ViewModels;

/// <summary>One saved card in the Cards list.</summary>
public sealed class CardItemViewModel : ViewModelBase
{
    public CardItemViewModel(CardSave card) => Card = card;

    public CardSave Card { get; }

    public string Name => string.IsNullOrWhiteSpace(Card.Name) ? "No name" : Card.Name;
    public string TypeLabel => Card.Tag.DisplayName();
    public bool IsHf => Card.Sense == TagSenseType.Hf;
    public bool IsLf => Card.Sense == TagSenseType.Lf;
    public bool IsUnknownType => Card.Sense == TagSenseType.Undefined;

    public string UidHex => Card.UidHex;
    public string UidLabel => IsLf ? "ID" : "UID";
    public string SakHex => Card.Sak.ToString("X2");
    public string AtqaHex => Card.Atqa.Length == 0 ? "-" : Hex.Spaced(CardTags.SwapAtqa(Card.Atqa));
    public string AtsHex => Card.Ats.Length == 0 ? "-" : Hex.Spaced(Card.Ats);

    public IBrush ColorBrush => HexBrushConverter.Brush(Card.Color) ?? Brushes.OrangeRed;

    public bool HasData => Card.Data.Any(d => d.Length > 0);

    /// <summary>A MIFARE Classic card with memory can also be exported as .bin / .eml.</summary>
    public bool CanExportDump => Card.Tag.IsMifareClassic() && HasData;

    public string Summary => $"{TypeLabel} · {UidLabel} {UidHex}";

    public bool Matches(string? filter) =>
        string.IsNullOrWhiteSpace(filter)
        || Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || TypeLabel.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || Hex.Compact(Card.Uid).Contains(filter.Replace(" ", string.Empty), StringComparison.OrdinalIgnoreCase);
}

/// <summary>A slot as a target for "emulate this card", labelled for the card's frequency.</summary>
public sealed class SlotTargetViewModel : ViewModelBase
{
    public SlotTargetViewModel(SlotInfo info, TagSenseType sense)
    {
        Index = info.Index;
        IsActive = info.IsActive;
        var (type, enabled, nick) = sense == TagSenseType.Lf
            ? (info.LfType, info.LfEnabled, info.LfNick)
            : (info.HfType, info.HfEnabled, info.HfNick);
        IsEmpty = type == TagSpecificType.Undefined || !enabled;
        Label = IsEmpty ? "Empty" : (string.IsNullOrEmpty(nick) ? type.DisplayName() : nick);
    }

    public int Index { get; }
    public bool IsActive { get; }
    public bool IsEmpty { get; }
    public string Label { get; }
}
