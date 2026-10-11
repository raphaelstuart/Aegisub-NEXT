using AegiNext.Desktop.Updates;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AegiNext.Desktop.Settings.Updates;

/// <summary>保留更新渠道的稳定身份与当前语言的显示名称。</summary>
public sealed class UpdateChannelChoice(UpdateChannel channel) : ObservableObject
{
    private string label = string.Empty;

    public UpdateChannel Channel { get; } = channel;
    public string Label => label;

    internal void UpdateLabel(string value)
    {
        SetProperty(ref label, value, nameof(Label));
    }
}
