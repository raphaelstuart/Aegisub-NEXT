using AegiNext.Desktop.Updates;

namespace AegiNext.Desktop.Settings.Updates;

/// <summary>携带已确认的自动检查设置与发布渠道。</summary>
public sealed class UpdateSettingsChangedEventArgs(bool autoCheckUpdates, UpdateChannel updateChannel) : EventArgs
{
    public bool AutoCheckUpdates { get; } = autoCheckUpdates;
    public UpdateChannel UpdateChannel { get; } = updateChannel;
}
