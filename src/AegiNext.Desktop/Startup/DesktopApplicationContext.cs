using System.Collections.Immutable;
using AegiNext.Application.ColorTags;
using AegiNext.Application.Presets;
using AegiNext.Core.Projects;
using AegiNext.Media.Encoding.Presets;
using AegiNext.Desktop.Editing;
using AegiNext.Desktop.I18n;
using AegiNext.Desktop.Rendering;
using AegiNext.Desktop.Layouts;
using AegiNext.Application.Tasks;
using AegiNext.Desktop.Settings;
using AegiNext.Desktop.Settings.AudioAnalysis;
using AegiNext.Desktop.Settings.Transfer;
using AegiNext.Desktop.Updates;
using AegiNext.Desktop.Workspace;
using AegiNext.Media.Analysis;
using AegiNext.Rendering.Fonts;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace AegiNext.Desktop.Startup;

internal sealed class DesktopApplicationContext : IAsyncDisposable
{
    private readonly Lock lifetime = new();
    private readonly SubtitleFontSelectionService fonts;
    private readonly FontNamePreviewCache fontNamePreviews;
    private readonly WorkbenchPreferences? initialPreferences;
    private readonly HttpClient updateHttpClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private WorkbenchPreferences preferences;
    private Task? disposeTask;
    private int queuedStyles;
    private int queuedEffects;
    private int queuedExports;
    private int queuedColorTags;
    private bool closing;
    private Exception? preferencesLoadError;
    private readonly Dictionary<PersonalLibraryKind, Exception> libraryLoadErrors = [];

    internal DesktopApplicationContext(WorkbenchPreferencesStore? preferencesStore = null,
        WorkbenchPreferences? initialPreferences = null, SubtitleFontSelectionService? fontSelectionService = null,
        IUpdateReleaseSource? updateReleaseSource = null)
    {
        PreferencesStore = preferencesStore ?? new(Environment.GetEnvironmentVariable("AEGINEXT_PREFERENCES_DIRECTORY"));
        this.initialPreferences = initialPreferences;
        preferences = initialPreferences ?? new();
        Tasks = new();
        Tasks.MaximumConcurrentTasks = preferences.MaximumConcurrentTasks;
        Updates = new(Tasks, updateReleaseSource ?? new GitHubReleaseSource(updateHttpClient), ApplicationVersion.Current);
        fonts = fontSelectionService ?? new(Tasks);
        fontNamePreviews = new(Path.Combine(PreferencesStore.DirectoryPath, "caches", "fonts", "v1"),
            new SystemFontNamePreviewRenderer(() => fonts.Catalog));
        fonts.PreviewProvider = fontNamePreviews;
        preferences.Validate();
        AudioAnalysisBudget = new(preferences.AudioAnalysis.Execution.MaximumWorkers);
        preferencesLoadError = PreferencesStore.LoadError;
        SettingsRestore = new(PreferencesStore.DirectoryPath);
        StyleLibrary = new(Path.Combine(PreferencesStore.DirectoryPath, "subtitle-styles.aegistyles"));
        EffectScriptLibrary = new(Path.Combine(PreferencesStore.DirectoryPath, "effect-scripts.json"));
        ExportPresetLibrary = new(Path.Combine(PreferencesStore.DirectoryPath, "export-presets.aegiexports"));
        ColorTagLibrary = new(Path.Combine(PreferencesStore.DirectoryPath, "subtitle-color-tags.json"));
        RecentProjects = new(PreferencesStore.DirectoryPath, Tasks, deferLoad: true);
        RecentProjects.ErrorChanged += OnRecentProjectsError;
        if (initialPreferences is not null)
        {
            ApplyAppearance(preferences);
        }
        Initialization = InitializeAndLoadFontsAsync(Tasks.Submit(new ApplicationInitializationTask(this)).Completion);
    }

    internal event EventHandler? PreferencesChanged;
    internal event Func<AudioAnalysisOptions, Task>? AudioAnalysisRebuildRequested;
    internal event EventHandler? StylesChanged;
    internal event EventHandler? EffectsChanged;
    internal event EventHandler? ExportPresetsChanged;
    internal event EventHandler? ColorTagsChanged;
    internal event EventHandler? BusyChanged;
    internal event EventHandler? ErrorChanged;
    internal WorkbenchPreferencesStore PreferencesStore { get; }
    internal SubtitleStylePresetLibrary StyleLibrary { get; }
    internal EffectScriptPresetLibrary EffectScriptLibrary { get; }
    internal VideoExportPresetLibrary ExportPresetLibrary { get; }
    internal SubtitleColorTagLibrary ColorTagLibrary { get; }
    internal UserSettingsRestoreService SettingsRestore { get; }
    internal RecentProjectService RecentProjects { get; }
    internal AegiTaskService Tasks { get; }
    internal UpdateCheckService Updates { get; }
    internal AudioAnalysisWorkerBudget AudioAnalysisBudget { get; }
    internal SubtitleFontSelectionService Fonts => fonts;
    internal IReadOnlyCollection<AegiTaskResource> SettingsResources =>
    [
        AegiTaskResource.DeferredStoragePath(Path.Combine(PreferencesStore.DirectoryPath, "preferences.json")),
        GetLibraryResource(PersonalLibraryKind.STYLE), GetLibraryResource(PersonalLibraryKind.EFFECT),
        GetLibraryResource(PersonalLibraryKind.EXPORT), GetLibraryResource(PersonalLibraryKind.COLOR_TAG),
        AegiTaskResource.DeferredStoragePath(Path.Combine(PreferencesStore.DirectoryPath, "recent-projects.json")),
        AegiTaskResource.DeferredStoragePath(Path.Combine(PreferencesStore.DirectoryPath, "layouts.json")),
        AegiTaskResource.Named("settings-restore:" + PreferencesStore.DirectoryPath)
    ];
    internal Task Initialization { get; }
    internal WorkspaceLayoutFile InitialLayout { get; private set; } = new();
    internal Exception? LastError { get; private set; }
    internal Exception? SettingsLoadError
    {
        get
        {
            lock (lifetime)
            {
                return preferencesLoadError ?? libraryLoadErrors.Values.FirstOrDefault();
            }
        }
    }

    internal WorkbenchPreferences Preferences
    {
        get
        {
            lock (lifetime)
            {
                return preferences;
            }
        }
    }

    internal Task Completion => Tasks.DrainAsync();

    internal bool StylesBusy => Volatile.Read(ref queuedStyles) > 0;
    internal bool EffectsBusy => Volatile.Read(ref queuedEffects) > 0;
    internal bool ExportPresetsBusy => Volatile.Read(ref queuedExports) > 0;
    internal bool ColorTagsBusy => Volatile.Read(ref queuedColorTags) > 0;

    internal void UpdatePreferences(Func<WorkbenchPreferences, WorkbenchPreferences> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        WorkbenchPreferences value;
        bool languageChanged;
        lock (lifetime)
        {
            ObjectDisposedException.ThrowIf(closing, this);
            value = update(preferences);
            ArgumentNullException.ThrowIfNull(value);
            value.Validate();
            if (preferences == value)
            {
                return;
            }

            languageChanged = !string.Equals(preferences.Language, value.Language, StringComparison.OrdinalIgnoreCase);
            preferences = value;
        }

        try
        {
            Tasks.MaximumConcurrentTasks = value.MaximumConcurrentTasks;
            AudioAnalysisBudget.UpdateMaximumWorkers(value.AudioAnalysis.Execution.MaximumWorkers);
            ApplyAppearance(value, languageChanged);
            PreferencesChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            if (AegiTaskExecutionContext.Current is { } stage)
            {
                _ = stage.RunStageAsync("Tasks.PreferencesSave", _ => SavePreferencesAsync(value),
                    [AegiTaskResource.DeferredStoragePath(Path.Combine(PreferencesStore.DirectoryPath, "preferences.json"))]);
            }
            else
            {
                _ = Tasks.Submit(new PreferencesWriteTask(this, value)).Completion;
            }
        }
    }

    internal Task RebuildAudioAnalysisAsync(AudioAnalysisPreferences value)
    {
        ArgumentNullException.ThrowIfNull(value);
        value.Validate();
        UpdatePreferences(current => current with { AudioAnalysis = value });
        var options = new AudioAnalysisOptions { Recipe = value.Recipe, Execution = value.Execution };
        var subscribers = AudioAnalysisRebuildRequested?.GetInvocationList()
            .Cast<Func<AudioAnalysisOptions, Task>>().ToArray() ?? [];
        return Task.WhenAll(subscribers.Select(subscriber => subscriber(options)));
    }

    internal Task RunStyleOperationAsync(Func<Task> operation)
    {
        return EnqueueLibraryOperation(operation, PersonalLibraryKind.STYLE, true);
    }

    internal Task RunEffectOperationAsync(Func<Task> operation)
    {
        return EnqueueLibraryOperation(operation, PersonalLibraryKind.EFFECT, true);
    }

    internal Task RunExportPresetOperationAsync(Func<Task> operation)
    {
        return EnqueueLibraryOperation(operation, PersonalLibraryKind.EXPORT, true);
    }

    internal Task RunColorTagOperationAsync(Func<Task> operation)
    {
        return EnqueueLibraryOperation(operation, PersonalLibraryKind.COLOR_TAG, true);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (lifetime)
        {
            closing = true;
            disposeTask ??= DisposeCoreAsync();
            return new(disposeTask);
        }
    }

    private Task EnqueueLibraryOperation(Func<Task> operation, PersonalLibraryKind kind, bool propagateFailure)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (lifetime)
        {
            ObjectDisposedException.ThrowIf(closing, this);
        }
        if (AegiTaskExecutionContext.Current is { } parent)
        {
            return parent.RunStageAsync("Tasks.LibraryOperation", _ => ExecuteLibraryOperationAsync(operation, kind, propagateFailure),
                [GetLibraryResource(kind)]);
        }
        return Tasks.Submit(new PersonalLibraryTask(this, kind, operation, propagateFailure)).Completion;
    }

    internal AegiTaskResource GetLibraryResource(PersonalLibraryKind kind)
    {
        return AegiTaskResource.DeferredStoragePath(GetLibraryResourcePath(kind));
    }

    internal AegiTaskResource GetCanonicalLibraryResource(PersonalLibraryKind kind)
    {
        return AegiTaskResource.StoragePath(GetLibraryResourcePath(kind));
    }

    private string GetLibraryResourcePath(PersonalLibraryKind kind)
    {
        var name = kind switch
        {
            PersonalLibraryKind.STYLE => "subtitle-styles.aegistyles",
            PersonalLibraryKind.EFFECT => "effect-scripts.json",
            PersonalLibraryKind.EXPORT => "export-presets.aegiexports",
            PersonalLibraryKind.COLOR_TAG => "subtitle-color-tags.json",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        return Path.Combine(PreferencesStore.DirectoryPath, name);
    }

    private async Task InitializeAndLoadFontsAsync(Task initialization)
    {
        try
        {
            await initialization;
        }
        catch (ApplicationInitializationRecoveryException)
        {
        }
        if (!closing)
        {
            _ = ObserveFontsAsync();
        }
    }

    private async Task ObserveFontsAsync()
    {
        try
        {
            await fonts.EnsureLoadedAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            ReportError(error);
        }
    }

    internal async Task InitializeAsync(AegiTaskExecutionContext context)
    {
        await context.RunStageAsync("Tasks.SettingsRestore", async _ =>
        {
            await Task.Run(() => SettingsRestoreStartup.ApplyOnce(PreferencesStore.DirectoryPath));
        });
        var loaded = initialPreferences ?? await Task.Run(PreferencesStore.Load);
        loaded.Validate();
        lock (lifetime)
        {
            preferences = loaded;
            preferencesLoadError = PreferencesStore.LoadError;
        }
        Tasks.MaximumConcurrentTasks = loaded.MaximumConcurrentTasks;
        AudioAnalysisBudget.UpdateMaximumWorkers(loaded.AudioAnalysis.Execution.MaximumWorkers);
        ApplyAppearance(loaded);
        PreferencesChanged?.Invoke(this, EventArgs.Empty);
        await RecentProjects.InitializeAsync();
        InitialLayout = await Task.Run(() =>
        {
            using var layoutStore = new WorkspaceLayoutStore(PreferencesStore.DirectoryPath);
            return layoutStore.Load();
        });
        LastError = SettingsRestoreStartup.GetError(PreferencesStore.DirectoryPath) ?? PreferencesStore.LoadError ?? RecentProjects.LastError;
        await context.RunStageAsync("Tasks.StyleLibrary", _ => ExecuteLibraryOperationAsync(() => StyleLibrary.LoadAsync(), PersonalLibraryKind.STYLE, false));
        await context.RunStageAsync("Tasks.EffectLibrary", _ => ExecuteLibraryOperationAsync(() => EffectScriptLibrary.LoadAsync(), PersonalLibraryKind.EFFECT, false));
        await context.RunStageAsync("Tasks.ExportPresetLibrary", _ => ExecuteLibraryOperationAsync(() => ExportPresetLibrary.LoadAsync(), PersonalLibraryKind.EXPORT, false));
        await context.RunStageAsync("Tasks.ColorTagLibrary", _ => ExecuteLibraryOperationAsync(
            () => ColorTagLibrary.LoadAsync(CreateDefaultColorTags()), PersonalLibraryKind.COLOR_TAG, false));
        var recoveryErrors = new List<Exception>();
        foreach (var error in new[] { SettingsRestoreStartup.GetError(PreferencesStore.DirectoryPath), preferencesLoadError,
                     RecentProjects.LastError })
        {
            if (error is not null)
            {
                recoveryErrors.Add(error);
            }
        }
        lock (lifetime)
        {
            recoveryErrors.AddRange(libraryLoadErrors.Values);
        }
        if (recoveryErrors.Count > 0)
        {
            throw new ApplicationInitializationRecoveryException(recoveryErrors);
        }
    }

    internal async Task ExecuteLibraryOperationAsync(Func<Task> operation, PersonalLibraryKind kind, bool propagateFailure)
    {
        IncrementLibraryOperations(kind, 1);
        BusyChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            var before = LibrarySnapshot(kind);
            await operation();
            var after = LibrarySnapshot(kind);
            if (!propagateFailure || !ReferenceEquals(before, after))
            {
                SetLibraryLoadError(kind, null);
            }
            if (!ReferenceEquals(before, after))
            {
                switch (kind)
                {
                    case PersonalLibraryKind.STYLE:
                        StylesChanged?.Invoke(this, EventArgs.Empty);
                        break;
                    case PersonalLibraryKind.EFFECT:
                        EffectsChanged?.Invoke(this, EventArgs.Empty);
                        break;
                    case PersonalLibraryKind.EXPORT:
                        ExportPresetsChanged?.Invoke(this, EventArgs.Empty);
                        break;
                    case PersonalLibraryKind.COLOR_TAG:
                        ColorTagsChanged?.Invoke(this, EventArgs.Empty);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (propagateFailure)
            {
                throw;
            }
        }
        catch (Exception error)
        {
            if (!propagateFailure)
            {
                SetLibraryLoadError(kind, error);
            }
            ReportError(error);
            if (propagateFailure)
            {
                throw;
            }
        }
        finally
        {
            IncrementLibraryOperations(kind, -1);
            BusyChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void IncrementLibraryOperations(PersonalLibraryKind kind, int change)
    {
        switch (kind)
        {
            case PersonalLibraryKind.STYLE:
                Interlocked.Add(ref queuedStyles, change);
                break;
            case PersonalLibraryKind.EFFECT:
                Interlocked.Add(ref queuedEffects, change);
                break;
            case PersonalLibraryKind.EXPORT:
                Interlocked.Add(ref queuedExports, change);
                break;
            case PersonalLibraryKind.COLOR_TAG:
                Interlocked.Add(ref queuedColorTags, change);
                break;
        }
    }

    private object LibrarySnapshot(PersonalLibraryKind kind)
    {
        return kind switch
        {
            PersonalLibraryKind.STYLE => StyleLibrary.Snapshot,
            PersonalLibraryKind.EFFECT => EffectScriptLibrary.Snapshot,
            PersonalLibraryKind.EXPORT => ExportPresetLibrary.Snapshot,
            PersonalLibraryKind.COLOR_TAG => ColorTagLibrary.Snapshot,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    internal async Task SavePreferencesAsync(WorkbenchPreferences value)
    {
        try
        {
            await PreferencesStore.SaveAsync(value);
            lock (lifetime)
            {
                preferencesLoadError = null;
            }
        }
        catch (Exception error)
        {
            ReportError(error);
            throw;
        }
    }

    private void SetLibraryLoadError(PersonalLibraryKind kind, Exception? error)
    {
        lock (lifetime)
        {
            if (error is null)
            {
                libraryLoadErrors.Remove(kind);
            }
            else
            {
                libraryLoadErrors[kind] = error;
            }
        }
    }

    private void ReportError(Exception error)
    {
        LastError = error;
        ErrorChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnRecentProjectsError(object? sender, EventArgs e)
    {
        if (RecentProjects.LastError is { } error)
        {
            ReportError(error);
        }
    }

    private static void ApplyAppearance(WorkbenchPreferences value, bool applyLanguage = true)
    {
        if (applyLanguage)
        {
            WorkbenchCompositionRoot.ApplyLanguagePreference(value.Language);
        }
        if (Avalonia.Application.Current is not { } application)
        {
            return;
        }

        application.RequestedThemeVariant = value.Theme switch
        {
            WorkbenchTheme.LIGHT => ThemeVariant.Light,
            WorkbenchTheme.DARK => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
        var accent = Color.Parse(value.AccentColor);
        application.Resources["SystemAccentColor"] = accent;
        if (application.Styles.OfType<FluentTheme>().FirstOrDefault() is not { } fluent)
        {
            return;
        }

        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            if (!fluent.Palettes.TryGetValue(variant, out var palette))
            {
                palette = new();
                fluent.Palettes[variant] = palette;
            }

            palette.Accent = accent;
        }
    }

    private static ImmutableArray<SubtitleColorTag> CreateDefaultColorTags()
    {
        return
        [
            new() { Name = Localization.Get("Settings.ColorTagDefault.Red"), ColorHex = "#FF3B30" },
            new() { Name = Localization.Get("Settings.ColorTagDefault.Orange"), ColorHex = "#FF9500" },
            new() { Name = Localization.Get("Settings.ColorTagDefault.Yellow"), ColorHex = "#FFCC00" },
            new() { Name = Localization.Get("Settings.ColorTagDefault.Green"), ColorHex = "#34C759" },
            new() { Name = Localization.Get("Settings.ColorTagDefault.Blue"), ColorHex = "#007AFF" },
            new() { Name = Localization.Get("Settings.ColorTagDefault.Purple"), ColorHex = "#AF52DE" },
            new() { Name = Localization.Get("Settings.ColorTagDefault.Gray"), ColorHex = "#8E8E93" }
        ];
    }

    private async Task DisposeCoreAsync()
    {
        await Updates.DisposeAsync();
        updateHttpClient.Dispose();
        await fontNamePreviews.DisposeAsync();
        foreach (var task in Tasks.GetSnapshots().Where(value => !value.IsFinished && value.CanCancel))
        {
            Tasks.RequestCancel(task.Id);
        }
        await Tasks.DisposeAsync();
        AudioAnalysisBudget.Dispose();
        RecentProjects.ErrorChanged -= OnRecentProjectsError;
        await RecentProjects.DisposeAsync();
        StyleLibrary.Dispose();
        EffectScriptLibrary.Dispose();
        ExportPresetLibrary.Dispose();
        ColorTagLibrary.Dispose();
        SettingsRestore.Dispose();
        PreferencesStore.Dispose();
        PreferencesChanged = null;
        AudioAnalysisRebuildRequested = null;
        StylesChanged = null;
        EffectsChanged = null;
        ExportPresetsChanged = null;
        ColorTagsChanged = null;
        BusyChanged = null;
        ErrorChanged = null;
    }
}
