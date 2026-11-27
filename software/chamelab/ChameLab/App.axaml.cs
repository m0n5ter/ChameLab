using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using ChameLab.Localization;
using ChameLab.Services;
using ChameLab.Sync;
using ChameLab.ViewModels;
using ChameLab.Views;

namespace ChameLab;

public partial class App : Application
{
    private const string TrayBalloonShownSetting = "trayBalloonShown";
    private static readonly object ActivationGate = new();
    private static App? _desktopInstance;
    private static bool _activationPending;

    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private MainViewModel? _desktopVm;
    private TrayIcon? _tray;

    public override void Initialize()
    {
        // Migrate saved key search defaults once, before any recovery panel reads them.
        _ = MifareKeySearchSettingsViewModel.Shared;
        // Before any XAML loads, so every view reads its strings in the chosen language.
        LocalizationService.ApplySaved();
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>Quit was chosen from the tray: closing the window really closes the app.</summary>
    public bool IsQuitting { get; private set; }

    /// <summary>
    /// The desktop app stays running with its window closed while it syncs in the background
    /// (Settings → Device sync), so the user's other devices find it.
    /// Debug builds exit immediately when the window closes instead of staying in the tray.
    /// </summary>
#if DEBUG
    public bool KeepsRunning => false;
#else
    public bool KeepsRunning => _desktop is not null && AppServices.Sync.IsEnabled && SyncService.RunInBackground;
#endif

    public static Task ActivateDesktopAsync()
    {
        App? app;
        lock (ActivationGate)
        {
            app = _desktopInstance;
            if (app is null)
            {
                _activationPending = true;
                return Task.CompletedTask;
            }
        }
        return Dispatcher.UIThread.InvokeAsync(app.ShowWindow).GetTask();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        AppearanceService.ApplySaved();
        StartSync();
        var vm = new MainViewModel(AppServices.Device, AppServices.Cards, AppServices.Dictionaries, AppServices.Firmware);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            _desktopVm = vm;
            bool pendingActivation;
            lock (ActivationGate)
            {
                _desktopInstance = this;
                pendingActivation = _activationPending;
                _activationPending = false;
            }
            desktop.Exit += (_, _) =>
            {
                lock (ActivationGate)
                {
                    if (ReferenceEquals(_desktopInstance, this)) _desktopInstance = null;
                }
            };
            // Started with the system: only the tray icon until the user opens the window.
            var background = !pendingActivation && desktop.Args?.Contains(Autostart.BackgroundArgument) == true && KeepsRunning;
            if (!background) desktop.MainWindow = new MainWindow { DataContext = vm };
            UpdateBackground();
            SyncViewModel.BackgroundChanged += UpdateBackground;
            AppServices.Sync.Changed += UpdateBackground;
            if (this.TryGetFeature<IActivatableLifetime>() is { } activatable)
                activatable.Activated += (_, e) => { if (e.Kind == ActivationKind.Reopen) Dispatcher.UIThread.Post(ShowWindow); };
        }
        else if (ApplicationLifetime is IActivityApplicationLifetime activityLifetime)
        {
            activityLifetime.MainViewFactory = () => new MainView { DataContext = new MainViewModel(AppServices.Device, AppServices.Cards, AppServices.Dictionaries, AppServices.Firmware) };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
        {
            singleViewPlatform.MainView = new MainView { DataContext = vm };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Sync edits the libraries on the UI thread and runs while the app runs; phones connect only
    /// while ChameLab is in the foreground (the system would cut the connection anyway).
    /// </summary>
    private void StartSync()
    {
        SyncService.Ui = action => Dispatcher.UIThread.Post(action);
        LibrarySync.ConflictSuffix = () => Strings.Sync_ConflictSuffix;
        var sync = AppServices.Sync;
        sync.Start();
        if ((OperatingSystem.IsAndroid() || OperatingSystem.IsIOS()) && this.TryGetFeature<IActivatableLifetime>() is { } lifetime)
        {
            lifetime.Activated += (_, e) => { if (e.Kind == ActivationKind.Background) sync.Start(); };
            lifetime.Deactivated += (_, e) => { if (e.Kind == ActivationKind.Background) _ = sync.StopAsync(); };
        }
    }

    /// <summary>Shows the tray icon and keeps the app alive with no window while syncing in the background.</summary>
    private void UpdateBackground()
    {
        if (_desktop is null) return;
        var keep = KeepsRunning;
        _desktop.ShutdownMode = keep ? ShutdownMode.OnExplicitShutdown : ShutdownMode.OnMainWindowClose;
        if (keep && _tray is null)
        {
            var open = new NativeMenuItem(Strings.Tray_Open);
            open.Click += (_, _) => ShowWindow();
            var quit = new NativeMenuItem(Strings.Tray_Quit);
            quit.Click += (_, _) => Quit();
            _tray = new TrayIcon
            {
                Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://ChameLab/Assets/chameleon.ico"))),
                ToolTipText = Strings.Tray_Tooltip,
                Menu = new NativeMenu { Items = { open, new NativeMenuItemSeparator(), quit } },
            };
            _tray.Clicked += (_, _) => ShowWindow();
            TrayIcon.SetIcons(this, new TrayIcons { _tray });
        }
        else if (!keep && _tray is not null)
        {
            TrayIcon.SetIcons(this, new TrayIcons());
            _tray.Dispose();
            _tray = null;
            // Nothing keeps a hidden app alive now: show it so it can be closed.
            if (_desktop.MainWindow is not { IsVisible: true }) ShowWindow();
        }
    }

    private void ShowWindow()
    {
        if (_desktop is null) return;
        if (_desktop.MainWindow is null) _desktop.MainWindow = new MainWindow { DataContext = _desktopVm };
        var window = _desktop.MainWindow;
        window.Show();
        if (window is MainWindow main) main.RestoreIfMinimized();
        else if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        DesktopHost.BringToFront?.Invoke(window);
    }

    internal void NotifyHiddenToTray()
    {
        if (_desktop is null || !KeepsRunning) return;
        if (_tray is null) UpdateBackground();
        if (_tray is null || DesktopHost.ShowTrayBalloon is not { } show) return;
        if (TryConsumeFirstTrayHide()) show(_tray, Strings.Tray_HiddenTitle, Strings.Tray_HiddenMessage);
    }

    internal static bool TryConsumeFirstTrayHide()
    {
        var shown = false;
        AppSettings.Update(TrayBalloonShownSetting, existing =>
        {
            if (existing is JsonValue value && value.TryGetValue<bool>(out var alreadyShown) && alreadyShown)
                return existing;
            shown = true;
            return JsonValue.Create(true);
        });
        return shown;
    }

    private void Quit()
    {
        IsQuitting = true;
        _ = AppServices.Sync.StopAsync();
        _desktop?.Shutdown();
    }
}
