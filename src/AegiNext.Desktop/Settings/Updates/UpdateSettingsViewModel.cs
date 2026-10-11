using AegiNext.Desktop.I18n;
using AegiNext.Desktop.Updates;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AegiNext.Desktop.Settings.Updates;

/// <summary>呈现个人更新偏好并发布用户修改，不执行网络检查。</summary>
public sealed class UpdateSettingsViewModel : ObservableObject
{
    private readonly UpdateChannelChoice[] channels =
    [
        new(UpdateChannel.STABLE), new(UpdateChannel.INCLUDE_PRERELEASE)
    ];

    private bool autoCheckUpdates;
    private UpdateChannel selectedChannel;
    private bool updating;

    /// <summary>从已加载的偏好建立自动检查开关与发布渠道选项。</summary>
    public UpdateSettingsViewModel(WorkbenchPreferences preferences)
    {
        UpdatePreferences(preferences);
    }

    public event EventHandler<UpdateSettingsChangedEventArgs>? Changed;
    public IReadOnlyList<UpdateChannelChoice> Channels => channels;
    public string CurrentVersion { get; } = ApplicationVersion.Current;

    public bool AutoCheckUpdates
    {
        get => autoCheckUpdates;
        set
        {
            if (SetProperty(ref autoCheckUpdates, value))
            {
                NotifyChanged();
            }
        }
    }

    public UpdateChannelChoice? SelectedChannel
    {
        get => channels.FirstOrDefault(choice => choice.Channel == selectedChannel);
        set
        {
            if (value is null || !Enum.IsDefined(value.Channel) || value.Channel == selectedChannel)
            {
                return;
            }

            selectedChannel = value.Channel;
            OnPropertyChanged();
            NotifyChanged();
        }
    }

    /// <summary>回填共享偏好，保持渠道选项身份且不触发保存。</summary>
    public void UpdatePreferences(WorkbenchPreferences value)
    {
        ArgumentNullException.ThrowIfNull(value);
        value.Validate();
        updating = true;
        try
        {
            autoCheckUpdates = value.AutoCheckUpdates;
            selectedChannel = value.UpdateChannel;
            OnPropertyChanged(nameof(AutoCheckUpdates));
            RefreshLanguage();
        }
        finally
        {
            updating = false;
        }
    }

    /// <summary>更新渠道显示文本，保留选中渠道且不发布用户修改。</summary>
    public void RefreshLanguage()
    {
        var previousUpdating = updating;
        var previousChannel = selectedChannel;
        updating = true;
        try
        {
            channels[0].UpdateLabel(Localization.Get("Settings.UpdateChannelStable"));
            channels[1].UpdateLabel(Localization.Get("Settings.UpdateChannelIncludePrerelease"));
            OnPropertyChanged(nameof(Channels));
            selectedChannel = previousChannel;
            OnPropertyChanged(nameof(SelectedChannel));
        }
        finally
        {
            updating = previousUpdating;
        }
    }

    private void NotifyChanged()
    {
        if (!updating)
        {
            Changed?.Invoke(this, new(autoCheckUpdates, selectedChannel));
        }
    }
}
