using System.Collections.Concurrent;
using AegiNext.Core.Timing;
using AegiNext.Desktop.Controllers;
using AegiNext.Media.Audio;
using AegiNext.Media.Decoding;
using AegiNext.Media.Playback;

namespace AegiNext.Desktop.Tests.Controllers;

public sealed class VideoPreviewDecodeModeTests
{
    [Fact]
    public async Task SelectionWithoutMediaIsUsedByTheNextOpen()
    {
        VideoDecoderOptions? received = null;
        await using var controller = new VideoPreviewController(Probe,
            (_, _, _, options) =>
            {
                received = options;
                return new(_ => new PreviewTestSource(10, 0, 100));
            }, () => new PreviewTestConverter(), Dispatch, _ => { });

        await controller.SwitchDecodeModeAsync(VideoDecodeMode.Software);
        Assert.Null(controller.Snapshot.FilePath);
        Assert.Equal(VideoDecodeMode.Software, controller.DecodeMode);
        await controller.OpenAsync("next.mp4");
        Assert.Equal(VideoDecodeMode.Software, received!.Mode);
    }

    [Fact]
    public async Task SwitchWaitsForAConvertedFrameAndPreservesPausedPositionVolumeAndMute()
    {
        var sources = new ConcurrentQueue<PreviewTestSource>();
        var converters = new ConcurrentQueue<PreviewTestConverter>();
        var updates = new ConcurrentQueue<VideoPreviewUpdate>();
        var blocked = new PreviewTestConverter();
        blocked.BlockNextConversion();
        var opens = 0;
        await using var controller = new VideoPreviewController(Probe,
            (_, _, _, _) =>
            {
                var source = new PreviewTestSource(checked((byte)(Interlocked.Increment(ref opens) * 10)), 0, 100, 200);
                sources.Enqueue(source);
                return new(_ => source);
            }, () =>
            {
                var converter = converters.IsEmpty ? new PreviewTestConverter() : blocked;
                converters.Enqueue(converter);
                return converter;
            }, Dispatch, updates.Enqueue);
        try
        {
            await controller.OpenAsync("paused.mp4");
            await Eventually(() => updates.Any(update => update.Frame is not null));
            await controller.SeekAsync(new(150, 1000));
            await Eventually(() => controller.Snapshot.PresentedFrameTime == new MediaTime(100, 1000));
            controller.SetVolume(0.37F);
            controller.SetMuted(true);
            var epoch = controller.Snapshot.Epoch;

            var switching = controller.SwitchDecodeModeAsync(VideoDecodeMode.Hardware);
            await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(switching.IsCompleted);
            Assert.Equal(VideoDecodeMode.Auto, controller.DecodeMode);
            Assert.Equal(new MediaTime(150, 1000), controller.Snapshot.Position);
            blocked.Release();
            await switching.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(VideoDecodeMode.Hardware, controller.DecodeMode);
            Assert.Equal(VideoPlaybackState.PAUSED, controller.Snapshot.State);
            Assert.Equal(new MediaTime(150, 1000), controller.Snapshot.Position);
            Assert.Equal(0.37F, controller.Snapshot.Volume);
            Assert.True(controller.Snapshot.IsMuted);
            Assert.Equal(21, updates.Last(update => update.Frame is not null).Frame!.Pixels.Span[0]);
            Assert.DoesNotContain(updates, update => update.Snapshot.Epoch > epoch && update.Frame?.Pixels.Span[0] == 20);
            Assert.Equal(1, sources.First().DisposeCount);
        }
        finally
        {
            blocked.Release();
        }
    }

    [Fact]
    public async Task FailureDuringFirstConversionRestoresThePreviousModeAndPosition()
    {
        var updates = new ConcurrentQueue<VideoPreviewUpdate>();
        var opens = new ConcurrentQueue<VideoDecodeMode>();
        var failedConverter = new PreviewTestConverter
        {
            ConversionWork = marker =>
            {
                if (marker >= 20)
                {
                    throw new NotSupportedException("Hardware frame layout cannot be converted.");
                }
            }
        };
        var converterCount = 0;
        await using var controller = new VideoPreviewController(Probe,
            (_, _, _, options) =>
            {
                opens.Enqueue(options.Mode);
                return new(_ => new PreviewTestSource(options.Mode == VideoDecodeMode.Hardware ? (byte)20 : (byte)10,
                    0, 100, 200));
            }, () => Interlocked.Increment(ref converterCount) == 2 ? failedConverter : new PreviewTestConverter(),
            Dispatch, updates.Enqueue);
        await controller.OpenAsync("failure.mp4");
        await Eventually(() => controller.Snapshot.PresentedGeneration is not null);
        await controller.SeekAsync(new(150, 1000));

        var failure = await Assert.ThrowsAsync<NotSupportedException>(
            () => controller.SwitchDecodeModeAsync(VideoDecodeMode.Hardware).WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Contains("layout", failure.Message, StringComparison.Ordinal);
        Assert.Equal([VideoDecodeMode.Auto, VideoDecodeMode.Hardware, VideoDecodeMode.Auto], opens.ToArray());
        Assert.Equal(VideoDecodeMode.Auto, controller.DecodeMode);
        Assert.Equal(VideoPlaybackState.PAUSED, controller.Snapshot.State);
        Assert.Equal(new MediaTime(150, 1000), controller.Snapshot.Position);
        Assert.Null(controller.Snapshot.Error);
        Assert.Equal(11, updates.Last(update => update.Frame is not null).Frame!.Pixels.Span[0]);
    }

    [Fact]
    public async Task HardwareOpenFailureRestoresPlayingAndTheAudioGain()
    {
        var outputs = new ConcurrentQueue<PreviewAudioOutput>();
        var audioSources = new ConcurrentQueue<PreviewAudioSource>();
        var clock = new ManualPlaybackTimeProvider();
        await using var controller = new VideoPreviewController(
            (_, _) => Task.FromResult(new VideoPreviewMedia(0, MediaTime.Zero, new(2), 1)),
            (_, _, position, options) => options.Mode == VideoDecodeMode.Hardware
                ? throw new NotSupportedException("No supported GPU decoder.")
                : new(_ => new PreviewTestSource(10, 0, 100, 200, 1000), clock, externalPosition: position),
            () => new PreviewTestConverter(), Dispatch, _ => { },
            (_, _, position, _) =>
            {
                var source = new PreviewAudioSource();
                var output = new PreviewAudioOutput();
                audioSources.Enqueue(source);
                outputs.Enqueue(output);
                return Task.FromResult(new AudioPlaybackSession(source, output, position));
            });
        await controller.OpenAsync("playing.mp4");
        await controller.SeekAsync(new(150, 1000));
        controller.SetVolume(0.27F);
        controller.SetMuted(true);
        await controller.PlayAsync();

        await Assert.ThrowsAsync<NotSupportedException>(
            () => controller.SwitchDecodeModeAsync(VideoDecodeMode.Hardware).WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(VideoDecodeMode.Auto, controller.DecodeMode);
        Assert.Equal(VideoPlaybackState.PLAYING, controller.Snapshot.State);
        Assert.Equal(new MediaTime(150, 1000), controller.Snapshot.Position);
        Assert.Equal(0.27F, controller.Snapshot.Volume);
        Assert.True(controller.Snapshot.IsMuted);
        Assert.Equal(0, outputs.Last().Gain);
        Assert.False(outputs.Last().Paused);
        Assert.Equal(new MediaTime(150, 1000), audioSources.Last().LastSeek);
        Assert.Equal(1, outputs.First().DisposeCount);
    }

    [Fact]
    public async Task CloseCancelsASwitchAwaitingItsFirstPresentation()
    {
        var blocked = new PreviewTestConverter();
        blocked.BlockNextConversion();
        var converters = 0;
        await using var controller = new VideoPreviewController(Probe,
            (_, _, _, _) => new(_ => new PreviewTestSource(10, 0, 100, 200)),
            () => Interlocked.Increment(ref converters) == 2 ? blocked : new PreviewTestConverter(), Dispatch, _ => { });
        try
        {
            await controller.OpenAsync("closing.mp4");
            // Open may finish before the initial conversion worker creates its converter.
            await Eventually(() => controller.Snapshot.PresentedGeneration is not null);
            var switching = controller.SwitchDecodeModeAsync(VideoDecodeMode.Hardware);
            await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var closing = controller.CloseAsync();
            blocked.Release();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => switching.WaitAsync(TimeSpan.FromSeconds(5)));
            await closing.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(VideoPlaybackState.CLOSED, controller.Snapshot.State);
        }
        finally
        {
            blocked.Release();
        }
    }

    [Fact]
    public async Task ReplacingTheFileDuringASwitchRejectsItsLateFrameWithoutRestoringTheOldFile()
    {
        var blocked = new PreviewTestConverter();
        blocked.BlockNextConversion();
        var converters = 0;
        var updates = new ConcurrentQueue<VideoPreviewUpdate>();
        await using var controller = new VideoPreviewController(Probe,
            (path, _, _, options) => new(_ => new PreviewTestSource(Path.GetFileName(path) == "new.mp4"
                ? (byte)30 : options.Mode == VideoDecodeMode.Hardware ? (byte)20 : (byte)10, 0, 100, 200)),
            () => Interlocked.Increment(ref converters) == 2 ? blocked : new PreviewTestConverter(), Dispatch, updates.Enqueue);
        try
        {
            await controller.OpenAsync("old.mp4");
            await Eventually(() => controller.Snapshot.PresentedGeneration is not null);
            await controller.SeekAsync(new(150, 1000));
            var switching = controller.SwitchDecodeModeAsync(VideoDecodeMode.Hardware);
            await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var replacing = controller.OpenAsync("new.mp4");
            blocked.Release();
            await replacing.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => switching.WaitAsync(TimeSpan.FromSeconds(5)));
            await Eventually(() => updates.Any(update => update.Frame?.Pixels.Span[0] == 30));

            Assert.Equal("new.mp4", Path.GetFileName(controller.Snapshot.FilePath));
            Assert.Equal(VideoDecodeMode.Auto, controller.DecodeMode);
            Assert.DoesNotContain(updates, update => update.Frame?.Pixels.Span[0] is 20 or 21);
        }
        finally
        {
            blocked.Release();
        }
    }

    [Fact]
    public async Task CancellingTheSwitchRestoresThePreviousModeAndPausedPosition()
    {
        var blocked = new PreviewTestConverter();
        blocked.BlockNextConversion();
        var converters = 0;
        await using var controller = new VideoPreviewController(Probe,
            (_, _, _, _) => new(_ => new PreviewTestSource(10, 0, 100, 200)),
            () => Interlocked.Increment(ref converters) == 2 ? blocked : new PreviewTestConverter(), Dispatch, _ => { });
        try
        {
            await controller.OpenAsync("cancelled.mp4");
            await Eventually(() => controller.Snapshot.PresentedGeneration is not null);
            await controller.SeekAsync(new(150, 1000));
            using var cancellation = new CancellationTokenSource();
            var switching = controller.SwitchDecodeModeAsync(VideoDecodeMode.Hardware, cancellation.Token);
            await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await cancellation.CancelAsync();
            blocked.Release();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => switching.WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.Equal(VideoDecodeMode.Auto, controller.DecodeMode);
            Assert.Equal(VideoPlaybackState.PAUSED, controller.Snapshot.State);
            Assert.Equal(new MediaTime(150, 1000), controller.Snapshot.Position);
            Assert.Null(controller.Snapshot.Error);
        }
        finally
        {
            blocked.Release();
        }
    }

    [Fact]
    public async Task ReplacingTheFileDuringRollbackPreservesTheNewFileAndOriginalSwitchFailure()
    {
        var rollbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probes = 0;
        var failure = new NotSupportedException("The requested hardware decoder is unavailable.");
        var updates = new ConcurrentQueue<VideoPreviewUpdate>();
        await using var controller = new VideoPreviewController(async (path, token) =>
            {
                if (Path.GetFileName(path) == "old.mp4" && Interlocked.Increment(ref probes) == 3)
                {
                    rollbackEntered.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }

                return new(0, MediaTime.Zero, new(2));
            }, (path, _, _, options) => options.Mode == VideoDecodeMode.Hardware
                ? throw failure
                : new(_ => new PreviewTestSource(Path.GetFileName(path) == "new.mp4" ? (byte)30 : (byte)10, 0, 100, 200)),
            () => new PreviewTestConverter(), Dispatch, updates.Enqueue);
        await controller.OpenAsync("old.mp4");
        await controller.SeekAsync(new(150, 1000));
        var switching = controller.SwitchDecodeModeAsync(VideoDecodeMode.Hardware);
        await rollbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await controller.OpenAsync("new.mp4").WaitAsync(TimeSpan.FromSeconds(5));
        var result = await Assert.ThrowsAsync<NotSupportedException>(() => switching.WaitAsync(TimeSpan.FromSeconds(5)));
        await Eventually(() => updates.Any(update => update.Frame?.Pixels.Span[0] == 30));

        Assert.Same(failure, result);
        Assert.Equal("new.mp4", Path.GetFileName(controller.Snapshot.FilePath));
        Assert.Equal(VideoDecodeMode.Auto, controller.DecodeMode);
        Assert.Null(controller.Snapshot.Error);
        Assert.Equal(30, updates.Last(update => update.Frame is not null).Frame!.Pixels.Span[0]);
    }

    private static Task<VideoPreviewMedia> Probe(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new VideoPreviewMedia(0, MediaTime.Zero, new(2)));
    }

    private static Task Dispatch(Action action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        action();
        return Task.CompletedTask;
    }

    private static async Task Eventually(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            await Task.Delay(1, timeout.Token);
        }
    }
}
