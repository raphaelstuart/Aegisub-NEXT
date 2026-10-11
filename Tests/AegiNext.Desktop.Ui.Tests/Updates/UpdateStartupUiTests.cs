using AegiNext.Application;
using AegiNext.Desktop.Editing;
using AegiNext.Desktop.I18n;
using AegiNext.Desktop.Settings;
using AegiNext.Desktop.Startup;
using AegiNext.Desktop.Updates;
using AegiNext.Desktop.Views;
using AegiNext.Rendering.Fonts;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;

namespace AegiNext.Desktop.Ui.Tests.Updates;

/// <summary>验证启动检查次数、欢迎页输入及共享工作台生命周期。</summary>
public sealed class UpdateStartupUiTests
{
    /// <summary>自动检查等待初始化完成，再使用已加载的发布渠道。</summary>
    [AvaloniaFact]
    public async Task AutomaticCheckWaitsForInitializationAndUsesTheSelectedChannel()
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
            await store.SaveAsync(new() { AutoCheckUpdates = true, UpdateChannel = UpdateChannel.STABLE });
        }
        await using var application = new DesktopApplicationContext(new(environment.DirectoryPath),
            fontSelectionService: fonts, updateReleaseSource: source);
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
            Assert.Empty(coordinator.WelcomeWindow.OwnedWindows);
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
            coordinator.WelcomeWindow.Close();
            await coordinator.Completion;
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

    /// <summary>每次应用启动至多自动查询一次，欢迎窗口不挂载任何窗口菜单。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupChecksAtMostOnceWithoutAddingMenusToTheWelcomeWindow(bool automatic)
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
        Assert.Null(NativeMenu.GetMenu(coordinator.WelcomeWindow));
        Assert.Null(coordinator.WelcomeWindow.TitleBar.MenuContent);

        application.UpdatePreferences(current => current with { WindowMenuOnMac = true });
        Dispatcher.UIThread.RunJobs();
        Assert.Null(NativeMenu.GetMenu(coordinator.WelcomeWindow));
        Assert.Null(coordinator.WelcomeWindow.TitleBar.MenuContent);
    }

    /// <summary>更新请求等待及返回结果时，欢迎页、模态设置和新建工程窗口仍处理鼠标键盘输入。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingAutomaticCheckDoesNotBlockWelcomeSettingsOrProjectDialogInput(bool hasUpdate)
    {
        using var environment = new UiTestEnvironment();
        var result = new TaskCompletionSource<UpdateRelease?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new UpdateUiReleaseSource((_, token) => result.Task.WaitAsync(token));
        await using var application = new DesktopApplicationContext(new(environment.DirectoryPath),
            new() { AutoCheckUpdates = true, WindowMenuOnMac = true }, updateReleaseSource: source);
        await application.Initialization;
        var shutdownCount = 0;
        await using var coordinator = new DesktopStartupCoordinator(_ => { }, () => shutdownCount++, application);
        coordinator.Start();
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var welcome = coordinator.WelcomeWindow;

        var search = UiTestActions.Find<TextBox>(welcome, "ProjectSearchInput");
        var point = search.TranslatePoint(new(search.Bounds.Width / 2, search.Bounds.Height / 2), welcome)!.Value;
        welcome.MouseDown(point, MouseButton.Left);
        welcome.MouseUp(point, MouseButton.Left);
        welcome.KeyTextInput("startup input fixture");
        Assert.Equal("startup input fixture", welcome.ViewModel.SearchText);

        UiTestActions.Click(welcome, "WelcomeSettingsButton");
        var settings = Assert.Single(welcome.OwnedWindows.OfType<SettingsWindow>());
        UiTestActions.SelectSettingsPage(settings, SettingsPage.UPDATES);
        Assert.True(UiTestActions.Find<CheckBox>(settings, "AutoCheckUpdatesInput").IsChecked);
        settings.Close();
        await welcome.ViewModel.SettingsCommand.ExecutionTask!;

        UiTestActions.Click(welcome, "WelcomeNewProjectButton");
        var create = Assert.Single(welcome.OwnedWindows.OfType<NewProjectDialog>());
        Assert.True(create.IsVisible);
        UiTestActions.Click(create, "CancelButton");
        await welcome.ViewModel.NewProjectCommand.ExecutionTask!;
        Assert.False(welcome.ViewModel.IsBusy);
        Assert.False(coordinator.AutomaticUpdateCheck.IsCompleted);

        UiTestActions.Click(welcome, "WelcomeSettingsButton");
        settings = Assert.Single(welcome.OwnedWindows.OfType<SettingsWindow>());
        result.TrySetResult(hasUpdate ? CreateRelease() : null);
        await coordinator.AutomaticUpdateCheck;
        Dispatcher.UIThread.RunJobs();
        UiTestActions.SelectSettingsPage(settings, SettingsPage.UPDATES);
        Assert.True(UiTestActions.Find<CheckBox>(settings, "AutoCheckUpdatesInput").IsChecked);
        settings.Close();
        await welcome.ViewModel.SettingsCommand.ExecutionTask!;
        if (hasUpdate)
        {
            var update = Assert.Single(welcome.OwnedWindows.OfType<UpdateCheckWindow>());
            UiTestActions.Click(update, "UpdateCloseButton");
            Assert.False(update.IsVisible);
        }

        UiTestActions.Click(welcome, "WelcomeNewProjectButton");
        create = Assert.Single(welcome.OwnedWindows.OfType<NewProjectDialog>());
        UiTestActions.Click(create, "CancelButton");
        await welcome.ViewModel.NewProjectCommand.ExecutionTask!;
        Assert.Equal(1, source.CallCount);
        welcome.Close();
        await coordinator.Completion;
        Assert.False(welcome.IsVisible);
        Assert.Equal(1, shutdownCount);
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
        var main = Assert.IsType<MainWindow>(coordinator.MainWindow);
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

    private static IAsyncRelayCommand GetHelpUpdateCommand(MainWindow owner)
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
