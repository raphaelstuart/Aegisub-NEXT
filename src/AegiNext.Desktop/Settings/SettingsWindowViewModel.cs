using System.ComponentModel;
using AegiNext.Desktop.I18n;
using AegiNext.Desktop.Settings.Appearance;
using AegiNext.Desktop.Settings.Colors;
using AegiNext.Desktop.Settings.ColorTags;
using AegiNext.Desktop.Settings.Effects;
using AegiNext.Desktop.Settings.Export;
using AegiNext.Desktop.Settings.Shortcuts;
using AegiNext.Desktop.Settings.Styles;
using AegiNext.Desktop.Settings.Media;
using AegiNext.Desktop.Settings.Projects;
using AegiNext.Desktop.Settings.Preview;
using AegiNext.Desktop.Settings.TimingPostProcessor;
using AegiNext.Desktop.Settings.Transfer;
using AegiNext.Desktop.Settings.Tasks;
using AegiNext.Desktop.Settings.AudioAnalysis;
using AegiNext.Desktop.Settings.Updates;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AegiNext.Desktop.Settings;

/// <summary>设置导航和独立页面的组合模型，不拥有工程或控件。</summary>
public sealed class SettingsWindowViewModel : ObservableObject
{
    private int pageIndex;
    private bool navigating;
    internal Task NavigationCompletion { get; private set; } = Task.CompletedTask;
    public bool IsNavigationAvailable => !navigating;
    public bool HasUnsavedTemplates => Styles.IsDirty || Effects.IsDirty || ExportPresets.IsDirty || ColorTags.IsDirty;
    private string? externalError;
    private string title = Localization.Get("Settings.Settings");

    /// <summary>使用已加载偏好构造页面模型。</summary>
    public SettingsWindowViewModel(WorkbenchPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        preferences.Validate();
        Appearance = new(preferences);
        Colors = new(preferences);
        ColorTags = new();
        Shortcuts = new(preferences.ShortcutBindings);
        Styles = new();
        Effects = new();
        ExportPresets = new();
        Transfer = new();
        Media = new(preferences);
        Projects = new(preferences.Projects);
        Preview = new(preferences);
        TimingPostProcessor = new(preferences);
        Tasks = new(preferences);
        AudioAnalysis = new(preferences);
        Updates = new(preferences);
        Shortcuts.PropertyChanged += PageModelChanged;
        Styles.PropertyChanged += PageModelChanged;
        Effects.PropertyChanged += PageModelChanged;
        ExportPresets.PropertyChanged += PageModelChanged;
        Transfer.PropertyChanged += PageModelChanged;
        Projects.PropertyChanged += PageModelChanged;
        Preview.PropertyChanged += PageModelChanged;
        TimingPostProcessor.PropertyChanged += PageModelChanged;
        Tasks.PropertyChanged += PageModelChanged;
        AudioAnalysis.PropertyChanged += PageModelChanged;
        ColorTags.PropertyChanged += PageModelChanged;
    }

    public AppearanceSettingsViewModel Appearance { get; }
    public ColorsSettingsViewModel Colors { get; }
    public SubtitleColorTagsSettingsViewModel ColorTags { get; }
    public ShortcutSettingsViewModel Shortcuts { get; }
    public StyleSettingsViewModel Styles { get; }
    public EffectSettingsViewModel Effects { get; }
    public ExportSettingsViewModel ExportPresets { get; }
    public UserSettingsTransferViewModel Transfer { get; }
    public MediaSettingsViewModel Media { get; }
    public ProjectSettingsViewModel Projects { get; }
    public PreviewSettingsViewModel Preview { get; }
    public TimingPostProcessorSettingsViewModel TimingPostProcessor { get; }
    public TaskSettingsViewModel Tasks { get; }
    public AudioAnalysisSettingsViewModel AudioAnalysis { get; }
    public UpdateSettingsViewModel Updates { get; }
    public string Title => title;

    public SettingsPage CurrentPage
    {
        get => (SettingsPage)PageIndex;
        set => PageIndex = (int)value;
    }

    public bool IsAppearanceVisible => CurrentPage == SettingsPage.APPEARANCE;
    public bool IsShortcutsVisible => CurrentPage == SettingsPage.SHORTCUTS;
    public bool IsStylesVisible => CurrentPage == SettingsPage.STYLES;
    public bool IsEffectsVisible => CurrentPage == SettingsPage.EFFECTS;
    public bool IsColorsVisible => CurrentPage == SettingsPage.COLORS;
    public bool IsColorTagsVisible => CurrentPage == SettingsPage.SUBTITLE_COLOR_TAGS;
    public bool IsMediaVisible => CurrentPage == SettingsPage.MEDIA;
    public bool IsProjectsVisible => CurrentPage == SettingsPage.PROJECTS;
    public bool IsPreviewVisible => CurrentPage == SettingsPage.PREVIEW;
    public bool IsTimingPostProcessorVisible => CurrentPage == SettingsPage.TIMING_POST_PROCESSOR;
    public bool IsExportPresetsVisible => CurrentPage == SettingsPage.EXPORT_PRESETS;
    public bool IsTransferVisible => CurrentPage == SettingsPage.TRANSFER;
    public bool IsTasksVisible => CurrentPage == SettingsPage.TASKS;
    public bool IsAudioAnalysisVisible => CurrentPage == SettingsPage.AUDIO_ANALYSIS;
    public bool IsUpdatesVisible => CurrentPage == SettingsPage.UPDATES;

    public string PageTitle => Localization.Get("Settings." + (CurrentPage switch
    {
        SettingsPage.SHORTCUTS => "Shortcuts",
        SettingsPage.STYLES => "Styles",
        SettingsPage.EFFECTS => "Effects",
        SettingsPage.COLORS => "Colors",
        SettingsPage.SUBTITLE_COLOR_TAGS => "SubtitleColorTags",
        SettingsPage.MEDIA => "Media",
        SettingsPage.PROJECTS => "Projects",
        SettingsPage.PREVIEW => "Preview",
        SettingsPage.TIMING_POST_PROCESSOR => "TimingPostProcessor",
        SettingsPage.EXPORT_PRESETS => "ExportPresets",
        SettingsPage.TRANSFER => "Transfer",
        SettingsPage.TASKS => "Tasks",
        SettingsPage.AUDIO_ANALYSIS => "AudioAnalysis",
        SettingsPage.UPDATES => "Updates",
        _ => "Appearance"
    }));

    public string? Error => externalError ?? (CurrentPage switch
    {
        SettingsPage.SHORTCUTS => Shortcuts.Error,
        SettingsPage.STYLES => Styles.Error,
        SettingsPage.PROJECTS => Projects.Error,
        SettingsPage.PREVIEW => Preview.Error,
        SettingsPage.TIMING_POST_PROCESSOR => TimingPostProcessor.Error,
        SettingsPage.EXPORT_PRESETS => ExportPresets.Error,
        SettingsPage.TRANSFER => Transfer.Error,
        SettingsPage.TASKS => Tasks.Error,
        SettingsPage.AUDIO_ANALYSIS => AudioAnalysis.Error,
        SettingsPage.SUBTITLE_COLOR_TAGS => ColorTags.Error,
        _ => null
    });

    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    public int PageIndex
    {
        get => pageIndex;
        set
        {
            if (!navigating)
            {
                NavigationCompletion = SelectPageAsync((SettingsPage)value);
            }
        }
    }

    /// <summary>先处理当前页未保存的修改，再改变设置导航。</summary>
    public async Task<bool> SelectPageAsync(SettingsPage page)
    {
        if (!Enum.IsDefined(page) || navigating)
        {
            OnPropertyChanged(nameof(PageIndex));
            OnPropertyChanged(nameof(CurrentPage));
            return false;
        }

        if (CurrentPage == page)
        {
            return true;
        }

        navigating = true;
        OnPropertyChanged(nameof(IsNavigationAvailable));
        try
        {
            if (CurrentPage == SettingsPage.STYLES)
            {
                await Styles.SelectionCompletion;
            }
            else if (CurrentPage == SettingsPage.EFFECTS)
            {
                await Effects.SelectionCompletion;
            }
            else if (CurrentPage == SettingsPage.EXPORT_PRESETS)
            {
                await ExportPresets.SelectionCompletion;
            }
            else if (CurrentPage == SettingsPage.SUBTITLE_COLOR_TAGS)
            {
                await ColorTags.Completion;
            }

            var accepted = CurrentPage switch
            {
                SettingsPage.STYLES when Styles.SaveDraftAsync is not null => await Styles.PrepareToLeaveAsync(),
                SettingsPage.EFFECTS when Effects.SaveDraftAsync is not null => await Effects.PrepareToLeaveAsync(),
                SettingsPage.EXPORT_PRESETS when ExportPresets.SaveDraftAsync is not null => await ExportPresets
                    .PrepareToLeaveAsync(),
                SettingsPage.SUBTITLE_COLOR_TAGS when ColorTags.SaveDraftAsync is not null => await ColorTags
                    .PrepareToLeaveAsync(),
                _ => true
            };
            if (!accepted)
            {
                OnPropertyChanged(nameof(PageIndex));
                OnPropertyChanged(nameof(CurrentPage));
                return false;
            }

            SetPageIndex((int)page);
            return true;
        }
        finally
        {
            navigating = false;
            OnPropertyChanged(nameof(IsNavigationAvailable));
        }
    }

    private void SetPageIndex(int value)
    {
        if (SetProperty(ref pageIndex, value, nameof(PageIndex)))
        {
            Shortcuts.IsRecording = false;
            OnPropertyChanged(nameof(CurrentPage));
            OnPropertyChanged(nameof(IsAppearanceVisible));
            OnPropertyChanged(nameof(IsShortcutsVisible));
            OnPropertyChanged(nameof(IsStylesVisible));
            OnPropertyChanged(nameof(IsEffectsVisible));
            OnPropertyChanged(nameof(IsColorsVisible));
            OnPropertyChanged(nameof(IsColorTagsVisible));
            OnPropertyChanged(nameof(IsMediaVisible));
            OnPropertyChanged(nameof(IsProjectsVisible));
            OnPropertyChanged(nameof(IsPreviewVisible));
            OnPropertyChanged(nameof(IsTimingPostProcessorVisible));
            OnPropertyChanged(nameof(IsExportPresetsVisible));
            OnPropertyChanged(nameof(IsTransferVisible));
            OnPropertyChanged(nameof(IsTasksVisible));
            OnPropertyChanged(nameof(IsAudioAnalysisVisible));
            OnPropertyChanged(nameof(IsUpdatesVisible));
            OnPropertyChanged(nameof(PageTitle));
            RefreshError();
        }
    }


    /// <summary>外部存储或工程工作流返回错误时显示其结果。</summary>
    public void ShowError(string? message)
    {
        externalError = message;
        RefreshError();
    }

    /// <summary>更新展示语言，保留所有页面草稿。</summary>
    public void RefreshLanguage()
    {
        Appearance.RefreshLanguage();
        Colors.RefreshLanguage();
        ColorTags.RefreshLanguage();
        Shortcuts.RefreshLanguage();
        Styles.RefreshLanguage();
        Effects.RefreshLanguage();
        ExportPresets.RefreshLanguage();
        Transfer.RefreshLanguage();
        Media.RefreshLanguage();
        Projects.RefreshLanguage();
        Preview.RefreshLanguage();
        TimingPostProcessor.RefreshLanguage();
        Tasks.RefreshLanguage();
        AudioAnalysis.RefreshLanguage();
        Updates.RefreshLanguage();
        title = Localization.Get("Settings.Settings");
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(PageTitle));
        RefreshError();
    }

    private void PageModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Error))
        {
            externalError = null;
            RefreshError();
        }
    }

    private void RefreshError()
    {
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(HasError));
    }
}
