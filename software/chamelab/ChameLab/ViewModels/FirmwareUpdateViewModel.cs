using System;
using System.Threading.Tasks;
using System.Windows.Input;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;
using ChameLab.Transport;
using ReactiveUI;

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
    private string _checkMessage = string.Empty;
    private FirmwareRelease? _latest;
    private DeviceModel _latestModel;
    private DateTime _checkedAt;
    private string _currentVersion = "—";

    private FirmwareDialog _dialog;
    private string _installTitle = string.Empty;
    private string _stepText = string.Empty;
    private double _progress;
    private bool _progressKnown;
    private string? _error;

    // What the dialog installs: a release to download, or a package file picked by the user.
    private FirmwareRelease? _pendingRelease;
    private byte[]? _pendingFile;
    // Kept for Retry: a failed flash leaves the device waiting in its bootloader.
    private DfuPackage? _package;
    private IDfuTarget? _target;

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
        get => _checkMessage;
        private set => this.RaiseAndSetIfChanged(ref _checkMessage, value);
    }

    public FirmwareRelease? Latest
    {
        get => _latest;
        private set => this.RaiseAndSetIfChanged(ref _latest, value);
    }

    public string CurrentVersion
    {
        get => _currentVersion;
        private set => this.RaiseAndSetIfChanged(ref _currentVersion, value);
    }

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
        get => _installTitle;
        private set => this.RaiseAndSetIfChanged(ref _installTitle, value);
    }

    /// <summary>Release notes of the version about to be installed (empty for a file).</summary>
    public string InstallNotes => _pendingRelease?.Notes.Trim() ?? string.Empty;

    public string StepText
    {
        get => _stepText;
        private set => this.RaiseAndSetIfChanged(ref _stepText, value);
    }

    /// <summary>0..100 while the step reports progress.</summary>
    public double Progress
    {
        get => _progress;
        private set => this.RaiseAndSetIfChanged(ref _progress, value);
    }

    public bool ProgressKnown
    {
        get => _progressKnown;
        private set => this.RaiseAndSetIfChanged(ref _progressKnown, value);
    }

    public string? Error
    {
        get => _error;
        private set => this.RaiseAndSetIfChanged(ref _error, value);
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
        CheckMessage = "Checking for updates…";
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
                CheckMessage = $"Up to date (latest release {_latest.Tag})";
            }
            else
            {
                CheckState = FirmwareCheckState.Available;
                CheckMessage = _latest!.PackageUrl is null
                    ? $"{_latest.Tag} is available, but has no package for this model"
                    : CanInstall
                        ? $"{_latest.Tag} is available"
                        : $"{_latest.Tag} is available — connect over USB or Bluetooth to install it";
            }
        }
        catch (Exception ex)
        {
            CheckState = FirmwareCheckState.Failed;
            CheckMessage = $"Couldn't check for updates: {ex.Message}";
        }
    }

    private void ShowInstallLatest()
    {
        if (_latest is null) return;
        _pendingRelease = _latest;
        _pendingFile = null;
        _package = null;
        _target = null;
        InstallTitle = $"Install firmware {_latest.Tag}?";
        this.RaisePropertyChanged(nameof(InstallNotes));
        Error = null;
        Dialog = FirmwareDialog.Confirm;
    }

    private async Task InstallFromFileAsync()
    {
        if (!CanInstall) return;
        PickedFile? picked;
        try { picked = await StorageService.OpenAsync("Firmware DFU package", "zip"); }
        catch (Exception ex) { Error = ex.Message; Dialog = FirmwareDialog.Failed; return; }
        if (picked is not { } file) return;

        try { _package = DfuPackage.Parse(file.Data); }
        catch (Exception ex)
        {
            _package = null;
            _pendingRelease = null;
            _pendingFile = null;
            Error = $"{file.Name}: {ex.Message}";
            InstallTitle = "Can't install this file";
            Dialog = FirmwareDialog.Failed;
            return;
        }
        _pendingRelease = null;
        _pendingFile = file.Data;
        _target = null;
        InstallTitle = $"Install firmware from {file.Name}?";
        this.RaisePropertyChanged(nameof(InstallNotes));
        Error = null;
        Dialog = FirmwareDialog.Confirm;
    }

    private async Task InstallAsync()
    {
        Dialog = FirmwareDialog.Installing;
        Error = null;
        var name = _pendingRelease?.Tag ?? "the firmware";
        InstallTitle = $"Installing {name}";
        try
        {
            if (_package is null)
            {
                SetStep("Downloading…", 0);
                var zip = _pendingFile ?? await _service.DownloadAsync(_pendingRelease!,
                    new Progress<double>(f => SetStep("Downloading…", f)));
                _package = DfuPackage.Parse(zip);
            }
            // Prepared before the restart, so a retry still knows which bootloader is this device's.
            _target ??= _service.PrepareTarget();

            await _service.InstallAsync(_package, _target, new Progress<FirmwareUpdateProgress>(p =>
            {
                switch (p.Step)
                {
                    case FirmwareUpdateStep.Restarting: SetStep("Restarting the device into its bootloader…"); break;
                    case FirmwareUpdateStep.WaitingForBootloader: SetStep("Waiting for the bootloader…"); break;
                    case FirmwareUpdateStep.Flashing: SetStep($"Installing… {p.Fraction:P0}", p.Fraction); break;
                    case FirmwareUpdateStep.Reconnecting: SetStep("Waiting for the device to restart…"); break;
                }
            }));

            _package = null;
            _target = null;
            InstallTitle = "Firmware updated";
            StepText = _device.DeviceInfo is { } info
                ? $"{info.ModelName} now runs {FirmwareVersion.OfDevice(info.GitVersion, info.FirmwareVersion)?.ToString() ?? info.FirmwareVersion}. Slots and settings were kept."
                : "The device restarted with the new firmware.";
            Dialog = FirmwareDialog.Done;
        }
        catch (Exception ex)
        {
            InstallTitle = "Firmware update failed";
            Error = ex.Message;
            Dialog = FirmwareDialog.Failed;
        }
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
