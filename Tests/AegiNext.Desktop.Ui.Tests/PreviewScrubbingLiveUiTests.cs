using AegiNext.Core.Timing;
using AegiNext.Desktop.Controls;
using AegiNext.Desktop.Panels.Preview;
using AegiNext.Desktop.Settings;
using AegiNext.Media.Playback;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AegiNext.Desktop.Ui.Tests;

public sealed class PreviewScrubbingLiveUiTests
{
    [AvaloniaTheory]
    [InlineData(false, PreviewQuality.LOW, false)]
    [InlineData(true, PreviewQuality.LOW, false)]
    [InlineData(false, PreviewQuality.LOWEST, false)]
    [InlineData(true, PreviewQuality.LOWEST, false)]
    [InlineData(false, PreviewQuality.LOW, true)]
    [InlineData(true, PreviewQuality.LOW, true)]
    [InlineData(false, PreviewQuality.LOWEST, true)]
    [InlineData(true, PreviewQuality.LOWEST, true)]
    public async Task VideoPresentsIntermediateFramesWhilePointerRemainsPressedAndCanReturnToStart(bool useProgressBar,
        PreviewQuality quality, bool playing)
    {
        await using var context = new MainWindowTestContext();
        context.Session.UpdatePreferences(context.Session.Preferences with { PreviewQuality = quality });
        await context.OpenMediaAsync();
        await DrainAsync(() => context.Controller.Snapshot.PresentedGeneration is not null);
        if (playing)
        {
            await context.Controller.PlayAsync();
        }
        var initialGeneration = context.Controller.Snapshot.PresentedGeneration!.Value;
        var timeline = UiTestActions.Find<SubtitleTimelineControl>(context.Window, "Timeline");
        var slider = UiTestActions.Find<Slider>(context.Window, "PositionSlider");
        context.Window.UpdateLayout();
        var thumb = slider.GetVisualDescendants().OfType<Thumb>().Single();
        var start = useProgressBar
            ? thumb.TranslatePoint(new(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), context.Window)!.Value
            : timeline.TranslatePoint(new(timeline.HeaderWidth + 1, 8), context.Window)!.Value;
        var end = start + new Vector(useProgressBar ? (slider.Bounds.Width - thumb.Bounds.Width) * 0.6 : timeline.PixelsPerSecond * 10, 0);
        context.Window.MouseDown(start, MouseButton.Left);
        try
        {
            context.Window.MouseMove(end);
            var target = context.Session.ProjectPosition;
            Assert.True(target >= new MediaTime(5));
            await DrainAsync(() => context.Controller.Snapshot.PresentedAtPosition == target);
            Assert.Equal(VideoPlaybackState.PAUSED, context.Controller.Snapshot.State);
            Assert.True(context.Controller.Snapshot.PresentedGeneration > initialGeneration);
            Assert.True(context.Controller.Snapshot.PresentedFrameTime >= new MediaTime(5));
            Assert.True(useProgressBar ? context.ViewModel.Preview.IsScrubbing : context.ViewModel.Timeline.IsSeeking);
            Assert.True(context.ViewModel.Preview.Scene.IsInteractive);
            Assert.Equal(quality, context.ViewModel.Preview.Scene.Quality);
            var expectedSize = quality == PreviewQuality.LOWEST ? new PixelSize(568, 320) : new PixelSize(960, 540);
            Assert.Equal(expectedSize, UiTestActions.Find<EffectCanvasControl>(context.Window, "EffectCanvas").MaximumPreviewSize);
            context.Clock.Advance(TimeSpan.FromMilliseconds(200));
            Assert.Equal(target, context.Controller.Snapshot.Position);
            context.Window.MouseMove(start);
            var returned = context.Session.ProjectPosition;
            Assert.InRange((double)returned.Numerator / returned.Denominator, 0, 0.1);
            await DrainAsync(() => context.Controller.Snapshot.PresentedAtPosition == returned);
            Assert.True(useProgressBar ? context.ViewModel.Preview.IsScrubbing : context.ViewModel.Timeline.IsSeeking);
        }
        finally
        {
            context.Window.MouseUp(start, MouseButton.Left);
        }
        await DrainAsync(() => !context.ViewModel.Preview.Scene.IsInteractive);
        Assert.False(context.ViewModel.Preview.IsScrubbing);
        Assert.False(context.ViewModel.Timeline.IsSeeking);
        Assert.Equal(quality, context.Session.Preferences.PreviewQuality);
        await DrainAsync(() => context.Controller.Snapshot.PresentedAtPosition is { } final && final <= new MediaTime(1, 10));
        Assert.Equal(playing ? VideoPlaybackState.PLAYING : VideoPlaybackState.PAUSED, context.Controller.Snapshot.State);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContinuousMovesWaitForTheIntermediateConversionToPresentBeforeSeekingTheLatestTarget(bool playing)
    {
        using var converter = new CatchupPreviewConverter();
        var source = new PreviewTestSource(1, 0, 5000, 10000, 15000, 20000);
        await using var context = new MainWindowTestContext(videoSourceFactory: () => source, videoConverterFactory: () => converter);
        await context.OpenMediaAsync();
        await DrainAsync(() => context.Controller.Snapshot.PresentedGeneration is not null);
        if (playing)
        {
            await context.Controller.PlayAsync();
        }
        converter.Block(2);
        var delivered = new List<MediaTime>();
        context.Session.PreviewUpdated += (_, update) =>
        {
            if (update.Frame is not null && update.Snapshot.PresentedFrameTime is { } time)
            {
                delivered.Add(time);
            }
        };
        var timeline = UiTestActions.Find<SubtitleTimelineControl>(context.Window, "Timeline");
        context.Window.UpdateLayout();
        var first = timeline.TranslatePoint(new(timeline.HeaderWidth + timeline.PixelsPerSecond * 5, 8), context.Window)!.Value;
        var last = first + new Vector(timeline.PixelsPerSecond * 5, 0);
        var seeks = source.SeekCount + (playing ? 1 : 0);
        context.Window.MouseDown(first, MouseButton.Left);
        try
        {
            await DrainAsync(() => converter.Entered.Task.IsCompleted);
            for (var index = 1; index <= 20; index++)
            {
                context.Window.MouseMove(first + (last - first) * (index / 20d));
                await Task.Delay(5, TestContext.Current.CancellationToken);
                Dispatcher.UIThread.RunJobs();
            }
            Assert.Equal(new MediaTime(10), context.Session.ProjectPosition);
            Assert.Equal(seeks + 1, source.SeekCount);
            Assert.True(timeline.IsSeeking);
            converter.Release();
            await DrainAsync(() => delivered.Contains(new(10)));
            Assert.Contains(new MediaTime(5), delivered);
            Assert.Equal(seeks + 2, source.SeekCount);
        }
        finally
        {
            converter.Release();
            context.Window.MouseUp(last, MouseButton.Left);
        }
        await DrainAsync(() => !timeline.IsSeeking && context.Controller.Snapshot.Position == new MediaTime(10));
        Assert.Equal(playing ? VideoPlaybackState.PLAYING : VideoPlaybackState.PAUSED, context.Controller.Snapshot.State);
    }

    [AvaloniaFact]
    public async Task QualityChangeDuringBlockedInteractiveConversionAllowsTheLatestTargetToPresent()
    {
        using var converter = new CatchupPreviewConverter();
        await using var context = new MainWindowTestContext(videoConverterFactory: () => converter);
        await context.OpenMediaAsync();
        await DrainAsync(() => context.Controller.Snapshot.PresentedGeneration is not null);
        converter.Block(2);
        var timeline = UiTestActions.Find<SubtitleTimelineControl>(context.Window, "Timeline");
        context.Window.UpdateLayout();
        var first = timeline.TranslatePoint(new(timeline.HeaderWidth + timeline.PixelsPerSecond * 5, 8), context.Window)!.Value;
        var last = first + new Vector(timeline.PixelsPerSecond * 5, 0);
        context.Window.MouseDown(first, MouseButton.Left);
        try
        {
            await DrainAsync(() => converter.Entered.Task.IsCompleted);
            context.Window.MouseMove(last);
            var selector = UiTestActions.Find<ComboBox>(context.Window, "QualityCombo");
            selector.SelectedItem = selector.Items.OfType<PreviewQualityChoice>().Single(choice => choice.Id == PreviewQuality.HIGH);
            await DrainAsync(() => context.Controller.Snapshot.PresentedFrameTime == new MediaTime(10));
            Assert.True(timeline.IsSeeking);
            Assert.True(context.ViewModel.Preview.Scene.IsInteractive);
            Assert.Equal(PreviewQuality.HIGH, context.Session.Preferences.PreviewQuality);
        }
        finally
        {
            converter.Release();
            context.Window.MouseUp(last, MouseButton.Left);
        }
        await DrainAsync(() => !timeline.IsSeeking && context.Controller.Snapshot.Position == new MediaTime(10));
        Assert.False(context.ViewModel.Preview.Scene.IsInteractive);
    }

    [AvaloniaFact]
    public async Task ReleasingDuringBlockedConversionSupersedesTheWaitAndPresentsTheExactFinalTarget()
    {
        using var converter = new CatchupPreviewConverter();
        await using var context = new MainWindowTestContext(videoConverterFactory: () => converter);
        await context.OpenMediaAsync();
        await DrainAsync(() => context.Controller.Snapshot.PresentedGeneration is not null);
        converter.Block(2);
        var timeline = UiTestActions.Find<SubtitleTimelineControl>(context.Window, "Timeline");
        context.Window.UpdateLayout();
        var first = timeline.TranslatePoint(new(timeline.HeaderWidth + timeline.PixelsPerSecond * 5, 8), context.Window)!.Value;
        var last = first + new Vector(timeline.PixelsPerSecond * 5, 0);
        context.Window.MouseDown(first, MouseButton.Left);
        try
        {
            await DrainAsync(() => converter.Entered.Task.IsCompleted);
            context.Window.MouseMove(last);
        }
        finally
        {
            context.Window.MouseUp(last, MouseButton.Left);
        }
        await DrainAsync(() => context.Controller.Snapshot.PresentedFrameTime == new MediaTime(10));
        Assert.False(context.ViewModel.Preview.Scene.IsInteractive);
        Assert.False(timeline.IsSeeking);
        Assert.Equal(new MediaTime(10), context.Session.ProjectPosition);
    }

    [AvaloniaFact]
    public async Task AHundredPlayingTimelineMovesCoalesceBehindOneNativeSeekWithoutBlockingUiInput()
    {
        await using var context = new SeekSchedulingTestContext();
        await context.OpenMediaAsync();
        await context.Controller.PlayAsync();
        context.Source.SeekTargets.Clear();
        var timeline = UiTestActions.Find<SubtitleTimelineControl>(context.Window, "Timeline");
        context.Window.UpdateLayout();
        var firstPoint = timeline.TranslatePoint(new(timeline.HeaderWidth + timeline.PixelsPerSecond * 5, 8), context.Window)!.Value;
        var finalPoint = timeline.TranslatePoint(new(timeline.HeaderWidth + timeline.PixelsPerSecond * 10, 8), context.Window)!.Value;
        var blocked = context.Source.BlockNextSeek(new(5));
        context.Window.MouseDown(firstPoint, MouseButton.Left);
        try
        {
            await DrainAsync(() => blocked.Entered.IsCompleted);
            var readsBefore = context.Source.ReadCount;
            context.Source.SeekTargets.Clear();
            for (var index = 1; index <= 100; index++)
            {
                context.Window.MouseMove(firstPoint + (finalPoint - firstPoint) * (index / 100d));
            }
            Assert.Equal(new MediaTime(10), context.Window.Session.ProjectPosition);
            Assert.True(timeline.IsSeeking);
            Assert.Empty(context.Source.SeekTargets);
            Assert.Equal(readsBefore, context.Source.ReadCount);
            var heartbeat = false;
            Dispatcher.UIThread.Post(() => heartbeat = true, DispatcherPriority.Input);
            Dispatcher.UIThread.RunJobs();
            Assert.True(heartbeat);
            Assert.Empty(context.Source.SeekTargets);

            blocked.Release();
            await DrainAsync(() => context.Controller.Snapshot.PresentedFrameTime == new MediaTime(10) &&
                context.Controller.Snapshot.State == VideoPlaybackState.PAUSED);
            Assert.Equal([new MediaTime(10)], context.Source.SeekTargets.ToArray());
        }
        finally
        {
            blocked.Release();
            context.Window.MouseUp(finalPoint, MouseButton.Left);
        }
        await DrainAsync(() => context.Source.SeekTargets.Count == 2 && !timeline.IsSeeking &&
            context.Controller.Snapshot.State == VideoPlaybackState.PLAYING);
        Assert.Equal([new MediaTime(10), new(10)], context.Source.SeekTargets.ToArray());
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SynchronizingTheBoundSliderWhileCapturedDoesNotSubmitATransportSeek(bool playing)
    {
        var source = new PreviewTestSource(1, 0, 5000, 10000, 15000, 20000);
        await using var context = new MainWindowTestContext(videoSourceFactory: () => source);
        await context.OpenMediaAsync();
        if (playing)
        {
            await context.Controller.PlayAsync();
        }
        var slider = UiTestActions.Find<Slider>(context.Window, "PositionSlider");
        context.Window.UpdateLayout();
        var thumb = slider.GetVisualDescendants().OfType<Thumb>().Single();
        var point = thumb.TranslatePoint(new(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), context.Window)!.Value;
        var position = context.Session.ProjectPosition;
        context.Window.MouseDown(point, MouseButton.Left);
        try
        {
            await DrainAsync(() => context.Controller.Snapshot.State == VideoPlaybackState.PAUSED);
            var seekCount = source.SeekCount;
            context.ViewModel.Preview.Position = 4;
            Dispatcher.UIThread.RunJobs();
            Assert.True(context.ViewModel.Preview.IsScrubbing);
            Assert.Equal(position, context.Session.ProjectPosition);
            Assert.Equal(seekCount, source.SeekCount);
            context.ViewModel.CancelGestures();
        }
        finally
        {
            context.Window.MouseUp(point, MouseButton.Left);
        }
        Dispatcher.UIThread.RunJobs();
        await DrainAsync(() => context.Controller.Snapshot.State == VideoPlaybackState.PAUSED);
        Assert.Equal(position, context.Controller.Snapshot.Position);
    }

    [AvaloniaFact]
    public async Task ScrollingThePlayingTimelineViewportDoesNotReadOrSeekVideo()
    {
        var source = new PreviewTestSource(1, 0, 5000, 10000, 15000, 20000);
        await using var context = new MainWindowTestContext(videoSourceFactory: () => source);
        await context.OpenMediaAsync();
        await context.Controller.PlayAsync();
        context.ViewModel.Timeline.PixelsPerSecond = 120;
        var timeline = UiTestActions.Find<SubtitleTimelineControl>(context.Window, "Timeline");
        context.Window.UpdateLayout();
        var point = timeline.TranslatePoint(new(timeline.HeaderWidth + 100, 8), context.Window)!.Value;
        var before = timeline.ViewStart;
        var reads = source.ReadCount;
        var seeks = source.SeekCount;
        for (var index = 0; index < 100; index++)
        {
            context.Window.MouseWheel(point, new(0, -1), RawInputModifiers.Shift);
        }
        Dispatcher.UIThread.RunJobs();
        Assert.True(timeline.ViewStart > before);
        Assert.False(timeline.IsSeeking);
        Assert.Equal(reads, source.ReadCount);
        Assert.Equal(seeks, source.SeekCount);
        Assert.Equal(VideoPlaybackState.PLAYING, context.Controller.Snapshot.State);
    }

    private static async Task DrainAsync(Func<bool> completed)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!completed())
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Assert.True(DateTime.UtcNow < deadline, "The preview must present the target before mouse release.");
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }
}
