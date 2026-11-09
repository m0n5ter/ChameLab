using System.Collections.Generic;
using Avalonia.Media;
using ChameLab.Converters;
using ReactiveUI;
using ChameLab.Localization;

namespace ChameLab.ViewModels;

/// <summary>One key list offered before a dump (the built-in keys or a saved dictionary), with a check box.</summary>
public sealed class DumpKeySourceViewModel(string id, string name, IReadOnlyList<byte[]> keys, bool isSelected,
    string? color = null)
    : ReactiveObject
{
    /// <summary>Dictionary id, or <see cref="BuiltInId"/> for the built-in keys; keeps the choice across rebuilds.</summary>
    public string Id { get; } = id;

    public string Name { get; } = name;
    public bool IsKnownCardKeys => Id == Services.KnownCardKeys.Id;
    public IReadOnlyList<byte[]> Keys { get; } = keys;
    public int KeyCount => Keys.Count;
    public string CountText => Loc.Plural(Strings.Plural_Keys, Keys.Count);

    /// <summary>The dictionary's color; null for the built-in and the known card keys.</summary>
    public IBrush? ColorBrush { get; } = HexBrushConverter.Brush(color);

    public bool IsSelected
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = isSelected;

    public const string BuiltInId = "";
}
