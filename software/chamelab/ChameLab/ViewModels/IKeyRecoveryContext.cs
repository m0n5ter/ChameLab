using System.Collections.ObjectModel;
using System.Windows.Input;

namespace ChameLab.ViewModels;

/// <summary>Presentation contract for dictionary search, attacks, progress and sector keys.
/// Reading and writing supply independent card state and operations to the same panel.</summary>
public interface IKeyRecoveryContext
{
    bool CanDump { get; }
    bool ShowRecoveryTools { get; }
    bool ShowWriteProgress { get; }
    ICommand CancelDumpCommand { get; }
    ICommand CheckRecoveryCommand { get; }
    ICommand DarksideCommand { get; }
    ICommand DumpCommand { get; }
    string? DumpKeyCountText { get; }
    string? DumpKeyProgressTip { get; }
    ObservableCollection<DumpKeySourceViewModel> DumpKeySources { get; }
    int DumpKeyTotal { get; }
    int DumpKeyTried { get; }
    string? DumpNotice { get; }
    string? DumpProgressDetail { get; }
    string? DumpProgressText { get; }
    ObservableCollection<DumpSectorRow> DumpSectors { get; }
    string? DumpStopLabel { get; }
    string? DumpStopTip { get; }
    ICommand HardnestedCommand { get; }
    string? HardnestedWarning { get; }
    bool HasDumpKeyProgress { get; }
    bool HasDumpSectors { get; }
    bool IsBusy { get; }
    bool IsDumpRunning { get; }
    bool IsMfkey32Done { get; }
    bool IsMfkey32Progress { get; }
    bool IsUltralightDump { get; }
    string? Mfkey32CaptureText { get; }
    ICommand Mfkey32Command { get; }
    string? Mfkey32FoundKey { get; }
    string? Mfkey32Tip { get; }
    ICommand NestedCommand { get; }
    string? RecoverySectorHint { get; }
    string? RecoveryIdentity { get; }
    int RecoverySelectedSector { get; set; }
    bool ShowCheckRecovery { get; }
    bool ShowDarkside { get; }
    bool ShowDumpActions { get; }
    bool ShowDumpNotice { get; }
    bool ShowHardnested { get; }
    bool ShowMfkey32CaptureCount { get; }
    bool ShowMfkey32 { get; }
    bool ShowMfkey32FoundKey { get; }
    bool ShowNested { get; }
    bool ShowStaticNested { get; }
    ICommand StaticNestedCommand { get; }
    string? TryKeysTip { get; }
}
