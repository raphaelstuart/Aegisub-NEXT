using AegiNext.Application;
using AegiNext.Desktop.Editing;
using AegiNext.Desktop.I18n;
using AegiNext.Desktop.Settings;
using AegiNext.Desktop.Startup;
using AegiNext.Desktop.Updates;
using AegiNext.Rendering.Fonts;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;

namespace AegiNext.Desktop.Ui.Tests.Updates;

/// <summary>验证欢迎页更新入口、启动检查次数及共享工作台生命周期。</summary>
public sealed class UpdateStartupUiTests
{
    /// <summary>启动初期手动检查等待初始化完成，再使用已加载的发布渠道。</summary>
    [AvaloniaFact]
    public async Task EarlyManualCheckWaitsForInitializationAndUsesTheSelectedChannel()
    {
        using var environment = new UiTestEnvironment();
        var catalog = new TaskCompletionSource<SystemFontCatalog>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fonts = new SubtitleFontSelectionService(() => catalog.Task);
        var source = new UpdateUiReleaseSource((channel, _) =>
        {
            Assert.Equal(UpdateChannel.STABLE, channel);
            return Task.FromResult<UpdateRelease?>(null);
        });
        using (var store = new WorkbenchPreferencesStore(environment.DirectoryPath))
        {
            await store.SaveAsync(new() { AutoCheckUpdates = false, UpdateChannel = UpdateChannel.STABLE });
        }
        await using var application = new DesktopApplicationContext(new(environment.DirectoryPath),
            fontSelectionService: fonts, updateReleaseSource: source);
        await using var coordinator = new DesktopStartupCoordinator(_ => { }, () => { }, application);
        try
        {
            coordinator.Start();
            var check = GetHelpUpdateCommand(coordinator.WelcomeWindow).ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();

            Assert.True(coordinator.WelcomeWindow.IsVisible);
            Assert.False(application.Initialization.IsCompleted);
            Assert.Equal(0, source.CallCount);

            catalog.TrySetResult(SystemFontCatalog.Empty);
            await check;
            await coordinator.AutomaticUpdateCheck;
            Assert.Equal(1, source.CallCount);
            Assert.Single(coordinator.WelcomeWindow.OwnedWindows.OfType<UpdateCheckWindow>());
        }
        finally
        {
            catalog.TrySetResult(SystemFontCatalog.Empty);
            await coordinator.DisposeAsync();
        }
    }

    /// <summary>启动立即显示欢迎页，初始化完成之前不开始更新请求。</summary>
    [AvaloniaFact]
    public async Task WelcomeIsVisibleBeforeInitializationAndUpdateChecksWaitForIt()
    {
        using var environment = new UiTestEnvironment();
        var catalog = new TaskCompletionSource<SystemFontCatalog>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fonts = new SubtitleFontSelectionService(() => catalog.Task);
        var source = new UpdateUiReleaseSource((_, _) => Task.FromResult<UpdateRelease?>(null));
        await using var application = new DesktopApplicationContext(new(environment.DirectoryPath),
            new() { AutoCheckUpdates = true }, fonts, source);
        await using var coordinator = new DesktopStartupCoordinator(_ => { }, () => { }, application);
        try
        {
            coordinator.Start();
            Dispatcher.UIThread.RunJobs();

            Assert.True(coordinator.WelcomeWindow.IsVisible);
            Assert.False(application.Initialization.IsCompleted);
            Assert.Equal(0, source.CallCount);

            catalog.TrySetResult(SystemFontCatalog.Empty);
            await coordinator.AutomaticUpdateCheck;
            Assert.Equal(1, source.CallCount);
        }
        finally
        {
            catalog.TrySetResult(SystemFontCatalog.Empty);
            await coordinator.DisposeAsync();
        }
    }

    /// <summary>退出应用取消尚未完成的更新请求，延迟结果不会残留更新窗口。</summary>
    [AvaloniaFact]
    public async Task ExitingDuringAnAutomaticQueryCancelsItWithoutShowingAnUpdateWindow()
    {
        using var environment = new UiTestEnvironment();
        var result = new TaskCompletionSource<UpdateRelease?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new UpdateUiReleaseSource(async (_, token) =>
        {
            try
            {
                return await result.Task.WaitAsync(token);
            }
            catch (OperationCanceledException)
            {
                canceled.TrySetResult();
                throw;
            }
        });
        await using var application = new DesktopApplicationContext(new(environment.DirectoryPath),
            new() { AutoCheckUpdates = true }, updateReleaseSource: source);
        await application.Initialization;
        await using var coordinator = new DesktopStartupCoordinator(_ => { }, () => { }, application);
        try
        {
            coordinator.Start();
            await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await coordinator.DisposeAsync();
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            result.TrySetResult(CreateRelease());
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(1, source.CallCount);
            Assert.Empty(coordinator.WelcomeWindow.OwnedWindows);
            Assert.False(coordinator.WelcomeWindow.IsVisible);
            Assert.True(coordinator.AutomaticUpdateCheck.IsCompletedSuccessfully);
        }
        finally
        {
            result.TrySetCanceled();
            await coordinator.DisposeAsync();
        }
    }

    /// <summary>每次应用启动至多自动查询一次，关闭开关时帮助菜单仍可手动查询。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupChecksAtMostOnceAndTheWelcomeHelpMenuAlwaysAllowsManualChecks(bool automatic)
    {
        using var environment = new UiTestEnvironment();
        var source = new UpdateUiReleaseSource((_, _) => Task.FromResult<UpdateRelease?>(null));
        await using var application = new DesktopApplicationContext(new(environment.DirectoryPath),
            new() { AutoCheckUpdates = automatic }, updateReleaseSource: source);
        await application.Initialization;
        await using var coordinator = new DesktopStartupCoordinator(_ => { }, () => { }, application);

        coordinator.Start();
        coordinator.Start();
        await coordinator.AutomaticUpdateCheck;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(automatic ? 1 : 0, source.CallCount);
        Assert.Empty(coordinator.WelcomeWindow.OwnedWindows);
        var manual = GetHelpUpdateCommand(coordinator.WelcomeWindow);
        Assert.True(manual.CanExecute(null));

        await manual.ExecuteAsync(null);
        await WaitUntilAsync(() => coordinator.WelcomeWindow.OwnedWindows.OfType<UpdateCheckWindow>().Any());
        var window = Assert.Single(coordinator.WelcomeWindow.OwnedWindows.OfType<UpdateCheckWindow>());
        Assert.True(window.IsVisible);
        Assert.False(window.ViewModel.HasRelease);
        Assert.False(string.IsNullOrWhiteSpace(window.ViewModel.Message));
        Assert.Equal(automatic ? 2 : 1, source.CallCount);
        await coordinator.DisposeAsync();
        Assert.False(window.IsVisible);
    }

    /// <summary>欢迎页的非模态更新提示允许继续打开工程，工作台可以重新查看同一发布。</summary>
    [AvaloniaFact]
    public async Task WelcomeUpdateWindowDoesNotPreventCreatingAProjectAndWorkbenchCanReopenIt()
    {
        using var environment = new UiTestEnvironment();
        var release = CreateRelease();
        var source = new UpdateUiReleaseSource((_, _) => Task.FromResult<UpdateRelease?>(release));
        await using var application = new DesktopApplicationContext(new(environment.DirectoryPath),
            new()
            {
                AutoCheckUpdates = true,
                Projects = new() { WorkspaceRoot = environment.DirectoryPath }
            }, updateReleaseSource: source);
        await application.Initialization;
        var request = new ProjectCreationRequest("Update project fixture", environment.DirectoryPath);
        var dialogs = new StartupTestDialogService { NewProjectRequest = request };
        await using var coordinator = new DesktopStartupCoordinator(_ => { }, () => { }, application, dialogs: dialogs);

        coordinator.Start();
        await coordinator.AutomaticUpdateCheck;
        await WaitUntilAsync(() => coordinator.WelcomeWindow.OwnedWindows.OfType<UpdateCheckWindow>().Any());
        var welcomeUpdate = Assert.Single(coordinator.WelcomeWindow.OwnedWindows.OfType<UpdateCheckWindow>());
        Assert.True(welcomeUpdate.IsVisible);
        Assert.True(coordinator.WelcomeWindow.ViewModel.NewProjectCommand.CanExecute(null));

        await coordinator.WelcomeWindow.ViewModel.NewProjectCommand.ExecuteAsync(null);
        var main = Assert.IsType<AegiNext.Desktop.Views.MainWindow>(coordinator.MainWindow);
        Assert.True(main.IsVisible);
        Assert.False(coordinator.WelcomeWindow.IsVisible);
        Assert.False(welcomeUpdate.IsVisible);
        Assert.Equal(ProjectCreationService.GetProjectPath(request), main.Session.ProjectPath);
        Assert.Equal(1, dialogs.NewProjectCount);
        Assert.Equal(1, source.CallCount);

        var manual = GetHelpUpdateCommand(main);
        Assert.True(manual.CanExecute(null));
        await manual.ExecuteAsync(null);
        await WaitUntilAsync(() => main.OwnedWindows.OfType<UpdateCheckWindow>().Any());
        var workbenchUpdate = Assert.Single(main.OwnedWindows.OfType<UpdateCheckWindow>());
        Assert.True(workbenchUpdate.IsVisible);
        Assert.NotSame(welcomeUpdate, workbenchUpdate);
        Assert.Contains(workbenchUpdate, main.WindowRegistry.Windows);
        Assert.Equal(release.ReleaseNotesMarkdown, workbenchUpdate.ViewModel.ReleaseNotesMarkdown);
        Assert.Equal(1, source.CallCount);
        await coordinator.DisposeAsync();
        Assert.False(workbenchUpdate.IsVisible);
    }

    private static IAsyncRelayCommand GetHelpUpdateCommand(Window owner)
    {
        var root = Assert.IsType<NativeMenu>(NativeMenu.GetMenu(owner));
        var help = Assert.Single(root.Items.OfType<NativeMenuItem>(), item => item.Header == Localization.Get("Workbench.Help"));
        var update = Assert.Single(help.Menu!.Items.OfType<NativeMenuItem>(),
            item => item.Header == Localization.Get("Settings.CHECK_UPDATES"));
        return Assert.IsAssignableFrom<IAsyncRelayCommand>(update.Command);
    }

    private static UpdateRelease CreateRelease()
    {
        return new(new(2099, 1, 2, 3), "v2099.1.2.3", "Update fixture", "# Update fixture\n\nRelease notes.",
            new("https://github.com/Yohuke-no-Symphony/Aegisub-NEXT/releases/tag/v2099.1.2.3"));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Dispatcher.UIThread.RunJobs();
            Assert.True(DateTime.UtcNow < deadline, "更新提示未在所属窗口的 UI 调度完成后出现。");
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
        Dispatcher.UIThread.RunJobs();
    }
}
