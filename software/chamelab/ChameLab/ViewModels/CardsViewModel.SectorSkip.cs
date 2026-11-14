using System.Windows.Input;
using Avalonia.Threading;
using ChameLab.Localization;
using ChameLab.Models;
using ReactiveUI;

namespace ChameLab.ViewModels;

public sealed partial class CardsViewModel
{
    private MifareSectorControl? _dumpSectorControl;
    public ICommand SkipSectorCommand { get; }
    public bool ShowSkipSector => _dumpCts is { IsCancellationRequested: false } && !_mfkey32Running
        && _dumpSectorControl?.CurrentSector is >= 0;
    public string SkipSectorTip => string.Format(Strings.Recovery_SkipSectorTip, _dumpSectorControl?.CurrentSector ?? -1);

    private void BeginSectorControl()
    {
        _dumpSectorControl = new MifareSectorControl();
        _dumpSectorControl.Changed += () => Dispatcher.UIThread.Post(RefreshSkipSector);
    }

    private void SkipSector()
    {
        if (!ShowSkipSector || _dumpSectorControl is not { } control) return;
        var sector = control.SkipCurrent();
        if (sector >= 0 && sector < DumpSectors.Count) DumpSectors[sector].StopSearch();
        RaiseDumpToolbar();
    }

    private void RefreshSkipSector()
    {
        this.RaisePropertyChanged(nameof(ShowSkipSector));
        this.RaisePropertyChanged(nameof(SkipSectorTip));
    }
}
