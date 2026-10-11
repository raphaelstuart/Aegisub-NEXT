using System.Collections.Concurrent;
using AegiNext.Core.Timing;
using AegiNext.Desktop.Controllers;
using AegiNext.Desktop.Settings;

namespace AegiNext.Desktop.Tests.Controllers;

public sealed class CachedInteractivePreviewTests
{
    [Fact]
    public async Task CachedDeliveryDoesNotCompleteSeekOrReplaceItsPresentationGeneration()
    {
        var inner = new PreviewTestSource(10, 0, 40, 80, 120, 160);
        var source = new BlockingPreviewSeekSource(inner);
        var converter = new CachedPreviewTestConverter();
        var dispatcher = new PreviewTestDispatcher();
        var updates = new ConcurrentQueue<VideoPreviewUpdate>();
        var transient = new TaskCompletionSource<VideoPreviewUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var controller = Create(source, converter, dispatcher, update =>
        {
            updates.Enqueue(update);
            if (update.IsTransientPreview)
            {
                transient.TrySetResult(update);
            }
        });
        await OpenInteractiveAsync(controller);
        source.BlockNextSeek();
        try
        {
            var seek = controller.SeekInteractiveAsync(new(60, 1000));
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var cached = await transient.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new MediaTime(60, 1000), cached.RequestedPosition);
            Assert.Equal(MediaTime.Zero, cached.SourceFrameTime);
            Assert.Equal(new MediaTime(40, 1000), cached.SourceFrameEnd);
            Assert.Equal(200, cached.Frame!.Pixels.Span[0]);
            Assert.Null(cached.Snapshot.PresentedGeneration);
            Assert.Null(controller.Snapshot.PresentedGeneration);
            Assert.False(seek.IsCompleted);

            dispatcher.BlockNextDispatch();
            source.Release();
            await dispatcher.Queued.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(seek.IsCompleted);
            Assert.Null(controller.Snapshot.PresentedGeneration);
            dispatcher.RunPending();
            await seek.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new MediaTime(40, 1000), controller.Snapshot.PresentedFrameTime);
            Assert.NotNull(controller.Snapshot.PresentedGeneration);
            Assert.Single(updates, update => update.IsTransientPreview);
            Assert.Contains(updates, update => !update.IsTransientPreview && update.Frame?.Pixels.Span[0] == 11);
            var captured = Assert.Single(converter.ExactStates);
            Assert.Equal(new MediaTime(60, 1000), captured.TargetTime);
            Assert.True(captured.EvaluateAtTarget);
            Assert.Equal(new MediaTime(60, 1000), Assert.Single(converter.CachedStates).TargetTime);
            await controller.CloseAsync();
            AssertReleased(inner, converter);
        }
        finally
        {
            source.Release();
            dispatcher.RunPending();
        }
    }

    [Fact]
    public async Task FinalInteractiveSeekWaitsForTheExactFrameAfterCachedDragDelivery()
    {
        var inner = new PreviewTestSource(10, 0, 40, 80, 120, 160);
        var source = new BlockingPreviewSeekSource(inner);
        var converter = new CachedPreviewTestConverter();
        var dispatcher = new PreviewTestDispatcher();
        var updates = new ConcurrentQueue<VideoPreviewUpdate>();
        var transient = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var controller = Create(source, converter, dispatcher, update =>
        {
            updates.Enqueue(update);
            if (update.IsTransientPreview)
            {
                transient.TrySetResult();
            }
        });
        await OpenInteractiveAsync(controller);
        source.BlockNextSeek();
        try
        {
            var drag = controller.SeekInteractiveAsync(new(60, 1000));
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await transient.Task.WaitAsync(TimeSpan.FromSeconds(5));
            converter.Interactive = false;
            var completion = controller.CompleteInteractiveSeekAsync(new(120, 1000), false);
            Assert.False(completion.IsCompleted);
            Assert.Null(controller.Snapshot.PresentedGeneration);
            dispatcher.BlockNextDispatch();
            source.Release();
            await dispatcher.Queued.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(completion.IsCompleted);
            Assert.Null(controller.Snapshot.PresentedGeneration);
            dispatcher.RunPending();
            await completion.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => drag);
            Assert.Equal(new MediaTime(120, 1000), controller.Snapshot.PresentedFrameTime);
            Assert.Equal(1, converter.CachedCount);
            Assert.Single(updates, update => update.IsTransientPreview);
            Assert.Contains(updates, update => !update.IsTransientPreview && update.Frame?.Pixels.Span[0] == 13);
            Assert.Contains(converter.ExactStates, captured => captured.TargetTime == new MediaTime(120, 1000) &&
                !captured.IsInteractive && captured.EvaluateAtTarget);
            await controller.CloseAsync();
            AssertReleased(inner, converter);
        }
        finally
        {
            source.Release();
            dispatcher.RunPending();
        }
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("pause")]
    [InlineData("close")]
    public async Task InvalidatingCommandsRejectQueuedCachedDelivery(string invalidation)
    {
        var inner = new PreviewTestSource(10, 0, 40, 80, 120, 160);
        var source = new BlockingPreviewSeekSource(inner);
        var converter = new CachedPreviewTestConverter();
        var dispatcher = new PreviewTestDispatcher();
        var updates = new ConcurrentQueue<VideoPreviewUpdate>();
        await using var controller = Create(source, converter, dispatcher, updates.Enqueue);
        await OpenInteractiveAsync(controller);
        source.BlockNextSeek();
        dispatcher.BlockNextDispatch();
        try
        {
            var seek = controller.SeekInteractiveAsync(new(60, 1000));
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await dispatcher.Queued.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, converter.CachedCount);
            Assert.DoesNotContain(updates, update => update.IsTransientPreview);
            Task command;
            if (invalidation == "revision")
            {
                controller.InvalidatePreview();
                command = Task.CompletedTask;
            }
            else
            {
                command = invalidation == "pause" ? controller.PauseAsync() : controller.CloseAsync();
            }
            dispatcher.RunPending();
            Assert.DoesNotContain(updates, update => update.IsTransientPreview);
            source.Release();
            await AwaitCompletedOrSupersededAsync(seek);
            await command.WaitAsync(TimeSpan.FromSeconds(5));
            await controller.CloseAsync();
            Assert.DoesNotContain(updates, update => update.IsTransientPreview);
            AssertReleased(inner, converter);
        }
        finally
        {
            source.Release();
            dispatcher.RunPending();
        }
    }

    [Fact]
    public async Task ReplacingBlockedCachedConversionKeepsOneInFlightOperationOnTheConversionWorker()
    {
        var inner = new PreviewTestSource(10, 0, 40, 80, 120, 160);
        var source = new BlockingPreviewSeekSource(inner);
        var converter = new CachedPreviewTestConverter();
        var dispatcher = new PreviewTestDispatcher();
        var updates = new ConcurrentQueue<VideoPreviewUpdate>();
        await using var controller = Create(source, converter, dispatcher, updates.Enqueue);
        await OpenInteractiveAsync(controller);
        source.BlockNextSeek();
        converter.BlockNextCachedConversion();
        try
        {
            var previous = controller.SeekInteractiveAsync(new(60, 1000));
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await converter.CachedEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var latest = controller.SeekInteractiveAsync(new(120, 1000));
            source.Release();
            Assert.False(latest.IsCompleted);
            Assert.Equal(1, converter.CachedCount);
            converter.ReleaseCachedConversion();
            await latest.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => previous);
            Assert.Equal(1, converter.CachedCount);
            Assert.DoesNotContain(updates, update => update.IsTransientPreview);
            Assert.Equal(new MediaTime(120, 1000), controller.Snapshot.PresentedFrameTime);
            await controller.CloseAsync();
            AssertReleased(inner, converter);
        }
        finally
        {
            converter.ReleaseCachedConversion();
            source.Release();
            dispatcher.RunPending();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QualityInvalidationCannotPresentTheOldCapturedStateUnderTheNewRevision(bool completing)
    {
        var inner = new PreviewTestSource(10, 0, 40, 80, 120, 160);
        var source = new BlockingPreviewSeekSource(inner);
        var converter = new CachedPreviewTestConverter { Interactive = !completing };
        var dispatcher = new PreviewTestDispatcher();
        var updates = new ConcurrentQueue<VideoPreviewUpdate>();
        var transient = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var controller = Create(source, converter, dispatcher, update =>
        {
            updates.Enqueue(update);
            if (update.IsTransientPreview)
            {
                transient.TrySetResult();
            }
        });
        await OpenInteractiveAsync(controller);
        source.BlockNextSeek();
        try
        {
            var seek = completing
                ? controller.CompleteInteractiveSeekAsync(new(60, 1000), false)
                : controller.SeekInteractiveAsync(new(60, 1000));
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (!completing)
            {
                await transient.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            updates.Clear();
            converter.UpdateQuality(PreviewQuality.HIGH, 1);
            controller.InvalidatePreview();
            source.Release();
            await seek.WaitAsync(TimeSpan.FromSeconds(5));
            if (!completing)
            {
                await EventuallyAsync(() => inner.IssuedFrames.Any(frame => frame.Marker == 11 && frame.DisposeCount == 1));
                Assert.Empty(converter.ExactStates);
                Assert.DoesNotContain(updates, update => update.Frame is not null);
                Assert.Null(controller.Snapshot.PresentedGeneration);
                await controller.RefreshPausedPreviewAsync().WaitAsync(TimeSpan.FromSeconds(5));
                await EventuallyAsync(() => controller.Snapshot.PresentedGeneration is not null);
            }

            var captured = Assert.Single(converter.ExactStates);
            Assert.Equal(PreviewQuality.HIGH, captured.Quality);
            Assert.Equal(1, captured.QualityRevision);
            Assert.Equal(new MediaTime(60, 1000), captured.TargetTime);
            Assert.True(captured.EvaluateAtTarget);
            Assert.Equal(2, source.SeekTargets.Count);
            Assert.Single(updates, update => update.Frame is not null);
            Assert.Equal(new MediaTime(40, 1000), controller.Snapshot.PresentedFrameTime);
            await controller.CloseAsync();
            AssertReleased(inner, converter);
        }
        finally
        {
            source.Release();
            dispatcher.RunPending();
        }
    }

    private static VideoPreviewController Create(BlockingPreviewSeekSource source, CachedPreviewTestConverter converter,
        PreviewTestDispatcher dispatcher, Action<VideoPreviewUpdate> present)
    {
        return new((_, _) => Task.FromResult(new VideoPreviewMedia(0, MediaTime.Zero, new(1))),
            (_, _) => new(_ => source), () => converter, dispatcher.DispatchAsync, present);
    }

    private static async Task OpenInteractiveAsync(VideoPreviewController controller)
    {
        await controller.OpenAsync("cached-interactive.mkv");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (controller.Snapshot.PresentedFrameTime != MediaTime.Zero)
        {
            await Task.Delay(1, timeout.Token);
        }
        await controller.BeginInteractiveSeekAsync();
    }

    private static async Task AwaitCompletedOrSupersededAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(1, timeout.Token);
        }
    }

    private static void AssertReleased(PreviewTestSource source, CachedPreviewTestConverter converter)
    {
        Assert.Equal(1, source.DisposeCount);
        Assert.All(source.IssuedFrames, frame => Assert.Equal(1, frame.DisposeCount));
        Assert.Equal(1, converter.DisposeCount);
        Assert.Equal(1, converter.MaximumActiveCount);
        Assert.Single(converter.WorkerThreadIds.Distinct());
    }
}
