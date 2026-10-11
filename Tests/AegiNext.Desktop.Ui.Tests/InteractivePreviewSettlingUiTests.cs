using AegiNext.Core.Timing;
using AegiNext.Desktop.Controls;
using AegiNext.Desktop.Settings;
using AegiNext.Media.Playback;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;

namespace AegiNext.Desktop.Ui.Tests;

public sealed class InteractivePreviewSettlingUiTests
{
    [AvaloniaFact]
    public async Task HeldPointerSettlesAtFullQualityThenResumesInteractiveQualityAndFinallyPlayback()
    {
        await using var context = new MainWindowTestContext();
        context.Session.UpdatePreferences(context.Session.Preferences with { PreviewQuality = PreviewQuality.HIGH });
        await context.OpenMediaAsync();
        await context.Controller.PlayAsync();
        var timeline = UiTestActions.Find<SubtitleTimelineControl>(context.Window, "Timeline");
        context.Window.UpdateLayout();
        var first = timeline.TranslatePoint(new(timeline.HeaderWidth + timeline.PixelsPerSecond * 5, 8), context.Window)!.Value;
        var last = first + new Vector(timeline.PixelsPerSecond * 5, 0);
        context.Window.MouseDown(first, MouseButton.Left);
        try
        {
            await DrainAsync(() => !context.Session.GetPreviewState().IsInteractive &&
                context.Controller.Snapshot.PresentedAtPosition == new MediaTime(5));
            Assert.True(timeline.IsSeeking);
            Assert.Equal(VideoPlaybackState.PAUSED, context.Controller.Snapshot.State);
            Assert.Equal(new PixelSize(1920, 1080),
                UiTestActions.Find<EffectCanvasControl>(context.Window, "EffectCanvas").MaximumPreviewSize);
            context.Window.MouseMove(last);
            await DrainAsync(() => context.Session.GetPreviewState().IsInteractive &&
                context.Controller.Snapshot.PresentedAtPosition == new MediaTime(10));
            Assert.Equal(VideoPlaybackState.PAUSED, context.Controller.Snapshot.State);
        }
        finally
        {
            context.Window.MouseUp(last, MouseButton.Left);
        }
        await DrainAsync(() => context.Controller.Snapshot.State == VideoPlaybackState.PLAYING);
        Assert.Equal(new MediaTime(10), context.Controller.Snapshot.Position);
        Assert.False(context.Session.GetPreviewState().IsInteractive);
    }

    [AvaloniaFact]
    public async Task InteractiveTargetBurstUpdatesPositionWithoutRefreshingSceneForEveryInput()
    {
        await using var context = new MainWindowTestContext();
        await context.OpenMediaAsync();
        var timeline = UiTestActions.Find<SubtitleTimelineControl>(context.Window, "Timeline");
        context.Window.UpdateLayout();
        var first = timeline.TranslatePoint(new(timeline.HeaderWidth + timeline.PixelsPerSecond, 8), context.Window)!.Value;
        var last = first + new Vector(timeline.PixelsPerSecond * 4, 0);
        context.Window.MouseDown(first, MouseButton.Left);
        var scenes = 0;
        context.Session.InteractionDiagnostics.Enabled = true;
        var refreshes = 0;
        context.Session.InteractionDiagnostics.Recorded += value =>
        {
            if (value.Stage == "refresh")
            {
                refreshes++;
            }
        };
        context.ViewModel.Preview.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == "Scene")
            {
                scenes++;
            }
        };
        try
        {
            for (var index = 1; index <= 100; index++)
            {
                await context.ViewModel.Timeline.SeekAsync(new(100 + index * 4, 100));
            }
            Assert.Equal(new MediaTime(5), context.Session.ProjectPosition);
            Assert.Equal(new MediaTime(5), context.ViewModel.Timeline.Position);
            Assert.Equal(0, scenes);
            Assert.Equal(0, refreshes);
            await DrainAsync(() => context.Controller.Snapshot.PresentedAtPosition == new MediaTime(5));
            Assert.InRange(scenes, 1, 5);
        }
        finally
        {
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
}
