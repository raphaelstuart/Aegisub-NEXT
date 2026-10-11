using AegiNext.Application.Tasks;
using AegiNext.Desktop.Updates;

namespace AegiNext.Desktop.Tests.Updates;

public sealed class UpdateCheckServiceTests
{
    [Theory]
    [InlineData("0.7.0", (int)UpdateCheckStatus.UPDATE_AVAILABLE)]
    [InlineData("0.8.0", (int)UpdateCheckStatus.UP_TO_DATE)]
    [InlineData("0.9.0", (int)UpdateCheckStatus.UP_TO_DATE)]
    public async Task TaskComparesVersionsAndKeepsApplicationScope(string localVersion, int expected)
    {
        await using var tasks = new AegiTaskService();
        var source = new UpdateReleaseSourceStub((_, _) =>
        {
            Assert.Null(SynchronizationContext.Current);
            return Task.FromResult<UpdateRelease?>(UpdateTestData.Release());
        });
        var operation = new CheckUpdatesTask(source, UpdateChannel.INCLUDE_PRERELEASE, localVersion);

        var handle = tasks.Submit(operation);
        var result = await handle.Completion;

        Assert.Equal((UpdateCheckStatus)expected, result.Status);
        Assert.Equal(AegiTaskMode.Parallel, operation.Mode);
        Assert.Equal(AegiTaskEditRestriction.None, operation.EditRestriction);
        Assert.Null(operation.ScopeId);
        Assert.Empty(operation.Resources);
        Assert.True(operation.CanCancel);
        Assert.Equal(AegiTaskState.Succeeded, handle.Snapshot.State);
    }

    [Fact]
    public async Task EmptyChannelAndInvalidLocalVersionHaveDistinctResults()
    {
        await using var tasks = new AegiTaskService();
        var source = new UpdateReleaseSourceStub((_, _) => Task.FromResult<UpdateRelease?>(null));
        await using var valid = new UpdateCheckService(tasks, source, "1.0.0");
        await using var invalid = new UpdateCheckService(tasks, source, "development");

        Assert.Equal(UpdateCheckStatus.NO_RELEASE, (await valid.CheckAsync(UpdateCheckTrigger.MANUAL, UpdateChannel.STABLE)).Status);
        var result = await invalid.CheckAsync(UpdateCheckTrigger.MANUAL, UpdateChannel.STABLE);

        Assert.Equal(UpdateCheckStatus.FAILED, result.Status);
        Assert.IsType<InvalidDataException>(result.Error);
        Assert.Equal(1, source.CallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualJoinSharesAutomaticRequestAndPromotesItsOnlyNotification(bool fail)
    {
        await using var tasks = new AegiTaskService();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<UpdateRelease?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new UpdateReleaseSourceStub((channel, _) =>
        {
            Assert.Equal(UpdateChannel.STABLE, channel);
            entered.TrySetResult();
            return release.Task;
        });
        await using var service = new UpdateCheckService(tasks, source, "0.8.0");
        var notifications = new List<UpdateCheckCompletedEventArgs>();
        service.Completed += (_, args) => notifications.Add(args);
        var automatic = service.CheckAsync(UpdateCheckTrigger.AUTOMATIC, UpdateChannel.STABLE);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var manual = service.CheckAsync(UpdateCheckTrigger.MANUAL, UpdateChannel.INCLUDE_PRERELEASE);
        Assert.Same(automatic, manual);
        if (fail)
        {
            release.SetException(new HttpRequestException("Offline"));
        }
        else
        {
            release.SetResult(UpdateTestData.Release());
        }

        var result = await manual;

        Assert.Equal(1, source.CallCount);
        var notification = Assert.Single(notifications);
        Assert.Equal(UpdateCheckTrigger.MANUAL, notification.Trigger);
        Assert.Equal(UpdateChannel.STABLE, notification.Channel);
        Assert.Same(result, notification.Result);
        Assert.Equal(fail ? UpdateCheckStatus.FAILED : UpdateCheckStatus.UP_TO_DATE, result.Status);
        Assert.Equal(fail ? AegiTaskState.Failed : AegiTaskState.Succeeded, Assert.Single(tasks.GetSnapshots()).State);
    }

    [Fact]
    public async Task AutomaticJoinDoesNotDowngradeManualFeedback()
    {
        await using var tasks = new AegiTaskService();
        var release = new TaskCompletionSource<UpdateRelease?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new UpdateReleaseSourceStub((_, _) => release.Task);
        await using var service = new UpdateCheckService(tasks, source, "0.1.0");
        UpdateCheckCompletedEventArgs? notification = null;
        service.Completed += (_, args) => notification = args;
        var manual = service.CheckAsync(UpdateCheckTrigger.MANUAL, UpdateChannel.STABLE);
        var automatic = service.CheckAsync(UpdateCheckTrigger.AUTOMATIC, UpdateChannel.STABLE);
        release.SetResult(UpdateTestData.Release());

        await Task.WhenAll(manual, automatic);

        Assert.Equal(1, source.CallCount);
        Assert.Equal(UpdateCheckTrigger.MANUAL, notification!.Trigger);
    }

    [Fact]
    public async Task CompletedRequestDoesNotPreventLaterChecks()
    {
        await using var tasks = new AegiTaskService();
        var source = new UpdateReleaseSourceStub((_, _) => Task.FromResult<UpdateRelease?>(UpdateTestData.Release()));
        await using var service = new UpdateCheckService(tasks, source, "0.8.0");
        var previous = SynchronizationContext.Current;
        Task<UpdateCheckResult> first;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new UpdateImmediateSynchronizationContext());
            first = service.CheckAsync(UpdateCheckTrigger.AUTOMATIC, UpdateChannel.STABLE);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
        await first;

        var second = service.CheckAsync(UpdateCheckTrigger.MANUAL, UpdateChannel.STABLE);
        await second;

        Assert.NotSame(first, second);
        Assert.Equal(2, source.CallCount);
    }

    [Fact]
    public async Task DisposingAQueuedCheckCancelsWithoutStartingNetworkWork()
    {
        await using var tasks = new AegiTaskService { MaximumConcurrentTasks = 1 };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = tasks.Submit(new UpdateGateTask(entered, release.Task));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var source = new UpdateReleaseSourceStub((_, _) => Task.FromResult<UpdateRelease?>(UpdateTestData.Release()));
        await using var service = new UpdateCheckService(tasks, source, "0.1.0");
        var notified = false;
        service.Completed += (_, _) => notified = true;
        try
        {
            var pending = service.CheckAsync(UpdateCheckTrigger.MANUAL, UpdateChannel.STABLE);
            await service.DisposeAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Equal(0, source.CallCount);
            Assert.False(notified);
            Assert.Contains(tasks.GetSnapshots(), snapshot => snapshot.Name == "Tasks.CheckUpdates" && snapshot.State == AegiTaskState.Cancelled);
        }
        finally
        {
            release.TrySetResult();
            await blocker.Completion;
        }
    }

    [Fact]
    public async Task DisposingRunningCheckRejectsLateSuccessAndFurtherChecks()
    {
        await using var tasks = new AegiTaskService();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<UpdateRelease?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new UpdateReleaseSourceStub((_, _) =>
        {
            entered.TrySetResult();
            return release.Task;
        });
        await using var service = new UpdateCheckService(tasks, source, "0.1.0");
        var notified = false;
        service.Completed += (_, _) => notified = true;
        var pending = service.CheckAsync(UpdateCheckTrigger.MANUAL, UpdateChannel.STABLE);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var closing = service.DisposeAsync().AsTask();
        release.TrySetResult(UpdateTestData.Release());
        await closing.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(notified);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.CheckAsync(UpdateCheckTrigger.MANUAL, UpdateChannel.STABLE));
    }

    [Fact]
    public async Task ClosingDuringSubmissionCancelsTheHandleAssignedAfterwards()
    {
        await using var tasks = new AegiTaskService();
        var source = new UpdateReleaseSourceStub(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return null;
        });
        await using var service = new UpdateCheckService(tasks, source, "0.1.0");
        Task? closing = null;
        var notified = false;
        service.Completed += (_, _) => notified = true;
        tasks.Changed += (_, _) =>
        {
            if (closing is null && tasks.GetSnapshots().Any(snapshot => snapshot.Name == "Tasks.CheckUpdates"))
            {
                closing = service.DisposeAsync().AsTask();
            }
        };

        var pending = service.CheckAsync(UpdateCheckTrigger.MANUAL, UpdateChannel.STABLE);
        await closing!.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(notified);
        Assert.Equal(AegiTaskState.Cancelled, Assert.Single(tasks.GetSnapshots()).State);
    }

    [Fact]
    public async Task TaskCenterCancellationSuppressesResultAndAllowsAnotherCheck()
    {
        await using var tasks = new AegiTaskService();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queries = 0;
        var source = new UpdateReleaseSourceStub(async (_, token) =>
        {
            if (Interlocked.Increment(ref queries) == 1)
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return UpdateTestData.Release();
        });
        await using var service = new UpdateCheckService(tasks, source, "0.1.0");
        var notifications = 0;
        service.Completed += (_, _) => notifications++;
        var pending = service.CheckAsync(UpdateCheckTrigger.MANUAL, UpdateChannel.STABLE);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(tasks.RequestCancel(Assert.Single(tasks.GetSnapshots()).Id));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(0, notifications);
        var result = await service.CheckAsync(UpdateCheckTrigger.MANUAL, UpdateChannel.STABLE);

        Assert.Equal(UpdateCheckStatus.UPDATE_AVAILABLE, result.Status);
        Assert.Equal(1, notifications);
        Assert.Equal(2, source.CallCount);
    }

    [Fact]
    public async Task ObserverFailureDoesNotChangeTheResultOrSuppressOtherObservers()
    {
        await using var tasks = new AegiTaskService();
        var source = new UpdateReleaseSourceStub((_, _) => Task.FromResult<UpdateRelease?>(UpdateTestData.Release()));
        await using var service = new UpdateCheckService(tasks, source, "0.1.0");
        var notifications = 0;
        service.Completed += (_, _) => throw new InvalidOperationException("Observer failure");
        service.Completed += (_, _) => notifications++;

        var result = await service.CheckAsync(UpdateCheckTrigger.MANUAL, UpdateChannel.STABLE);
        await service.CheckAsync(UpdateCheckTrigger.MANUAL, UpdateChannel.STABLE);
        await service.DisposeAsync();

        Assert.Equal(UpdateCheckStatus.UPDATE_AVAILABLE, result.Status);
        Assert.Equal(2, notifications);
        Assert.Equal(2, source.CallCount);
    }

    [Fact]
    public async Task NotificationDoesNotHoldTheServiceLockAndShutdownDrainsIt()
    {
        await using var tasks = new AegiTaskService();
        var source = new UpdateReleaseSourceStub((_, _) => Task.FromResult<UpdateRelease?>(UpdateTestData.Release()));
        await using var service = new UpdateCheckService(tasks, source, "0.1.0");
        Task? closing = null;
        Thread? thread = null;
        var returnedWithoutLock = false;
        var shutdownWaitedForNotification = false;
        service.Completed += (_, _) =>
        {
            thread = new(() => closing = service.DisposeAsync().AsTask());
            thread.Start();
            returnedWithoutLock = thread.Join(TimeSpan.FromSeconds(2));
            shutdownWaitedForNotification = closing is { IsCompleted: false };
        };

        await service.CheckAsync(UpdateCheckTrigger.MANUAL, UpdateChannel.STABLE);
        Assert.True(thread!.Join(TimeSpan.FromSeconds(2)));
        await closing!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(returnedWithoutLock);
        Assert.True(shutdownWaitedForNotification);
    }
}
