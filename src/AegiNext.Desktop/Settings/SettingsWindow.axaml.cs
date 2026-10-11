using AegiNext.Core.Presets;
using AegiNext.Application.Presets;
using AegiNext.Desktop.Editing;
using AegiNext.Desktop.Controls.Common;
using AegiNext.Desktop.Shortcuts;
using AegiNext.Desktop.I18n;
using AegiNext.Desktop.Settings.Effects;
using AegiNext.Desktop.Settings.Projects;
using AegiNext.Desktop.Settings.Preview;
using AegiNext.Desktop.Settings.Tasks;
using AegiNext.Desktop.Settings.AudioAnalysis;
using AegiNext.Desktop.Settings.TimingPostProcessor;
using AegiNext.Desktop.Settings.Updates;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;

namespace AegiNext.Desktop.Settings;

/// <summary>设置宿主，只组合页面、呈现主题并转发语义请求。</summary>
public sealed partial class SettingsWindow : Window
{
    private bool allowClose;
    private bool preparingClose;
    internal Task CloseCompletion { get; private set; } = Task.CompletedTask;
    /// <summary>供 XAML 加载器构造默认设置宿主。</summary>
    public SettingsWindow() : this(new WorkbenchPreferences())
    {
    }

    /// <summary>创建使用显式偏好快照的设置窗口。</summary>
    public SettingsWindow(WorkbenchPreferences preferences) : this(new SettingsWindowViewModel(preferences), preferences)
    {
    }

    /// <summary>由组合根注入设置模型，窗口不读取全局偏好。</summary>
    public SettingsWindow(SettingsWindowViewModel viewModel, WorkbenchPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ViewModel = viewModel;
        DataContext = viewModel;
        AvaloniaXamlLoader.Load(this);
        TitleBar = this.FindControl<WindowTitleBar>("SettingsTitleBar")!;
        viewModel.Appearance.Changed += OnAppearanceChanged;
        viewModel.Colors.Changed += OnColorsChanged;
        viewModel.Shortcuts.Changed += OnShortcutsChanged;
        viewModel.Media.DecodeModeChanged += OnPreviewDecodeModeChanged;
        viewModel.Projects.Changed += OnProjectsChanged;
        viewModel.Preview.Changed += OnPreviewChanged;
        viewModel.Tasks.Changed += OnTasksChanged;
        viewModel.Updates.Changed += OnUpdatesChanged;
        viewModel.AudioAnalysis.Changed += OnAudioAnalysisChanged;
        viewModel.AudioAnalysis.RebuildRequested += OnAudioAnalysisRebuildRequested;
        viewModel.TimingPostProcessor.Changed += OnTimingPreferencesChanged;
        viewModel.TimingPostProcessor.AssociateRequested += OnTimingAssociateRequested;
        viewModel.TimingPostProcessor.UnlinkRequested += OnTimingUnlinkRequested;
        viewModel.Styles.UpsertRequested += OnUpsertStyleRequested;
        viewModel.Styles.DeleteRequested += OnDeleteStyleRequested;
        viewModel.Styles.ApplyRequested += OnApplyStyleRequested;
        viewModel.Styles.CaptureRequested += OnCaptureStyleRequested;
        viewModel.Styles.ImportRequested += OnImportStylesRequested;
        viewModel.Styles.ExportRequested += OnExportStylesRequested;
        viewModel.Effects.SaveRequested += OnUpsertEffectRequested;
        viewModel.Effects.DeleteRequested += OnDeleteEffectRequested;
        viewModel.Effects.ImportRequested += OnImportEffectRequested;
        viewModel.Effects.ExportRequested += OnExportEffectRequested;
        viewModel.Effects.ValidationFailed += OnEffectValidationFailed;
        Deactivated += OnDeactivated;
        Closed += OnClosed;
        Closing += OnClosing;
        Localization.LanguageChanged += OnLanguageChanged;
        UpdatePreferences(preferences);
    }

    public event EventHandler<SettingsAppearanceChangedEventArgs>? AppearanceChanged;
    public event EventHandler<SettingsColorsChangedEventArgs>? ColorsChanged;
    public event EventHandler<SettingsShortcutsChangedEventArgs>? ShortcutsChanged;
    public event EventHandler<SettingsPreviewDecodeModeChangedEventArgs>? PreviewDecodeModeChanged;
    public event EventHandler<ProjectPreferencesChangedEventArgs>? ProjectsChanged;
    public event EventHandler<PreviewSettingsChangedEventArgs>? PreviewChanged;
    public event EventHandler<TaskSettingsChangedEventArgs>? TasksChanged;
    public event EventHandler<UpdateSettingsChangedEventArgs>? UpdatesChanged;
    public event EventHandler<AudioAnalysisPreferencesChangedEventArgs>? AudioAnalysisChanged;
    public event EventHandler<AudioAnalysisRebuildRequestedEventArgs>? AudioAnalysisRebuildRequested;
    public event EventHandler<TimingPostProcessorPreferencesChangedEventArgs>? TimingPreferencesChanged;
    public event EventHandler<TimingPostProcessorAssociationEventArgs>? TimingAssociateRequested;
    public event EventHandler<TimingPostProcessorAssociationEventArgs>? TimingUnlinkRequested;
    public event EventHandler<SettingsStyleEventArgs>? UpsertStyleRequested;
    public event EventHandler<SettingsStyleDeleteEventArgs>? DeleteStyleRequested;
    public event EventHandler? CaptureStyleRequested;
    public event EventHandler<SettingsStyleEventArgs>? ApplyStyleRequested;
    public event EventHandler? ImportStylesRequested;
    public event EventHandler<SettingsStylesExportEventArgs>? ExportStylesRequested;
    public event EventHandler<SettingsEffectEventArgs>? UpsertEffectRequested;
    public event EventHandler<SettingsEffectDeleteEventArgs>? DeleteEffectRequested;
    public event EventHandler? ImportEffectRequested;
    public event EventHandler<SettingsEffectsExportEventArgs>? ExportEffectRequested;
    public event EventHandler<EffectScriptValidationFailedEventArgs>? EffectValidationFailed;
    public SettingsWindowViewModel ViewModel { get; }
    public WindowTitleBar TitleBar { get; }
    public SettingsPage CurrentPage => ViewModel.CurrentPage;
    public bool IsShortcutCaptureActive => ViewModel.Shortcuts.IsCaptureActive;

    /// <summary>选择页面，所有草稿保持在页面模型中。</summary>
    public void SelectPage(SettingsPage page)
    {
        if (!Enum.IsDefined(page))
        {
            throw new ArgumentOutOfRangeException(nameof(page));
        }

        ViewModel.PageIndex = (int)page;
    }

    /// <summary>更新已经持久化的外观；快捷键编辑草稿保持独立。</summary>
    public void UpdatePreferences(WorkbenchPreferences value)
    {
        ArgumentNullException.ThrowIfNull(value);
        value.Validate();
        RequestedThemeVariant = value.Theme switch
        {
            WorkbenchTheme.LIGHT => ThemeVariant.Light,
            WorkbenchTheme.DARK => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
        ViewModel.Appearance.UpdatePreferences(value);
        ViewModel.Colors.UpdatePreferences(value);
        ViewModel.Media.UpdatePreferences(value);
        ViewModel.Projects.UpdatePreferences(value.Projects);
        ViewModel.Preview.UpdatePreferences(value);
        ViewModel.Tasks.UpdatePreferences(value);
        ViewModel.Updates.UpdatePreferences(value);
        ViewModel.AudioAnalysis.UpdatePreferences(value);
        ViewModel.TimingPostProcessor.UpdatePreferences(value);
        RefreshLanguage();
    }

    /// <summary>即时刷新各页面的语言，保留未确认输入。</summary>
    public void RefreshLanguage()
    {
        ViewModel.RefreshLanguage();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RefreshLanguage();
    }

    internal void CloseImmediately()
    {
        allowClose = true;
        Close();
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (allowClose || (ViewModel.Styles.SaveDraftAsync is null && ViewModel.Effects.SaveDraftAsync is null &&
                          ViewModel.ExportPresets.SaveDraftAsync is null && ViewModel.ColorTags.SaveDraftAsync is null))
        {
            return;
        }
        if (!ViewModel.HasUnsavedTemplates && ViewModel.NavigationCompletion.IsCompleted &&
            ViewModel.Styles.SelectionCompletion.IsCompleted && ViewModel.Effects.SelectionCompletion.IsCompleted &&
            ViewModel.ExportPresets.SelectionCompletion.IsCompleted && ViewModel.ColorTags.Completion.IsCompleted)
        {
            return;
        }
        e.Cancel = true;
        if (!preparingClose)
        {
            CloseCompletion = PrepareCloseAsync(e.CloseReason == WindowCloseReason.OwnerWindowClosing ? Owner as Window : null);
        }
    }

    private async Task PrepareCloseAsync(Window? closingOwner)
    {
        preparingClose = true;
        try
        {
            await ViewModel.NavigationCompletion;
            await ViewModel.Styles.SelectionCompletion;
            await ViewModel.Effects.SelectionCompletion;
            await ViewModel.ExportPresets.SelectionCompletion;
            await ViewModel.ColorTags.Completion;
            if (allowClose || await ViewModel.Styles.PrepareToLeaveAsync() && await ViewModel.Effects.PrepareToLeaveAsync() &&
                await ViewModel.ExportPresets.PrepareToLeaveAsync() && await ViewModel.ColorTags.PrepareToLeaveAsync())
            {
                allowClose = true;
                Close();
                if (closingOwner is not null)
                {
                    Dispatcher.UIThread.Post(closingOwner.Close);
                }
            }
        }
        finally
        {
            preparingClose = false;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        this.FindControl<Styles.StyleSettingsView>("StylesView")!.Dispose();
        Localization.LanguageChanged -= OnLanguageChanged;
        ViewModel.Appearance.Changed -= OnAppearanceChanged;
        ViewModel.Colors.Changed -= OnColorsChanged;
        ViewModel.Shortcuts.Changed -= OnShortcutsChanged;
        ViewModel.Media.DecodeModeChanged -= OnPreviewDecodeModeChanged;
        ViewModel.Projects.Changed -= OnProjectsChanged;
        ViewModel.Preview.Changed -= OnPreviewChanged;
        ViewModel.Tasks.Changed -= OnTasksChanged;
        ViewModel.Updates.Changed -= OnUpdatesChanged;
        ViewModel.AudioAnalysis.Changed -= OnAudioAnalysisChanged;
        ViewModel.AudioAnalysis.RebuildRequested -= OnAudioAnalysisRebuildRequested;
        ViewModel.TimingPostProcessor.Changed -= OnTimingPreferencesChanged;
        ViewModel.TimingPostProcessor.AssociateRequested -= OnTimingAssociateRequested;
        ViewModel.TimingPostProcessor.UnlinkRequested -= OnTimingUnlinkRequested;
        ViewModel.Styles.UpsertRequested -= OnUpsertStyleRequested;
        ViewModel.Styles.DeleteRequested -= OnDeleteStyleRequested;
        ViewModel.Styles.ApplyRequested -= OnApplyStyleRequested;
        ViewModel.Styles.CaptureRequested -= OnCaptureStyleRequested;
        ViewModel.Styles.ImportRequested -= OnImportStylesRequested;
        ViewModel.Styles.ExportRequested -= OnExportStylesRequested;
        ViewModel.Effects.SaveRequested -= OnUpsertEffectRequested;
        ViewModel.Effects.DeleteRequested -= OnDeleteEffectRequested;
        ViewModel.Effects.ImportRequested -= OnImportEffectRequested;
        ViewModel.Effects.ExportRequested -= OnExportEffectRequested;
        ViewModel.Effects.ValidationFailed -= OnEffectValidationFailed;
        Deactivated -= OnDeactivated;
        Closed -= OnClosed;
        Closing -= OnClosing;
        ViewModel.Shortcuts.CancelCapture();
        ViewModel.ColorTags.Dispose();
    }

    private void OnAppearanceChanged(object? sender, SettingsAppearanceChangedEventArgs e)
    {
        AppearanceChanged?.Invoke(this, e);
    }

    private void OnColorsChanged(object? sender, SettingsColorsChangedEventArgs e)
    {
        ColorsChanged?.Invoke(this, e);
    }

    private void OnShortcutsChanged(object? sender, SettingsShortcutsChangedEventArgs e)
    {
        ShortcutsChanged?.Invoke(this, e);
    }

    private void OnPreviewDecodeModeChanged(object? sender, SettingsPreviewDecodeModeChangedEventArgs e)
    {
        PreviewDecodeModeChanged?.Invoke(this, e);
    }

    private void OnProjectsChanged(object? sender, ProjectPreferencesChangedEventArgs e)
    {
        ProjectsChanged?.Invoke(this, e);
    }

    private void OnPreviewChanged(object? sender, PreviewSettingsChangedEventArgs e)
    {
        PreviewChanged?.Invoke(this, e);
    }

    private void OnTasksChanged(object? sender, TaskSettingsChangedEventArgs e)
    {
        TasksChanged?.Invoke(this, e);
    }

    private void OnUpdatesChanged(object? sender, UpdateSettingsChangedEventArgs e)
    {
        UpdatesChanged?.Invoke(this, e);
    }

    private void OnAudioAnalysisChanged(object? sender, AudioAnalysisPreferencesChangedEventArgs e)
    {
        AudioAnalysisChanged?.Invoke(this, e);
    }

    private void OnAudioAnalysisRebuildRequested(object? sender, AudioAnalysisRebuildRequestedEventArgs e)
    {
        AudioAnalysisRebuildRequested?.Invoke(this, e);
    }

    private void OnTimingPreferencesChanged(object? sender, TimingPostProcessorPreferencesChangedEventArgs e)
    {
        TimingPreferencesChanged?.Invoke(this, e);
    }

    private void OnTimingAssociateRequested(object? sender, TimingPostProcessorAssociationEventArgs e)
    {
        TimingAssociateRequested?.Invoke(this, e);
    }

    private void OnTimingUnlinkRequested(object? sender, TimingPostProcessorAssociationEventArgs e)
    {
        TimingUnlinkRequested?.Invoke(this, e);
    }

    private void OnUpsertStyleRequested(object? sender, SettingsStyleEventArgs e)
    {
        UpsertStyleRequested?.Invoke(this, e);
    }

    private void OnDeleteStyleRequested(object? sender, SettingsStyleDeleteEventArgs e)
    {
        DeleteStyleRequested?.Invoke(this, e);
    }

    private void OnApplyStyleRequested(object? sender, SettingsStyleEventArgs e)
    {
        ApplyStyleRequested?.Invoke(this, e);
    }

    private void OnCaptureStyleRequested(object? sender, EventArgs e)
    {
        CaptureStyleRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnImportStylesRequested(object? sender, EventArgs e)
    {
        ImportStylesRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnExportStylesRequested(object? sender, SettingsStylesExportEventArgs e)
    {
        ExportStylesRequested?.Invoke(this, e);
    }

    private void OnUpsertEffectRequested(object? sender, SettingsEffectEventArgs e)
    {
        UpsertEffectRequested?.Invoke(this, e);
    }

    private void OnDeleteEffectRequested(object? sender, SettingsEffectDeleteEventArgs e)
    {
        DeleteEffectRequested?.Invoke(this, e);
    }

    private void OnImportEffectRequested(object? sender, EventArgs e)
    {
        ImportEffectRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnExportEffectRequested(object? sender, SettingsEffectsExportEventArgs e)
    {
        ExportEffectRequested?.Invoke(this, e);
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        ViewModel.Shortcuts.CancelCapture();
    }

    private void OnEffectValidationFailed(object? sender, EffectScriptValidationFailedEventArgs e)
    {
        EffectValidationFailed?.Invoke(this, e);
    }

    /// <summary>同步已持久化快捷键。</summary>
    public void UpdateShortcuts(IEnumerable<ShortcutBinding> bindings)
    {
        ViewModel.Shortcuts.UpdateBindings(bindings);
    }

    /// <summary>同步已持久化样式库和选择。</summary>
    public void UpdateStyles(IEnumerable<SubtitleStylePreset> presets, Guid? selectedId = null)
    {
        ViewModel.Styles.UpdateStyles(presets, selectedId);
    }

    /// <summary>同步个人脚本库；内置脚本始终只读，其他未保存草稿继续保留。</summary>
    public void UpdateEffects(IEnumerable<EffectScriptPreset> presets, Guid? selectedId = null)
    {
        ViewModel.Effects.UpdateEffects(presets, selectedId);
    }

    /// <summary>脚本存储或工程工作流运行期间禁止重复提交。</summary>
    public void SetEffectOperationBusy(bool busy)
    {
        ViewModel.Effects.IsBusy = busy;
    }

    /// <summary>为模板位置编辑注入真实字体测量，不将渲染资源交给设置页面。</summary>
    public void SetSubtitlePositionMeasurement(Func<SubtitleStylePreset, SubtitlePositionMeasurement> measure)
    {
        ViewModel.Styles.SetPositionMeasurement(measure);
    }

    /// <summary>工程流程执行期间禁止继续改变样式草稿。</summary>
    public void SetStyleOperationBusy(bool busy)
    {
        ViewModel.Styles.IsBusy = busy;
    }

    /// <summary>同步字幕选择允许的样式操作。</summary>
    public void UpdateSelectionAvailability(bool available)
    {
        ViewModel.Styles.HasSelectedSubtitle = available;
    }

    /// <summary>显示会话存储或工程流程错误。</summary>
    public void ShowError(string? message)
    {
        ViewModel.ShowError(message);
    }
}
