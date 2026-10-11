using System.Collections.Immutable;
using System.ComponentModel;
using AegiNext.Application.Tasks;
using AegiNext.Application.ColorTags;
using AegiNext.Core.Effects;
using AegiNext.Core.Presets;
using AegiNext.Core.Projects;
using AegiNext.Desktop.Editing;
using AegiNext.Desktop.I18n;
using AegiNext.Desktop.Layouts;
using AegiNext.Desktop.Settings.Effects;
using AegiNext.Desktop.Settings.Export;
using AegiNext.Desktop.Settings.Projects;
using AegiNext.Desktop.Settings.Presets;
using AegiNext.Desktop.Settings.Preview;
using AegiNext.Desktop.Settings.Tasks;
using AegiNext.Desktop.Settings.AudioAnalysis;
using AegiNext.Desktop.Settings.Updates;
using AegiNext.Desktop.Settings.TimingPostProcessor;
using AegiNext.Desktop.Settings.Transfer;
using AegiNext.Desktop.Startup;
using AegiNext.Desktop.Workspace;
using AegiNext.Desktop.Workspace.Diagnostics;
using AegiNext.Desktop.Windowing;
using Avalonia.Controls;

namespace AegiNext.Desktop.Settings;

internal sealed class SettingsWindowCoordinator(DesktopApplicationContext applicationContext,
    Func<Window, IWorkbenchDialogService>? createDialogs = null, Func<WorkspaceLayoutFile>? captureLayout = null,
    Action? requestApplicationExit = null) : IDisposable
{
    private WorkbenchSession? session;
    private IWorkbenchDialogService? dialogs;
    private WorkbenchPreferences? presentedPreferences;
    private bool disposed;
    private IWindowChrome? chrome;
    private SettingsExportPresetCoordinator? exportPresets;
    private SettingsTransferCoordinator? transfer;
    private Task closedTransferCompletion = Task.CompletedTask;
    private CancellationTokenSource? deletionCancellation;
    private bool deletionActive;
    private readonly HashSet<AegiTaskHandle> libraryTasks = [];
    internal Task ExportCompletion => exportPresets?.Completion ?? Task.CompletedTask;
    internal Task TransferCompletion => Task.WhenAll(closedTransferCompletion, transfer?.Completion ?? Task.CompletedTask);
    internal Task TimingCompletion { get; private set; } = Task.CompletedTask;
    internal Task DeletionCompletion { get; private set; } = Task.CompletedTask;

    internal event Action<WorkbenchLogEntry>? EffectScriptErrorReported;
    internal SettingsWindow? Window { get; private set; }

    internal Task OpenAsync(Window owner, WorkbenchSession? session = null, SettingsPage? page = null,
        Action<Window>? registerWindow = null, bool modal = false)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(owner);
        if (Window is { } existing)
        {
            if (page is { } requested)
            {
                existing.SelectPage(requested);
            }

            existing.Activate();
            return Task.CompletedTask;
        }

        var window = new SettingsWindow(applicationContext.Preferences);
        window.ViewModel.Styles.SetFonts(applicationContext.Fonts);
        Window = window;
        this.session = session;
        presentedPreferences = applicationContext.Preferences;
        dialogs = createDialogs?.Invoke(window) ?? new WindowWorkbenchDialogService(window);
        deletionCancellation = new();
        try
        {
            Subscribe(window);
            exportPresets = new(applicationContext, window, dialogs, session);
            transfer = new(applicationContext, window, dialogs, captureLayout, requestApplicationExit);
            window.ViewModel.Styles.SetPositionMeasurement((preset, text) => session is null
                ? MeasureDefaultPosition(preset, text)
                : session.MeasureStylePosition(preset, text));
            window.UpdateShortcuts(applicationContext.Preferences.ShortcutBindings);
            window.UpdateStyles(applicationContext.StyleLibrary.Snapshot.Presets);
            window.UpdateEffects(applicationContext.EffectScriptLibrary.Snapshot.Presets);
            window.ViewModel.ColorTags.UpdateLibrary(applicationContext.ColorTagLibrary.Snapshot);
            RefreshAvailability();
            RefreshMediaSettings();
            window.ShowError(applicationContext.LastError?.Message);
            if (page is { } selected)
            {
                window.SelectPage(selected);
            }

            if (registerWindow is null)
            {
                chrome = WindowChrome.Attach(window, window.TitleBar);
            }
            else
            {
                registerWindow(window);
            }
            if (modal)
            {
                return window.ShowDialog(owner);
            }

            window.Show(owner);
            return Task.CompletedTask;
        }
        catch
        {
            CancelDeletion();
            DisposeTransfer();
            exportPresets?.Dispose();
            exportPresets = null;
            Unsubscribe(window);
            Window = null;
            this.session = null;
            dialogs = null;
            presentedPreferences = null;
            window.Close();
            chrome?.Dispose();
            chrome = null;
            throw;
        }
    }

    internal void Close()
    {
        Window?.Close();
    }

    /// <summary>关闭设置并解除窗口、共享资源与工程会话事件。</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        CancelDeletion();
        DisposeTransfer();
        exportPresets?.Dispose();
        Window?.CloseImmediately();
        EffectScriptErrorReported = null;
    }

    private void Subscribe(SettingsWindow window)
    {
        window.ViewModel.Styles.SaveDraftAsync = preset => RunAsync(() => SaveStylePresetAsync(preset));
        window.ViewModel.Effects.SaveDraftAsync = preset => RunAsync(() =>
            applicationContext.RunEffectOperationAsync(() => applicationContext.EffectScriptLibrary.UpsertAsync(preset)), true);
        window.ViewModel.Styles.ConfirmLeaveAsync = () => dialogs!.ConfirmPresetChangesAsync(false);
        window.ViewModel.Effects.ConfirmLeaveAsync = () => dialogs!.ConfirmPresetChangesAsync(true);
        window.ViewModel.ColorTags.SaveDraftAsync = async (document, expected) =>
            await RunAsync(() => SaveColorTagsAsync(document, expected, window))
                ? applicationContext.ColorTagLibrary.Snapshot : null;
        window.ViewModel.ColorTags.ConfirmLeaveAsync = () => dialogs!.ConfirmColorTagChangesAsync();
        applicationContext.PreferencesChanged += OnPreferencesChanged;
        applicationContext.StylesChanged += OnStylesChanged;
        applicationContext.EffectsChanged += OnEffectsChanged;
        applicationContext.ColorTagsChanged += OnColorTagsChanged;
        applicationContext.BusyChanged += OnBusyChanged;
        applicationContext.ErrorChanged += OnErrorChanged;
        window.AppearanceChanged += OnAppearanceChanged;
        window.ColorsChanged += OnColorsChanged;
        window.ShortcutsChanged += OnShortcutsChanged;
        window.PreviewDecodeModeChanged += OnPreviewDecodeModeChanged;
        window.ViewModel.Media.AudioCalibrationChanged += OnAudioCalibrationChanged;
        window.ProjectsChanged += OnProjectsChanged;
        window.PreviewChanged += OnPreviewChanged;
        window.TasksChanged += OnTasksChanged;
        window.UpdatesChanged += OnUpdatesChanged;
        window.AudioAnalysisChanged += OnAudioAnalysisChanged;
        window.AudioAnalysisRebuildRequested += OnAudioAnalysisRebuildRequested;
        window.TimingPreferencesChanged += OnTimingPreferencesChanged;
        window.TimingAssociateRequested += OnTimingAssociationRequested;
        window.TimingUnlinkRequested += OnTimingAssociationRequested;
        window.UpsertStyleRequested += OnUpsertStyleRequested;
        window.DeleteStyleRequested += OnDeleteStyleRequested;
        window.CaptureStyleRequested += OnCaptureStyleRequested;
        window.ApplyStyleRequested += OnApplyStyleRequested;
        window.ImportStylesRequested += OnImportStylesRequested;
        window.ExportStylesRequested += OnExportStylesRequested;
        window.EffectValidationFailed += OnEffectValidationFailed;
        window.UpsertEffectRequested += OnUpsertEffectRequested;
        window.DeleteEffectRequested += OnDeleteEffectRequested;
        window.ImportEffectRequested += OnImportEffectRequested;
        window.ExportEffectRequested += OnExportEffectRequested;
        window.Closed += OnWindowClosed;
        if (session is { } active)
        {
            active.PreviewDecodeModeChanged += OnSessionPreviewDecodeModeChanged;
            active.AudioClockChanged += OnSessionPreviewDecodeModeChanged;
            active.SelectionChanged += OnSelectionChanged;
            active.Styles.BusyChanged += OnBusyChanged;
            active.StyleLibraryChanged += OnBusyChanged;
            active.EffectLibraryChanged += OnBusyChanged;
            active.ViewModel.PropertyChanged += OnSessionStateChanged;
        }
    }

    private void Unsubscribe(SettingsWindow window)
    {
        window.ViewModel.Styles.SaveDraftAsync = null;
        window.ViewModel.Effects.SaveDraftAsync = null;
        window.ViewModel.Styles.ConfirmLeaveAsync = null;
        window.ViewModel.Effects.ConfirmLeaveAsync = null;
        window.ViewModel.ColorTags.SaveDraftAsync = null;
        window.ViewModel.ColorTags.ConfirmLeaveAsync = null;
        applicationContext.PreferencesChanged -= OnPreferencesChanged;
        applicationContext.StylesChanged -= OnStylesChanged;
        applicationContext.EffectsChanged -= OnEffectsChanged;
        applicationContext.ColorTagsChanged -= OnColorTagsChanged;
        applicationContext.BusyChanged -= OnBusyChanged;
        applicationContext.ErrorChanged -= OnErrorChanged;
        window.AppearanceChanged -= OnAppearanceChanged;
        window.ColorsChanged -= OnColorsChanged;
        window.ShortcutsChanged -= OnShortcutsChanged;
        window.PreviewDecodeModeChanged -= OnPreviewDecodeModeChanged;
        window.ViewModel.Media.AudioCalibrationChanged -= OnAudioCalibrationChanged;
        window.ProjectsChanged -= OnProjectsChanged;
        window.PreviewChanged -= OnPreviewChanged;
        window.TasksChanged -= OnTasksChanged;
        window.UpdatesChanged -= OnUpdatesChanged;
        window.AudioAnalysisChanged -= OnAudioAnalysisChanged;
        window.AudioAnalysisRebuildRequested -= OnAudioAnalysisRebuildRequested;
        window.TimingPreferencesChanged -= OnTimingPreferencesChanged;
        window.TimingAssociateRequested -= OnTimingAssociationRequested;
        window.TimingUnlinkRequested -= OnTimingAssociationRequested;
        window.UpsertStyleRequested -= OnUpsertStyleRequested;
        window.DeleteStyleRequested -= OnDeleteStyleRequested;
        window.CaptureStyleRequested -= OnCaptureStyleRequested;
        window.ApplyStyleRequested -= OnApplyStyleRequested;
        window.ImportStylesRequested -= OnImportStylesRequested;
        window.ExportStylesRequested -= OnExportStylesRequested;
        window.EffectValidationFailed -= OnEffectValidationFailed;
        window.UpsertEffectRequested -= OnUpsertEffectRequested;
        window.DeleteEffectRequested -= OnDeleteEffectRequested;
        window.ImportEffectRequested -= OnImportEffectRequested;
        window.ExportEffectRequested -= OnExportEffectRequested;
        window.Closed -= OnWindowClosed;
        if (session is { } active)
        {
            active.PreviewDecodeModeChanged -= OnSessionPreviewDecodeModeChanged;
            active.AudioClockChanged -= OnSessionPreviewDecodeModeChanged;
            active.SelectionChanged -= OnSelectionChanged;
            active.Styles.BusyChanged -= OnBusyChanged;
            active.StyleLibraryChanged -= OnBusyChanged;
            active.EffectLibraryChanged -= OnBusyChanged;
            active.ViewModel.PropertyChanged -= OnSessionStateChanged;
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (sender is SettingsWindow window && ReferenceEquals(Window, window))
        {
            CancelDeletion();
            DisposeTransfer();
            exportPresets?.Dispose();
            exportPresets = null;
            Unsubscribe(window);
            chrome?.Dispose();
            chrome = null;
            Window = null;
            session = null;
            dialogs = null;
            presentedPreferences = null;
        }
    }

    private void DisposeTransfer()
    {
        if (transfer is { } current)
        {
            current.Dispose();
            closedTransferCompletion = Task.WhenAll(closedTransferCompletion, current.Completion);
            transfer = null;
        }
    }

    private void CancelDeletion()
    {
        deletionCancellation?.Cancel();
        foreach (var task in libraryTasks.ToArray())
        {
            task.RequestCancel();
        }
        deletionCancellation?.Dispose();
        deletionCancellation = null;
        deletionActive = false;
    }

    private void OnPreferencesChanged(object? sender, EventArgs e)
    {
        var current = applicationContext.Preferences;
        var previous = presentedPreferences;
        presentedPreferences = current;
        Window?.UpdatePreferences(current);
        if (previous is null || !previous.ShortcutBindings.SequenceEqual(current.ShortcutBindings))
        {
            Window?.UpdateShortcuts(current.ShortcutBindings);
        }
    }

    private void OnStylesChanged(object? sender, EventArgs e)
    {
        Window?.UpdateStyles(applicationContext.StyleLibrary.Snapshot.Presets);
        RefreshTimingStyles();
    }

    private void OnEffectsChanged(object? sender, EventArgs e)
    {
        Window?.UpdateEffects(applicationContext.EffectScriptLibrary.Snapshot.Presets);
    }

    private void OnColorTagsChanged(object? sender, EventArgs e) =>
        Window?.ViewModel.ColorTags.UpdateLibrary(applicationContext.ColorTagLibrary.Snapshot);

    private Task SaveColorTagsAsync(SubtitleColorTagLibraryDocument document, SubtitleColorTagLibraryDocument expected,
        SettingsWindow window) => applicationContext.RunColorTagOperationAsync(() =>
        applicationContext.ColorTagLibrary.ReplaceAsync(document, () =>
        {
            if (disposed || !ReferenceEquals(Window, window))
            {
                throw new OperationCanceledException();
            }
            if (!ReferenceEquals(applicationContext.ColorTagLibrary.Snapshot, expected))
            {
                throw new InvalidDataException(Localization.Get("Settings.ColorTagLibraryChanged"));
            }
        }));

    private void OnBusyChanged(object? sender, EventArgs e)
    {
        RefreshAvailability();
    }

    private void OnErrorChanged(object? sender, EventArgs e)
    {
        Window?.ShowError(applicationContext.LastError?.Message);
    }

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        RefreshAvailability();
    }

    private void OnSessionStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkbenchViewModel.IsBusy))
        {
            RefreshAvailability();
        }
        else if (e.PropertyName == nameof(WorkbenchViewModel.Error))
        {
            Window?.ShowError(session?.ViewModel.Error);
        }
    }

    private void RefreshAvailability()
    {
        var projectBusy = session is { } active && (active.IsProjectBusy || active.IsClosing);
        Window?.SetStyleOperationBusy(deletionActive || applicationContext.StylesBusy || projectBusy || session?.Styles.IsBusy == true);
        Window?.SetEffectOperationBusy(deletionActive || applicationContext.EffectsBusy || projectBusy || session?.EffectScripts.IsBusy == true);
        if (Window is { } window)
        {
            window.ViewModel.ColorTags.IsBusy = applicationContext.ColorTagsBusy;
        }
        Window?.UpdateSelectionAvailability(session is { HasSelectedCue: true, IsProjectBusy: false, IsClosing: false });
        RefreshTimingStyles();
    }

    private void OnSessionPreviewDecodeModeChanged(object? sender, EventArgs e)
    {
        RefreshMediaSettings();
    }

    private void RefreshMediaSettings()
    {
        if (Window is not { } window)
        {
            return;
        }

        window.ViewModel.Media.IsBusy = session?.IsPreviewDecodeModeSwitching == true || session?.IsSwitchingAudioDevice == true;
        window.ViewModel.Media.UpdatePreferences(applicationContext.Preferences);
        var info = session?.PreviewDecodeSessionInfo;
        window.ViewModel.Media.UpdateDecodeStatus(info?.ActiveBackend.ToString(), info?.HardwareConfirmed == true,
            info?.FallbackReason);
        window.ViewModel.Media.UpdateAudioStatus(session?.AudioClock);
    }

    private void OnAppearanceChanged(object? sender, SettingsAppearanceChangedEventArgs e)
    {
        UpdatePreferences(value => value with
        {
            Theme = e.Theme, Language = e.Language, WindowMenuOnMac = e.WindowMenuOnMac
        });
    }

    private void OnColorsChanged(object? sender, SettingsColorsChangedEventArgs e)
    {
        UpdatePreferences(value => value with { AccentColor = e.AccentColor, AudioGraph = e.AudioGraph, TimelineClips = e.TimelineClips });
    }

    private void OnProjectsChanged(object? sender, ProjectPreferencesChangedEventArgs e)
    {
        UpdatePreferences(value => value with { Projects = e.Preferences });
    }

    private void OnTasksChanged(object? sender, TaskSettingsChangedEventArgs e)
    {
        UpdatePreferences(value => value with { MaximumConcurrentTasks = e.MaximumConcurrentTasks });
    }

    private void OnUpdatesChanged(object? sender, UpdateSettingsChangedEventArgs e)
    {
        UpdatePreferences(value => value with { AutoCheckUpdates = e.AutoCheckUpdates, UpdateChannel = e.UpdateChannel });
    }

    private void OnAudioAnalysisChanged(object? sender, AudioAnalysisPreferencesChangedEventArgs e)
    {
        UpdatePreferences(value => value with { AudioAnalysis = e.Preferences });
    }

    private void OnAudioAnalysisRebuildRequested(object? sender, AudioAnalysisRebuildRequestedEventArgs e)
    {
        _ = RunAsync(() => applicationContext.RebuildAudioAnalysisAsync(e.Preferences));
    }

    private void OnPreviewChanged(object? sender, PreviewSettingsChangedEventArgs e)
    {
        UpdatePreferences(value => value with { SubtitleAuditionMilliseconds = e.SubtitleAuditionMilliseconds });
    }

    private void RefreshTimingStyles()
    {
        if (Window is { } window)
        {
            window.ViewModel.TimingPostProcessor.UpdateStyles(applicationContext.StyleLibrary.Snapshot.Presets);
            window.ViewModel.TimingPostProcessor.IsBusy = applicationContext.StylesBusy;
        }
    }

    private void OnTimingPreferencesChanged(object? sender, TimingPostProcessorPreferencesChangedEventArgs e)
    {
        UpdatePreferences(value => value with { TimingPostProcessor = e.Preferences });
    }

    private void OnTimingAssociationRequested(object? sender, TimingPostProcessorAssociationEventArgs e)
    {
        if (Window is not { } target || applicationContext.StylesBusy || !TimingCompletion.IsCompleted)
        {
            return;
        }

        TimingCompletion = SaveTimingAssociationAsync(target, e);
    }

    private async Task SaveTimingAssociationAsync(SettingsWindow target, TimingPostProcessorAssociationEventArgs request)
    {
        var model = target.ViewModel.TimingPostProcessor;
        model.IsBusy = true;
        target.ShowError(null);
        var saved = false;
        try
        {
            await applicationContext.RunStyleOperationAsync(() =>
                applicationContext.StyleLibrary.SetTimingPostProcessorAsync(request.StyleIds, request.Options));
            saved = true;
        }
        catch (Exception error)
        {
            if (ReferenceEquals(Window, target))
            {
                target.ShowError(Localization.Format("Settings.TimingAssociationFailed", error.Message));
            }
        }
        finally
        {
            model.IsBusy = false;
            if (ReferenceEquals(Window, target))
            {
                RefreshTimingStyles();
                if (saved)
                {
                    if (request.Options is null)
                    {
                        model.ShowUnlinked(request.StyleIds.Count);
                    }
                    else
                    {
                        model.ShowResult(request.StyleIds.Count);
                    }
                }
            }
        }
    }

    private void OnShortcutsChanged(object? sender, SettingsShortcutsChangedEventArgs e)
    {
        UpdatePreferences(value => value with { ShortcutBindings = e.Bindings });
    }

    private void UpdatePreferences(Func<WorkbenchPreferences, WorkbenchPreferences> update)
    {
        _ = RunAsync(() =>
        {
            applicationContext.UpdatePreferences(update);
            return Task.CompletedTask;
        });
    }

    private void OnPreviewDecodeModeChanged(object? sender, SettingsPreviewDecodeModeChangedEventArgs e)
    {
        var active = session;
        var targetWindow = Window;
        _ = RunAsync(async () =>
        {
            if (active is null)
            {
                applicationContext.UpdatePreferences(value => value with { PreviewDecodeMode = e.Mode });
            }
            else if (!await active.SetPreviewDecodeModeAsync(e.Mode) && active.LastError is { } error)
            {
                if (ReferenceEquals(Window, targetWindow))
                {
                    targetWindow?.ShowError(error.Message);
                }
            }

            RefreshMediaSettings();
        });
    }

    private void OnAudioCalibrationChanged(object? sender, SettingsAudioCalibrationChangedEventArgs e)
    {
        var active = session;
        _ = RunAsync(async () =>
        {
            if (active is null)
            {
                throw new InvalidOperationException("音频校准需要已打开媒体的工作台。");
            }
            await active.SetAudioCalibrationAsync(e.Calibration);
            RefreshMediaSettings();
        });
    }

    private void OnUpsertStyleRequested(object? sender, SettingsStyleEventArgs e)
    {
        _ = RunAsync(() => SaveStylePresetAsync(e.Preset));
    }

    private Task SaveStylePresetAsync(SubtitleStylePreset preset)
    {
        return applicationContext.RunStyleOperationAsync(() =>
        {
            var current = applicationContext.StyleLibrary.Snapshot.Presets.FirstOrDefault(value => value.Id == preset.Id);
            var updated = current is null ? preset : preset with { TimingPostProcessor = current.TimingPostProcessor };
            return applicationContext.StyleLibrary.UpsertAsync(updated);
        });
    }

    private void OnDeleteStyleRequested(object? sender, SettingsStyleDeleteEventArgs e)
    {
        RequestPresetDeletion(e.Ids, e.IsDraftOnly, e.DraftId, false);
    }

    private void RequestPresetDeletion(ImmutableArray<Guid> ids, bool isDraftOnly, Guid? draftId, bool effects)
    {
        if (disposed || deletionActive || Window is not { } target || dialogs is not { } service
            || deletionCancellation is not { } cancellation || (effects ? applicationContext.EffectsBusy : applicationContext.StylesBusy))
        {
            return;
        }

        deletionActive = true;
        RefreshAvailability();
        var token = cancellation.Token;
        DeletionCompletion = RunAsync(async () =>
        {
            try
            {
                var names = isDraftOnly
                    ? ImmutableArray.Create(effects ? target.ViewModel.Effects.Name : target.ViewModel.Styles.Name)
                    : effects
                        ? ids.Select(id => applicationContext.EffectScriptLibrary.Snapshot.Presets.Single(preset => preset.Id == id).Name).ToImmutableArray()
                        : ids.Select(id => applicationContext.StyleLibrary.Snapshot.Presets.Single(preset => preset.Id == id).Name).ToImmutableArray();
                var accepted = await service.ConfirmPresetDeletionAsync(new(names, isDraftOnly), token).WaitAsync(token);
                token.ThrowIfCancellationRequested();
                if (!accepted || disposed || !ReferenceEquals(Window, target))
                {
                    return;
                }

                if (!isDraftOnly)
                {
                    if (effects)
                    {
                        await applicationContext.RunEffectOperationAsync(() => applicationContext.EffectScriptLibrary.RemoveAsync(ids, token));
                    }
                    else
                    {
                        await applicationContext.RunStyleOperationAsync(() => applicationContext.StyleLibrary.RemoveAsync(ids, token));
                    }
                }
                if (disposed || !ReferenceEquals(Window, target))
                {
                    return;
                }

                var currentDraftId = effects ? target.ViewModel.Effects.Draft?.Id : target.ViewModel.Styles.Draft?.Id;
                if (currentDraftId is { } current && (isDraftOnly ? current == draftId : ids.Contains(current)))
                {
                    if (effects)
                    {
                        target.ViewModel.Effects.DiscardDraft();
                    }
                    else
                    {
                        target.ViewModel.Styles.DiscardDraft();
                    }
                }
            }
            finally
            {
                if (ReferenceEquals(Window, target))
                {
                    deletionActive = false;
                    RefreshAvailability();
                }
            }
        }, effects);
    }

    private void OnCaptureStyleRequested(object? sender, EventArgs e)
    {
        if (session is { } active)
        {
            _ = RunAsync(() =>
            {
                active.Styles.Queue(active.Styles.CaptureAsync);
                return active.Styles.Completion;
            });
        }
    }

    private void OnApplyStyleRequested(object? sender, SettingsStyleEventArgs e)
    {
        if (session is { } active)
        {
            _ = RunAsync(() =>
            {
                active.Styles.Queue(() => active.Styles.ApplyAsync(e.Preset));
                return active.Styles.Completion;
            });
        }
    }

    private void OnImportStylesRequested(object? sender, EventArgs e)
    {
        var service = dialogs!;
        var target = Window;
        var token = deletionCancellation!.Token;
        _ = RunAsync(async () =>
        {
            var paths = await service.OpenFilesAsync("ImportStyles", "StyleFiles", ["*.aegistyles"]).WaitAsync(token);
            token.ThrowIfCancellationRequested();
            if (paths.Count > 0 && !disposed && ReferenceEquals(Window, target))
            {
                await SubmitLibraryTaskAsync(new SettingsStyleImportTask(applicationContext, paths), token);
            }
        });
    }

    private void OnExportStylesRequested(object? sender, SettingsStylesExportEventArgs e)
    {
        var service = dialogs!;
        var target = Window;
        var token = deletionCancellation!.Token;
        var presets = e.Presets;
        _ = RunAsync(async () =>
        {
            string? destination = null;
            var batch = presets.Length > 1;
            if (presets.Length == 1)
            {
                destination = await service.SaveFileAsync("ExportStyles", "StyleFiles", ["*.aegistyles"],
                    ".aegistyles", "styles.aegistyles").WaitAsync(token);
            }
            else if (batch)
            {
                destination = await service.OpenFolderAsync("ExportStyles").WaitAsync(token);
            }

            token.ThrowIfCancellationRequested();
            if (destination is not null && !disposed && ReferenceEquals(Window, target))
            {
                await SubmitLibraryTaskAsync(new SettingsStyleExportTask(applicationContext, presets, destination, batch), token);
            }
        });
    }

    private void OnEffectValidationFailed(object? sender, EffectScriptValidationFailedEventArgs e)
    {
        if (session is { IsClosing: false } active)
        {
            EffectScriptErrorReported?.Invoke(active.LogError("Effects", e.Error));
        }
    }

    private void OnUpsertEffectRequested(object? sender, SettingsEffectEventArgs e)
    {
        _ = RunAsync(() => applicationContext.RunEffectOperationAsync(() => applicationContext.EffectScriptLibrary.UpsertAsync(e.Preset)), true);
    }

    private void OnDeleteEffectRequested(object? sender, SettingsEffectDeleteEventArgs e)
    {
        RequestPresetDeletion(e.Ids, e.IsDraftOnly, e.DraftId, true);
    }

    private void OnImportEffectRequested(object? sender, EventArgs e)
    {
        var service = dialogs!;
        var target = Window;
        var token = deletionCancellation!.Token;
        _ = RunAsync(async () =>
        {
            var paths = await service.OpenFilesAsync("ImportEffectScripts", "EffectScriptFiles", ["*.aegifx"]).WaitAsync(token);
            token.ThrowIfCancellationRequested();
            if (paths.Count > 0 && !disposed && ReferenceEquals(Window, target))
            {
                await SubmitLibraryTaskAsync(new SettingsEffectImportTask(applicationContext, paths), token);
            }
        }, true);
    }

    private void OnExportEffectRequested(object? sender, SettingsEffectsExportEventArgs e)
    {
        var service = dialogs!;
        var target = Window;
        var token = deletionCancellation!.Token;
        var presets = e.Presets;
        _ = RunAsync(async () =>
        {
            string? destination = null;
            var batch = presets.Length > 1;
            if (presets.Length == 1)
            {
                var script = EffectScriptParser.Parse(presets[0].Source);
                destination = await service.SaveFileAsync("ExportEffectScripts", "EffectScriptFiles", ["*.aegifx"],
                    ".aegifx", script.Id + ".aegifx").WaitAsync(token);
            }
            else if (batch)
            {
                destination = await service.OpenFolderAsync("ExportEffectScripts").WaitAsync(token);
            }

            token.ThrowIfCancellationRequested();
            if (destination is not null && !disposed && ReferenceEquals(Window, target))
            {
                await SubmitLibraryTaskAsync(new SettingsEffectExportTask(applicationContext, presets, destination, batch), token);
            }
        }, true);
    }

    private async Task SubmitLibraryTaskAsync(AegiTask task, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var handle = applicationContext.Tasks.Submit(task);
        libraryTasks.Add(handle);
        try
        {
            if (disposed || cancellationToken.IsCancellationRequested)
            {
                handle.RequestCancel();
            }
            await handle.Completion;
        }
        finally
        {
            libraryTasks.Remove(handle);
        }
    }

    private async Task<bool> RunAsync(Func<Task> operation, bool effectOperation = false)
    {
        if (disposed || Window is not { } targetWindow)
        {
            return false;
        }

        var targetSession = session;
        targetWindow.ShowError(null);
        try
        {
            await operation();
            return true;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            if (ReferenceEquals(Window, targetWindow))
            {
                targetWindow.ShowError(error.Message);
            }

            if (effectOperation)
            {
                ReportEffectError(targetSession, error);
            }
            else if (targetSession is { IsClosing: false })
            {
                targetSession.ShowError(error);
            }
        }
        return false;
    }

    private void ReportEffectError(WorkbenchSession? targetSession, Exception error)
    {
        if (targetSession is { IsClosing: false })
        {
            targetSession.ShowError(error, false);
            EffectScriptErrorReported?.Invoke(targetSession.LogError("Effects", error));
        }
    }

    private SubtitlePositionMeasurement MeasureDefaultPosition(SubtitleStylePreset preset, string text)
    {
        var document = new ProjectDocument();
        return Rendering.SubtitleStylePositionMeasurer.Measure(preset, document.Width, document.Height,
            text, applicationContext.Fonts.Catalog);
    }
}
