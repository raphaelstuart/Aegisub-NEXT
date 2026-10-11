using AegiNext.Desktop.I18n;
using System.ComponentModel;
using System.Windows.Input;
using AegiNext.Core.Projects;
using AegiNext.Desktop.Controllers;
using AegiNext.Desktop.Controls.Common;
using AegiNext.Desktop.Layouts;
using AegiNext.Desktop.Menus;
using AegiNext.Desktop.Panels;
using AegiNext.Desktop.Panels.Preview;
using AegiNext.Desktop.Panels.Timeline;
using AegiNext.Desktop.Panels.Subtitles;
using AegiNext.Desktop.Panels.SubtitleDetails;
using AegiNext.Desktop.Panels.Styles;
using AegiNext.Desktop.Panels.Effects;
using AegiNext.Desktop.Panels.Masks;
using AegiNext.Desktop.Panels.Export;
using AegiNext.Desktop.Panels.Log;
using AegiNext.Desktop.Settings;
using AegiNext.Desktop.Shortcuts;
using AegiNext.Desktop.Updates;
using AegiNext.Desktop.Windowing;
using AegiNext.Desktop.Workspace;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.OpenGL;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;

namespace AegiNext.Desktop.Views;

/// <summary>组合固定功能面板、窗口宿主与工作台会话的桌面主窗口。</summary>
public sealed partial class MainWindow : Window, IAsyncDisposable
{
    private readonly DispatcherTimer clockTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly WorkbenchMenuCatalog menuCatalog;
    private readonly WorkbenchWindowRegistry windowRegistry;
    private readonly Dictionary<string, Control> panels;
    private readonly ContentControl workspaceHost;
    private readonly WorkbenchLayoutController layouts;
    private readonly Dictionary<string, ICommand> presetCommands = new(StringComparer.Ordinal);
    private readonly Dictionary<Window, string> floatingTitles = [];
    private bool closing;
    private bool closeCompleted;
    private Task? disposeTask;
    private Task? closeOperation;
    private readonly SettingsWindowCoordinator settingsCoordinator;
    private readonly UpdateWindowCoordinator updates;
    private readonly bool ownsUpdates;
    private SettingsWindow? settingsWindow => settingsCoordinator.Window;

    /// <summary>通过显式组合根创建唯一工作台会话和固定长生命周期面板。</summary>
    public MainWindow() : this((Func<Action<VideoPreviewUpdate>, VideoPreviewController>?)null)
    {
    }

    internal MainWindow(Func<Action<VideoPreviewUpdate>, VideoPreviewController>? controllerFactory)
        : this(controllerFactory, null)
    {
    }

    internal MainWindow(WorkbenchSession preparedSession, UpdateWindowCoordinator? updates = null) : this(null, preparedSession, updates)
    {
    }

    private MainWindow(Func<Action<VideoPreviewUpdate>, VideoPreviewController>? controllerFactory,
        WorkbenchSession? preparedSession, UpdateWindowCoordinator? updates = null)
    {
        if (preparedSession is not null &&
            (preparedSession.ProjectPath is not { } projectPath || !File.Exists(projectPath)))
        {
            throw new InvalidOperationException("必须先保存或打开项目文件，才能进入主界面。");
        }

        WorkbenchStartupPreferences? startup = null;
        AvaloniaXamlLoader.Load(this);
        Session = preparedSession ?? WorkbenchCompositionRoot.Create(new WindowWorkbenchDialogService(this,
            registerWindow: RegisterAuxiliaryWindow), controllerFactory, startup: startup);
        ViewModel = Session.ViewModel;
        DataContext = ViewModel;
        InitializeTaskCenter(Session.ApplicationContext.Tasks);
        panels = new(StringComparer.Ordinal)
        {
            ["preview"] = new PreviewPanelView(ViewModel.Preview, Session),
            ["timeline"] = new TimelinePanelView(ViewModel.Timeline, Session),
            ["subtitles"] = new SubtitlesPanelView(ViewModel.Subtitles, Session),
            ["styles"] = new StylesPanelView(ViewModel.Styles, Session),
            ["effects"] = new EffectsPanelView(ViewModel.Effects, Session),
            [WorkbenchPanelIds.MASKS] = new MaskPanelView(ViewModel.Masks, Session),
            ["export"] = new ExportPanelView(ViewModel.Export, Session),
            [WorkbenchPanelIds.SUBTITLE_DETAILS] = new SubtitleDetailsPanelView(Session),
            [WorkbenchPanelIds.LOG] = new LogPanelView(ViewModel.Log, Session.Journal, () => Session.IsClosing)
        };
        workspaceHost = this.FindControl<ContentControl>("WorkspaceHost")!;
        menuCatalog = new(ViewModel.GetCommand);
        windowRegistry = new(menuCatalog, Session.InvalidateTimingSession, ViewModel.CancelGestures,
            includeApplicationMenu: preparedSession is null);
        windowRegistry.Register(this, () => ViewModel.Title, this.FindControl<WindowTitleBar>("TitleBar")!,
            WorkbenchWindowRole.MAIN);
        ownsUpdates = updates is null;
        this.updates = updates ?? new(Session.ApplicationContext.Updates, () => Session.Preferences);
        if (ownsUpdates)
        {
            Opened += OnUpdateOwnerOpened;
        }
        layouts = new(this, panels, Session.PreferencesStore.DirectoryPath, ViewModel.TryCommitDrafts,
            ViewModel.CancelGestures, RegisterWorkspaceWindow, Session.ApplicationContext.Tasks,
            Session.ApplicationContext.InitialLayout);
        settingsCoordinator = new(Session.ApplicationContext, captureLayout: layouts.CaptureFile,
            requestApplicationExit: RequestApplicationExit);
        settingsCoordinator.EffectScriptErrorReported += RevealEffectScriptError;
        workspaceHost.Content = layouts.Host;
        layouts.Changed += OnLayoutChanged;
        layouts.Error += OnLayoutError;
        layouts.FloatingWindowTitleChanged += OnFloatingWindowTitleChanged;
        ViewModel.HostCommandHandler = HandleHostCommandAsync;
        ViewModel.ContextCommandAvailability = command => command == WorkbenchCommand.MERGE_SUBTITLE
            ? windowRegistry.GetFocusCommandAvailability(command) : null;
        ViewModel.TryExecuteContextCommand = command => command == WorkbenchCommand.MERGE_SUBTITLE &&
            windowRegistry.TryExecuteContextCommand(command);
        windowRegistry.FocusCommandContextChanged += OnFocusCommandContextChanged;
        ViewModel.PropertyChanged += OnViewModelChanged;
        ViewModel.Log.PropertyChanged += OnLogChanged;
        ViewModel.DraftErrorFocusRequested += OnDraftErrorFocusRequested;
        ViewModel.PanelActivationRequested += OnPanelActivationRequested;
        Session.PreferencesChanged += OnPreferencesChanged;
        clockTimer.Tick += (_, _) => Session.Tick();
        Opened += (_, _) => clockTimer.Start();
        Opened += InitializePreviewGraphics;
        Closed += (_, _) => clockTimer.Stop();
        ApplyWindowPreferences();
        RefreshLayoutMenu();
        RefreshPanelAvailability();
        RefreshLogIndicator();
        if (layouts.LastError is { } layoutError)
        {
            Session.ShowError(new InvalidOperationException(layoutError));
        }
    }

    internal WorkbenchSession Session { get; }
    internal WorkbenchViewModel ViewModel { get; }
    internal IReadOnlyDictionary<string, Control> Panels => panels;
    internal WorkbenchLayoutController Layouts => layouts;
    internal WorkbenchWindowRegistry WindowRegistry => windowRegistry;
    internal ProjectDocument DocumentSnapshot => Session.DocumentSnapshot;
    internal bool IsApplicationExitRequested { get; private set; }

    private async void InitializePreviewGraphics(object? sender, EventArgs args)
    {
        try
        {
            var compositor = ElementComposition.GetElementVisual(this)?.Compositor;
            if (compositor is not null &&
                await compositor.TryGetRenderInterfaceFeature(typeof(IOpenGlTextureSharingRenderInterfaceContextFeature))
                    is IOpenGlTextureSharingRenderInterfaceContextFeature graphics &&
                !closing && disposeTask is null)
            {
                Session.ConfigurePreviewGraphics(graphics);
            }
        }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException or Avalonia.OpenGL.OpenGlException)
        {
            System.Diagnostics.Trace.TraceWarning("Preview GPU feature unavailable: {0}", error.Message);
        }
    }

    internal void RequestApplicationExit()
    {
        IsApplicationExitRequested = true;
        Close();
    }

    internal ICommand GetCommand(WorkbenchCommand command) => ViewModel.GetCommand(command);
    internal Task OpenMediaAsync(string path, bool updateProject) => Session.OpenMediaAsync(path, updateProject);

    /// <inheritdoc />
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!closeCompleted && disposeTask?.IsCompletedSuccessfully != true)
        {
            e.Cancel = true;
            if (!closing)
            {
                closing = true;
                closeOperation = CompleteCloseAsync();
            }
        }
        base.OnClosing(e);
    }

    private async Task CompleteCloseAsync()
    {
        try
        {
            if (!await Session.RequestCloseAsync(layouts.FlushAsync))
            {
                closing = false;
                IsApplicationExitRequested = false;
                return;
            }
            await DisposeOnceAsync();
            closeCompleted = true;
            Close();
        }
        catch (Exception error)
        {
            Session.ShowError(error);
            closing = false;
            IsApplicationExitRequested = false;
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (closeOperation is { IsCompleted: false } pendingClose)
        {
            return new(DisposeAfterCloseAsync(pendingClose));
        }
        return DisposeOnceAsync();
    }

    private async Task DisposeAfterCloseAsync(Task pendingClose)
    {
        await pendingClose;
        await DisposeOnceAsync();
    }

    private ValueTask DisposeOnceAsync()
    {
        disposeTask ??= DisposeCoreAsync();
        return new(disposeTask);
    }

    private async Task DisposeCoreAsync()
    {
        Opened -= OnUpdateOwnerOpened;
        updates.ReleaseOwner(this);
        if (ownsUpdates)
        {
            updates.Dispose();
        }
        DisposeTaskCenter();
        ViewModel.Log.PropertyChanged -= OnLogChanged;
        workspaceHost.IsEnabled = false;
        clockTimer.Stop();
        settingsCoordinator.EffectScriptErrorReported -= RevealEffectScriptError;
        settingsCoordinator.Dispose();
        await settingsCoordinator.TransferCompletion;
        aboutWindow?.Close();
        try
        {
            try
            {
                if (!Session.IsClosing)
                {
                    await layouts.FlushAsync();
                }
            }
            finally
            {
                layouts.Dispose();
                await Session.DisposeAsync();
            }
        }
        finally
        {
            foreach (var panel in panels.Values.OfType<IWorkbenchPanelView>())
            {
                panel.Dispose();
            }
            ViewModel.PropertyChanged -= OnViewModelChanged;
            ViewModel.DraftErrorFocusRequested -= OnDraftErrorFocusRequested;
            ViewModel.PanelActivationRequested -= OnPanelActivationRequested;
            Session.PreferencesChanged -= OnPreferencesChanged;
            layouts.Changed -= OnLayoutChanged;
            layouts.Error -= OnLayoutError;
            layouts.FloatingWindowTitleChanged -= OnFloatingWindowTitleChanged;
            windowRegistry.FocusCommandContextChanged -= OnFocusCommandContextChanged;
            windowRegistry.Dispose();
        }
    }

    private void OnFocusCommandContextChanged(object? sender, EventArgs e) =>
        ((IAsyncRelayCommand)ViewModel.GetCommand(WorkbenchCommand.MERGE_SUBTITLE)).NotifyCanExecuteChanged();

    private void OnUpdateOwnerOpened(object? sender, EventArgs e) => updates.SetOwner(this, RegisterAuxiliaryWindow);

    private void RegisterAuxiliaryWindow(Window window)
    {
        windowRegistry.RegisterAuxiliary(window);
    }

    private void RegisterWorkspaceWindow(Window window)
    {
        windowRegistry.Register(window, () => window switch
        {
            WorkbenchFloatingHostWindow => $"{ViewModel.Title} — {floatingTitles.GetValueOrDefault(window, string.Empty)}",
            LayoutPresetManagerWindow => $"{Localization.Get("Layout.Manage")} — AegiNext",
            _ => $"{Localization.Get("Layout.SaveAs")} — AegiNext"
        }, role: window is WorkbenchFloatingHostWindow ? WorkbenchWindowRole.FLOATING : WorkbenchWindowRole.AUXILIARY);
        window.Closed += (_, _) => floatingTitles.Remove(window);
    }

    private async Task HandleHostCommandAsync(WorkbenchHostCommandEventArgs request)
    {
        if (request.Command is WorkbenchCommand.END_TEXT_INPUT or WorkbenchCommand.COPY_CLIPS or WorkbenchCommand.PASTE_CLIPS)
        {
            windowRegistry.TryExecuteFocusCommand(request.Command);
        }
        else if (request.Command == WorkbenchCommand.OPEN_SETTINGS)
        {
            OpenSettings(request.SettingsPage);
        }
        else if (request.Command == WorkbenchCommand.OPEN_ABOUT)
        {
            OpenAbout();
        }
        else if (request.Command == WorkbenchCommand.CHECK_UPDATES)
        {
            await updates.CheckManuallyAsync();
        }
        else if (request.Command == WorkbenchCommand.OPEN_SUBTITLE_DETAILS)
        {
            if (ViewModel.TryCommitDrafts())
            {
                if (layouts.IsVisible(WorkbenchPanelIds.SUBTITLE_DETAILS))
                {
                    layouts.Activate(WorkbenchPanelIds.SUBTITLE_DETAILS);
                }
                else
                {
                    layouts.Float(WorkbenchPanelIds.SUBTITLE_DETAILS);
                }
            }
        }
        else if (request.Command == WorkbenchCommand.CLOSE_PROJECT)
        {
            Close();
        }
        else if (request.Command == WorkbenchCommand.EXIT)
        {
            RequestApplicationExit();
        }
        else if (request.Command == WorkbenchCommand.LAYOUT_SAVE)
        {
            if (layouts.Presets.Single(preset => preset.Id == layouts.CurrentPresetId).IsReadOnly)
            {
                await layouts.ShowSaveAsAsync();
            }
            else
            {
                await layouts.SaveAsync();
            }
        }
        else if (request.Command == WorkbenchCommand.LAYOUT_SAVE_AS)
        {
            await layouts.ShowSaveAsAsync();
        }
        else if (request.Command == WorkbenchCommand.LAYOUT_MANAGE)
        {
            await layouts.ShowManagerAsync();
        }
        else if (request.Command == WorkbenchCommand.LAYOUT_RESTORE_DEFAULT)
        {
            await layouts.RestoreDefaultAsync();
        }
        else
        {
            var id = request.Command switch
            {
                WorkbenchCommand.VIEW_PREVIEW => "preview", WorkbenchCommand.VIEW_TIMELINE => "timeline",
                WorkbenchCommand.VIEW_SUBTITLES => "subtitles", WorkbenchCommand.VIEW_STYLES => "styles",
                WorkbenchCommand.VIEW_MASKS => WorkbenchPanelIds.MASKS,
                WorkbenchCommand.VIEW_EFFECTS => "effects", WorkbenchCommand.VIEW_EXPORT => "export",
                WorkbenchCommand.VIEW_LOG => WorkbenchPanelIds.LOG, _ => null
            };
            if (id is not null)
            {
                layouts.Activate(id);
            }
        }
    }

    private void OnLayoutChanged(object? sender, EventArgs e) => RefreshLayoutMenu();
    private void OnLogChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LogPanelViewModel.UnreadErrorCount))
        {
            RefreshLogIndicator();
        }
    }

    private void RefreshLogIndicator()
    {
        var count = ViewModel.Log.UnreadErrorCount;
        layouts.SetPanelUnreadCount(WorkbenchPanelIds.LOG, count);
        menuCatalog.UpdateUnreadLogErrors(count);
    }
    private void OnLayoutError(object? sender, EventArgs e)
    {
        if (layouts.LastError is { } error)
        {
            Session.ShowError(new InvalidOperationException(error));
        }
    }
    private void OnFloatingWindowTitleChanged(Window window, string title)
    {
        floatingTitles[window] = title;
        windowRegistry.RefreshTitles();
    }
    private void RefreshLayoutMenu()
    {
        var choices = layouts.Presets.Select(preset =>
        {
            if (!presetCommands.TryGetValue(preset.Id, out var command))
            {
                command = new AsyncRelayCommand(async () =>
                {
                    await Session.RunCommandAsync(() => layouts.ApplyPresetAsync(preset.Id));
                    RefreshLayoutMenu();
                }, () => !Session.IsProjectBusy && !Session.IsClosing);
                presetCommands.Add(preset.Id, command);
            }
            return new LayoutMenuChoice(preset.Id, WorkbenchLayoutController.GetPresetName(preset),
                preset.Id == layouts.CurrentPresetId, command);
        }).ToArray();
        menuCatalog.UpdateLayouts(choices, layouts.IsModified);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.Title))
        {
            windowRegistry.RefreshTitles();
        }
        else if (e.PropertyName == nameof(ViewModel.Error))
        {
            settingsWindow?.ShowError(ViewModel.Error);
        }
        else if (e.PropertyName == nameof(ViewModel.IsBusy))
        {
            RefreshPanelAvailability();
            foreach (var command in presetCommands.Values.OfType<AsyncRelayCommand>())
            {
                command.NotifyCanExecuteChanged();
            }
        }
    }
    private void OnPanelActivationRequested(string panelId) => layouts.Activate(panelId);

    private void OnDraftErrorFocusRequested(object? sender, EventArgs e)
    {
        var id = ViewModel.InvalidPanelId ?? "subtitles";
        if (panels.TryGetValue(id, out var panel) && panel is IWorkbenchPanelView input)
        {
            layouts.Activate(id);
            input.FocusInvalidField(ViewModel.InvalidFieldKey);
        }
    }
    private void RefreshPanelAvailability()
    {
        foreach (var pair in panels)
        {
            if (pair.Key != WorkbenchPanelIds.LOG)
            {
                pair.Value.SetCurrentValue(InputElement.IsEnabledProperty, !Session.IsClosing &&
                    !Session.ApplicationContext.Tasks.IsEditingRestricted(Session.TaskScope));
            }
        }
    }

    private void OnPreferencesChanged(object? sender, EventArgs e) => ApplyWindowPreferences();
    private void ApplyWindowPreferences()
    {
        windowRegistry.UpdatePreferences(Session.Preferences);
        ViewModel.Log.RefreshLanguage();
    }
}
