using AegiNext.Core.Timing;
using AegiNext.Desktop.Controllers;
using AegiNext.Media.Audio;
using AegiNext.Media.Playback;
using AegiNext.Media.Preview;

namespace AegiNext.Desktop.Tests.Controllers;

public sealed class InteractivePreviewOwnershipTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QualityInvalidationDuringFinalDeliveryWaitsForTheReplacementExactFrameBeforeResuming(bool blockDispatch)
    {
        var quality = 0;
        var original = new PreviewTestConverter();
        var replacement = new PreviewTestConverter();
        var converter = new InteractivePreviewQualityConverter(() => Volatile.Read(ref quality), original, replacement);
        var output = new PreviewAudioOutput { ClockQuality = AudioClockQuality.SYSTEM };
        var dispatcher = new PreviewTestDispatcher();
        var replacementPresented = 0;
        var resumedBeforeReplacement = 0;
        await using var controller = Create(converter, dispatcher, output, update =>
        {
            if (update.Frame is { } frame && update.Snapshot.PresentedFrameTime == new MediaTime(4) &&
                converter.GetQuality(frame) == 1)
            {
                Volatile.Write(ref replacementPresented, 1);
            }
        });
        await controller.OpenAsync("interactive-quality-ownership.mkv");
        await EventuallyAsync(() => controller.Snapshot.PresentedGeneration is not null);
        await controller.PlayAsync();
        await controller.BeginInteractiveSeekAsync();
        output.PlaybackStarted = () =>
        {
            if (Volatile.Read(ref replacementPresented) == 0)
            {
                Interlocked.Increment(ref resumedBeforeReplacement);
            }
        };
        if (blockDispatch)
        {
            dispatcher.BlockNextDispatch();
        }
        else
        {
            original.BlockNextConversion();
        }
        var completion = controller.CompleteInteractiveSeekAsync(new(4), true);
        try
        {
            await (blockDispatch ? dispatcher.Queued.Task : original.Entered.Task).WaitAsync(TimeSpan.FromSeconds(5));
            replacement.BlockNextConversion();
            Volatile.Write(ref quality, 1);
            controller.InvalidatePreview();
            original.Release();
            dispatcher.RunPending();
            await replacement.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(completion.IsCompleted);
            Assert.True(output.Paused);
            Assert.Equal(0, Volatile.Read(ref replacementPresented));
            Assert.Equal(0, Volatile.Read(ref resumedBeforeReplacement));
            replacement.Release();
            await completion.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, Volatile.Read(ref replacementPresented));
            Assert.Equal(0, Volatile.Read(ref resumedBeforeReplacement));
            Assert.False(output.Paused);
            Assert.Equal(VideoPlaybackState.PLAYING, controller.Snapshot.State);
        }
        finally
        {
            original.Release();
            replacement.Release();
            dispatcher.RunPending();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ANewerPausePreventsAnOlderFinalDeliveryFromResumingPlayback(bool blockDispatch)
    {
        var converter = new PreviewTestConverter();
        var output = new PreviewAudioOutput { ClockQuality = AudioClockQuality.SYSTEM };
        var dispatcher = new PreviewTestDispatcher();
        var starts = 0;
        output.PlaybackStarted = () => Interlocked.Increment(ref starts);
        await using var controller = Create(converter, dispatcher, output, _ => { });
        await controller.OpenAsync("interactive-pause-ownership.mkv");
        await EventuallyAsync(() => controller.Snapshot.PresentedGeneration is not null);
        await controller.PlayAsync();
        await controller.BeginInteractiveSeekAsync();
        var startsBeforeCompletion = Volatile.Read(ref starts);
        if (blockDispatch)
        {
            dispatcher.BlockNextDispatch();
        }
        else
        {
            converter.BlockNextConversion();
        }
        var completion = controller.CompleteInteractiveSeekAsync(new(4), true);
        try
        {
            await (blockDispatch ? dispatcher.Queued.Task : converter.Entered.Task).WaitAsync(TimeSpan.FromSeconds(5));
            var pause = controller.PauseAsync();
            converter.Release();
            dispatcher.RunPending();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion.WaitAsync(TimeSpan.FromSeconds(5)));
            await pause.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(VideoPlaybackState.PAUSED, controller.Snapshot.State);
            Assert.True(output.Paused);
            Assert.Equal(startsBeforeCompletion, Volatile.Read(ref starts));
        }
        finally
        {
            converter.Release();
            dispatcher.RunPending();
        }
    }

    private static VideoPreviewController Create(IVideoPreviewConverter converter, PreviewTestDispatcher dispatcher,
        PreviewAudioOutput output, Action<VideoPreviewUpdate> present)
    {
        return new(
            (_, _) => Task.FromResult(new VideoPreviewMedia(0, MediaTime.Zero, new(6), 1)),
            (_, _, clock) => new(_ => new PreviewTestSource(10, 0, 1000, 2000, 3000, 4000, 5000), externalPosition: clock),
            () => converter, dispatcher.DispatchAsync, present,
            (_, _, position, _) => Task.FromResult(new AudioPlaybackSession(new PreviewAudioSource(), output, position)));
    }

    private static async Task EventuallyAsync(Func<bool> completed)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!completed())
        {
            await Task.Delay(1, timeout.Token);
        }
    }
}
