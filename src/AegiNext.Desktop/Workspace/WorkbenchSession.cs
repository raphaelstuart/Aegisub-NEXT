using System.Collections.Immutable;
using System.Globalization;
using AegiNext.Application;
using AegiNext.Application.Tasks;
using AegiNext.Application.Presets;
using AegiNext.Core.Projects;
using AegiNext.Core.Timing;
using AegiNext.Core.Media;
using AegiNext.Desktop.Controllers;
using AegiNext.Desktop.Editing;
using AegiNext.Desktop.I18n;
using AegiNext.Desktop.Rendering;
using AegiNext.Desktop.Settings;
using AegiNext.Desktop.Shortcuts;
using AegiNext.Desktop.Startup;
using AegiNext.Desktop.Workspace.Diagnostics;
using AegiNext.Media.Playback;
using AegiNext.Media.Analysis;
using Avalonia.Threading;
using Avalonia.OpenGL;

namespace AegiNext.Desktop.Workspace;

internal sealed partial class WorkbenchSession : IAsyncDisposable
{
    private static readonly string[] blendKeys = ["Normal", "Multiply", "Screen", "AddBlend", "Overlay", "Darken", "Lighten", "Difference"];
    private static readonly string[] interpolationKeys = ["Hold", "Linear", "EaseIn", "EaseOut", "Smooth", "Power"];
    private static readonly string[] speedKeys = ["Fast", "Medium", "Slow"];
    private readonly ProjectEditor editor;
    private readonly VideoPreviewController controller;
    private readonly IWorkbenchDialogService dialogs;
    private readonly Func<Action, CancellationToken, Task> dispatch;
    private readonly DesktopApplicationContext applicationContext;
    private readonly bool ownsApplicationContext;
    private readonly WorkbenchPreferencesStore preferencesStore;
    private readonly SubtitleStylePresetLibrary styleLibrary;
    private readonly EffectScriptPresetLibrary effectScriptLibrary;
    private readonly EffectScriptLibraryCoordinator effectScripts;
    private readonly string scratchDirectory;
    private readonly Dictionary<Guid, int> textCarets = [];
    private readonly ProjectWorkflowCoordinator workflow;
    private readonly AnalysisCoordinator analysis;
    private readonly ExportCoordinator export;
    private readonly ExportPresetCoordinator exportPresets;
    private readonly StyleLibraryCoordinator styles;
    private readonly LayerEditingCoordinator layerEditing;
    internal ClipMaskEditingCoordinator MaskEditing { get; }
    internal AnimationPropertyEditingCoordinator PropertyEditing { get; }
    private readonly PlaybackSeekingCoordinator playback;
    private readonly PreviewFrameCatalog previewFrames = new();
    private ProjectPreviewState previewState = new(new(), Path.GetTempPath());
    private IOpenGlTextureSharingRenderInterfaceContextFeature? previewGraphics;
    private WorkbenchPreferences preferences;
    private string? projectPath;
    private string projectDirectory;
    private Exception? previewRenderError;
    private readonly WorkbenchUpdateState updateState = new();
    private bool closeRequested;
    private Task<bool>? closeOperation;
    private string? taskScopeDisplayName;
    private AegiTaskScopeCloseLease? scopeCloseLease;
    private bool lastEditRestriction;
    private bool closing;
    private bool stylesDirty;
    private bool effectsDirty;
    private Task? disposeTask;
    private Task documentChangeTask = Task.CompletedTask;
    private TimingSession timingSession = new();
    private ProjectClipIndex? clipIndex;

    internal WorkbenchSession(IWorkbenchDialogService dialogs,
        Func<Action<VideoPreviewUpdate>, VideoPreviewController>? controllerFactory = null,
        Func<Action, CancellationToken, Task>? dispatch = null,
        ProjectEditor? editor = null,
        WorkbenchPreferencesStore? preferencesStore = null,
        IWorkbenchExportService? exportService = null,
        WorkbenchPreferences? initialPreferences = null,
        DesktopApplicationContext? applicationContext = null,
        TimeProvider? persistenceTimeProvider = null, IProjectPersistenceStorage? persistenceStorage = null,
        Func<string, int, MediaTime, CancellationToken, Task<VideoTimingIndex>>? videoTimingProbe = null,
        Func<string, int, MediaTimelineMapping, MediaTime, string, AudioAnalysisOptions, AudioAnalysisWorkerBudget?, AudioAnalysisSession>?
            analysisSessionFactory = null)
    {
        this.dialogs = dialogs;
        this.videoTimingProbe = videoTimingProbe ?? ProbeVideoTimingAsync;
        this.dispatch = dispatch ?? DispatchAsync;
        this.editor = editor ?? new();
        ownsApplicationContext = applicationContext is null;
        this.applicationContext = applicationContext ?? new(preferencesStore, initialPreferences);
        this.preferencesStore = this.applicationContext.PreferencesStore;
        taskScopeDisplayName = ProjectDisplayName;
        this.applicationContext.Tasks.RegisterScope(TaskScope, taskScopeDisplayName);
        preferences = this.applicationContext.Preferences;
        scratchDirectory = Path.Combine(Path.GetTempPath(), "AegiNext", Guid.NewGuid().ToString("N"));
        projectDirectory = scratchDirectory;
        styleLibrary = this.applicationContext.StyleLibrary;
        effectScriptLibrary = this.applicationContext.EffectScriptLibrary;
        PropertyEditing = new(this);
        ViewModel = new(this);
        controller = controllerFactory?.Invoke(ApplyUpdate) ?? new(this.dispatch, ApplyUpdate,
            () => new ProjectPreviewConverter(GetPreviewState,
                error => Volatile.Write(ref previewRenderError, error), previewFrames, () => Fonts.Catalog,
                () => Volatile.Read(ref previewGraphics)));
        controller.ConfigureDecodeMode(preferences.PreviewDecodeMode);
        InitializeAudioCalibration();
        workflow = new(this, dialogs);
        Details = new(this);
        Details.Changed += OnSubtitleDetailsChanged;
        analysis = new(this, analysisSessionFactory);
        export = new(this, dialogs, exportService ?? new VideoWorkbenchExportService(new AegiNext.Media.Encoding.VideoExporter()));
        exportPresets = new(this);
        styles = new(this, dialogs);
        styles.BusyChanged += OnTimingLibrariesBusyChanged;
        effectScripts = new(this, dialogs);
        layerEditing = new(this, dialogs);
        MaskEditing = new(this);
        playback = new(this, controller);
        persistence = new(persistenceTimeProvider ?? TimeProvider.System,
            action => this.dispatch(action, CancellationToken.None), CapturePersistenceState,
            OnProjectAutomaticallySaved, error => ShowError(error, false), persistenceStorage, this.applicationContext.Tasks, TaskScope);
        persistence.UpdatePreferences(preferences.Projects);
        this.applicationContext.Tasks.Changed += OnTaskStateChanged;
        this.applicationContext.Fonts.Changed += OnFontsChanged;
        this.applicationContext.PreferencesChanged += OnApplicationPreferencesChanged;
        this.applicationContext.AudioAnalysisRebuildRequested += OnAudioAnalysisRebuildRequested;
        this.applicationContext.StylesChanged += OnApplicationStylesChanged;
        this.applicationContext.BusyChanged += OnTimingLibrariesBusyChanged;
        this.applicationContext.EffectsChanged += OnApplicationEffectsChanged;
        this.applicationContext.ExportPresetsChanged += OnApplicationExportPresetsChanged;
        this.applicationContext.BusyChanged += OnApplicationLibrariesBusyChanged;
        this.applicationContext.ErrorChanged += OnApplicationErrorChanged;
        this.editor.StateChanged += OnEditorStateChanged;
        ViewModel.Styles.PropertyChanged += OnStylePropertyChanged;
        ViewModel.Effects.PropertyChanged += OnEffectPropertyChanged;
        ViewModel.Preview.PropertyChanged += OnPreviewPropertyChanged;
        ViewModel.Export.PropertyChanged += OnExportPropertyChanged;
        SubscribeTimelinePreferences();
        ApplyPreferences();
        ResetTimelineViewState(this.editor.Snapshot);
        RefreshDocument();
        InitializeTaskInputTracking();
        styles.Initialize();
        effectScripts.Initialize();
        exportPresets.Refresh();
        Localization.LanguageChanged += OnLanguageChanged;
        foreach (var diagnostic in Localization.Diagnostics)
        {
            LogWarning("Localization", diagnostic.Message, diagnostic.FilePath);
        }
        if (this.applicationContext.LastError is { } error)
        {
            ShowError(error);
        }
    }

    internal event EventHandler<VideoPreviewUpdate>? PreviewUpdated;
    internal event EventHandler? PreferencesChanged;
    internal event EventHandler? StyleLibraryChanged;
    internal event EventHandler? EffectLibraryChanged;
    internal void NotifyEffectLibraryChanged() => EffectLibraryChanged?.Invoke(this, EventArgs.Empty);
    internal event EventHandler? SelectionChanged;
    internal event EventHandler? SubtitleScrollRequested;
    internal WorkbenchViewModel ViewModel { get; }
    internal Exception? LastError { get; private set; }
    internal ProjectEditor Editor => editor;
    internal SubtitleDetailsCoordinator Details { get; }
    internal VideoPreviewController Controller => controller;
    internal PreviewFrameCatalog PreviewFrames => previewFrames;
    internal void ConfigurePreviewGraphics(IOpenGlTextureSharingRenderInterfaceContextFeature graphics) =>
        Volatile.Write(ref previewGraphics, graphics);
    internal string TaskScope { get; } = Guid.NewGuid().ToString("N");
    internal DesktopApplicationContext ApplicationContext => applicationContext;
    internal WorkbenchPreferences Preferences => applicationContext.Preferences;
    internal WorkbenchPreferencesStore PreferencesStore => preferencesStore;
    internal SubtitleStylePresetLibrary StyleLibrary => styleLibrary;
    internal EffectScriptPresetLibrary EffectScriptLibrary => effectScriptLibrary;
    internal EffectScriptLibraryCoordinator EffectScripts => effectScripts;

    internal ProjectClipIndex ClipIndex
    {
        get
        {
            if (clipIndex is null || !ReferenceEquals(clipIndex.Document, editor.Snapshot))
            {
                clipIndex = new(editor.Snapshot);
            }

            return clipIndex;
        }
    }

    internal ProjectDocument DocumentSnapshot => editor.Snapshot;
    internal static CultureInfo InterfaceCulture => CultureInfo.GetCultureInfo(Localization.CurrentLanguageID);
    internal bool IsClosing => closing;
    internal bool IsProjectBusy => applicationContext.Tasks.IsEditingRestricted(TaskScope);
    internal bool IsUpdating => updateState.IsActive;
    internal WorkbenchUpdateLease BeginWorkbenchUpdate() => updateState.Acquire();
    internal Guid? SelectedLayerId { get => SceneEditing.LayerId; set => SceneEditing.LayerId = value; }
    internal Guid? SelectedCueId { get => SceneEditing.CueId; set => SceneEditing.CueId = value; }
    internal MediaTime? SelectedKeyTime { get => SceneEditing.KeyframeTime; set => SceneEditing.KeyframeTime = value; }
    internal bool HasSelectedCue => SelectedCue is not null;
    internal string ProjectDirectory => projectDirectory;
    internal string? ProjectPath => projectPath;
    internal string ProjectDisplayName => WorkbenchProjectTitle.GetDisplayName(editor.Snapshot, projectPath,
        Localization.Get("Workbench.Untitled"));
    internal string ScratchDirectory => scratchDirectory;
    internal MediaTime ProjectPosition => (playback.PendingPosition ?? controller.Snapshot.Position) - (controller.Snapshot.Start ?? MediaTime.Zero);
    internal ProjectLayer? SelectedLayer => SelectedLayerId is { } id && ClipIndex.TryGetClip(id, out var clip) ? clip : null;
    internal SubtitleLine? SelectedCue => editor.Snapshot.Subtitles.FirstOrDefault(value => value.Id == SelectedCueId);
    internal AnalysisCoordinator Analysis => analysis;
    internal ExportCoordinator Export => export;
    internal StyleLibraryCoordinator Styles => styles;

    internal Task OpenMediaAsync(string path, bool updateProject) => workflow.OpenMediaAsync(path, updateProject);
    internal Task<ProjectOpenResult> CreateProjectAsync(string path, CancellationToken cancellationToken = default)
    {
        return workflow.CreateProjectAsync(path, cancellationToken);
    }

    internal Task<ProjectOpenResult> CreateProjectAsync(ProjectCreationRequest request, CancellationToken cancellationToken = default)
    {
        return workflow.CreateProjectAsync(request, cancellationToken);
    }

    internal Task<ProjectOpenResult> CreateProjectFromDialogAsync(ProjectCreationRequest request,
        CancellationToken operationCancellation)
    {
        return workflow.CreateProjectFromDialogAsync(request, operationCancellation);
    }

    internal Task<ProjectOpenResult> OpenProjectAsync(string path, CancellationToken cancellationToken = default)
    {
        return workflow.OpenProjectAsync(path, cancellationToken);
    }

    internal Task RequestSettingsAsync(SettingsPage? page = null)
    {
        return ViewModel.RequestHostCommandAsync(WorkbenchCommand.OPEN_SETTINGS, page);
    }

    internal void UpdatePreferences(WorkbenchPreferences value)
    {
        value.Validate();
        applicationContext.UpdatePreferences(_ => value);
    }

    internal void UpdatePreferences(Func<WorkbenchPreferences, WorkbenchPreferences> update)
    {
        applicationContext.UpdatePreferences(update);
    }

    private void OnApplicationPreferencesChanged(object? sender, EventArgs e)
    {
        if (closing)
        {
            return;
        }

        var value = applicationContext.Preferences;
        var previousPreferences = preferences;
        var qualityChanged = preferences.PreviewQuality != value.PreviewQuality;
        preferences = value;
        persistence.UpdatePreferences(value.Projects);
        if (qualityChanged)
        {
            previewQualityRevision++;
            controller.InvalidatePreview();
        }
        ApplyPreferences();
        QueueAudioCalibrationPreferences(previousPreferences, value);
        analysis.UpdateExecutionOptions(value.AudioAnalysis.Execution);
        if (qualityChanged)
        {
            _ = RunCommandAsync(controller.RefreshPausedPreviewAsync);
        }
    }

    private Task OnAudioAnalysisRebuildRequested(AudioAnalysisOptions options)
    {
        return closing ? Task.CompletedTask : analysis.RebuildAsync(options);
    }

    private void OnApplicationStylesChanged(object? sender, EventArgs e)
    {
        styles.Refresh();
    }

    private void OnApplicationEffectsChanged(object? sender, EventArgs e)
    {
        if (!closing)
        {
            effectScripts.RefreshChoices();
            NotifyEffectLibraryChanged();
        }
    }

    private void OnApplicationErrorChanged(object? sender, EventArgs e)
    {
        if (!closing && applicationContext.LastError is { } error)
        {
            ShowError(error, false);
        }
    }

    internal WorkbenchLogEntry? ShowError(Exception error, bool recordLog = true)
    {
        var entry = recordLog ? LogError("Workspace", error) : null;
        LastError = error;
        var text = error.Message;
        ViewModel.Error = text.Length > 700 ? text[..700] + "…" : text;
        ViewModel.RefreshCommands();
        return entry;
    }

    internal async Task RunCommandAsync(Func<Task> command, Action<WorkbenchLogEntry>? onFailure = null)
    {
        if (closing)
        {
            return;
        }

        try
        {
            LastError = null;
            ViewModel.Error = null;
            await command();
        }
        catch (OperationCanceledException)
        {
            if (!closing)
            {
                LogInfo("Workflow", Localization.Get("Workbench.Cancelled"));
            }
        }
        catch (Exception error)
        {
            if (!closing)
            {
                var entry = ShowError(error)!;
                onFailure?.Invoke(entry);
            }
        }
        finally
        {
            if (!closing)
            {
                Tick();
                RestorePlacementDiagnostic();
                ViewModel.RefreshCommands();
            }
        }
    }

    internal void DismissError(Exception error)
    {
        if (ReferenceEquals(LastError, error))
        {
            LastError = null;
            ViewModel.Error = null;
        }
    }

    internal Task EditAsync(Action action)
    {
        if (!IsUpdating && !IsProjectBusy && !closing && TryCommitDrafts())
        {
            InvalidateTimingSession();
            action();
        }

        return Task.CompletedTask;
    }

    internal AegiTaskEditLease AcquireEditingLease()
    {
        return AegiTaskExecutionContext.Current is { } context
            ? context.AcquireEditLease()
            : applicationContext.Tasks.AcquireScopeEditLease(TaskScope);
    }

    private void OnTaskStateChanged(object? sender, EventArgs e)
    {
        if (disposeTask is not null)
        {
            return;
        }
        _ = dispatch(() =>
        {
            if (disposeTask is not null)
            {
                return;
            }
            var restricted = IsProjectBusy;
            if (restricted && !lastEditRestriction)
            {
                playback.Invalidate();
                ViewModel.CancelGestures();
            }
            lastEditRestriction = restricted;
            ViewModel.IsBusy = restricted || closing;
            ViewModel.Export.RefreshTaskState();
            ViewModel.RefreshCommands();
        }, CancellationToken.None);
    }

    private void OnFontsChanged(object? sender, EventArgs e)
    {
        _ = dispatch(() =>
        {
            if (!closing)
            {
                if (stylesDirty || effectsDirty)
                {
                    QueueInspectorPreview(true);
                }
                else
                {
                    ClearInspectorPreview();
                }
                ViewModel.RefreshCommands();
            }
        }, CancellationToken.None);
    }

    internal async Task WaitForProjectIdleAsync()
    {
        await applicationContext.Tasks.DrainScopeAsync(TaskScope);
        await documentChangeTask;
    }

    internal Task<bool> RequestCloseAsync(Func<Task>? beforeDispose = null)
    {
        if (closing || closeRequested)
        {
            return Task.FromResult(false);
        }
        closeOperation = RequestCloseCoreAsync(beforeDispose);
        return closeOperation;
    }

    private async Task<bool> RequestCloseCoreAsync(Func<Task>? beforeDispose)
    {
        closeRequested = true;
        try
        {
            var prompted = HasUnsavedChanges;
            var choice = prompted ? await dialogs.ConfirmUnsavedAsync() : 2;
            if (choice == 0)
            {
                return false;
            }
            string? destination = projectPath;
            if (choice == 1 && destination is null)
            {
                destination = await dialogs.SaveFileAsync("Save", "Projects", ["*.aeginext"], ".aeginext", ProjectDisplayName + ".aeginext");
                if (destination is null)
                {
                    return false;
                }
            }

            using var closeLease = applicationContext.Tasks.BeginCloseScope(TaskScope);
            scopeCloseLease = closeLease;
            closing = true;
            ViewModel.IsBusy = true;
            ViewModel.RefreshCommands();
            try
            {
                await using var persistencePause = await persistence.PauseAsync();
                await projectOperationsCancellation.CancelAsync();
                analysis.Cancel();
                export.Cancel();
                await closeLease.DrainAsync();
                await Task.WhenAll(timingProcessingCompletion, audioCalibrationCompletion, documentChangeTask,
                    analysis.Completion, export.Completion, styles.Completion, effectScripts.Completion)
                    .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
                if (!prompted && HasUnsavedChanges)
                {
                    choice = await dialogs.ConfirmUnsavedAsync();
                    if (choice == 0)
                    {
                        RestoreAfterCancelledClose();
                        return false;
                    }
                }
                if (choice == 1)
                {
                    if (!TryCommitDraftsForClose(closeLease) || !await workflow.SaveProjectForCloseAsync(closeLease, destination))
                    {
                        RestoreAfterCancelledClose();
                        return false;
                    }
                }
                if (beforeDispose is not null)
                {
                    await beforeDispose();
                }
                await DisposeOnceAsync();
                scopeCloseLease = null;
                return true;
            }
            catch
            {
                RestoreAfterCancelledClose();
                throw;
            }
        }
        finally
        {
            closeRequested = false;
        }
    }

    private void RestoreAfterCancelledClose()
    {
        scopeCloseLease?.Dispose();
        scopeCloseLease = null;
        closing = false;
        projectOperationsCancellation.Dispose();
        projectOperationsCancellation = new();
        ViewModel.IsBusy = IsProjectBusy;
        ViewModel.RefreshCommands();
    }

    /// <summary>等待会话任务结束并释放资源；重复调用等待同一次释放。</summary>
    public ValueTask DisposeAsync()
    {
        if (closeOperation is { IsCompleted: false } pendingClose)
        {
            return new(DisposeAfterCloseAsync(pendingClose));
        }
        return DisposeOnceAsync();
    }

    private async Task DisposeAfterCloseAsync(Task<bool> pendingClose)
    {
        await ((Task)pendingClose).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        await DisposeOnceAsync();
    }

    private ValueTask DisposeOnceAsync()
    {
        disposeTask ??= DisposeCoreAsync();
        return new(disposeTask);
    }

    private async Task DisposeCoreAsync()
    {
        using var closeLease = scopeCloseLease ?? applicationContext.Tasks.BeginCloseScope(TaskScope);
        InvalidateTimingSession();
        closing = true;
        DisposeTaskInputTracking();
        applicationContext.Tasks.Changed -= OnTaskStateChanged;
        applicationContext.Fonts.Changed -= OnFontsChanged;
        await projectOperationsCancellation.CancelAsync();
        await closeLease.DrainAsync();
        await timingProcessingCompletion.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        await audioCalibrationCompletion.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        await persistence.DisposeAsync();
        applicationContext.PreferencesChanged -= OnApplicationPreferencesChanged;
        applicationContext.AudioAnalysisRebuildRequested -= OnAudioAnalysisRebuildRequested;
        applicationContext.StylesChanged -= OnApplicationStylesChanged;
        applicationContext.BusyChanged -= OnTimingLibrariesBusyChanged;
        styles.BusyChanged -= OnTimingLibrariesBusyChanged;
        applicationContext.EffectsChanged -= OnApplicationEffectsChanged;
        applicationContext.ExportPresetsChanged -= OnApplicationExportPresetsChanged;
        applicationContext.BusyChanged -= OnApplicationLibrariesBusyChanged;
        applicationContext.ErrorChanged -= OnApplicationErrorChanged;
        Localization.LanguageChanged -= OnLanguageChanged;
        Details.Changed -= OnSubtitleDetailsChanged;
        Details.Dispose();
        ClearInspectorPreview();
        editor.StateChanged -= OnEditorStateChanged;
        playback.Invalidate();
        ViewModel.CancelGestures();
        analysis.Cancel();
        export.Cancel();
        try
        {
            await documentChangeTask;
            analysis.Cancel();
            await Task.WhenAll(analysis.Completion, export.Completion, styles.Completion, effectScripts.Completion);
        }
        finally
        {
            await controller.DisposeAsync();
            previewFrames.Clear();
            videoTimingCache = null;
            layerPlacement.Dispose();
            analysis.Dispose();
            export.Dispose();
            closeLease.CompleteClose();
            if (ownsApplicationContext)
            {
                await applicationContext.DisposeAsync();
            }
            DisposeJournal();
            projectOperationsCancellation.Dispose();
            PreviewUpdated = null;
            if (Directory.Exists(scratchDirectory))
            {
                Directory.Delete(scratchDirectory, true);
            }
        }
    }

    private void ApplyPreferences()
    {
        using var updateLease = BeginWorkbenchUpdate();
        try
        {
            ViewModel.Preview.Volume = preferences.Volume;
            ApplyTimelinePreferences();
            controller.SetVolume(preferences.Volume);
        }
        finally
        {
            updateLease.Dispose();
        }
        RefreshLocalizedState();
        PreferencesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnSubtitleDetailsChanged(object? sender, EventArgs e)
    {
        if (!closing && !IsUpdating)
        {
            RefreshEditingPreview();
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RefreshLocalizedState();
    }

    private void RefreshLocalizedState()
    {
        using var updateLease = BeginWorkbenchUpdate();
        try
        {
            ViewModel.Preview.EmptyLabel = Localization.Get("Preview.Empty");
            ViewModel.Preview.RefreshQualities(preferences.PreviewQuality);
            ViewModel.Styles.RefreshAppearanceEditing();
            foreach (var row in ViewModel.Subtitles.Rows)
            {
                row.RefreshLanguage();
            }
            ViewModel.Subtitles.RefreshColorTags();
            ViewModel.Effects.RefreshChoices(blendKeys.Select(key => Localization.Get("Workbench." + key)).ToArray(),
                MaskPropertyChoices(),
                interpolationKeys.Select(key => Localization.Get("Workbench." + key)).ToArray());
            ViewModel.Masks.Refresh();
            ViewModel.Export.RefreshChoices([Localization.Get("Workbench.Automatic"), "H.264", "HEVC / H.265"],
                speedKeys.Select(key => Localization.Get("Workbench." + key)).ToArray(),
                [Localization.Get("Workbench.Copy"), "AAC", Localization.Get("Workbench.NoAudio")]);
            effectScripts.RefreshChoices();
            analysis.RefreshLanguage();
            export.RefreshLanguage();
            RefreshTitle();
            Tick();
        }
        finally
        {
            updateLease.Dispose();
        }

    }

    private void ApplyUpdate(VideoPreviewUpdate update)
    {
        if (!closing)
        {
            var identity = update.Frame is { } presentedFrame ? previewFrames.FindIdentity(presentedFrame) : null;
            if (identity is not null && identity.QualityRevision != previewQualityRevision)
            {
                Tick();
                return;
            }
            if (update.ClearFrame)
            {
                ViewModel.Preview.HasFrame = false;
            }
            else if (update.Frame is not null)
            {
                ViewModel.Preview.HasFrame = true;
            }

            var presented = update.Frame is { } frame
                ? update with { BackgroundFrame = update.BackgroundFrame ?? identity?.Background ?? frame, CompositionDocument = identity?.Document, CompositionTime = identity?.Time, IsInteractiveComposition = identity?.Interactive ?? false }
                : update;
            PreviewUpdated?.Invoke(this, presented);
            if (update.Frame is not null)
            {
                InteractionDiagnostics.Record("delivered", update.Snapshot.PresentedFrameTime, update.Snapshot.PresentedFrameEnd);
            }
            Tick();
        }
    }

    internal void Tick()
    {
        RefreshAudioClockStatus();
        var snapshot = controller.Snapshot;
        RefreshPreviewDecodeSessionInfo(snapshot.DecodeSessionInfo);
        var preview = ViewModel.Preview;
        var relative = ProjectPosition;
        UpdateTimingPreview(snapshot.State, relative);
        preview.FileTitle = snapshot.FilePath is { } path ? Path.GetFileName(path) : Localization.Get("Preview.Preview");
        preview.IsOpening = snapshot.IsOpening || switchingPreviewDecodeMode || switchingAudioDevice;
        preview.CanPlay = !closing && !switchingPreviewDecodeMode && !switchingAudioDevice && snapshot.Error is null && snapshot.State is VideoPlaybackState.PAUSED or VideoPlaybackState.PLAYING or VideoPlaybackState.ENDED;
        preview.IsPlaying = snapshot.State == VideoPlaybackState.PLAYING || snapshot.AudioAuditionActive;
        preview.IsCatchingUp = snapshot.State == VideoPlaybackState.PLAYING && preview.HasFrame &&
            snapshot.PresentationLateness is { } lateness && lateness >= new MediaTime(1, 4);
        preview.PlayLabel = Localization.Get("Preview." + (preview.IsPlaying ? "Pause" : "Play"));
        preview.MuteLabel = Localization.Get("Preview." + (preview.IsMuted ? "Unmute" : "Mute"));
        preview.VolumeLabel = Localization.Get("Preview.Volume");
        preview.CanSeek = preview.CanPlay && snapshot.Duration is { } duration && duration > MediaTime.Zero;
        preview.Duration = snapshot.Duration is { } known ? Math.Max(0.001, ToSeconds(known)) : 1;
        if (!preview.IsScrubbing)
        {
            preview.Position = Math.Clamp(ToSeconds(relative), 0, preview.Duration);
        }

        preview.TimeLabel = $"{FormatTime(relative)} / {(snapshot.Duration is { } end ? FormatTime(end) : "--:--")}";
        var timeline = ViewModel.Timeline;
        timeline.MediaDuration = snapshot.Duration is { } mediaDuration ? ToSeconds(mediaDuration) : 0;
        timeline.Position = relative;
        ViewModel.Effects.Position = EditingPosition;
        MaskEditing.Refresh();
        RefreshAnimatedInspectorAtTime();
        RefreshEditingTargetLabel();
        RefreshEditingPreview();
        layerEditing.RefreshKeyframeAvailability();
        var durationSeconds = Math.Max(snapshot.Duration is { } value ? ToSeconds(value) : 60,
            editor.Snapshot.Subtitles.Select(line => ToSeconds(line.End)).DefaultIfEmpty(60).Max());
        timeline.ScrollMaximum = Math.Max(0, durationSeconds - timeline.VisibleDuration);
        timeline.ViewportSize = Math.Max(1, timeline.VisibleDuration);
        if (timeline.IsPlaybackFollowEnabled && playback.PendingPosition is null && !timeline.IsSeeking && snapshot.State == VideoPlaybackState.PLAYING &&
            (ToSeconds(relative) < timeline.ViewStart || ToSeconds(relative) > timeline.ViewStart + timeline.VisibleDuration))
        {
            timeline.ViewStart = Math.Max(0, ToSeconds(relative) - timeline.VisibleDuration / 5);
        }

        var diagnostics = controller.Snapshot;
        var renderError = Volatile.Read(ref previewRenderError);
        SetDiagnosticError("Video playback", diagnostics.Error);
        SetDiagnosticError("Audio playback", diagnostics.AudioError);
        SetDiagnosticError("Preview rendering", renderError);
        if ((diagnostics.Error ?? diagnostics.AudioError ?? renderError) is { } error)
        {
            ShowError(error, false);
        }

        ViewModel.RefreshCommands();
    }

    private void OnDocumentChanged(object? sender, EventArgs e)
    {
        ClearInspectorPreview();
        if (!committingTimingCreation)
        {
            RefreshDocument();
        }
        if (IsProjectBusy || closing)
        {
            return;
        }

        documentChangeTask = RunCommandAsync(async () =>
        {
            if (!workflow.IsPreviewBindingSynchronized())
            {
                await workflow.SynchronizePreviewBindingAsync();
            }
            else if (controller.Snapshot.State == VideoPlaybackState.PAUSED)
            {
                await controller.SeekAsync(playback.PendingPosition ?? controller.Snapshot.Position);
            }
        });
    }

    internal void SetProjectLocation(string? path, string directory)
    {
        if (!PathsEqual(projectPath, path) || !PathsEqual(projectDirectory, directory))
        {
            projectGeneration++;
        }
        projectPath = path;
        projectDirectory = directory;
        RefreshTitle();
    }

    private void RefreshTitle()
    {
        ViewModel.Title = WorkbenchProjectTitle.Format(ProjectDisplayName, HasUnsavedChanges);
        if (!closing && taskScopeDisplayName != ProjectDisplayName)
        {
            taskScopeDisplayName = ProjectDisplayName;
            applicationContext.Tasks.RegisterScope(TaskScope, taskScopeDisplayName);
        }
    }

    internal void ResetSelection()
    {
        ViewModel.Timeline.ClearTrackSolo();
        ViewModel.Timeline.ResumePlaybackFollow();
        InvalidateTimingSession();
        ResetSubtitleSelection();
        ClearInspectorPreview();
        SelectedCueId = null;
        SelectedLayerId = null;
        SelectedKeyTime = null;
        ClearTimingPreview();
        stylesDirty = false;
        effectsDirty = false;
        SceneEditing.DraftTarget = null;
        SceneEditing.GestureTarget = null;
        changedEffectFields.Clear();
    }

    internal static double ToSeconds(MediaTime time) => (double)time.Numerator / time.Denominator;
    internal static string FormatTime(MediaTime time)
    {
        var milliseconds = Math.Max(0, time.ToTimeSpan(MediaTimeRounding.FLOOR).Ticks / TimeSpan.TicksPerMillisecond);
        return string.Create(CultureInfo.InvariantCulture, $"{milliseconds / 3_600_000:00}:{milliseconds / 60_000 % 60:00}:{milliseconds / 1000 % 60:00}.{milliseconds % 1000:000}");
    }

    private static async Task DispatchAsync(Action action, CancellationToken token) => await Dispatcher.UIThread.InvokeAsync(action, DispatcherPriority.Render, token);
}
