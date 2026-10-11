using AegiNext.Desktop.Controls.Common;
using AegiNext.Desktop.Startup;
using AegiNext.Desktop.Windowing;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;

namespace AegiNext.Desktop.Views;

/// <summary>呈现最近项目与应用启动入口的独立欢迎窗口。</summary>
public sealed partial class WelcomeWindow : Window
{
    private readonly IWindowChrome chrome;
    private WelcomeViewModel? viewModel;

    /// <summary>构造不拥有应用资源的 XAML 设计宿主。</summary>
    public WelcomeWindow()
    {
        AvaloniaXamlLoader.Load(this);
        TitleBar = this.FindControl<WindowTitleBar>("WelcomeTitleBar")!;
        chrome = WindowChrome.Attach(this, TitleBar);
        Activated += OnActivated;
        Closed += OnClosed;
    }

    internal WelcomeWindow(WelcomeViewModel viewModel) : this()
    {
        this.viewModel = viewModel;
        DataContext = viewModel;
    }

    internal WelcomeViewModel ViewModel => viewModel ?? throw new InvalidOperationException("欢迎窗口尚未绑定启动模型。");
    internal WindowTitleBar TitleBar { get; }

    private void OnActivated(object? sender, EventArgs e) => viewModel?.RefreshAvailability();

    private void OnProjectTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is not Avalonia.Visual source ||
            source.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault()?.DataContext is not RecentProjectListItem item)
        {
            return;
        }

        ViewModel.SelectedProject = item;
        if (ViewModel.OpenSelectedProjectCommand.CanExecute(null))
        {
            ViewModel.OpenSelectedProjectCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnProjectKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None &&
            ViewModel.OpenSelectedProjectCommand.CanExecute(null))
        {
            ViewModel.OpenSelectedProjectCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnRemoveProject(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { CommandParameter: RecentProjectListItem item } &&
            ViewModel.RemoveProjectCommand.CanExecute(item))
        {
            ViewModel.RemoveProjectCommand.Execute(item);
        }
    }

    private void OnToggleProjectPin(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { CommandParameter: RecentProjectListItem item } &&
            ViewModel.ToggleProjectPinCommand.CanExecute(item))
        {
            ViewModel.ToggleProjectPinCommand.Execute(item);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Activated -= OnActivated;
        chrome.Dispose();
        viewModel?.Dispose();
    }
}
