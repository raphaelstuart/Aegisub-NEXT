using AegiNext.Application.Tasks;
using AegiNext.Desktop.Settings;
using AegiNext.Desktop.Updates;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;

namespace AegiNext.Desktop.Ui.Tests.Updates;

/// <summary>验证应用级更新提示的静默策略、窗口切换与释放边界。</summary>
public sealed class UpdateWindowCoordinatorUiTests
{
    /// <summary>关闭自动检查时不提交网络查询任务。</summary>
    [AvaloniaFact]
    public async Task DisabledAutomaticChecksNeverQueryTheReleaseSource()
    {
        using var environment = new UiTestEnvironment();
        await using var tasks = new AegiTaskService();
        var source = new UpdateUiReleaseSource((_, _) => Task.FromResult<UpdateRelease?>(CreateRelease()));
        await using var service = new UpdateCheckService(tasks, source, "2026.10.11");
        using var coordinator = new UpdateWindowCoordinator(service, () => new() { AutoCheckUpdates = false });
        var owner = new Window();
        try
        {
            owner.Show();
            coordinator.SetOwner(owner);
            await coordinator.CheckAutomaticallyAsync();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(0, source.CallCount);
            Assert.Empty(tasks.GetSnapshots());
            Assert.Null(coordinator.CurrentWindow);
        }
        finally
        {
            coordinator.Dispose();
            owner.Close();
        }
    }

    /// <summary>自动检查无新版本或失败保持静默，随后手动检查仍给出反馈。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticNoUpdateAndFailureStaySilentWhileManualChecksReportTheResult(bool fail)
    {
        using var environment = new UiTestEnvironment();
        await using var tasks = new AegiTaskService();
        var source = new UpdateUiReleaseSource((_, _) => fail
            ? Task.FromException<UpdateRelease?>(new IOException("Offline fixture"))
            : Task.FromResult<UpdateRelease?>(null));
        await using var service = new UpdateCheckService(tasks, source, "2026.10.11");
        using var coordinator = new UpdateWindowCoordinator(service, () => new() { AutoCheckUpdates = true });
        var owner = new Window();
        try
        {
            owner.Show();
            coordinator.SetOwner(owner);
            await coordinator.CheckAutomaticallyAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.Null(coordinator.CurrentWindow);
            Assert.Empty(owner.OwnedWindows);

            await coordinator.CheckManuallyAsync();
            await WaitUntilAsync(() => coordinator.CurrentWindow is not null);
            var window = Assert.IsType<UpdateCheckWindow>(coordinator.CurrentWindow);
            Assert.True(window.IsVisible);
            Assert.False(window.ViewModel.HasRelease);
            Assert.False(string.IsNullOrWhiteSpace(window.ViewModel.Message));
            Assert.Equal(fail, window.ViewModel.CanRetry);
        }
        finally
        {
            coordinator.Dispose();
            owner.Close();
        }
    }

    /// <summary>手动加入尚未完成的自动查询后共享一次请求，并收到无更新或失败反馈。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualCheckJoiningAnAutomaticQueryPromotesItsNoUpdateOrFailureFeedback(bool fail)
    {
        using var environment = new UiTestEnvironment();
        await using var tasks = new AegiTaskService();
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new UpdateUiReleaseSource(async (_, token) =>
        {
            await finish.Task.WaitAsync(token);
            if (fail)
            {
                throw new IOException("Offline fixture");
            }
            return null;
        });
        await using var service = new UpdateCheckService(tasks, source, "2026.10.11");
        using var coordinator = new UpdateWindowCoordinator(service, () => new() { AutoCheckUpdates = true });
        var owner = new Window();
        try
        {
            owner.Show();
            coordinator.SetOwner(owner);
            var automatic = coordinator.CheckAutomaticallyAsync();
            await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var manual = coordinator.CheckManuallyAsync();
            Assert.Null(coordinator.CurrentWindow);
            finish.TrySetResult();
            await Task.WhenAll(automatic, manual);
            await WaitUntilAsync(() => coordinator.CurrentWindow is not null);

            var window = Assert.IsType<UpdateCheckWindow>(coordinator.CurrentWindow);
            Assert.Same(window, Assert.Single(owner.OwnedWindows));
            Assert.Equal(1, source.CallCount);
            Assert.False(window.ViewModel.HasRelease);
            Assert.Equal(fail, window.ViewModel.CanRetry);
            Assert.False(string.IsNullOrWhiteSpace(window.ViewModel.Message));
        }
        finally
        {
            finish.TrySetCanceled();
            coordinator.Dispose();
            owner.Close();
        }
    }

    /// <summary>并发手动请求共享一次查询，已有提示再次打开只激活原窗口。</summary>
    [AvaloniaFact]
    public async Task RepeatedManualChecksShareTheRequestAndKeepOneRegisteredWindow()
    {
        using var environment = new UiTestEnvironment();
        await using var tasks = new AegiTaskService();
        var result = new TaskCompletionSource<UpdateRelease?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new UpdateUiReleaseSource((_, token) => result.Task.WaitAsync(token));
        await using var service = new UpdateCheckService(tasks, source, "2026.10.11");
        using var coordinator = new UpdateWindowCoordinator(service, () => new());
        var registrations = new List<Window>();
        var owner = new Window();
        try
        {
            owner.Show();
            coordinator.SetOwner(owner, registrations.Add);
            var first = coordinator.CheckManuallyAsync();
            await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var second = coordinator.CheckManuallyAsync();
            result.TrySetResult(CreateRelease());
            await Task.WhenAll(first, second);
            await WaitUntilAsync(() => coordinator.CurrentWindow is not null);
            var window = Assert.IsType<UpdateCheckWindow>(coordinator.CurrentWindow);
            Assert.Same(window, Assert.Single(owner.OwnedWindows));
            Assert.Same(window, Assert.Single(registrations));
            Assert.Equal(1, source.CallCount);

            await coordinator.CheckManuallyAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.Same(window, coordinator.CurrentWindow);
            Assert.Equal(1, source.CallCount);
            Assert.Single(registrations);
        }
        finally
        {
            result.TrySetCanceled();
            coordinator.Dispose();
            owner.Close();
        }
    }

    /// <summary>过渡期间到达的新结果只在下一可见所属窗口恢复后提示。</summary>
    [AvaloniaFact]
    public async Task ResultArrivingDuringOwnerTransitionUsesTheNextVisibleOwner()
    {
        using var environment = new UiTestEnvironment();
        await using var tasks = new AegiTaskService();
        var result = new TaskCompletionSource<UpdateRelease?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new UpdateUiReleaseSource((_, token) => result.Task.WaitAsync(token));
        await using var service = new UpdateCheckService(tasks, source, "2026.10.11");
        using var coordinator = new UpdateWindowCoordinator(service, () => new() { AutoCheckUpdates = true });
        var welcome = new Window();
        var workbench = new Window();
        var registrations = new List<Window>();
        try
        {
            welcome.Show();
            coordinator.SetOwner(welcome);
            var check = coordinator.CheckAutomaticallyAsync();
            await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            coordinator.BeginOwnerTransition();
            welcome.Hide();
            result.TrySetResult(CreateRelease());
            await check;
            Dispatcher.UIThread.RunJobs();
            Assert.Null(coordinator.CurrentWindow);
            Assert.Empty(welcome.OwnedWindows);

            workbench.Show();
            coordinator.SetOwner(workbench, registrations.Add);
            await WaitUntilAsync(() => coordinator.CurrentWindow is not null);
            var window = Assert.IsType<UpdateCheckWindow>(coordinator.CurrentWindow);
            Assert.Same(window, Assert.Single(workbench.OwnedWindows));
            Assert.Same(window, Assert.Single(registrations));
            Assert.Equal(1, source.CallCount);
        }
        finally
        {
            result.TrySetCanceled();
            coordinator.Dispose();
            welcome.Close();
            workbench.Close();
        }
    }

    /// <summary>窗口切换关闭旧提示且不自动重放，但手动入口可重新查看已有结果。</summary>
    [AvaloniaFact]
    public async Task OwnerTransitionClosesTheUpdateWithoutReplayingItAndManualCheckReopensTheResult()
    {
        using var environment = new UiTestEnvironment();
        await using var tasks = new AegiTaskService();
        var source = new UpdateUiReleaseSource((_, _) => Task.FromResult<UpdateRelease?>(CreateRelease()));
        await using var service = new UpdateCheckService(tasks, source, "2026.10.11");
        using var coordinator = new UpdateWindowCoordinator(service, () => new() { AutoCheckUpdates = true });
        var firstOwner = new Window();
        var nextOwner = new Window();
        try
        {
            firstOwner.Show();
            coordinator.SetOwner(firstOwner);
            await coordinator.CheckAutomaticallyAsync();
            await WaitUntilAsync(() => coordinator.CurrentWindow is not null);
            var original = Assert.IsType<UpdateCheckWindow>(coordinator.CurrentWindow);
            coordinator.BeginOwnerTransition();
            Assert.False(original.IsVisible);
            Assert.Null(coordinator.CurrentWindow);
            firstOwner.Close();
            nextOwner.Show();
            coordinator.SetOwner(nextOwner);
            Dispatcher.UIThread.RunJobs();
            Assert.Null(coordinator.CurrentWindow);
            Assert.Empty(nextOwner.OwnedWindows);

            await coordinator.CheckManuallyAsync();
            await WaitUntilAsync(() => coordinator.CurrentWindow is not null);
            var reopened = Assert.IsType<UpdateCheckWindow>(coordinator.CurrentWindow);
            Assert.NotSame(original, reopened);
            Assert.Same(reopened, Assert.Single(nextOwner.OwnedWindows));
            Assert.Equal(1, source.CallCount);
        }
        finally
        {
            coordinator.Dispose();
            firstOwner.Close();
            nextOwner.Close();
        }
    }

    /// <summary>释放展示协调器后完成的查询不会创建新窗口。</summary>
    [AvaloniaFact]
    public async Task DisposedCoordinatorIgnoresAQueryThatCompletesAfterShutdown()
    {
        using var environment = new UiTestEnvironment();
        await using var tasks = new AegiTaskService();
        var result = new TaskCompletionSource<UpdateRelease?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new UpdateUiReleaseSource((_, token) => result.Task.WaitAsync(token));
        await using var service = new UpdateCheckService(tasks, source, "2026.10.11");
        using var coordinator = new UpdateWindowCoordinator(service, () => new() { AutoCheckUpdates = true });
        var owner = new Window();
        try
        {
            owner.Show();
            coordinator.SetOwner(owner);
            var check = coordinator.CheckAutomaticallyAsync();
            await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            coordinator.Dispose();
            result.TrySetResult(CreateRelease());
            await check;
            Dispatcher.UIThread.RunJobs();

            Assert.Null(coordinator.CurrentWindow);
            Assert.Empty(owner.OwnedWindows);
        }
        finally
        {
            result.TrySetCanceled();
            coordinator.Dispose();
            owner.Close();
        }
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
            Assert.True(DateTime.UtcNow < deadline, "更新窗口未在 UI 调度完成后出现。");
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
        Dispatcher.UIThread.RunJobs();
    }
}
