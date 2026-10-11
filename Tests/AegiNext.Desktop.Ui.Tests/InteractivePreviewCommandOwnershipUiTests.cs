using AegiNext.Core.Timing;
using AegiNext.Desktop.Controls;
using AegiNext.Desktop.Shortcuts;
using AegiNext.Media.Playback;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;

namespace AegiNext.Desktop.Ui.Tests;

public sealed class InteractivePreviewCommandOwnershipUiTests
{
    [AvaloniaFact]
    public async Task RelativeSeekCommandOwnsTheNewPositionAndOldPointerReleaseCannotRestoreTheDragTarget()
    {
        await using var context = new MainWindowTestContext();
        await context.OpenMediaAsync();
        await context.Controller.PlayAsync();
        var timeline = UiTestActions.Find<SubtitleTimelineControl>(context.Window, "Timeline");
        context.Window.UpdateLayout();
        var point = timeline.TranslatePoint(new(timeline.HeaderWidth + timeline.PixelsPerSecond * 5, 8), context.Window)!.Value;
        context.Window.MouseDown(point, MouseButton.Left);
        try
        {
            await DrainAsync(() => context.Controller.Snapshot.PresentedAtPosition == new MediaTime(5));
            await context.ViewModel.ExecuteCommandAsync(WorkbenchCommand.SEEK_FORWARD);
            await DrainAsync(() => context.Controller.Snapshot.State == VideoPlaybackState.PLAYING &&
                context.Controller.Snapshot.Position == new MediaTime(10));
            Assert.False(context.ViewModel.Timeline.IsSeeking);
            Assert.False(timeline.IsSeeking);
            Assert.Equal(new MediaTime(10), context.Session.ProjectPosition);
        }
        finally
        {
            context.Window.MouseUp(point, MouseButton.Left);
        }

        await PumpForAsync(TimeSpan.FromMilliseconds(200));
        Assert.Equal(new MediaTime(10), context.Session.ProjectPosition);
        Assert.Equal(VideoPlaybackState.PLAYING, context.Controller.Snapshot.State);
        Assert.False(context.Session.GetPreviewState().IsInteractive);
    }

    [AvaloniaFact]
    public async Task CancelAfterReleaseRejectsTheBlockedFinalConversionAndItsPlaybackIntent()
    {
        using var converter = new CatchupPreviewConverter();
        await using var context = new MainWindowTestContext(videoConverterFactory: () => converter);
        await context.OpenMediaAsync();
        await context.Controller.PlayAsync();
        var timeline = UiTestActions.Find<SubtitleTimelineControl>(context.Window, "Timeline");
        context.Window.UpdateLayout();
        var first = timeline.TranslatePoint(new(timeline.HeaderWidth + timeline.PixelsPerSecond * 5, 8), context.Window)!.Value;
        var last = first + new Vector(timeline.PixelsPerSecond * 5, 0);
        context.Window.MouseDown(first, MouseButton.Left);
        try
        {
            await DrainAsync(() => context.Controller.Snapshot.PresentedAtPosition == new MediaTime(5));
            converter.Block(3);
            context.Window.MouseMove(last);
            context.Window.MouseUp(last, MouseButton.Left);
            await DrainAsync(() => converter.Entered.Task.IsCompleted);
            Assert.False(context.ViewModel.Timeline.IsSeeking);
            context.ViewModel.CancelGestures();
            converter.Release();
            await PumpForAsync(TimeSpan.FromMilliseconds(200));

            Assert.Equal(VideoPlaybackState.PAUSED, context.Controller.Snapshot.State);
            Assert.False(context.Session.IsTransportPlaybackRequested);
            Assert.False(context.ViewModel.Preview.IsScrubbing);
            Assert.False(context.Session.GetPreviewState().IsInteractive);
        }
        finally
        {
            converter.Release();
            context.Window.MouseUp(last, MouseButton.Left);
        }
    }

    private static async Task DrainAsync(Func<bool> completed)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!completed())
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Assert.True(DateTime.UtcNow < deadline);
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }

    private static async Task PumpForAsync(TimeSpan duration)
    {
        var deadline = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }
}
