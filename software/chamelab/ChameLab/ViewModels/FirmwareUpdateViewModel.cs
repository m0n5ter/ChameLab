using System;
using System.Threading.Tasks;
using System.Windows.Input;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;
using ChameLab.Transport;
using ReactiveUI;
using ChameLab.Localization;

namespace ChameLab.ViewModels;

public enum FirmwareCheckState { None, Checking, UpToDate, Available, Failed }

public enum FirmwareDialog { None, Confirm, Installing, Done, Failed }

/// <summary>
/// Firmware version check against the GitHub releases (on every connect) and the
/// install flow, whose dialog lives in the shell so it survives the device
/// disconnecting while it restarts into the bootloader.
/// </summary>
public sealed class FirmwareUpdateViewModel : ViewModelBase
{
    private static readonly TimeSpan CheckCacheLifetime = TimeSpan.FromMinutes(30);

    private readonly DeviceService _device;
    private readonly FirmwareUpdateService _service;

    private FirmwareCheckState _checkState;
    private FirmwareRelease? _latest;
    private DeviceModel _latestModel;
    private DateTime _checkedAt;

    private FirmwareDialog _dialog;

    // What the dialog installs: a release to download, or a package file picked by the user.
    private FirmwareRelease? _pendingRelease;
    private byte[]? _pendingFile;
    // Kept for Retry: a failed flash leaves the device waiting in its bootloader.
    private DfuPackage? _package;
    private IDfuTarget? _target;
    // How the device was connected when the dialog opened; it is gone while the bootloader runs.
    private bool _overBle;

    public FirmwareUpdateViewModel(DeviceService device, FirmwareUpdateService service)
    {
        _device = device;
        _service = service;
        CheckCommand = ReactiveCommand.CreateFromTask(() => CheckAsync(force: true));
        InstallLatestCommand = ReactiveCommand.Create(ShowInstallLatest);
        InstallFromFileCommand = ReactiveCommand.CreateFromTask(InstallFromFileAsync);
        ConfirmCommand = ReactiveCommand.CreateFromTask(InstallAsync);
        RetryCommand = ReactiveCommand.CreateFromTask(InstallAsync);
        CloseCommand = ReactiveCommand.Create(Close);
        OpenReleaseCommand = ReactiveCommand.CreateFromTask(OpenReleaseAsync);
    }

    public ICommand CheckCommand { get; }
    public ICommand InstallLatestCommand { get; }
    public ICommand InstallFromFileCommand { get; }
    public ICommand ConfirmCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand OpenReleaseCommand { get; }

    /// <summary>Installing needs the bootloader over USB serial (desktop) or BLE (phones).</summary>
    public bool CanInstall => _service.CanInstall(_device.Candidate?.Kind);

    public FirmwareCheckState CheckState
    {
        get => _checkState;
        private set
        {
            this.RaiseAndSetIfChanged(ref _checkState, value);
            this.RaisePropertyChanged(nameof(IsUpdateAvailable));
            this.RaisePropertyChanged(nameof(IsChecking));
            this.RaisePropertyChanged(nameof(CanInstallLatest));
        }
    }

    public bool IsChecking => _checkState == FirmwareCheckState.Checking;
    public bool IsUpdateAvailable => _checkState == FirmwareCheckState.Available;
    public bool CanInstallLatest => IsUpdateAvailable && CanInstall && _latest?.PackageUrl is not null;

    public string CheckMessage
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    public FirmwareRelease? Latest
    {
        get => _latest;
        private set => this.RaiseAndSetIfChanged(ref _latest, value);
    }

    public string CurrentVersion
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = "-";

    public FirmwareDialog Dialog
    {
        get => _dialog;
        private set
        {
            this.RaiseAndSetIfChanged(ref _dialog, value);
            this.RaisePropertyChanged(nameof(IsDialogOpen));
            this.RaisePropertyChanged(nameof(IsConfirming));
            this.RaisePropertyChanged(nameof(IsInstalling));
            this.RaisePropertyChanged(nameof(IsDone));
            this.RaisePropertyChanged(nameof(IsFailed));
            this.RaisePropertyChanged(nameof(CanRetry));
        }
    }

    /// <summary>A failed install can be retried; a file that isn't a package can't.</summary>
    public bool CanRetry => _package is not null || _pendingRelease is not null || _pendingFile is not null;

    public bool IsDialogOpen => _dialog != FirmwareDialog.None;
    public bool IsConfirming => _dialog == FirmwareDialog.Confirm;
    public bool IsInstalling => _dialog == FirmwareDialog.Installing;
    public bool IsDone => _dialog == FirmwareDialog.Done;
    public bool IsFailed => _dialog == FirmwareDialog.Failed;

    public string InstallTitle
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    public string InstallHint => _overBle
        ? Strings.Firmware_HowBle
        : Strings.Firmware_HowUsb;

    public string KeepConnectedHint => _overBle
        ? Strings.Firmware_KeepClose
        : Strings.Firmware_DontUnplug;

    /// <summary>Release notes of the version about to be installed, as Markdown (empty for a file).</summary>
    public string InstallNotes => _pendingRelease?.Notes.Trim() ?? string.Empty;

    public string StepText
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>0..100 while the step reports progress.</summary>
    public double Progress
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public bool ProgressKnown
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public string? Error
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>Called on every connect: shows the device version and checks for a newer release.</summary>
    public Task OnConnectedAsync()
    {
        if (_device.DeviceInfo is not { } info) return Task.CompletedTask;
        CurrentVersion = FirmwareVersion.OfDevice(info.GitVersion, info.FirmwareVersion)?.ToString()
                         ?? info.FirmwareVersion;
        this.RaisePropertyChanged(nameof(CanInstall));
        this.RaisePropertyChanged(nameof(CanInstallLatest));
        return CheckAsync(force: false);
    }

    private async Task CheckAsync(bool force)
    {
        if (_device.DeviceInfo is not { } info || IsChecking) return;
        CheckState = FirmwareCheckState.Checking;
        CheckMessage = Strings.Update_Checking;
        try
        {
            // GitHub allows 60 anonymous API calls an hour; reconnects reuse a recent answer.
            if (force || _latest is null || _latestModel != info.Model || DateTime.UtcNow - _checkedAt > CheckCacheLifetime)
            {
                Latest = await _service.GetLatestAsync(info.Model);
                _latestModel = info.Model;
                _checkedAt = DateTime.UtcNow;
            }

            var current = FirmwareVersion.OfDevice(info.GitVersion, info.FirmwareVersion);
            if (current is { } c && !(_latest!.Version > c))
            {
                CheckState = FirmwareCheckState.UpToDate;
                CheckMessage = string.Format(Strings.Firmware_UpToDate, _latest.Version);
            }
            else
            {
                CheckState = FirmwareCheckState.Available;
                CheckMessage = _latest!.PackageUrl is null
                    ? string.Format(Strings.Firmware_NoPackage, _latest.Version)
                    : CanInstall
                        ? string.Format(Strings.Firmware_Available, _latest.Version)
                        : string.Format(Strings.Firmware_AvailableConnect, _latest.Version);
            }
        }
        catch (Exception ex)
        {
            CheckState = FirmwareCheckState.Failed;
            CheckMessage = string.Format(Strings.Update_CheckFailed, ex.Message);
        }
    }

    private void ShowInstallLatest()
    {
        if (_latest is null) return;
        _pendingRelease = _latest;
        _pendingFile = null;
        _package = null;
        _target = null;
        InstallTitle = string.Format(Strings.Firmware_InstallPrompt, _latest.Version);
        SetTransport();
        Error = null;
        Dialog = FirmwareDialog.Confirm;
    }

    private async Task InstallFromFileAsync()
    {
        if (!CanInstall) return;
        PickedFile? picked;
        try { picked = await StorageService.OpenAsync(Strings.Firmware_PackageTitle, "zip"); }
        catch (Exception ex) { Error = ex.Message; Dialog = FirmwareDialog.Failed; return; }
        if (picked is not { } file) return;

        try { _package = DfuPackage.Parse(file.Data); }
        catch (Exception ex)
        {
            _package = null;
            _pendingRelease = null;
            _pendingFile = null;
            Error = $"{file.Name}: {ex.Message}";
            InstallTitle = Strings.Firmware_CantInstallFile;
            Dialog = FirmwareDialog.Failed;
            return;
        }
        _pendingRelease = null;
        _pendingFile = file.Data;
        _target = null;
        InstallTitle = string.Format(Strings.Firmware_InstallFromPrompt, file.Name);
        SetTransport();
        Error = null;
        Dialog = FirmwareDialog.Confirm;
    }

    private async Task InstallAsync()
    {
        Dialog = FirmwareDialog.Installing;
        Error = null;
        var name = _pendingRelease?.Version.ToString() ?? Strings.Firmware_TheFirmware;
        InstallTitle = string.Format(Strings.Firmware_Installing, name);
        try
        {
            if (_package is null)
            {
                SetStep(Strings.Firmware_Downloading, 0);
                var zip = _pendingFile ?? await _service.DownloadAsync(_pendingRelease!,
                    new Progress<double>(f => SetStep(Strings.Firmware_Downloading, f)));
                _package = DfuPackage.Parse(zip);
            }
            // Prepared before the restart, so a retry still knows which bootloader is this device's.
            _target ??= _service.PrepareTarget();

            await _service.InstallAsync(_package, _target, new Progress<FirmwareUpdateProgress>(p =>
            {
                switch (p.Step)
                {
                    case FirmwareUpdateStep.Restarting: SetStep(Strings.Firmware_Restarting); break;
                    case FirmwareUpdateStep.WaitingForBootloader: SetStep(Strings.Firmware_WaitingBootloader); break;
                    case FirmwareUpdateStep.Flashing: SetStep(string.Format(Strings.Firmware_Flashing, p.Fraction), p.Fraction); break;
                    case FirmwareUpdateStep.Reconnecting: SetStep(Strings.Firmware_WaitingRestart); break;
                }
            }));

            _package = null;
            _target = null;
            InstallTitle = Strings.Firmware_Updated;
            StepText = _device.DeviceInfo is { } info
                ? string.Format(Strings.Firmware_NowRuns, info.ModelName, FirmwareVersion.OfDevice(info.GitVersion, info.FirmwareVersion)?.ToString() ?? info.FirmwareVersion)
                : Strings.Firmware_Restarted;
            Dialog = FirmwareDialog.Done;
        }
        catch (Exception ex)
        {
            InstallTitle = Strings.Firmware_Failed;
            Error = ex.Message;
            Dialog = FirmwareDialog.Failed;
        }
    }

    private void SetTransport()
    {
        _overBle = _device.Candidate?.Kind == TransportKind.Ble;
        this.RaisePropertyChanged(nameof(InstallNotes));
        this.RaisePropertyChanged(nameof(InstallHint));
        this.RaisePropertyChanged(nameof(KeepConnectedHint));
    }

    private void SetStep(string text, double? fraction = null)
    {
        StepText = text;
        ProgressKnown = fraction is not null;
        Progress = (fraction ?? 0) * 100;
    }

    private void Close()
    {
        if (IsInstalling) return;
        Dialog = FirmwareDialog.None;
    }

    private async Task OpenReleaseAsync()
    {
        var url = _latest?.PageUrl ?? $"https://github.com/{FirmwareUpdateService.Repository}/releases";
        if (StorageService.TopLevelAccessor?.Invoke()?.Launcher is { } launcher)
            await launcher.LaunchUriAsync(new Uri(url));
    }
}
