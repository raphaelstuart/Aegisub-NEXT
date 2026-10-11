using AegiNext.Core.Timing;
using AegiNext.Desktop.Controllers;
using AegiNext.Media.Audio;
using AegiNext.Media.Playback;

namespace AegiNext.Desktop.Tests.Controllers;

public sealed class InteractivePreviewSessionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IntermediateSeeksLeaveAudioPausedAndOnlyFinalSeekRepositionsIt(bool resume)
    {
        var output = new PreviewAudioOutput { ClockQuality = AudioClockQuality.SYSTEM };
        var audio = new PreviewAudioSource();
        await using var controller = new VideoPreviewController(
            (_, _) => Task.FromResult(new VideoPreviewMedia(0, MediaTime.Zero, new(6), 1)),
            (_, _, clock) => new(_ => new PreviewTestSource(10, 0, 1000, 2000, 3000, 4000, 5000), externalPosition: clock),
            () => new PreviewTestConverter(), Dispatch, _ => { },
            (_, _, position, _) => Task.FromResult(new AudioPlaybackSession(audio, output, position)));
        await controller.OpenAsync("interactive.mkv");
        if (resume)
        {
            await controller.PlayAsync();
        }
        await controller.BeginInteractiveSeekAsync();
        var pauses = output.PauseCount;
        var clears = output.ClearCount;
        var audioPosition = audio.LastSeek;
        await controller.SeekInteractiveAsync(new(2)).WaitAsync(TimeSpan.FromSeconds(5));
        await controller.SeekInteractiveAsync(new(3)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(VideoPlaybackState.PAUSED, controller.Snapshot.State);
        Assert.True(output.Paused);
        Assert.Equal(pauses, output.PauseCount);
        Assert.Equal(clears, output.ClearCount);
        Assert.Equal(audioPosition, audio.LastSeek);
        await controller.CompleteInteractiveSeekAsync(new(4), resume).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new MediaTime(4), audio.LastSeek);
        Assert.Equal(resume ? VideoPlaybackState.PLAYING : VideoPlaybackState.PAUSED, controller.Snapshot.State);
        Assert.Equal(!resume, output.Paused);
    }

    [Fact]
    public async Task FinalSeekWaitsForPresentationBeforeResumingAudio()
    {
        var output = new PreviewAudioOutput();
        var dispatcher = new PreviewTestDispatcher();
        await using var controller = new VideoPreviewController(
            (_, _) => Task.FromResult(new VideoPreviewMedia(0, MediaTime.Zero, new(6), 1)),
            (_, _, clock) => new(_ => new PreviewTestSource(10, 0, 1000, 2000, 3000, 4000, 5000), externalPosition: clock),
            () => new PreviewTestConverter(), dispatcher.DispatchAsync, _ => { },
            (_, _, position, _) => Task.FromResult(new AudioPlaybackSession(new PreviewAudioSource(), output, position)));
        await controller.OpenAsync("interactive-dispatch.mkv");
        await controller.BeginInteractiveSeekAsync();
        dispatcher.BlockNextDispatch();
        var completion = controller.CompleteInteractiveSeekAsync(new(4), true);
        await dispatcher.Queued.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.False(completion.IsCompleted);
            Assert.True(output.Paused);
        }
        finally
        {
            dispatcher.RunPending();
        }
        await completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(output.Paused);
    }

    private static Task Dispatch(Action action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        action();
        return Task.CompletedTask;
    }
}
