using System;
using System.Linq;
using ChameLab.Models;
using ChameLab.Protocol;
using ReactiveUI;

namespace ChameLab.ViewModels;

/// <summary>
/// One sector in the dump grid: the search state of its key A and key B, plus the
/// recovered keys as text for the tooltip, and whether all its blocks have been read.
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

    private bool _isRead;
    public bool IsRead => _isRead;

    public void UpdateReadState(MifareDump dump)
    {
        this.RaiseAndSetIfChanged(ref _isRead,
            Enumerable.Range(FirstBlock, LastBlock - FirstBlock + 1).All(b => dump.GetBlock(b) is { Length: 16 }),
            nameof(IsRead));
        var trailer = dump.GetBlock(LastBlock);
        // Display stored keys offline; authentication state still comes only from ApplyKey.
        this.RaiseAndSetIfChanged(ref _savedKeyA,
            trailer is { Length: 16 } ? Convert.ToHexString(trailer.AsSpan(0, 6)) : null, nameof(KeyAText));
        this.RaiseAndSetIfChanged(ref _savedKeyB,
            trailer is { Length: 16 } ? Convert.ToHexString(trailer.AsSpan(10, 6)) : null, nameof(KeyBText));
    }

    private string? _savedKeyA;
    private string? _savedKeyB;

    private KeyCellState _keyAState;
    private string? _keyA;
    private string? _keyAProgress;
    private double _keyAFraction = -1;
    public KeyCellState KeyAState => _keyAState;
    public double KeyAProgress => _keyAFraction;
    public string KeyAText => CellText(_keyAState, _keyA ?? _savedKeyA, _keyAProgress);

    private KeyCellState _keyBState;
    private string? _keyB;
    private string? _keyBProgress;
    private double _keyBFraction = -1;
    public KeyCellState KeyBState => _keyBState;
    public double KeyBProgress => _keyBFraction;
    public string KeyBText => CellText(_keyBState, _keyB ?? _savedKeyB, _keyBProgress);

    private static string CellText(KeyCellState state, string? key, string? progress) => state switch
    {
        KeyCellState.Found => key ?? string.Empty,
        KeyCellState.Searching => progress is null ? "searching…" : $"searching {progress}",
        KeyCellState.NotFound => "not found",
        _ => key ?? "—",
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
