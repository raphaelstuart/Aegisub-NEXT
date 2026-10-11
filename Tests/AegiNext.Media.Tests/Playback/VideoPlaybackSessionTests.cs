using System.Diagnostics.CodeAnalysis;
using AegiNext.Core.Timing;
using AegiNext.Media.Playback;
using AegiNext.Media.Tests.Decoding;

namespace AegiNext.Media.Tests.Playback;

public sealed class VideoPlaybackSessionTests
{
    [Fact]
    [SuppressMessage("ReSharper", "DisposeOnUsingVariable", Justification = "The test explicitly verifies repeated session and presentation disposal while keeping automatic cleanup on assertion failure.")]
    public async Task OpeningPublishesAnOwnedFrameAndClosingDoesNotReclaimIt()
    {
        var source = new FakeVideoFrameSource(0, 40, 100);
        await using var session = new VideoPlaybackSession(_ => source);
        await session.OpenAsync();
        using var presentation = Assert.IsType<VideoPresentation>(await session.ReadPresentationAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        var frame = Assert.IsType<FakeVideoFrame>(presentation.PositionedFrame.Frame);

        Assert.Equal(VideoPlaybackState.PAUSED, session.Snapshot.State);
        Assert.Equal(MediaTime.Zero, session.Snapshot.DisplayTime);
        await Task.WhenAll(session.CloseAsync(), session.CloseAsync());
        await session.DisposeAsync();

        Assert.Equal(VideoPlaybackState.CLOSED, session.Snapshot.State);
        Assert.Equal(1, source.DisposeCount);
        Assert.Equal(0, frame.DisposeCount);
        Assert.Equal(0, Assert.Single(frame.CopyPlane(0)));
        Assert.Null(await session.ReadPresentationAsync());
        presentation.Dispose();
        presentation.Dispose();
        Assert.Equal(1, frame.DisposeCount);
    }

    [Fact]
    public async Task PausedTimeIsFrozenAndPlaybackUsesExactFrameBoundaries()
    {
        var source = new FakeVideoFrameSource(0, 40, 100, 160);
        var clock = new ManualPlaybackTimeProvider();
        await using var session = new VideoPlaybackSession(_ => source, clock);
        await session.OpenAsync();
        using var first = await session.ReadPresentationAsync();
        clock.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(MediaTime.Zero, session.Snapshot.Position);
        await session.PlayAsync();
        await EventuallyAsync(() => clock.ActiveTimerCount != 0);
        clock.Advance(TimeSpan.FromMilliseconds(39));
        Assert.Equal(MediaTime.Zero, session.Snapshot.DisplayTime);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        using var second = Assert.IsType<VideoPresentation>(await session.ReadPresentationAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(new MediaTime(40, 1000), second.PositionedFrame.Time);

        await session.PauseAsync();
        var frozen = session.Snapshot.Position;
        using var paused = await session.ReadPresentationAsync();
        clock.Advance(TimeSpan.FromSeconds(100));
        Assert.Equal(VideoPlaybackState.PAUSED, session.Snapshot.State);
        Assert.Equal(frozen, session.Snapshot.Position);
        Assert.Equal(0, source.CancelCount);
    }

    [Fact]
    public async Task SeekPublishesOnlyTheSelectedGenerationAndEndsPaused()
    {
        var source = new FakeVideoFrameSource(0, 40, 100);
        await using var session = new VideoPlaybackSession(_ => source, presentationCapacity: 1);
        await session.OpenAsync();
        var originalGeneration = session.Snapshot.Generation;
        var result = await session.SeekAsync(new(50, 1000));
        using var presentation = Assert.IsType<VideoPresentation>(await session.ReadPresentationAsync());

        Assert.True(result.Generation > originalGeneration);
        Assert.Equal(result.Generation, presentation.Generation);
        Assert.Equal(result.Generation, session.Snapshot.Generation);
        Assert.Equal(new MediaTime(50, 1000), result.RequestedTime);
        Assert.Equal(new MediaTime(40, 1000), result.SelectedTime);
        Assert.Equal(new MediaTime(100, 1000), result.NextFrameTime);
        Assert.Equal(new MediaTime(50, 1000), session.Snapshot.Position);
        Assert.Equal(VideoPlaybackState.PAUSED, session.Snapshot.State);
        Assert.Equal(1, Assert.IsType<FakeVideoFrame>(presentation.PositionedFrame.Frame).Marker);
        Assert.Equal(1, source.IssuedFrames.First().DisposeCount);
        Assert.Equal(0, source.CancelCount);
    }

    [Fact]
    public async Task ANewSeekDisposesAnOlderBlockedSeekWithoutPublishingIt()
    {
        var source = new FakeVideoFrameSource(0, 40, 100, 160);
        await using var session = new VideoPlaybackSession(_ => source);
        await session.OpenAsync();
        using var first = await session.ReadPresentationAsync();
        source.BlockNextSeek();
        var oldSeek = session.SeekAsync(new(50, 1000));
        await source.SeekEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var latestSeek = session.SeekAsync(new(120, 1000));
        Assert.Equal(2, source.SupersedeCount);
        source.ReleaseSeek();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oldSeek);
        var latest = await latestSeek.WaitAsync(TimeSpan.FromSeconds(5));
        using var presentation = Assert.IsType<VideoPresentation>(await session.ReadPresentationAsync());
        Assert.Equal(latest.Generation, presentation.Generation);
        Assert.Equal(new MediaTime(100, 1000), presentation.PositionedFrame.Time);
        Assert.Equal(1, source.IssuedFrames.Single(frame => frame.Marker == 1).DisposeCount);
        Assert.Equal(0, source.CancelCount);
    }

    [Fact]
    public async Task PreCancelledCommandCannotChangeGenerationOrPoisonTheSource()
    {
        var source = new FakeVideoFrameSource(0, 40, 100);
        await using var session = new VideoPlaybackSession(_ => source);
        await session.OpenAsync();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var before = session.Snapshot;

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.SeekAsync(new(80, 1000), cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(before, session.Snapshot);
        Assert.Equal(0, source.SupersedeCount);
        Assert.Equal(0, source.CancelCount);
        Assert.Equal(new MediaTime(40, 1000), (await session.SeekAsync(new(50, 1000))).SelectedTime);
    }

    [Fact]
    public async Task CancellingAStartedSeekDoesNotCancelTheSharedSource()
    {
        var source = new FakeVideoFrameSource(0, 40, 100);
        await using var session = new VideoPlaybackSession(_ => source);
        await session.OpenAsync();
        source.BlockNextSeek();
        using var cancellation = new CancellationTokenSource();
        var seek = session.SeekAsync(new(50, 1000), cancellation.Token);
        await source.SeekEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        source.ReleaseSeek();

        var result = await seek.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new MediaTime(40, 1000), result.SelectedTime);
        Assert.Equal(0, source.CancelCount);
        Assert.Equal(VideoPlaybackState.PAUSED, session.Snapshot.State);
    }

    [Fact]
    public async Task ClosingUnblocksNativeWorkAndReleasesEveryUndeliveredFrame()
    {
        var source = new FakeVideoFrameSource(0, 40, 100);
        await using var session = new VideoPlaybackSession(_ => source);
        await session.OpenAsync();
        source.BlockNextSeek();
        var seek = session.SeekAsync(new(50, 1000));
        await source.SeekEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => seek);
        Assert.Equal(VideoPlaybackState.CLOSED, session.Snapshot.State);
        Assert.Equal(1, source.DisposeCount);
        Assert.All(source.IssuedFrames, frame => Assert.Equal(1, frame.DisposeCount));
        Assert.Null(await session.ReadPresentationAsync());
    }

    [Fact]
    public async Task CancellingAConsumerWaitDoesNotCancelPlayback()
    {
        var source = new FakeVideoFrameSource(0, 40, 100);
        await using var session = new VideoPlaybackSession(_ => source);
        await session.OpenAsync();
        using var first = await session.ReadPresentationAsync();
        using var cancellation = new CancellationTokenSource();
        var read = session.ReadPresentationAsync(cancellation.Token).AsTask();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.Equal(0, source.CancelCount);
        await session.SeekAsync(new(50, 1000));
        using var selected = Assert.IsType<VideoPresentation>(await session.ReadPresentationAsync());
        Assert.Equal(new MediaTime(40, 1000), selected.PositionedFrame.Time);
    }

    [Fact]
    [SuppressMessage("ReSharper", "DisposeOnUsingVariable", Justification = "The test races close with disposal after source failure while keeping automatic cleanup on assertion failure.")]
    public async Task SourceFailureIsObservableAndCannotMasqueradeAsEof()
    {
        var failure = new InvalidDataException("Corrupted fixture timeline.");
        var source = new FakeVideoFrameSource(0) { ReadFailure = failure };
        await using var session = new VideoPlaybackSession(_ => source);

        var openError = await Assert.ThrowsAnyAsync<Exception>(() => session.OpenAsync());

        Assert.Same(failure, openError);
        Assert.Equal(VideoPlaybackState.FAULTED, session.Snapshot.State);
        Assert.Same(failure, session.Snapshot.Error);
        var readError = await Assert.ThrowsAsync<InvalidOperationException>(() => session.ReadPresentationAsync().AsTask());
        Assert.Same(failure, readError.InnerException);
        await Task.WhenAll(session.CloseAsync(), session.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(VideoPlaybackState.CLOSED, session.Snapshot.State);
        Assert.Same(failure, session.Snapshot.Error);
        Assert.Equal(1, source.DisposeCount);
    }

    [Fact]
    public async Task EmptySourceEndsAndPlayDoesNotAutomaticallyRewind()
    {
        var source = new FakeVideoFrameSource();
        await using var session = new VideoPlaybackSession(_ => source);
        await session.OpenAsync();
        Assert.Equal(VideoPlaybackState.ENDED, session.Snapshot.State);

        await session.PlayAsync();

        Assert.Equal(VideoPlaybackState.ENDED, session.Snapshot.State);
        Assert.Equal(0, source.CancelCount);
    }

    [Fact]
    public async Task BoundarySeekReportsClampedPositionAndLastFrame()
    {
        var source = new FakeVideoFrameSource(100, 140, 200);
        await using var session = new VideoPlaybackSession(_ => source);
        await session.OpenAsync();
        var before = await session.SeekAsync(MediaTime.Zero);
        Assert.True(before.IsBeforeFirst);
        Assert.Equal(new MediaTime(100, 1000), session.Snapshot.Position);

        var after = await session.SeekAsync(new(100));

        Assert.True(after.ReachedEnd);
        Assert.Null(after.NextFrameTime);
        Assert.Equal(new MediaTime(200, 1000), after.SelectedTime);
        Assert.Equal(new MediaTime(200, 1000), session.Snapshot.Position);
        using var tail = Assert.IsType<VideoPresentation>(await session.ReadPresentationAsync());
        Assert.Equal(after.Generation, tail.Generation);
        await session.PlayAsync();
        Assert.Equal(VideoPlaybackState.ENDED, session.Snapshot.State);
    }

    [Fact]
    public async Task SlowConsumerKeepsOnlyTheBoundedLatestFrameAndReleasesDroppedFrames()
    {
        var source = new FakeVideoFrameSource(0, 40, 80, 120, 160);
        var clock = new ManualPlaybackTimeProvider();
        await using var session = new VideoPlaybackSession(_ => source, clock, presentationCapacity: 1);
        await session.OpenAsync();
        await session.PlayAsync();
        for (var index = 1; index <= 3; index++)
        {
            await EventuallyAsync(() => clock.ActiveTimerCount != 0);
            clock.Advance(TimeSpan.FromMilliseconds(40));
            var expected = new MediaTime(index * 40, 1000);
            await EventuallyAsync(() => session.Snapshot.DisplayTime == expected);
        }

        using var latest = Assert.IsType<VideoPresentation>(await session.ReadPresentationAsync());

        Assert.Equal(new MediaTime(120, 1000), latest.PositionedFrame.Time);
        Assert.All(source.IssuedFrames.Where(frame => frame.Marker < 3), frame => Assert.Equal(1, frame.DisposeCount));
        Assert.Equal(0, source.IssuedFrames.Single(frame => frame.Marker == 3).DisposeCount);
    }

    [Fact]
    public async Task ClockJumpDropsExpiredIntervalsInsteadOfPublishingACatchupBurst()
    {
        var source = new FakeVideoFrameSource(0, 40, 80, 120, 160);
        var clock = new ManualPlaybackTimeProvider();
        await using var session = new VideoPlaybackSession(_ => source, clock);
        await session.OpenAsync();
        using var initial = await session.ReadPresentationAsync();
        await session.PlayAsync();
        await EventuallyAsync(() => clock.ActiveTimerCount != 0);

        clock.Advance(TimeSpan.FromMilliseconds(110));
        using var selected = Assert.IsType<VideoPresentation>(await session.ReadPresentationAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(new MediaTime(80, 1000), selected.PositionedFrame.Time);
        Assert.Equal(new MediaTime(120, 1000), selected.PositionedFrame.NextFrameTime);
        Assert.Equal(new MediaTime(110, 1000), session.Snapshot.Position);
        Assert.Equal(1, source.IssuedFrames.Single(frame => frame.Marker == 1).DisposeCount);
    }

    [Fact]
    public async Task CloseInterruptsClockAndConsumerWaitsWithoutAdvancingTime()
    {
        var source = new FakeVideoFrameSource(0, 40000, 80000);
        var clock = new ManualPlaybackTimeProvider();
        await using var session = new VideoPlaybackSession(_ => source, clock);
        await session.OpenAsync();
        using var initial = await session.ReadPresentationAsync();
        await session.PlayAsync();
        await EventuallyAsync(() => clock.ActiveTimerCount != 0);
        var waiting = session.ReadPresentationAsync().AsTask();

        await session.CloseAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, clock.ActiveTimerCount);
        Assert.Equal(0, clock.GetTimestamp());
        Assert.Equal(1, source.DisposeCount);
    }

    private static async Task EventuallyAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            await Task.Delay(1, timeout.Token);
        }
    }
}
