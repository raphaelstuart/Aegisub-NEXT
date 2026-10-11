using AegiNext.Desktop.Settings;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;

namespace AegiNext.Desktop.Updates;

internal sealed class UpdateWindowCoordinator : IDisposable
{
    private readonly UpdateCheckService service;
    private readonly Func<WorkbenchPreferences> preferences;
    private readonly Func<Uri, Task<bool>> launchUri;
    private Window? owner;
    private Action<Window>? registerWindow;
    private UpdateCheckCompletedEventArgs? retainedResult;
    private UpdateCheckCompletedEventArgs? presentedResult;
    private bool pendingPresentation;
    private bool disposed;

    internal UpdateWindowCoordinator(UpdateCheckService service, Func<WorkbenchPreferences> preferences,
        Func<Uri, Task<bool>>? launchUri = null)
    {
        this.service = service;
        this.preferences = preferences;
        this.launchUri = launchUri ?? (uri => CurrentWindow?.Launcher.LaunchUriAsync(uri) ?? Task.FromResult(false));
        service.Completed += OnCheckCompleted;
    }

    internal UpdateCheckWindow? CurrentWindow { get; private set; }

    internal void UpdatePreferences()
    {
        if (CurrentWindow is { } window)
        {
            window.RequestedThemeVariant = preferences().Theme switch
            {
                WorkbenchTheme.LIGHT => ThemeVariant.Light,
                WorkbenchTheme.DARK => ThemeVariant.Dark,
                _ => ThemeVariant.Default
            };
        }
    }

    internal Task CheckManuallyAsync()
    {
        if (disposed)
        {
            return Task.CompletedTask;
        }
        if (CurrentWindow is { } window && presentedResult?.Channel == preferences().UpdateChannel)
        {
            window.Activate();
            return Task.CompletedTask;
        }
        if (retainedResult is { } result && result.Channel == preferences().UpdateChannel)
        {
            Present(result);
            return Task.CompletedTask;
        }
        return CheckAsync(UpdateCheckTrigger.MANUAL);
    }

    internal Task CheckAutomaticallyAsync() => disposed || !preferences().AutoCheckUpdates
        ? Task.CompletedTask : CheckAsync(UpdateCheckTrigger.AUTOMATIC);

    internal void BeginOwnerTransition()
    {
        if (CurrentWindow is { } window)
        {
            retainedResult = presentedResult;
            window.Close();
        }
        owner = null;
        registerWindow = null;
    }

    internal void ReleaseOwner(Window window)
    {
        if (ReferenceEquals(owner, window))
        {
            BeginOwnerTransition();
        }
    }

    internal void SetOwner(Window window, Action<Window>? register = null)
    {
        if (disposed)
        {
            return;
        }
        if (!ReferenceEquals(owner, window))
        {
            BeginOwnerTransition();
        }
        owner = window;
        registerWindow = register;
        if (pendingPresentation && retainedResult is { } result)
        {
            Present(result);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        disposed = true;
        service.Completed -= OnCheckCompleted;
        CurrentWindow?.Close();
        owner = null;
        registerWindow = null;
        retainedResult = null;
        presentedResult = null;
    }

    private async Task CheckAsync(UpdateCheckTrigger trigger)
    {
        if (disposed)
        {
            return;
        }
        retainedResult = null;
        try
        {
            await service.CheckAsync(trigger, preferences().UpdateChannel);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException) when (disposed)
        {
        }
    }

    private void OnCheckCompleted(object? sender, UpdateCheckCompletedEventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            Present(e);
        }
        else
        {
            Dispatcher.UIThread.Post(() => Present(e));
        }
    }

    private void Present(UpdateCheckCompletedEventArgs result)
    {
        if (disposed || result.Trigger == UpdateCheckTrigger.AUTOMATIC && result.Result.Status != UpdateCheckStatus.UPDATE_AVAILABLE)
        {
            return;
        }
        if (owner is null || !owner.IsVisible)
        {
            retainedResult = result;
            pendingPresentation = true;
            return;
        }
        pendingPresentation = false;
        retainedResult = null;
        presentedResult = result;
        if (CurrentWindow is { } existing)
        {
            existing.ViewModel.Update(result.Result, result.Channel);
            existing.Activate();
            return;
        }
        var viewModel = new UpdateCheckViewModel(result.Result, result.Channel,
            () => CheckAsync(UpdateCheckTrigger.MANUAL), launchUri);
        var window = new UpdateCheckWindow(viewModel);
        CurrentWindow = window;
        UpdatePreferences();
        window.Closed += OnWindowClosed;
        registerWindow?.Invoke(window);
        window.Show(owner);
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (sender is UpdateCheckWindow window)
        {
            window.Closed -= OnWindowClosed;
            if (ReferenceEquals(CurrentWindow, window))
            {
                CurrentWindow = null;
            }
        }
    }
}
