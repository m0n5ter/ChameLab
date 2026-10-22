using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;
using ReactiveUI;

namespace ChameLab.ViewModels;

/// <summary>Where the slot LED takes a card's color from.</summary>
public enum SlotColorMode
{
    /// <summary>The firmware's own HF / LF color (the only choice on the original firmware).</summary>
    Default,
    /// <summary>The color of the saved card the frequency holds.</summary>
    Card,
    /// <summary>A color picked for the slot.</summary>
    Custom,
}

/// <summary>
/// One frequency (HF or LF) of the slot editor. The card it holds is shown as the saved card
/// it matches (same type and name), or by name and type with an offer to save it. A card in the
/// slot is always emulated (saving enables it). Picking a saved card, clearing and the LED color
/// are edits held until the slot is saved.
/// </summary>
public sealed class SlotSideViewModel : ViewModelBase
{
    private readonly CardLibrary _library;
    private readonly Action _changed;

    // What the frequency holds on the device; the edits are compared against it.
    private TagSpecificType _savedType;
    private string _savedNick;
    private SlotColorMode _savedMode;
    private string _savedCustom;

    private CardSave? _pending;
    private bool _cleared;
    private SlotColorMode _colorMode;
    private string _customColor;
    private CardItemViewModel? _match;
    // The color of the last matched card, kept for Card mode if the card goes away meanwhile.
    private string _cardColor = CardSave.DefaultColor;

    public SlotSideViewModel(
        TagSenseType sense, SlotInfo info, CardLibrary library, bool supportsColors, Action changed,
        Action<SlotSideViewModel> pick, Func<SlotSideViewModel, Task> saveToLibrary, Action<CardSave> openCard)
    {
        Sense = sense;
        SupportsColors = supportsColors;
        _library = library;
        _changed = changed;

        var hf = sense == TagSenseType.Hf;
        _savedType = hf ? info.HfType : info.LfType;
        _savedNick = hf ? info.HfNick : info.LfNick;
        FindMatch();

        var color = (hf ? info.HfColor : info.LfColor) is { } c ? SlotColors.SnapToPalette(c) : null;
        (_savedMode, _savedCustom) = InferColor(color);
        (_colorMode, _customColor) = (_savedMode, _savedCustom);

        PickCommand = ReactiveCommand.Create(() => pick(this));
        ClearCommand = ReactiveCommand.Create(Clear);
        SaveToLibraryCommand = ReactiveCommand.CreateFromTask(() => saveToLibrary(this));
        OpenCardCommand = ReactiveCommand.Create(() => { if (_match is { } m) openCard(m.Card); });
    }

    public TagSenseType Sense { get; }
    public bool IsHf => Sense == TagSenseType.Hf;
    public bool IsLf => Sense == TagSenseType.Lf;
    public string Label => IsHf ? "HF" : "LF";
    public string Band => IsHf ? "13.56 MHz" : "125 kHz";

    /// <summary>The firmware takes per-card LED colors (ChameLab firmware).</summary>
    public bool SupportsColors { get; }

    // ---- The card ------------------------------------------------------

    /// <summary>The tag type the frequency holds once saved.</summary>
    public TagSpecificType Type => _pending is { } card
        ? ChameleonClient.SlotTypeFor(card.Tag)
        : _cleared ? TagSpecificType.Undefined : _savedType;

    /// <summary>The slot nick once saved.</summary>
    public string Nick => _pending is { } card
        ? ChameleonClient.SlotNickFor(card)
        : _cleared ? string.Empty : _savedNick;

    public bool HasCard => Type != TagSpecificType.Undefined;
    public bool IsEmpty => !HasCard;

    /// <summary>The saved card the frequency holds (the picked one, or the one with the slot's type and name).</summary>
    public CardItemViewModel? Match => _match;
    public bool HasMatch => _match is not null;

    /// <summary>A card that is not in Saved cards: shown by name and type, with Save.</summary>
    public bool IsUnmatched => HasCard && _match is null;

    public string Name => string.IsNullOrEmpty(Nick) ? Type.DisplayName() : Nick;
    public string TypeLabel => Type.DisplayName();

    /// <summary>What the slot holds on the device, for reading the card back.</summary>
    public TagSpecificType SavedType => _savedType;
    public string SavedNick => _savedNick;
    public string? SavedColor => ColorOf(_savedMode, _savedCustom);

    /// <summary>Saving the card to the library named it; the slot now carries that name.</summary>
    public void Renamed(string nick)
    {
        _savedNick = nick;
        FindMatch();
        RaiseCard();
    }

    /// <summary>Puts <paramref name="card"/> into the frequency (on save), colored like the card where the firmware can.</summary>
    public void Pick(CardSave card)
    {
        _pending = card;
        _cleared = false;
        FindMatch();
        _colorMode = SupportsColors ? SlotColorMode.Card : SlotColorMode.Default;
        RaiseAll();
    }

    private void Clear()
    {
        _pending = null;
        _cleared = _savedType != TagSpecificType.Undefined;
        FindMatch();
        RaiseAll();
    }

    /// <summary>Puts the fields back to what the frequency holds.</summary>
    public void Discard()
    {
        _pending = null;
        _cleared = false;
        FindMatch();
        (_colorMode, _customColor) = (_savedMode, _savedCustom);
        RaiseAll();
    }

    /// <summary>The library changed: the card may have been saved, renamed or deleted.</summary>
    public void OnLibraryChanged()
    {
        if (_pending is { } card && _library.Cards.FirstOrDefault(c => c.Id == card.Id) is { } fresh)
            _pending = fresh;
        FindMatch();
        RaiseAll();
    }

    private void FindMatch()
    {
        var match = _pending is { } card
            ? _library.Cards.FirstOrDefault(c => c.Id == card.Id) ?? card
            : HasCard ? _library.Cards.FirstOrDefault(Holds) : null;
        _match = match is null ? null : new CardItemViewModel(match);
        if (match is not null) _cardColor = match.Color;
    }

    // Matched by type and name, like the Cards page tells which slots hold a card.
    private bool Holds(CardSave card) =>
        card.Sense == Sense
        && ChameleonClient.SlotTypeFor(card.Tag) == _savedType
        && ChameleonClient.SlotNickFor(card) == _savedNick;

    // ---- LED color ----------------------------------------------------

    /// <summary>The card's own color can be used: our firmware and a saved card to take it from.</summary>
    public bool CanUseCardColor => SupportsColors && HasMatch;

    public SlotColorMode ColorMode
    {
        get => _colorMode;
        set
        {
            if (_colorMode == value) return;
            _colorMode = value;
            RaiseColor();
        }
    }

    public bool IsColorDefault { get => _colorMode == SlotColorMode.Default; set { if (value) ColorMode = SlotColorMode.Default; } }
    public bool IsColorCard { get => _colorMode == SlotColorMode.Card; set { if (value) ColorMode = SlotColorMode.Card; } }
    public bool IsColorCustom { get => _colorMode == SlotColorMode.Custom; set { if (value) ColorMode = SlotColorMode.Custom; } }

    // The swatch list deselects (and writes null) when the color isn't in the palette.
    public string CustomColor
    {
        get => _customColor;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || value == _customColor) return;
            _customColor = value;
            RaiseColor();
        }
    }

    /// <summary>The color written to the device; null is the firmware default.</summary>
    public string? EffectiveColor => ColorOf(_colorMode, _customColor);

    /// <summary>The color the slot LED shows for the card.</summary>
    public string LedPreview => EffectiveColor
        ?? (Partner is { } other && IsLit && other.IsLit && other.EffectiveColor is null ? SlotColors.DefaultDual
            : IsHf ? SlotColors.DefaultHf : SlotColors.DefaultLf);

    /// <summary>The other frequency of the slot: two cards without colors of their own light the LED red.</summary>
    public SlotSideViewModel? Partner { get; set; }

    // Saving enables every card, so a card the slot holds will light the LED.
    private bool IsLit => HasCard;

    /// <summary>The other frequency changed, which may change this one's default color.</summary>
    public void RaiseLed() => this.RaisePropertyChanged(nameof(LedPreview));

    private string? ColorOf(SlotColorMode mode, string custom) => mode switch
    {
        SlotColorMode.Card => _match?.Card.Color ?? _cardColor,
        SlotColorMode.Custom => custom,
        _ => null,
    };

    /// <summary>Which choice the color read from the device stands for.</summary>
    private (SlotColorMode, string) InferColor(string? color)
    {
        var custom = color ?? _match?.Card.Color ?? SlotColors.Palette[0];
        if (!SupportsColors || color is null) return (SlotColorMode.Default, custom);
        if (_match is { } m && SlotColors.SameLed(color, m.Card.Color)) return (SlotColorMode.Card, custom);
        return (SlotColorMode.Custom, custom);
    }

    // ---- Saving --------------------------------------------------------

    /// <summary>The card or color differ from what the frequency holds.</summary>
    public bool IsDirty =>
        IsEmpty
            ? _cleared
            : _pending is not null
              || _colorMode != _savedMode
              || (_colorMode == SlotColorMode.Custom && _customColor != _savedCustom);

    /// <summary>Writes the edits to <paramref name="slot"/> (not yet to flash).</summary>
    public async Task WriteAsync(ChameleonClient client, int slot, IProgress<MifareProgress>? progress)
    {
        var (pending, cleared, hasCard, color) = (_pending, _cleared, HasCard, EffectiveColor);
        if (cleared)
        {
            await client.DeleteSlotSenseTypeAsync(slot, Sense);
            return;
        }
        if (!hasCard) return;
        if (pending is not null) await client.LoadCardToSlotAsync(pending, slot, progress);
        await client.SetSlotEnableAsync(slot, Sense, enabled: true);
        if (SupportsColors) await client.SetSlotColorAsync(slot, Sense, color);
    }

    /// <summary>The edits are on the device now.</summary>
    public void MarkSaved()
    {
        _savedType = Type;
        _savedNick = Nick;
        _savedMode = IsEmpty ? SlotColorMode.Default : _colorMode;
        _savedCustom = _customColor;
        _pending = null;
        _cleared = false;
        FindMatch();
        RaiseAll();
    }

    public ICommand PickCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand SaveToLibraryCommand { get; }
    public ICommand OpenCardCommand { get; }

    private void RaiseCard()
    {
        foreach (var name in new[]
                 {
                     nameof(Type), nameof(Nick), nameof(HasCard), nameof(IsEmpty), nameof(Match), nameof(HasMatch),
                     nameof(IsUnmatched), nameof(Name), nameof(TypeLabel), nameof(CanUseCardColor),
                 })
            this.RaisePropertyChanged(name);
        _changed();
    }

    private void RaiseColor()
    {
        foreach (var name in new[]
                 {
                     nameof(ColorMode), nameof(IsColorDefault), nameof(IsColorCard), nameof(IsColorCustom),
                     nameof(CustomColor), nameof(EffectiveColor), nameof(LedPreview),
                 })
            this.RaisePropertyChanged(name);
        _changed();
    }

    private void RaiseAll()
    {
        RaiseCard();
        RaiseColor();
    }
}
