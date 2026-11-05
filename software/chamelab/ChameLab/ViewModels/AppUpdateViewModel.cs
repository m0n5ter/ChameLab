using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using ChameLab.Services;
using ReactiveUI;
using ChameLab.Localization;

namespace ChameLab.ViewModels;

public enum AppUpdateState { None, Checking, UpToDate, Available, Failed }

/// <summary>
/// An update channel as the Settings picker lists it. Equal by channel alone, so the choice
/// still matches its entry once the list is rebuilt in another language.
/// </summary>
public sealed record UpdateChannelOption(UpdateChannel Channel, string Name)
{
    public bool Equals(UpdateChannelOption? other) => other is not null && other.Channel == Channel;

    public override int GetHashCode() => Channel.GetHashCode();
}

/// <summary>
/// Checks GitHub for a newer ChameLab quietly at startup and every few hours after, and on demand
/// from Settings; a newer release shows up in the top bar and opens the website download section.
/// </summary>
public sealed class AppUpdateViewModel : ViewModelBase
{
    /// <summary>How often the quiet check repeats; dev builds land with every push to main.</summary>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(4);

    private AppUpdateState _state;
    private bool _running;
    private DispatcherTimer? _timer;
    private UpdateChannelOption _channel;

    public AppUpdateViewModel()
    {
        CheckCommand = ReactiveCommand.CreateFromTask(() => CheckAsync(quiet: false));
        OpenDownloadCommand = ReactiveCommand.CreateFromTask(OpenDownloadAsync);
        _channel = Channels.First(c => c.Channel == AppUpdateService.Channel);
    }

    public ICommand CheckCommand { get; }
    public ICommand OpenDownloadCommand { get; }

    public string CurrentVersion => AppUpdateService.CurrentVersion;

    /// <summary>False in the web app and in local builds, which have nothing to update.</summary>
    public bool IsSupported => AppUpdateService.IsSupported;

    /// <summary>Read afresh when the views are rebuilt for a new language.</summary>
    public IReadOnlyList<UpdateChannelOption> Channels =>
    [
        new(UpdateChannel.Stable, Strings.Settings_UpdateChannelStable),
        new(UpdateChannel.Dev, Strings.Settings_UpdateChannelDev),
    ];

    /// <summary>Which releases to offer; remembered, and checked right away when it changes.</summary>
    public UpdateChannelOption Channel
    {
        get => _channel;
        set
        {
            if (value is null || value.Channel == _channel.Channel) return;
            this.RaiseAndSetIfChanged(ref _channel, value);
            AppUpdateService.Channel = value.Channel;
            _ = CheckAsync(quiet: false);
        }
    }

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

    /// <summary>
    /// Checks now and then every <see cref="CheckInterval"/>. These checks are quiet: a failure
    /// (offline, rate limited) only shows up in Settings and keeps an update already found.
    /// </summary>
    public Task CheckInBackgroundAsync()
    {
        if (!IsSupported) return Task.CompletedTask;
        if (_timer is null)
        {
            _timer = new DispatcherTimer { Interval = CheckInterval };
            _timer.Tick += (_, _) => _ = CheckAsync(quiet: true);
            _timer.Start();
        }
        return CheckAsync(quiet: true);
    }

    private async Task CheckAsync(bool quiet)
    {
        if (_running || !IsSupported) return;
        _running = true;
        var channel = _channel.Channel;
        // A quiet check leaves the state alone until it knows, so the top bar icon doesn't blink.
        if (!quiet)
        {
            State = AppUpdateState.Checking;
            Message = Strings.Update_Checking;
        }
        try
        {
            var latest = await AppUpdateService.GetLatestAsync(channel);
            Latest = latest;
            if (AppUpdateService.IsNewer(latest))
            {
                State = AppUpdateState.Available;
                Message = string.Format(Strings.Update_AppAvailable, latest.Tag);
            }
            else
            {
                State = AppUpdateState.UpToDate;
                Message = Strings.Update_UpToDate;
            }
        }
        catch (Exception ex)
        {
            if (!quiet || State != AppUpdateState.Available)
            {
                State = AppUpdateState.Failed;
                Message = string.Format(Strings.Update_CheckFailed, ex.Message);
            }
        }
        finally
        {
            _running = false;
        }
        // The channel changed while this check ran: its answer is for the other one.
        if (channel != _channel.Channel) await CheckAsync(quiet: false);
    }

    private async Task OpenDownloadAsync()
    {
        var url = Latest?.IsDevelopment == true
            ? "https://m0n5ter.github.io/ChameLab/download/#dev"
            : "https://m0n5ter.github.io/ChameLab/download/#stable";
        if (StorageService.TopLevelAccessor?.Invoke()?.Launcher is { } launcher)
            await launcher.LaunchUriAsync(new Uri(url));
    }
}
