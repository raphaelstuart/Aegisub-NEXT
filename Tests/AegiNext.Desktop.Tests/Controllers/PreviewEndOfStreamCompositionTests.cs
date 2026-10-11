using System.Collections.Concurrent;
using AegiNext.Core.Projects;
using AegiNext.Core.Timing;
using AegiNext.Desktop.Controllers;
using AegiNext.Desktop.Rendering;
using AegiNext.Desktop.Settings;
using AegiNext.Media.Playback;

namespace AegiNext.Desktop.Tests.Controllers;

public sealed class PreviewEndOfStreamCompositionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task PlaybackEndUsesTheLastFrameTimeAfterAnExactSeek(bool resumeWithSeek, bool changeQualityDuringPlayback)
    {
        var target = new MediaTime(1, 4);
        var document = new ProjectDocument
        {
            Width = 1,
            Height = 1,
            Layers = [new()
            {
                Kind = LayerKind.SHAPE,
                Shape = new(ShapeKind.RECTANGLE, 1, 1),
                Fill = new(1, 0, 0),
                End = new(1)
            }]
        };
        var state = new ProjectPreviewState(document, Path.GetTempPath(), target);
        var catalog = new PreviewFrameCatalog();
        var clock = new ManualPlaybackTimeProvider();
        var source = new PreviewTestSource(10, 0, 1000, 2000);
        var renderingErrors = new ConcurrentQueue<Exception>();
        var updates = new ConcurrentQueue<VideoPreviewUpdate>();
        var firstFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource<VideoPreviewUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var controller = new VideoPreviewController(
            (_, _) => Task.FromResult(new VideoPreviewMedia(0, MediaTime.Zero, new(3))),
            (_, _) => new(_ => source, clock),
            () => new ProjectPreviewConverter(() => Volatile.Read(ref state), error =>
            {
                if (error is not null)
                {
                    renderingErrors.Enqueue(error);
                }
            }, catalog, createConverter: _ => new PreviewTestConverter()),
            (action, token) =>
            {
                token.ThrowIfCancellationRequested();
                action();
                return Task.CompletedTask;
            }, update =>
            {
                updates.Enqueue(update);
                if (update.Frame is not null)
                {
                    firstFrame.TrySetResult();
                }
                if (update.Frame is not null && update.Snapshot.State == VideoPlaybackState.ENDED)
                {
                    ended.TrySetResult(update);
                }
            });

        await controller.OpenAsync("seek-to-playback-end.mkv");
        await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await controller.CompleteInteractiveSeekAsync(target, resumeWithSeek).WaitAsync(TimeSpan.FromSeconds(5));
        var seekFrame = updates.Last(update => update.Frame is not null);
        Assert.Equal(target, catalog.FindIdentity(seekFrame.Frame!)!.Time);
        Assert.Equal(255, seekFrame.Frame!.Pixels.Span[2]);
        if (!resumeWithSeek)
        {
            await controller.PlayAsync();
        }
        if (changeQualityDuringPlayback)
        {
            Volatile.Write(ref state, state with { Quality = PreviewQuality.HIGH, QualityRevision = 1 });
            controller.InvalidatePreview();
        }

        await clock.WaitForScheduledTimerAsync().WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(5));
        var final = await ended.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var identity = Assert.IsType<PreviewFrameRecord>(catalog.FindIdentity(final.Frame!));
        Assert.Equal(new MediaTime(2), final.Snapshot.PresentedFrameTime);
        Assert.Equal(new MediaTime(2), identity.Time);
        Assert.Equal(changeQualityDuringPlayback ? 1 : 0, identity.QualityRevision);
        Assert.Equal(12, final.Frame!.Pixels.Span[0]);
        Assert.Equal(0, final.Frame.Pixels.Span[2]);
        Assert.Empty(renderingErrors);
        Assert.Null(controller.Snapshot.Error);
        await controller.CloseAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, source.DisposeCount);
        Assert.All(source.IssuedFrames, frame => Assert.Equal(1, frame.DisposeCount));
    }
}
