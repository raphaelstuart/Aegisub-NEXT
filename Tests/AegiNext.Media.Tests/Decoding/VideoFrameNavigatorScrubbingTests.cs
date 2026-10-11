using AegiNext.Core.Timing;
using AegiNext.Media.Decoding;
using AegiNext.Media.Playback;

namespace AegiNext.Media.Tests.Decoding;

public sealed class VideoFrameNavigatorScrubbingTests
{
    [Fact]
    public void NearbyForwardSeekConsumesLookaheadWithoutRepeatingTheGop()
    {
        var decoder = new FakeVideoDecoder(0, 40, 80, 120, 160, 200);
        using (var navigator = new VideoFrameNavigator(_ => decoder))
        {
            using var first = navigator.SeekFrame(new(40, 1000));
            var reads = decoder.ReadCount;
            using var next = Assert.IsType<PositionedVideoFrame>(navigator.SeekFrame(new(120, 1000)));
            Assert.Equal(new MediaTime(120, 1000), next.Time);
            Assert.Equal(new MediaTime(160, 1000), next.NextFrameTime);
            Assert.Equal(3, next.Frame.CopyPlane(0)[0]);
            Assert.Single(decoder.SeekTargets);
            Assert.Equal(2, decoder.ReadCount - reads);
            Assert.Equal(0, decoder.CancelCount);
        }

        Assert.All(decoder.IssuedFrames, frame => Assert.Equal(1, frame.DisposeCount));
    }

    [Fact]
    public void NearbyForwardSeekStillMergesDuplicatePtsAndPreservesItsLookahead()
    {
        var decoder = new FakeVideoDecoder(0, 40, 80, 80, 160, 200);
        using var navigator = new VideoFrameNavigator(_ => decoder);
        using var first = navigator.SeekFrame(new(40, 1000));
        using var next = Assert.IsType<PositionedVideoFrame>(navigator.SeekFrame(new(90, 1000)));
        Assert.Equal(new MediaTime(80, 1000), next.Time);
        Assert.Equal(3, next.Frame.CopyPlane(0)[0]);
        Assert.Single(decoder.SeekTargets);
        using var after = Assert.IsType<PositionedVideoFrame>(navigator.ReadFrame());
        Assert.Equal(new MediaTime(160, 1000), after.Time);
    }

    [Fact]
    public void ReverseWithinPreviousFrameAndDistantForwardTargetsUsePreciseKeyframeSeeking()
    {
        var decoder = new FakeVideoDecoder(0, 40, 80, 120, 1000, 1040, 2000);
        using var navigator = new VideoFrameNavigator(_ => decoder, maximumCachedFrames: 0);
        using var first = navigator.SeekFrame(new(40, 1000));
        using var sameInterval = Assert.IsType<PositionedVideoFrame>(navigator.SeekFrame(new(50, 1000)));
        Assert.Equal(new MediaTime(40, 1000), sameInterval.Time);
        Assert.Equal(2, decoder.SeekTargets.Count);
        using var distant = Assert.IsType<PositionedVideoFrame>(navigator.SeekFrame(new(1)));
        Assert.Equal(new MediaTime(1), distant.Time);
        Assert.Equal(3, decoder.SeekTargets.Count);
        using var backward = Assert.IsType<PositionedVideoFrame>(navigator.SeekFrame(MediaTime.Zero));
        Assert.Equal(MediaTime.Zero, backward.Time);
        Assert.Equal(4, decoder.SeekTargets.Count);
    }

    [Fact]
    public void AbandoningInsideADuplicatePtsGroupPreservesTheLastFrameAndDecoderOwnership()
    {
        var decoder = new FakeVideoDecoder(0, 40, 80, 80, 80, 160, 200);
        using (var navigator = new VideoFrameNavigator(_ => decoder))
        {
            using var first = navigator.SeekFrame(new(40, 1000));
            var checks = 0;
            Assert.ThrowsAny<OperationCanceledException>(() => navigator.SeekFrame(new(80, 1000), () => ++checks >= 5));
            using var selected = Assert.IsType<PositionedVideoFrame>(navigator.SeekFrame(new(80, 1000)));
            Assert.Equal(4, selected.Frame.CopyPlane(0)[0]);
            Assert.Equal(new MediaTime(160, 1000), selected.NextFrameTime);
            Assert.Single(decoder.SeekTargets);
            Assert.Equal(0, decoder.CancelCount);
            Assert.Equal(0, decoder.DisposeCount);
        }

        Assert.Equal(1, decoder.DisposeCount);
        Assert.All(decoder.IssuedFrames, frame => Assert.Equal(1, frame.DisposeCount));
    }

    [Fact]
    public async Task SupersededActivePrerollStopsBetweenFramesWithoutCancellingOrReopeningTheDecoder()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var block = 0;
        var created = 0;
        var decoder = new FakeVideoDecoder(Enumerable.Range(0, 100).Select(index => (long?)(index * 40)).ToArray())
        {
            FrameFactory = index =>
            {
                if (index == 3 && Interlocked.Exchange(ref block, 0) == 1)
                {
                    entered.TrySetResult();
                    release.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                }

                return new(index * 40, index);
            }
        };
        await using var session = new VideoPlaybackSession(_ => new VideoFrameNavigator(_ =>
        {
            Interlocked.Increment(ref created);
            return decoder;
        }));
        try
        {
            await session.OpenAsync();
            using var opened = await session.ReadPresentationAsync();
            Volatile.Write(ref block, 1);
            var obsolete = session.SeekAsync(new(2));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task<VideoSeekResult>? current = null;
            await Task.Run(() =>
            {
                current = session.SeekAsync(new(80, 1000));
            }).WaitAsync(TimeSpan.FromSeconds(5));
            release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => obsolete);
            await current!.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(decoder.ReadCount <= 12, $"Superseded preroll decoded {decoder.ReadCount} frames; expected it to stop between reads.");
            Assert.Equal(1, created);
            Assert.Equal(0, decoder.CancelCount);
            Assert.Null(session.Snapshot.Error);
            Assert.Equal(VideoPlaybackState.PAUSED, session.Snapshot.State);
            using var result = Assert.IsType<VideoPresentation>(await session.ReadPresentationAsync());
            Assert.Equal(new MediaTime(80, 1000), result.PositionedFrame.Time);
        }
        finally
        {
            release.TrySetResult();
        }
    }
}
