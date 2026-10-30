using System;
using System.Threading.Tasks;
using System.Windows.Input;
using ChameLab.Services;
using ReactiveUI;

namespace ChameLab.ViewModels;

public enum AppUpdateState { None, Checking, UpToDate, Available, Failed }

/// <summary>
/// Checks GitHub for a newer ChameLab once at startup (quietly) and on demand from Settings;
/// a newer release shows up in the top bar and opens the website download section.
/// </summary>
public sealed class AppUpdateViewModel : ViewModelBase
{
    private AppUpdateState _state;

    public AppUpdateViewModel()
    {
        CheckCommand = ReactiveCommand.CreateFromTask(CheckAsync);
        OpenDownloadCommand = ReactiveCommand.CreateFromTask(OpenDownloadAsync);
    }

    public ICommand CheckCommand { get; }
    public ICommand OpenDownloadCommand { get; }

    public string CurrentVersion => AppUpdateService.CurrentVersion;

    /// <summary>False in the web app and in local builds, which have nothing to update.</summary>
    public bool IsSupported => AppUpdateService.IsSupported;

    public AppUpdateState State
    {
        get => _state;
        private set
        {
            this.RaiseAndSetIfChanged(ref _state, value);
            this.RaisePropertyChanged(nameof(IsChecking));
            this.RaisePropertyChanged(nameof(IsUpdateAvailable));
        }
    }

    public bool IsChecking => _state == AppUpdateState.Checking;
    public bool IsUpdateAvailable => _state == AppUpdateState.Available;

    /// <summary>The outcome of the last check, for Settings; empty until one ran.</summary>
    public string Message
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    public AppRelease? Latest
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>The startup check: a failure (offline, rate limited) only shows up in Settings.</summary>
    public Task CheckInBackgroundAsync() => IsSupported ? CheckAsync() : Task.CompletedTask;

    private async Task CheckAsync()
    {
        if (IsChecking || !IsSupported) return;
        State = AppUpdateState.Checking;
        Message = "Checking for updates…";
        try
        {
            var latest = await AppUpdateService.GetLatestAsync();
            Latest = latest;
            if (AppUpdateService.IsNewer(latest))
            {
                State = AppUpdateState.Available;
                Message = $"ChameLab {latest.Tag} is available.";
            }
            else
            {
                State = AppUpdateState.UpToDate;
                Message = "You have the latest version.";
            }
        }
        catch (Exception ex)
        {
            State = AppUpdateState.Failed;
            Message = $"Couldn't check for updates: {ex.Message}";
        }
    }

    private async Task OpenDownloadAsync()
    {
        const string url = "https://m0n5ter.github.io/ChameLab/#stable";
        if (StorageService.TopLevelAccessor?.Invoke()?.Launcher is { } launcher)
            await launcher.LaunchUriAsync(new Uri(url));
    }
}
