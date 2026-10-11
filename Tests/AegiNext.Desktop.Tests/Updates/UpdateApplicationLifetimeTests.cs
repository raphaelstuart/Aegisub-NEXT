using AegiNext.Desktop.Startup;
using AegiNext.Desktop.Updates;

namespace AegiNext.Desktop.Tests.Updates;

[Collection("Workspace session")]
public sealed class UpdateApplicationLifetimeTests
{
    [Fact]
    public async Task FailedCheckKeepsItsTaskRecordWithoutChangingApplicationError()
    {
        using var directory = new TemporaryWorkbenchDirectory();
        var source = new UpdateReleaseSourceStub((_, _) => Task.FromException<UpdateRelease?>(new HttpRequestException("Offline")));
        await using var context = new DesktopApplicationContext(new(directory.Path), new(), updateReleaseSource: source);
        await context.Initialization;
        var previousError = context.LastError;

        var result = await context.Updates.CheckAsync(UpdateCheckTrigger.AUTOMATIC, UpdateChannel.INCLUDE_PRERELEASE);

        Assert.Equal(UpdateCheckStatus.FAILED, result.Status);
        Assert.Same(previousError, context.LastError);
        Assert.Equal(ApplicationVersion.Current, result.CurrentVersion);
        Assert.Contains(context.Tasks.GetSnapshots(), snapshot =>
            snapshot.Name == "Tasks.CheckUpdates" && snapshot.State == AegiNext.Application.Tasks.AegiTaskState.Failed);
    }

    [Fact]
    public async Task ApplicationDisposalCancelsItsSharedCheckAndReleasesObservers()
    {
        using var directory = new TemporaryWorkbenchDirectory();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new UpdateReleaseSourceStub(async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return null;
        });
        await using var context = new DesktopApplicationContext(new(directory.Path), new(), updateReleaseSource: source);
        await context.Initialization;
        var notified = false;
        context.Updates.Completed += (_, _) => notified = true;
        var pending = context.Updates.CheckAsync(UpdateCheckTrigger.AUTOMATIC, UpdateChannel.INCLUDE_PRERELEASE);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await context.DisposeAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(notified);
    }
}
