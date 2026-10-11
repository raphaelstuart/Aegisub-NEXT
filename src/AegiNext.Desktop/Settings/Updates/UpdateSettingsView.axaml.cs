using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace AegiNext.Desktop.Settings.Updates;

/// <summary>使用编译绑定呈现自动更新检查与渠道设置。</summary>
public sealed partial class UpdateSettingsView : UserControl
{
    /// <summary>隔离父级上下文并加载更新设置页面。</summary>
    public UpdateSettingsView()
    {
        DataContext = null;
        AvaloniaXamlLoader.Load(this);
    }
}
