using System.Windows.Input;
using AegiNext.Desktop.I18n;
using AegiNext.Desktop.Menus;
using AegiNext.Desktop.Settings;
using AegiNext.Desktop.Shortcuts;
using AegiNext.Desktop.Updates;
using AegiNext.Desktop.Views;
using AegiNext.Desktop.Workspace;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;

namespace AegiNext.Desktop.Startup;

internal sealed class DesktopStartupCoordinator : IAsyncDisposable
{
    private readonly Action<Window> setMainWindow;
    private readonly Action shutdown;
    private readonly DesktopApplicationContext context;
    private readonly SettingsWindowCoordinator settings;
    private readonly UpdateWindowCoordinator updates;
    private readonly IWorkbenchDialogService dialogs;
    private readonly Func<IWorkbenchDialogService, DesktopApplicationContext, WorkbenchSession> sessionFactory;
    private readonly CancellationTokenSource cancellation = new();
    private readonly HashSet<Key> pressedKeys = [];
    private readonly WorkbenchMenuCatalog menuCatalog;
    private readonly WorkbenchApplicationMenu? applicationMenu;
    private readonly WelcomeWindowMenu welcomeMenu;
    private readonly AsyncRelayCommand checkUpdatesCommand;
    private readonly RelayCommand exitCommand;
    private readonly RelayCommand unavailableCommand = new(() => { }, () => false);
    private ShortcutRouter shortcuts;
    private Window activeWindow;
    private Task operation = Task.CompletedTask;
    private Task? disposeTask;
    private Task? shutdownTask;
    private Task? automaticUpdateCheck;
    private bool closing;
    private bool allowWelcomeClose;
    private readonly IClassicDesktopStyleApplicationLifetime? desktopLifetime;

    internal DesktopStartupCoordinator(IClassicDesktopStyleApplicationLifetime desktop, DesktopApplicationContext? context = null)
        : this(window => desktop.MainWindow = window, () => desktop.Shutdown(), context)
    {
        desktopLifetime = desktop;
        desktop.ShutdownRequested += OnShutdownRequested;
    }

    internal DesktopStartupCoordinator(Action<Window> setMainWindow, Action shutdown,
        DesktopApplicationContext? context = null,
        Func<IWorkbenchDialogService, DesktopApplicationContext, WorkbenchSession>? sessionFactory = null,
        IWorkbenchDialogService? dialogs = null)
    {
        this.setMainWindow = setMainWindow;
        this.shutdown = shutdown;
        this.context = context ?? new();
        this.sessionFactory = sessionFactory ?? ((service, applicationContext) => new(service, applicationContext: applicationContext));
        settings = new(this.context, requestApplicationExit: RequestApplicationExit);
        updates = new(this.context.Updates, () => this.context.Preferences);
        checkUpdatesCommand = new(() => CheckForUpdatesAfterInitializationAsync(UpdateCheckTrigger.MANUAL),
            AsyncRelayCommandOptions.AllowConcurrentExecutions);
        var viewModel = new WelcomeViewModel(this.context.RecentProjects,
            () => BeginOpen(true, null), path => BeginOpen(false, path), OpenSettingsAsync);
        WelcomeWindow = new(viewModel);
        activeWindow = WelcomeWindow;
        this.dialogs = dialogs ?? new WindowWorkbenchDialogService(() => activeWindow,
            registerWindow: RegisterAuxiliaryWindow);
        shortcuts = new(this.context.Preferences.ShortcutBindings);
        exitCommand = new(RequestApplicationExit);
        menuCatalog = new(GetWelcomeCommand);
        menuCatalog.Update(this.context.Preferences);
        welcomeMenu = new(WelcomeWindow, WelcomeWindow.TitleBar, menuCatalog);
        welcomeMenu.UpdatePreferences(this.context.Preferences);
        if (OperatingSystem.IsMacOS() && Avalonia.Application.Current is { } application)
        {
            applicationMenu = new(application, menuCatalog);
        }
        this.context.PreferencesChanged += OnPreferencesChanged;
        this.context.ErrorChanged += OnApplicationError;
        Localization.LanguageChanged += OnLanguageChanged;
        WelcomeWindow.Closing += OnWelcomeClosing;
        WelcomeWindow.Deactivated += OnDeactivated;
        WelcomeWindow.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        WelcomeWindow.AddHandler(InputElement.KeyUpEvent, OnKeyUp, RoutingStrategies.Tunnel);
        OnApplicationError(this, EventArgs.Empty);
    }

    internal WelcomeWindow WelcomeWindow { get; }
    internal MainWindow? MainWindow { get; private set; }
    internal DesktopApplicationContext ApplicationContext => context;
    internal Task Completion => disposeTask ?? operation;
    internal Task AutomaticUpdateCheck => automaticUpdateCheck ?? Task.CompletedTask;

    internal void Start()
    {
        setMainWindow(WelcomeWindow);
        WelcomeWindow.Show();
        updates.SetOwner(WelcomeWindow);
        automaticUpdateCheck ??= CheckForUpdatesAfterInitializationAsync(UpdateCheckTrigger.AUTOMATIC);
    }

    private async Task CheckForUpdatesAfterInitializationAsync(UpdateCheckTrigger trigger)
    {
        try
        {
            await context.Initialization.WaitAsync(cancellation.Token);
            if (!closing)
            {
                await (trigger == UpdateCheckTrigger.AUTOMATIC ? updates.CheckAutomaticallyAsync() : updates.CheckManuallyAsync());
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private Task BeginOpen(bool create, string? path)
    {
        if (closing || MainWindow is not null || WelcomeWindow.ViewModel.IsBusy ||
            WelcomeWindow.OwnedWindows.Any(window => window.IsVisible && window is not UpdateCheckWindow))
        {
            return Task.CompletedTask;
        }
        WelcomeWindow.ViewModel.IsBusy = true;
        WelcomeWindow.ViewModel.Error = null;
        updates.BeginOwnerTransition();
        operation = PrepareAsync(create, path);
        return operation;
    }

    private async Task PrepareAsync(bool create, string? path)
    {
        WorkbenchSession? candidate = null;
        MainWindow? pendingWindow = null;
        try
        {
            ProjectOpenResult? result = null;
            if (create)
            {
                var created = await dialogs.ShowNewProjectAsync(context.Preferences.Projects.WorkspaceRoot,
                    async (request, token) =>
                    {
                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancellation.Token);
                        await context.Initialization;
                        linked.Token.ThrowIfCancellationRequested();
                        WorkbenchSession? trial = sessionFactory(dialogs, context);
                        try
                        {
                            var attempt = await trial.CreateProjectFromDialogAsync(request, linked.Token);
                            if (attempt.Status == ProjectOpenStatus.OPENED)
                            {
                                candidate = trial;
                                result = attempt;
                                trial = null;
                            }
                            else
                            {
                                linked.Token.ThrowIfCancellationRequested();
                            }
                            return attempt;
                        }
                        finally
                        {
                            if (trial is not null)
                            {
                                await trial.DisposeAsync();
                            }
                        }
                    }, cancellation.Token);
                if (!created || candidate is null || result is null)
                {
                    return;
                }
            }
            else
            {
                if (path is null)
                {
                    path = await dialogs.OpenFileAsync("OpenProject", "Projects", ["*.aeginext"]);
                    if (path is null)
                    {
                        return;
                    }
                }
                cancellation.Token.ThrowIfCancellationRequested();
                await context.Initialization;
                cancellation.Token.ThrowIfCancellationRequested();
                candidate = sessionFactory(dialogs, context);
                result = await candidate.OpenProjectAsync(path, cancellation.Token);
            }
            if (result.Status != ProjectOpenStatus.OPENED)
            {
                WelcomeWindow.ViewModel.Error = result.Error?.Message;
                return;
            }
            foreach (var diagnostic in result.Diagnostics)
            {
                candidate.ShowError(diagnostic);
            }
            cancellation.Token.ThrowIfCancellationRequested();
            var window = new MainWindow(candidate, updates);
            pendingWindow = window;
            MainWindow = window;
            activeWindow = window;
            window.Closed += OnMainWindowClosed;
            setMainWindow(window);
            window.Show();
            candidate = null;
            pendingWindow = null;
            WelcomeWindow.Hide();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            if (!closing)
            {
                WelcomeWindow.ViewModel.Error = create && candidate?.ProjectPath is { } createdPath
                    ? Localization.Format("Workbench.NewProjectActivationFailed", createdPath, error.Message)
                    : error.Message;
            }
        }
        finally
        {
            if (pendingWindow is not null)
            {
                pendingWindow.Closed -= OnMainWindowClosed;
                await pendingWindow.DisposeAsync();
                pendingWindow.Close();
                MainWindow = null;
                activeWindow = WelcomeWindow;
                setMainWindow(WelcomeWindow);
            }
            if (candidate is not null)
            {
                await candidate.DisposeAsync();
            }
            WelcomeWindow.ViewModel.IsBusy = false;
            if (!closing)
            {
                updates.SetOwner(activeWindow, MainWindow is { } main ? main.WindowRegistry.RegisterAuxiliary : null);
            }
        }
    }

    private async Task OpenSettingsAsync()
    {
        if (closing)
        {
            return;
        }
        if (MainWindow is { } window)
        {
            await window.Session.RequestSettingsAsync();
            return;
        }
        await settings.OpenAsync(WelcomeWindow, modal: true);
    }

    private void RegisterAuxiliaryWindow(Window window)
    {
        MainWindow?.WindowRegistry.RegisterAuxiliary(window);
    }

    private ICommand GetWelcomeCommand(WorkbenchCommand command) => command switch
    {
        WorkbenchCommand.NEW_PROJECT => WelcomeWindow.ViewModel.NewProjectCommand,
        WorkbenchCommand.OPEN_PROJECT => WelcomeWindow.ViewModel.OpenProjectCommand,
        WorkbenchCommand.OPEN_SETTINGS => WelcomeWindow.ViewModel.SettingsCommand,
        WorkbenchCommand.CHECK_UPDATES => checkUpdatesCommand,
        WorkbenchCommand.EXIT => exitCommand,
        _ => unavailableCommand
    };

    private void OnPreferencesChanged(object? sender, EventArgs e)
    {
        shortcuts = new(context.Preferences.ShortcutBindings);
        menuCatalog.Update(context.Preferences);
        welcomeMenu.UpdatePreferences(context.Preferences);
        updates.UpdatePreferences();
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => menuCatalog.RefreshLanguage();

    private void OnApplicationError(object? sender, EventArgs e)
    {
        if (!closing && MainWindow is null && context.LastError is { } error)
        {
            WelcomeWindow.ViewModel.Error = error.Message;
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || closing || WelcomeWindow.OwnedWindows.Any(window => window.IsVisible && window is not UpdateCheckWindow) ||
            WelcomeWindow.GetVisualDescendants().OfType<Control>().Any(control =>
                control.ContextMenu?.IsOpen == true || control.ContextFlyout?.IsOpen == true ||
                control is Popup { IsOpen: true } || control is MenuBase { IsOpen: true }))
        {
            return;
        }
        var isTextInput = e.Source is Avalonia.Visual visual &&
            visual.GetSelfAndVisualAncestors().Any(item => item is TextBox);
        if (shortcuts.TryResolve(e.Key, e.KeyModifiers, isTextInput, out var command))
        {
            var action = GetWelcomeCommand(command);
            if (action.CanExecute(null))
            {
                if (pressedKeys.Add(e.Key))
                {
                    action.Execute(null);
                }
                e.Handled = true;
            }
        }
    }

    private void OnDeactivated(object? sender, EventArgs e) => pressedKeys.Clear();

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (pressedKeys.Remove(e.Key))
        {
            e.Handled = true;
        }
    }

    private void OnWelcomeClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!allowWelcomeClose)
        {
            e.Cancel = true;
            RequestApplicationExit();
        }
    }

    private void OnMainWindowClosed(object? sender, EventArgs e)
    {
        if (sender is not MainWindow window || !ReferenceEquals(window, MainWindow))
        {
            return;
        }

        window.Closed -= OnMainWindowClosed;
        updates.ReleaseOwner(window);
        MainWindow = null;
        if (window.IsApplicationExitRequested)
        {
            _ = RequestShutdownAsync();
            return;
        }

        activeWindow = WelcomeWindow;
        setMainWindow(WelcomeWindow);
        pressedKeys.Clear();
        WelcomeWindow.ViewModel.RefreshAvailability();
        WelcomeWindow.ViewModel.Error = null;
        WelcomeWindow.Show();
        WelcomeWindow.Activate();
        updates.SetOwner(WelcomeWindow);
    }

    private void RequestApplicationExit()
    {
        if (closing)
        {
            return;
        }
        if (MainWindow is { } window)
        {
            window.RequestApplicationExit();
        }
        else
        {
            _ = RequestShutdownAsync();
        }
    }

    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        e.Cancel = true;
        if (!closing)
        {
            if (MainWindow is { } window)
            {
                window.RequestApplicationExit();
            }
            else
            {
                WelcomeWindow.Close();
            }
        }
    }

    private Task RequestShutdownAsync()
    {
        shutdownTask ??= ShutdownCoreAsync();
        return shutdownTask;
    }

    private async Task ShutdownCoreAsync()
    {
        await DisposeAsync();
        shutdown();
    }

    public ValueTask DisposeAsync()
    {
        if (disposeTask is not null)
        {
            return new(disposeTask);
        }
        closing = true;
        cancellation.Cancel();
        disposeTask ??= DisposeCoreAsync();
        return new(disposeTask);
    }

    private async Task DisposeCoreAsync()
    {
        updates.Dispose();
        await context.Updates.DisposeAsync();
        if (automaticUpdateCheck is { } updateCheck)
        {
            await updateCheck;
        }
        settings.Dispose();
        await settings.TransferCompletion;
        await operation;
        if (desktopLifetime is { } lifetime)
        {
            lifetime.ShutdownRequested -= OnShutdownRequested;
        }
        context.PreferencesChanged -= OnPreferencesChanged;
        context.ErrorChanged -= OnApplicationError;
        Localization.LanguageChanged -= OnLanguageChanged;
        applicationMenu?.Dispose();
        welcomeMenu.Dispose();
        if (MainWindow is { } window)
        {
            window.Closed -= OnMainWindowClosed;
            await window.DisposeAsync();
            window.Close();
            MainWindow = null;
        }
        WelcomeWindow.Closing -= OnWelcomeClosing;
        WelcomeWindow.Deactivated -= OnDeactivated;
        WelcomeWindow.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
        WelcomeWindow.RemoveHandler(InputElement.KeyUpEvent, OnKeyUp);
        allowWelcomeClose = true;
        WelcomeWindow.Close();
        await context.DisposeAsync();
        cancellation.Dispose();
    }
}
