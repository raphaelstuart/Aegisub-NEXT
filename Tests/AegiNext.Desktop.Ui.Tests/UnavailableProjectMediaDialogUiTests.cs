using AegiNext.Application;
using AegiNext.Core.Projects;
using AegiNext.Core.Timing;
using AegiNext.Desktop.Controllers;
using AegiNext.Desktop.I18n;
using AegiNext.Desktop.Settings;
using AegiNext.Desktop.Startup;
using AegiNext.Desktop.Views;
using AegiNext.Desktop.Workspace;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;

namespace AegiNext.Desktop.Ui.Tests;

public sealed class UnavailableProjectMediaDialogUiTests
{
    [AvaloniaTheory]
    [InlineData("en-US", WorkbenchTheme.LIGHT, true)]
    [InlineData("zh-CN", WorkbenchTheme.DARK, false)]
    public async Task RegisteredDialogShowsTheReferenceAndReturnsTheClickedDecision(string language, WorkbenchTheme theme, bool proceed)
    {
        await using var context = new MainWindowTestContext();
        context.Session.UpdatePreferences(value => value with { Theme = theme });
        Localization.SetLanguage(language);
        var path = "/Media/字幕 ABC 123/" + new string('x', 150) + ".mkv";
        var service = new WindowWorkbenchDialogService(context.Window, registerWindow: context.WindowRegistry.RegisterAuxiliary);
        var answer = service.ConfirmUnavailableMediaAsync(path, "Cannot read 中文 123", TestContext.Current.CancellationToken);
        var dialog = Assert.Single(context.Window.OwnedWindows.OfType<UnavailableProjectMediaDialog>());
        try
        {
            Dispatcher.UIThread.RunJobs();
            dialog.UpdateLayout();
            Assert.Contains("business-surface", dialog.Classes);
            Assert.Equal(SizeToContent.Height, dialog.SizeToContent);
            Assert.Equal(Localization.Get("Workbench.UnavailableMediaTitle"), dialog.Title);
            Assert.Contains(path, UiTestActions.Find<TextBox>(dialog, "UnavailableMediaDetails").Text);
            var message = UiTestActions.Find<TextBlock>(dialog, "UnavailableMediaMessage");
            Assert.Contains("PingFang SC", message.FontFamily.Name, StringComparison.Ordinal);
            Assert.Equal(20, message.LineHeight);
            var button = UiTestActions.Find<Button>(dialog, "ContinueButton");
            var bottom = button.TranslatePoint(new Avalonia.Point(0, button.Bounds.Height), dialog)!.Value.Y;
            Assert.InRange(dialog.ClientSize.Height - bottom, 19, 21);
            Assert.InRange(dialog.ClientSize.Height, 150, 400);

            UiTestActions.Click(dialog, proceed ? "ContinueButton" : "CancelButton");

            Assert.Equal(proceed, await answer);
            Assert.Empty(context.Window.OwnedWindows.OfType<UnavailableProjectMediaDialog>());
        }
        finally
        {
            dialog.Close(false);
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EscapeAndWindowCloseDeclineWithoutLeavingAWindow(bool escape)
    {
        await using var context = new MainWindowTestContext();
        var service = new WindowWorkbenchDialogService(context.Window, registerWindow: context.WindowRegistry.RegisterAuxiliary);
        var answer = service.ConfirmUnavailableMediaAsync("missing.mkv", "File missing", TestContext.Current.CancellationToken);
        var dialog = Assert.Single(context.Window.OwnedWindows.OfType<UnavailableProjectMediaDialog>());
        try
        {
            if (escape)
            {
                dialog.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
            }
            else
            {
                dialog.Close();
            }
            Assert.False(await answer);
            Assert.False(dialog.IsVisible);
        }
        finally
        {
            dialog.Close(false);
        }
    }

    [AvaloniaFact]
    public async Task CancellationClosesThePendingDialogAndPrecancelledRequestsDoNotShowIt()
    {
        await using var context = new MainWindowTestContext();
        using var cancellation = new CancellationTokenSource();
        var service = new WindowWorkbenchDialogService(context.Window, registerWindow: context.WindowRegistry.RegisterAuxiliary);
        var answer = service.ConfirmUnavailableMediaAsync("missing.mkv", "File missing", cancellation.Token);
        var dialog = Assert.Single(context.Window.OwnedWindows.OfType<UnavailableProjectMediaDialog>());
        try
        {
            cancellation.Cancel();
            Dispatcher.UIThread.RunJobs();

            Assert.False(await answer);
            Assert.Empty(context.Window.OwnedWindows.OfType<UnavailableProjectMediaDialog>());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.ConfirmUnavailableMediaAsync("missing.mkv", "File missing", cancellation.Token));
            Assert.Empty(context.Window.OwnedWindows.OfType<UnavailableProjectMediaDialog>());
        }
        finally
        {
            dialog.Close(false);
        }
    }

    [AvaloniaFact]
    public void DialogRefreshesLanguageAndReleasesItsBindingsWhenClosed()
    {
        using var environment = new UiTestEnvironment();
        var owner = new Window();
        var dialog = new UnavailableProjectMediaDialog("missing 中文 ABC 123.mkv", "File missing");
        try
        {
            owner.Show();
            dialog.Show(owner);
            Localization.SetLanguage("zh-CN");
            Assert.Equal("媒体不可用", dialog.Title);
            Assert.Equal("继续打开", UiTestActions.Find<Button>(dialog, "ContinueButton").Content);
            var details = UiTestActions.Find<TextBox>(dialog, "UnavailableMediaDetails").Text;
            Localization.SetLanguage("en-US");
            Assert.Equal("Media unavailable", dialog.Title);
            Assert.Equal(details, UiTestActions.Find<TextBox>(dialog, "UnavailableMediaDetails").Text);
            var button = UiTestActions.Find<Button>(dialog, "ContinueButton");
            dialog.Close();
            var title = dialog.Title;
            var content = button.Content;
            Localization.SetLanguage("zh-CN");
            Assert.Equal(title, dialog.Title);
            Assert.Equal(content, button.Content);
            Assert.Empty(owner.OwnedWindows);
        }
        finally
        {
            dialog.Close();
            owner.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WelcomeOpensOneOfflineWorkbenchOnlyWhenTheUserContinues(bool proceed)
    {
        using var environment = new UiTestEnvironment(disableAutomaticUpdates: true);
        var path = Path.Combine(environment.DirectoryPath, "Missing media.aeginext");
        var asset = new ProjectAsset(Guid.NewGuid(), ProjectAssetKind.MEDIA, "missing.mkv");
        await ProjectStore.SaveAsync(new()
        {
            Width = 1, Height = 1, Assets = [asset], Media = new(asset.Id, 0, null, MediaTime.Zero)
        }, path, TestContext.Current.CancellationToken);
        var dialogs = new StartupTestDialogService { OpenPath = path, UnavailableMediaChoice = proceed };
        await using var coordinator = new DesktopStartupCoordinator(_ => { }, () => { },
            sessionFactory: CreateUnavailableMediaSession, dialogs: dialogs);
        coordinator.Start();
        coordinator.WelcomeWindow.Show();

        await coordinator.WelcomeWindow.ViewModel.OpenProjectCommand.ExecuteAsync(null);

        Assert.Equal(1, dialogs.UnavailableMediaCount);
        Assert.False(coordinator.WelcomeWindow.ViewModel.HasError);
        if (proceed)
        {
            Assert.True(coordinator.MainWindow!.IsVisible);
            Assert.False(coordinator.WelcomeWindow.IsVisible);
            Assert.Equal(path, coordinator.MainWindow.Session.ProjectPath);
            Assert.Equal(asset, Assert.Single(coordinator.MainWindow.Session.DocumentSnapshot.Assets));
            Assert.Null(coordinator.MainWindow.Session.Controller.Snapshot.FilePath);
            Assert.Equal(path, Assert.Single(coordinator.ApplicationContext.RecentProjects.Entries).Path);
        }
        else
        {
            Assert.Null(coordinator.MainWindow);
            Assert.True(coordinator.WelcomeWindow.IsVisible);
            Assert.Empty(coordinator.ApplicationContext.RecentProjects.Entries);
        }
    }

    [AvaloniaFact]
    public async Task ClosingWelcomeCancelsPendingMediaConfirmationAndRejectsLateContinue()
    {
        using var environment = new UiTestEnvironment(disableAutomaticUpdates: true);
        var path = Path.Combine(environment.DirectoryPath, "Pending media.aeginext");
        var asset = new ProjectAsset(Guid.NewGuid(), ProjectAssetKind.MEDIA, "missing.mkv");
        await ProjectStore.SaveAsync(new()
        {
            Width = 1, Height = 1, Assets = [asset], Media = new(asset.Id, 0, null, MediaTime.Zero)
        }, path, TestContext.Current.CancellationToken);
        var dialogs = new StartupTestDialogService
        {
            OpenPath = path, PendingMediaConfirmation = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var shutdownCount = 0;
        await using var coordinator = new DesktopStartupCoordinator(_ => { }, () => shutdownCount++,
            sessionFactory: CreateUnavailableMediaSession, dialogs: dialogs);
        coordinator.Start();
        coordinator.WelcomeWindow.Show();
        var operation = coordinator.WelcomeWindow.ViewModel.OpenProjectCommand.ExecuteAsync(null);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (dialogs.UnavailableMediaCount == 0)
        {
            Dispatcher.UIThread.RunJobs();
            Assert.True(DateTime.UtcNow < deadline);
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        coordinator.WelcomeWindow.Close();
        await operation;
        await coordinator.Completion;
        dialogs.PendingMediaConfirmation.SetResult(true);

        Assert.Null(coordinator.MainWindow);
        Assert.False(coordinator.WelcomeWindow.IsVisible);
        Assert.Empty(coordinator.ApplicationContext.RecentProjects.Entries);
        Assert.Equal(1, shutdownCount);
    }

    private static WorkbenchSession CreateUnavailableMediaSession(IWorkbenchDialogService dialogs, DesktopApplicationContext application)
    {
        return new(dialogs, update => new VideoPreviewController(
            (path, _) => Task.FromException<VideoPreviewMedia>(new FileNotFoundException("Media fixture missing", path)),
            (_, _) => throw new InvalidOperationException("Unavailable media must not create a decoder."),
            () => new UiPreviewConverter(), DispatchAsync, update), DispatchAsync, applicationContext: application);
    }

    private static async Task DispatchAsync(Action action, CancellationToken cancellationToken)
    {
        await Dispatcher.UIThread.InvokeAsync(action, DispatcherPriority.Normal, cancellationToken);
    }
}
