using System.Collections.Generic;
using ReactiveUI;

namespace ChameLab.ViewModels;

/// <summary>One key list offered before a dump (the built-in keys or a saved dictionary), with a check box.</summary>
public sealed class DumpKeySourceViewModel : ReactiveObject
{
    private bool _isSelected;

    public DumpKeySourceViewModel(string id, string name, IReadOnlyList<byte[]> keys, bool isSelected)
    {
        Id = id;
        Name = name;
        Keys = keys;
        _isSelected = isSelected;
    }

    /// <summary>Dictionary id, or <see cref="BuiltInId"/> for the built-in keys; keeps the choice across rebuilds.</summary>
    public string Id { get; }
    public string Name { get; }
    public bool IsKnownCardKeys => Id == ChameLab.Services.KnownCardKeys.Id;
    public IReadOnlyList<byte[]> Keys { get; }
    public string CountText => Keys.Count == 1 ? "1 key" : $"{Keys.Count} keys";

    public bool IsSelected
    {
        get => _isSelected;
        set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }

    public const string BuiltInId = "";
}
