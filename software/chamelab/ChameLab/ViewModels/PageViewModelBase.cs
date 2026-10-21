using System;
using System.Threading.Tasks;
using System.Windows.Input;
using ReactiveUI;

namespace ChameLab.ViewModels;

/// <summary>
/// Base for navigable pages: carries a title/icon, a busy flag and a status
/// line, plus helpers to run device work with consistent error surfacing.
/// </summary>
public abstract class PageViewModelBase : ViewModelBase
{
    private bool _isBusy;
    private string? _status;
    private bool _statusIsError;

    protected PageViewModelBase(string title, string icon)
    {
        Title = title;
        Icon = icon;
        DismissStatusCommand = ReactiveCommand.Create(() => { Status = null; });
    }

    /// <summary>How long a non-error status stays on screen before it clears itself.</summary>
    private static readonly TimeSpan StatusLifetime = TimeSpan.FromSeconds(5);

    public ICommand DismissStatusCommand { get; }

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

    public string? Status
    {
        get => _status;
        protected set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    public bool StatusIsError
    {
        get => _statusIsError;
        protected set => this.RaiseAndSetIfChanged(ref _statusIsError, value);
    }

    /// <summary>Called when the page becomes visible; override to refresh data.</summary>
    public virtual Task OnActivatedAsync() => Task.CompletedTask;

    protected void SetStatus(string message, bool isError = false)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Status = message;
            StatusIsError = isError;
            if (isError) return;
            // Success/info messages fade out on their own unless replaced meanwhile.
            Avalonia.Threading.DispatcherTimer.RunOnce(() =>
            {
                if (ReferenceEquals(Status, message) && !StatusIsError) Status = null;
            }, StatusLifetime);
        });
    }

    protected async Task RunAsync(Func<Task> work, string? successMessage = null)
    {
        if (IsBusy) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => IsBusy = true);
        try
        {
            await work();
            if (successMessage is not null) SetStatus(successMessage);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, isError: true);
        }
        finally
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => IsBusy = false);
        }
    }
}
