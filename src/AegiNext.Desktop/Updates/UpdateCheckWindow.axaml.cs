using AegiNext.Desktop.Controls.Common;
using AegiNext.Desktop.Windowing;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Markdown.Avalonia;

namespace AegiNext.Desktop.Updates;

/// <summary>显示 GitHub 发布说明与更新检查结果的非模态窗口。</summary>
public sealed class UpdateCheckWindow : Window, IWindowTitleBarHost
{
    private IWindowChrome? chrome;

    /// <summary>创建不执行检查的 XAML 设计宿主。</summary>
    public UpdateCheckWindow() : this(new(new(UpdateCheckStatus.UP_TO_DATE, ApplicationVersion.Current),
        UpdateChannel.INCLUDE_PRERELEASE, () => Task.CompletedTask, _ => Task.FromResult(false)))
    {
    }

    internal UpdateCheckWindow(UpdateCheckViewModel viewModel)
    {
        ViewModel = viewModel;
        AvaloniaXamlLoader.Load(this);
        TitleBar = this.FindControl<WindowTitleBar>("UpdateTitleBar")!;
        this.FindControl<MarkdownScrollViewer>("ReleaseNotesViewer")!.Plugins = new()
        {
            HyperlinkCommand = viewModel.OpenLinkCommand
        };
        DataContext = viewModel;
        chrome = WindowChrome.Attach(this, TitleBar);
        Closed += OnClosed;
    }

    internal UpdateCheckViewModel ViewModel { get; }
    internal WindowTitleBar TitleBar { get; }
    WindowTitleBar IWindowTitleBarHost.TitleBar => TitleBar;

    void IWindowTitleBarHost.ReleaseStandaloneChrome() => ReleaseChrome();

    private void ReleaseChrome()
    {
        chrome?.Dispose();
        chrome = null;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void OnClosed(object? sender, EventArgs e)
    {
        Closed -= OnClosed;
        ReleaseChrome();
        ViewModel.Dispose();
    }
}
