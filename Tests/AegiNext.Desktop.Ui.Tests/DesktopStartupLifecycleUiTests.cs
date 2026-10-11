using AegiNext.Application;
using AegiNext.Desktop.Settings;
using AegiNext.Desktop.Shortcuts;
using AegiNext.Desktop.Startup;
using AegiNext.Desktop.Views;
using AegiNext.Desktop.Workspace;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;

namespace AegiNext.Desktop.Ui.Tests;

public sealed class DesktopStartupLifecycleUiTests
{
    [AvaloniaFact]
    public async Task StartAfterAsynchronousInitializationShowsWelcomeWithoutFrameworkAutoShow()
    {
        using var environment = new UiTestEnvironment(disableAutomaticUpdates: true);
        await using var application = new DesktopApplicationContext(new(environment.DirectoryPath));
        await application.Initialization;
        Window? active = null;
        await using var coordinator = new DesktopStartupCoordinator(window => active = window, () => { }, application);

        coordinator.Start();

        Assert.Same(coordinator.WelcomeWindow, active);
        Assert.True(coordinator.WelcomeWindow.IsVisible);
        Assert.Null(coordinator.MainWindow);
        await coordinator.DisposeAsync();
        Assert.False(coordinator.WelcomeWindow.IsVisible);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAtCommittedProjectAcceptsPanelCloseButRespectsApplicationShutdown(bool shutdown)
    {
        using var environment = new UiTestEnvironment(disableAutomaticUpdates: true);
        var request = new ProjectCreationRequest("Committed 中文", environment.DirectoryPath);
        var path = ProjectCreationService.GetProjectPath(request);
        DesktopStartupCoordinator? coordinator = null;
        WorkbenchSession? candidate = null;
        var shutdownCount = 0;
        var closedAtCommit = false;
        coordinator = new(_ => { }, () => shutdownCount++,
            sessionFactory: (service, application) =>
            {
                var session = new WorkbenchSession(service, applicationContext: application);
                candidate = session;
                session.Editor.Changed += (_, _) =>
                {
                    if (!closedAtCommit && session.ProjectPath == path && File.Exists(path))
                    {
                        closedAtCommit = true;
                        if (shutdown)
                        {
                            coordinator!.WelcomeWindow.Close();
                        }
                        else
                        {
                            Assert.Single(coordinator!.WelcomeWindow.OwnedWindows.OfType<NewProjectDialog>()).Close(false);
                        }
                    }
                };
                return session;
            });
        await using (coordinator)
        {
            coordinator.Start();
            coordinator.WelcomeWindow.Show();
            var operation = coordinator.WelcomeWindow.ViewModel.NewProjectCommand.ExecuteAsync(null);
            var dialog = Assert.Single(coordinator.WelcomeWindow.OwnedWindows.OfType<NewProjectDialog>());
            UiTestActions.SetText(UiTestActions.Find<TextBox>(dialog, "ProjectNameInput"), request.Name);
            UiTestActions.SetText(UiTestActions.Find<TextBox>(dialog, "ProjectLocationInput"), request.ParentDirectory);
            UiTestActions.Click(dialog, "CreateProjectButton");
            await operation;

            Assert.True(closedAtCommit);
            Assert.True(File.Exists(path));
            Assert.False(dialog.IsVisible);
            Assert.NotNull(candidate);
            if (shutdown)
            {
                await coordinator.Completion;
                Assert.Null(coordinator.MainWindow);
                Assert.True(candidate.IsClosing);
                Assert.Equal(1, shutdownCount);
            }
            else
            {
                Assert.Same(candidate, coordinator.MainWindow!.Session);
                Assert.True(coordinator.MainWindow.IsVisible);
                Assert.Equal(path, coordinator.MainWindow.Session.ProjectPath);
                Assert.Equal(0, shutdownCount);
            }
        }
    }

    [AvaloniaFact]
    public async Task NewProjectCreatesItsNamedDirectoryBeforeTransferringToOneWorkbench()
    {
        using var environment = new UiTestEnvironment(disableAutomaticUpdates: true);
        Window? active = null;
        var shutdownCount = 0;
        var request = new ProjectCreationRequest("New project", environment.DirectoryPath);
        var path = ProjectCreationService.GetProjectPath(request);
        var dialogs = new StartupTestDialogService { NewProjectRequest = request };
        await using var coordinator = new DesktopStartupCoordinator(window => active = window, () => shutdownCount++,
            dialogs: dialogs);
        await coordinator.ApplicationContext.Initialization;
        coordinator.Start();
        Assert.Same(coordinator.WelcomeWindow, active);
        Assert.Null(coordinator.MainWindow);
        coordinator.ApplicationContext.UpdatePreferences(value => value with
        {
            Projects = value.Projects with { WorkspaceRoot = environment.DirectoryPath }
        });
        coordinator.WelcomeWindow.Show();
        await coordinator.WelcomeWindow.ViewModel.NewProjectCommand.ExecuteAsync(null);
        var window = coordinator.MainWindow!;
        Assert.Same(window, active);
        Assert.True(window.IsVisible);
        Assert.False(coordinator.WelcomeWindow.IsVisible);
        Assert.Equal(path, window.Session.ProjectPath);
        Assert.True(File.Exists(path));
        Assert.Equal(window.Session.DocumentSnapshot.Id,
            (await ProjectStore.LoadAsync(path, TestContext.Current.CancellationToken)).Id);
        Assert.False(window.Session.Editor.HasUnsavedChanges);
        Assert.Equal(path, Assert.Single(coordinator.ApplicationContext.RecentProjects.Entries).Path);
        Assert.Equal(1, dialogs.NewProjectCount);
        Assert.Equal(0, dialogs.SaveCount);
        Assert.Equal(environment.DirectoryPath, dialogs.WorkspaceRoot);
        Assert.True(Directory.Exists(Path.Combine(environment.DirectoryPath, request.Name, "backup")));
        Assert.Equal(0, shutdownCount);
        await coordinator.DisposeAsync();
        Assert.False(window.IsVisible);
    }

    [AvaloniaFact]
    public async Task FailedOpenPreservesWelcomeAndSharedSettingsThenSuccessfulRetryTransfers()
    {
        using var environment = new UiTestEnvironment(disableAutomaticUpdates: true);
        var path = Path.Combine(environment.DirectoryPath, "中文 123.aeginext");
        await File.WriteAllTextAsync(path, "invalid project", TestContext.Current.CancellationToken);
        var dialogs = new StartupTestDialogService { OpenPath = path };
        var windows = new List<Window>();
        await using var coordinator = new DesktopStartupCoordinator(windows.Add, () => { }, dialogs: dialogs);
        await coordinator.ApplicationContext.Initialization;
        coordinator.Start();
        coordinator.WelcomeWindow.Show();
        coordinator.ApplicationContext.UpdatePreferences(value => value with { Theme = WorkbenchTheme.DARK });
        await coordinator.WelcomeWindow.ViewModel.OpenProjectCommand.ExecuteAsync(null);
        Assert.Null(coordinator.MainWindow);
        Assert.True(coordinator.WelcomeWindow.IsVisible);
        Assert.True(coordinator.WelcomeWindow.ViewModel.HasError);
        Assert.Empty(coordinator.ApplicationContext.RecentProjects.Entries);
        Assert.Equal(WorkbenchTheme.DARK, coordinator.ApplicationContext.Preferences.Theme);
        await ProjectStore.SaveAsync(new(), path, TestContext.Current.CancellationToken);
        await coordinator.WelcomeWindow.ViewModel.OpenProjectCommand.ExecuteAsync(null);
        Assert.Equal(2, windows.Count);
        Assert.True(coordinator.MainWindow!.IsVisible);
        Assert.Equal(path, coordinator.MainWindow.Session.ProjectPath);
        Assert.Equal(WorkbenchTheme.DARK, coordinator.MainWindow.Session.Preferences.Theme);
        Assert.Equal(path, Assert.Single(coordinator.ApplicationContext.RecentProjects.Entries).Path);
    }

    [AvaloniaFact]
    public async Task PickerCancellationAndRepeatedClicksDoNotCreateSessions()
    {
        using var environment = new UiTestEnvironment(disableAutomaticUpdates: true);
        var dialogs = new StartupTestDialogService
        {
            PendingSelection = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var sessionCount = 0;
        await using var coordinator = new DesktopStartupCoordinator(_ => { }, () => { },
            sessionFactory: (service, application) =>
            {
                sessionCount++;
                return new(service, applicationContext: application);
            }, dialogs: dialogs);
        coordinator.Start();
        coordinator.WelcomeWindow.Show();
        var command = coordinator.WelcomeWindow.ViewModel.OpenProjectCommand;
        var pending = command.ExecuteAsync(null);
        Assert.False(command.CanExecute(null));
        await coordinator.WelcomeWindow.ViewModel.NewProjectCommand.ExecuteAsync(null);
        Assert.Equal(1, dialogs.OpenCount);
        Assert.Equal(0, dialogs.SaveCount);
        dialogs.PendingSelection.SetResult(null);
        await pending;
        Assert.Equal(0, sessionCount);
        Assert.Null(coordinator.MainWindow);
        Assert.True(coordinator.WelcomeWindow.IsVisible);
        Assert.False(coordinator.WelcomeWindow.ViewModel.HasError);
    }

    [AvaloniaFact]
    public async Task ClosingDuringSelectionRejectsLateSuccessAndFlushesPreferences()
    {
        using var environment = new UiTestEnvironment(disableAutomaticUpdates: true);
        var path = Path.Combine(environment.DirectoryPath, "Late.aeginext");
        await ProjectStore.SaveAsync(new(), path, TestContext.Current.CancellationToken);
        var dialogs = new StartupTestDialogService
        {
            PendingSelection = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var shutdownCount = 0;
        var coordinator = new DesktopStartupCoordinator(_ => { }, () => shutdownCount++, dialogs: dialogs);
        try
        {
            coordinator.Start();
            coordinator.WelcomeWindow.Show();
            coordinator.ApplicationContext.UpdatePreferences(value => value with { Language = "zh-CN" });
            var open = coordinator.WelcomeWindow.ViewModel.OpenProjectCommand.ExecuteAsync(null);
            coordinator.WelcomeWindow.Close();
            coordinator.WelcomeWindow.Close();
            Assert.True(coordinator.WelcomeWindow.IsVisible);
            dialogs.PendingSelection.SetResult(path);
            await open;
            await coordinator.Completion;
            Dispatcher.UIThread.RunJobs();
            Assert.Null(coordinator.MainWindow);
            Assert.False(coordinator.WelcomeWindow.IsVisible);
            Assert.Equal(1, shutdownCount);
            using var store = new WorkbenchPreferencesStore(environment.DirectoryPath);
            Assert.Equal("zh-CN", store.Load().Language);
        }
        finally
        {
            await coordinator.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task FailedWindowPromotionReleasesTheCandidateAndAllowsRetry()
    {
        using var environment = new UiTestEnvironment(disableAutomaticUpdates: true);
        var rejectWorkbench = true;
        MainWindow? rejected = null;
        Window? active = null;
        var dialogs = new StartupTestDialogService
        {
            NewProjectRequest = new("Promotion", environment.DirectoryPath)
        };
        await using var coordinator = new DesktopStartupCoordinator(window =>
        {
            if (rejectWorkbench && window is MainWindow workbench)
            {
                rejected = workbench;
                throw new InvalidOperationException("Rejected window promotion.");
            }
            active = window;
        }, () => { }, dialogs: dialogs);
        coordinator.Start();
        coordinator.WelcomeWindow.Show();
        await coordinator.WelcomeWindow.ViewModel.NewProjectCommand.ExecuteAsync(null);
        Assert.NotNull(rejected);
        Assert.False(rejected.IsVisible);
        Assert.True(rejected.Session.IsClosing);
        Assert.Null(coordinator.MainWindow);
        Assert.Same(coordinator.WelcomeWindow, active);
        Assert.True(coordinator.WelcomeWindow.ViewModel.HasError);
        Assert.Contains(ProjectCreationService.GetProjectPath(dialogs.NewProjectRequest!),
            coordinator.WelcomeWindow.ViewModel.Error);
        rejectWorkbench = false;
        Assert.True(File.Exists(ProjectCreationService.GetProjectPath(dialogs.NewProjectRequest!)));
        dialogs.NewProjectRequest = new("Promotion retry", environment.DirectoryPath);
        await coordinator.WelcomeWindow.ViewModel.NewProjectCommand.ExecuteAsync(null);
        Assert.True(coordinator.MainWindow!.IsVisible);
        Assert.Same(coordinator.MainWindow, active);
    }

    [AvaloniaFact]
    public async Task CancelledNewProjectPanelDoesNotCreateASession()
    {
        using var environment = new UiTestEnvironment(disableAutomaticUpdates: true);
        var dialogs = new StartupTestDialogService
        {
            PendingNewProjectSelection = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var sessions = 0;
        await using var coordinator = new DesktopStartupCoordinator(_ => { }, () => { },
            sessionFactory: (service, application) =>
            {
                sessions++;
                return new(service, applicationContext: application);
            }, dialogs: dialogs);
        coordinator.Start();
        coordinator.WelcomeWindow.Show();
        var pending = coordinator.WelcomeWindow.ViewModel.NewProjectCommand.ExecuteAsync(null);
        try
        {
            Assert.Equal(1, dialogs.NewProjectCount);
            Assert.Equal(0, dialogs.SaveCount);
            Assert.Equal(0, sessions);
            Assert.Null(coordinator.MainWindow);
            Assert.False(coordinator.WelcomeWindow.ViewModel.OpenProjectCommand.CanExecute(null));
            dialogs.PendingNewProjectSelection.SetResult(null);
            await pending;

            Assert.Equal(0, sessions);
            Assert.Null(coordinator.MainWindow);
            Assert.True(coordinator.WelcomeWindow.IsVisible);
            Assert.False(coordinator.WelcomeWindow.ViewModel.IsBusy);
            Assert.False(coordinator.WelcomeWindow.ViewModel.HasError);
            Assert.Empty(coordinator.ApplicationContext.RecentProjects.Entries);
        }
        finally
        {
            dialogs.PendingNewProjectSelection.TrySetResult(null);
            await pending;
        }
    }

    [AvaloniaFact]
    public async Task FailedNewProjectCreateKeepsWelcomeAndDisposesTheCandidateBeforeRetry()
    {
        using var environment = new UiTestEnvironment(disableAutomaticUpdates: true);
        var blocked = Path.Combine(environment.DirectoryPath, "Blocked");
        Directory.CreateDirectory(blocked);
        var dialogs = new StartupTestDialogService { NewProjectRequest = new("Blocked", environment.DirectoryPath) };
        WorkbenchSession? failed = null;
        await using var coordinator = new DesktopStartupCoordinator(_ => { }, () => { },
            sessionFactory: (service, application) =>
            {
                failed = new(service, applicationContext: application);
                return failed;
            }, dialogs: dialogs);
        coordinator.Start();
        coordinator.WelcomeWindow.Show();

        await coordinator.WelcomeWindow.ViewModel.NewProjectCommand.ExecuteAsync(null);

        Assert.NotNull(failed);
        Assert.True(failed.IsClosing);
        Assert.Null(coordinator.MainWindow);
        Assert.True(coordinator.WelcomeWindow.IsVisible);
        Assert.Equal(ProjectOpenStatus.FAILED, dialogs.CreationResult!.Status);
        Assert.Empty(coordinator.ApplicationContext.RecentProjects.Entries);
        var context = coordinator.ApplicationContext;
        context.UpdatePreferences(value => value with { Theme = WorkbenchTheme.DARK });
        dialogs.NewProjectRequest = new("Retry", environment.DirectoryPath);
        var retryPath = ProjectCreationService.GetProjectPath(dialogs.NewProjectRequest);
        await coordinator.WelcomeWindow.ViewModel.NewProjectCommand.ExecuteAsync(null);

        Assert.Same(context, coordinator.MainWindow!.Session.ApplicationContext);
        Assert.Equal(retryPath, coordinator.MainWindow.Session.ProjectPath);
        Assert.True(File.Exists(retryPath));
        Assert.True(coordinator.MainWindow.IsVisible);
    }

    [AvaloniaFact]
    public async Task ClosingWorkbenchReturnsToTheSameWelcomeAndReopensWithTheSameApplicationContext()
    {
        using var environment = new UiTestEnvironment(disableAutomaticUpdates: true);
        var request = new ProjectCreationRequest("Return", environment.DirectoryPath);
        var path = ProjectCreationService.GetProjectPath(request);
        var dialogs = new StartupTestDialogService { NewProjectRequest = request, OpenPath = path };
        var shutdownCount = 0;
        Window? active = null;
        await using var coordinator = new DesktopStartupCoordinator(window => active = window, () => shutdownCount++,
            dialogs: dialogs);
        coordinator.Start();
        var welcome = coordinator.WelcomeWindow;
        var viewModel = welcome.ViewModel;
        var context = coordinator.ApplicationContext;
        welcome.Show();
        await viewModel.NewProjectCommand.ExecuteAsync(null);
        var first = coordinator.MainWindow!;
        context.UpdatePreferences(value => value with { Theme = WorkbenchTheme.DARK });
        viewModel.Error = "Previous diagnostic";

        first.Close();
        await WaitUntilAsync(() => coordinator.MainWindow is null);

        Assert.False(first.IsVisible);
        Assert.True(first.Session.IsClosing);
        Assert.Same(welcome, active);
        Assert.Same(welcome, coordinator.WelcomeWindow);
        Assert.Same(viewModel, welcome.ViewModel);
        Assert.True(welcome.IsVisible);
        Assert.False(viewModel.HasError);
        Assert.Equal(0, shutdownCount);
        Assert.Equal(path, Assert.Single(viewModel.Projects).Path);
        await viewModel.OpenProjectCommand.ExecuteAsync(null);
        var second = coordinator.MainWindow!;

        Assert.NotSame(first, second);
        Assert.Same(context, second.Session.ApplicationContext);
        Assert.Same(context.StyleLibrary, second.Session.StyleLibrary);
        Assert.Same(second, active);
        Assert.Equal(path, second.Session.ProjectPath);
        Assert.Equal(WorkbenchTheme.DARK, second.Session.Preferences.Theme);
        Assert.True(second.IsVisible);
        Assert.False(welcome.IsVisible);
        Assert.Equal(0, shutdownCount);
    }

    [AvaloniaFact]
    public async Task ExplicitQuitRespectsCancelledUnsavedConfirmationAndThenShutsDownOnce()
    {
        using var environment = new UiTestEnvironment(disableAutomaticUpdates: true);
        var dialogs = new StartupTestDialogService
        {
            NewProjectRequest = new("Quit", environment.DirectoryPath),
            UnsavedChoice = 0
        };
        var shutdownCount = 0;
        await using var coordinator = new DesktopStartupCoordinator(_ => { }, () => shutdownCount++, dialogs: dialogs);
        coordinator.Start();
        coordinator.WelcomeWindow.Show();
        await coordinator.WelcomeWindow.ViewModel.NewProjectCommand.ExecuteAsync(null);
        var window = coordinator.MainWindow!;
        window.Session.Editor.Apply("Pending change", document => document with { Name = "Pending" });

        await window.ViewModel.ExecuteCommandAsync(WorkbenchCommand.EXIT);
        await WaitUntilAsync(() => dialogs.ConfirmationCount == 1 && !window.IsApplicationExitRequested);

        Assert.Same(window, coordinator.MainWindow);
        Assert.True(window.IsVisible);
        Assert.False(window.Session.IsClosing);
        Assert.True(window.Session.Editor.HasUnsavedChanges);
        Assert.False(coordinator.WelcomeWindow.IsVisible);
        Assert.Equal(0, shutdownCount);
        dialogs.UnsavedChoice = 2;
        await window.ViewModel.ExecuteCommandAsync(WorkbenchCommand.EXIT);
        await WaitUntilAsync(() => shutdownCount == 1);
        await coordinator.Completion;

        Assert.Null(coordinator.MainWindow);
        Assert.False(window.IsVisible);
        Assert.False(coordinator.WelcomeWindow.IsVisible);
        Assert.Equal(2, dialogs.ConfirmationCount);
        Assert.Equal(1, shutdownCount);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Startup lifecycle transition did not complete.");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }
}
