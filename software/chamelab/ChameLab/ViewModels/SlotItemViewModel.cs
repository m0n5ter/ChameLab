using System;
using System.Linq;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;
using ReactiveUI;

namespace ChameLab.ViewModels;

/// <summary>Row for one of the eight emulation slots.</summary>
public sealed class SlotItemViewModel(SlotInfo info, CardLibrary? library = null) : ViewModelBase
{
    private SlotInfo _info = info;
    private bool _isActive = info.IsActive;

    /// <summary>The extra LF cards are named after the saved cards they match.</summary>
    public void OnLibraryChanged() => this.RaisePropertyChanged(nameof(LfNames));

    /// <summary>Takes the slot as read back from the device, keeping this row (and the list selection).</summary>
    public void Update(SlotInfo info)
    {
        _info = info;
        IsActive = info.IsActive;
        foreach (var name in new[]
                 {
                     nameof(Info), nameof(HfNick), nameof(LfNick), nameof(HfType), nameof(LfType),
                     nameof(HasHf), nameof(HasLf), nameof(IsEmpty), nameof(HfLed), nameof(LfLed), nameof(LedLeft), nameof(LedRight),
                     nameof(HfLabel), nameof(LfLabel), nameof(LfNames), nameof(HfTypeLabel), nameof(LfTypeLabel), nameof(Summary),
                 })
            this.RaisePropertyChanged(name);
    }

    public int Index => _info.Index;
    public string Header => $"Slot {_info.Index}";

    public SlotInfo Info => _info;
    public string HfNick => _info.HfNick;
    public string LfNick => _info.LfNick;

    public TagSpecificType HfType => _info.HfType;
    public TagSpecificType LfType => _info.LfType;

    public bool HasHf => _info.HfType != TagSpecificType.Undefined && _info.HfEnabled;
    public bool HasLf => _info.LfType != TagSpecificType.Undefined && _info.LfEnabled;
    public bool IsEmpty => !HasHf && !HasLf;

    /// <summary>The LED color the device shows for each card (#RRGGBB), null when the frequency is empty.</summary>
    public string? HfLed => SlotColors.Effective(_info, HasHf, HasLf).Hf;
    public string? LfLed => SlotColors.Effective(_info, HasHf, HasLf).Lf;

    /// <summary>The two halves of a split LED dot: HF left, LF right; a single card fills both.</summary>
    public string? LedLeft => HfLed ?? LfLed;
    public string? LedRight => LfLed ?? HfLed;

    public string HfLabel => HasHf
        ? (!string.IsNullOrEmpty(_info.HfNick) ? _info.HfNick : _info.HfType.DisplayName())
        : "-";

    public string LfLabel => HasLf
        ? (!string.IsNullOrEmpty(_info.LfNick) ? _info.LfNick : _info.LfType.DisplayName())
        : "-";

    /// <summary>Every card of the LF side in the order it cycles through them: its own card, then the extra ones.</summary>
    public string LfNames => HasLf
        ? string.Join("  •  ", _info.LfCycleIds.Select(ExtraName).Prepend(LfLabel))
        : "-";

    private string ExtraName(byte[] uid) =>
        library?.Cards.FirstOrDefault(c => c.Sense == TagSenseType.Lf &&
            ChameleonClient.SlotTypeFor(c.Tag) == TagSpecificType.Em410x && c.Uid.AsSpan().SequenceEqual(uid))?.Name
        ?? $"EM410x {Convert.ToHexString(uid)}";

    /// <summary>Type of each card, under its name in the slot tile; a dash (hidden) when the frequency is empty.</summary>
    public string HfTypeLabel => HasHf ? _info.HfType.DisplayName() : "-";
    public string LfTypeLabel => HasLf ? _info.LfType.DisplayName() : "-";

    public string Summary => IsEmpty
        ? "Empty slot"
        : string.Join("  •  ",
            new[] { HasHf ? $"HF: {HfLabel}" : null, HasLf ? $"LF: {LfLabel}" : null }
            .Where(x => x is not null)!);

    public bool IsActive
    {
        get => _isActive;
        set => this.RaiseAndSetIfChanged(ref _isActive, value);
    }
}
