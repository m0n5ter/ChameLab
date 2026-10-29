using System;
using System.Threading.Tasks;
using System.Windows.Input;
using ReactiveUI;

namespace ChameLab.ViewModels;

/// <summary>
/// Base for navigable pages: carries a title/icon, a busy flag and the page's messages,
/// plus helpers to run device work with consistent error surfacing.
/// Messages come in three kinds, each shown where it gets in the way least:
/// <list type="bullet">
/// <item><see cref="Notice"/>: a short confirmation that clears itself and never takes input.</item>
/// <item><see cref="Error"/>: a failure, shown above the page (pushing it down) until dismissed,
/// a later operation succeeds or the page is left.</item>
/// <item><see cref="SaveError"/>: why the open edits could not be saved, shown in the save bar.</item>
/// </list>
/// </summary>
public abstract class PageViewModelBase : ViewModelBase
{
    private bool _isBusy;
    private string? _notice;
    private string? _error;
    private string? _saveError;

    protected PageViewModelBase(string title, string icon)
    {
        Title = title;
        Icon = icon;
        DismissErrorCommand = ReactiveCommand.Create(ClearError);
    }

    /// <summary>How long a notice stays on screen before it clears itself.</summary>
    private static readonly TimeSpan NoticeLifetime = TimeSpan.FromSeconds(3);

    public ICommand DismissErrorCommand { get; }

    public string Title { get; }

    /// <summary>Material-style glyph key resolved by the view (see IconConverter).</summary>
    public string Icon { get; }

    /// <summary>
    /// Width the page needs beside the docked nav pane before it has to squeeze;
    /// below it the pane overlays the content instead.
    /// </summary>
    public virtual double MinContentWidth => 0;

    public bool IsBusy
    {
        get => _isBusy;
        private set => this.RaiseAndSetIfChanged(ref _isBusy, value);
    }

    /// <summary>A passing confirmation (see <see cref="Notify"/>).</summary>
    public string? Notice
    {
        get => _notice;
        private set => this.RaiseAndSetIfChanged(ref _notice, value);
    }

    /// <summary>The last failure on this page (see <see cref="ReportError"/>).</summary>
    public string? Error
    {
        get => _error;
        private set => this.RaiseAndSetIfChanged(ref _error, value);
    }

    /// <summary>Why the open edits could not be saved (see <see cref="ReportSaveError"/>).</summary>
    public string? SaveError
    {
        get => _saveError;
        private set => this.RaiseAndSetIfChanged(ref _saveError, value);
    }

    /// <summary>Called when the page becomes visible; override to refresh data.</summary>
    public virtual Task OnActivatedAsync() => Task.CompletedTask;

    /// <summary>Called when the page is left for another; override to undo anything the page set up.</summary>
    public virtual Task OnDeactivatedAsync() => Task.CompletedTask;

    /// <summary>
    /// Confirms something whose result is not already on screen. Saving, deleting or duplicating
    /// shows for itself (the save bar goes, the list changes), so those need no notice.
    /// </summary>
    protected void Notify(string message) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        Notice = message;
        Avalonia.Threading.DispatcherTimer.RunOnce(() =>
        {
            if (ReferenceEquals(Notice, message)) Notice = null;
        }, NoticeLifetime);
    });

    /// <summary>Reports a failure above the page. It stays until dismissed, replaced or outdated.</summary>
    protected void ReportError(string message) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        Notice = null;
        Error = message;
    });

    /// <summary>Reports why the open edits could not be saved, next to the Save button.</summary>
    protected void ReportSaveError(string message) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => SaveError = message);

    protected void ClearError() => Avalonia.Threading.Dispatcher.UIThread.Post(() => Error = null);

    /// <summary>Call when a save starts, the edits are dropped or another item opens.</summary>
    protected void ClearSaveError() => Avalonia.Threading.Dispatcher.UIThread.Post(() => SaveError = null);

    /// <summary>The page was left: its messages belonged to what was done there.</summary>
    public void ClearMessages()
    {
        Notice = null;
        Error = null;
    }

    /// <summary>Runs device work; a failure goes above the page. Starting it clears an earlier failure.</summary>
    protected Task RunAsync(Func<Task> work, string? successMessage = null) =>
        RunAsync(work, successMessage, ReportError);

    /// <summary>Like <see cref="RunAsync(Func{Task}, string?)"/>, for saving edits: a failure shows in the save bar.</summary>
    protected Task RunSaveAsync(Func<Task> work, string? successMessage = null)
    {
        ClearSaveError();
        return RunAsync(work, successMessage, ReportSaveError);
    }

    private async Task RunAsync(Func<Task> work, string? successMessage, Action<string> report)
    {
        if (IsBusy) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => IsBusy = true);
        ClearError();
        try
        {
            await work();
            if (successMessage is not null) Notify(successMessage);
        }
        catch (Exception ex)
        {
            report(ex.Message);
        }
        finally
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => IsBusy = false);
        }
    }
}
