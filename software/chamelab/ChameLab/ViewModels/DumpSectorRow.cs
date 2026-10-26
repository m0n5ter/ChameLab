using System;
using ChameLab.Models;
using ChameLab.Protocol;
using ReactiveUI;

namespace ChameLab.ViewModels;

/// <summary>
/// One sector in the dump grid: the search state of its key A and key B, plus the
/// recovered keys as text for the tooltip. Blocks are not tracked per sector any more —
/// the grid shows the two key cells and the dump summary reports unreadable blocks.
/// </summary>
public sealed class DumpSectorRow : ReactiveObject, ISectorKeys
{
    public DumpSectorRow(int sector)
    {
        Sector = sector;
        FirstBlock = MifareClassic.SectorFirstBlock(sector);
        LastBlock = FirstBlock + MifareClassic.BlocksInSector(sector) - 1;
    }

    public int Sector { get; }
    public int FirstBlock { get; }
    public int LastBlock { get; }

    private KeyCellState _keyAState;
    private string? _keyA;
    private string? _keyAProgress;
    private double _keyAFraction = -1;
    public KeyCellState KeyAState => _keyAState;
    public double KeyAProgress => _keyAFraction;
    public string KeyAText => CellText(_keyAState, _keyA, _keyAProgress);

    private KeyCellState _keyBState;
    private string? _keyB;
    private string? _keyBProgress;
    private double _keyBFraction = -1;
    public KeyCellState KeyBState => _keyBState;
    public double KeyBProgress => _keyBFraction;
    public string KeyBText => CellText(_keyBState, _keyB, _keyBProgress);

    private static string CellText(KeyCellState state, string? key, string? progress) => state switch
    {
        KeyCellState.Found => key ?? string.Empty,
        KeyCellState.Searching => progress is null ? "searching…" : $"searching {progress}",
        KeyCellState.NotFound => "not found",
        _ => "—",
    };

    /// <summary>Applies a key-search tick (searching progress, a found key, or a dead end).</summary>
    public void ApplyKey(MifareKeyProgress p)
    {
        var state = p.Phase switch
        {
            MifareKeyPhase.Found => KeyCellState.Found,
            MifareKeyPhase.NotFound => KeyCellState.NotFound,
            _ => KeyCellState.Searching,
        };
        var hex = p.Key is null ? null : Convert.ToHexString(p.Key);
        var progress = p.Total > 0 ? $"{p.Tried}/{p.Total}" : null;
        var fraction = p.Total > 0 ? Math.Clamp((double)p.Tried / p.Total, 0, 1) : -1;
        if (p.KeyType == MfKeyType.A)
        {
            if (hex is not null) _keyA = hex;
            _keyAProgress = progress;
            _keyAFraction = fraction;
            _keyAState = state;
            this.RaisePropertyChanged(nameof(KeyAState));
            this.RaisePropertyChanged(nameof(KeyAProgress));
            this.RaisePropertyChanged(nameof(KeyAText));
        }
        else
        {
            if (hex is not null) _keyB = hex;
            _keyBProgress = progress;
            _keyBFraction = fraction;
            _keyBState = state;
            this.RaisePropertyChanged(nameof(KeyBState));
            this.RaisePropertyChanged(nameof(KeyBProgress));
            this.RaisePropertyChanged(nameof(KeyBText));
        }
    }
}
