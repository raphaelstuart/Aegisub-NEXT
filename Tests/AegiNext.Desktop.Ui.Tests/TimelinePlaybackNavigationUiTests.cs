using AegiNext.Core.Timing;
using AegiNext.Desktop.Controls;
using AegiNext.Desktop.Shortcuts;
using AegiNext.Media.Playback;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace AegiNext.Desktop.Ui.Tests;

public sealed class TimelinePlaybackNavigationUiTests
{
    [AvaloniaTheory]
    [InlineData("Horizontal")]
    [InlineData("Vertical")]
    [InlineData("Zoom")]
    [InlineData("Overview")]
    public async Task ManualNavigationSuspendsPlaybackFollowWithoutSeekingOrEditing(string navigation)
    {
        await using var context = new MainWindowTestContext();
        await context.OpenMediaAsync();
        for (var track = 0; track < 20; track++)
        {
            context.Session.Editor.AddTrack($"Track {track}");
        }
        var timeline = UiTestActions.Find<SubtitleTimelineControl>(context.Window, "Timeline");
        context.ViewModel.Timeline.PixelsPerSecond = 200;
        await context.Controller.PlayAsync();
        context.Session.Tick();
        var source = context.Session.DocumentSnapshot;
        var seeks = 0;
        timeline.SeekRequested += (_, _) => seeks++;
        var point = timeline.TranslatePoint(new(timeline.HeaderWidth + 100, timeline.RulerHeight + 10), context.Window)!.Value;

        switch (navigation)
        {
            case "Horizontal":
                context.Window.MouseWheel(point, new(-16, 0), RawInputModifiers.None);
                break;
            case "Vertical":
                context.Window.MouseWheel(point, new(0, -2), RawInputModifiers.None);
                break;
            case "Zoom":
                context.Window.MouseWheel(point, new(0, 3), RawInputModifiers.Control);
                break;
            case "Overview":
                var overview = UiTestActions.Find<TimelineOverviewControl>(context.Window, "TimelineMinimap");
                var origin = overview.TranslatePoint(overview.ViewportRectangle.Center, context.Window)!.Value;
                var target = origin + new Vector(60, 0);
                context.Window.MouseDown(origin, MouseButton.Left);
                context.Window.MouseMove(target);
                context.Window.MouseUp(target, MouseButton.Left);
                break;
        }

        var navigated = context.ViewModel.Timeline.Viewport;
        Assert.False(context.ViewModel.Timeline.IsPlaybackFollowEnabled);
        using (var image = new RenderTargetBitmap(new((int)timeline.Bounds.Width, (int)timeline.Bounds.Height)))
        {
            image.Render(timeline);
            Assert.Equal(0, timeline.CachedDrawingBytes);
        }
        for (var tick = 0; tick < 4; tick++)
        {
            context.Clock.Advance(TimeSpan.FromMilliseconds(125));
            context.Session.Tick();
            Assert.Equal(navigated, context.ViewModel.Timeline.Viewport);
        }
        Assert.Equal(VideoPlaybackState.PLAYING, context.Controller.Snapshot.State);
        Assert.Equal(new MediaTime(1, 2), context.ViewModel.Timeline.Position);
        Assert.Equal(0, seeks);
        Assert.Same(source, context.Session.DocumentSnapshot);
    }

    [AvaloniaTheory]
    [InlineData(WorkbenchCommand.PLAY_PAUSE)]
    [InlineData(WorkbenchCommand.TIMING_ENTER)]
    public async Task StartingPlaybackOrTimingResumesFollowAfterManualNavigation(WorkbenchCommand command)
    {
        await using var context = new MainWindowTestContext();
        await context.OpenMediaAsync();
        var timeline = UiTestActions.Find<SubtitleTimelineControl>(context.Window, "Timeline");
        context.ViewModel.Timeline.PixelsPerSecond = 200;
        var point = timeline.TranslatePoint(new(timeline.HeaderWidth + 100, timeline.RulerHeight + 10), context.Window)!.Value;
        context.Window.MouseWheel(point, new(-16, 0), RawInputModifiers.None);
        Assert.False(context.ViewModel.Timeline.IsPlaybackFollowEnabled);

        await context.Session.ExecuteCommandAsync(command);
        Assert.True(context.ViewModel.Timeline.IsPlaybackFollowEnabled);
        if (command == WorkbenchCommand.TIMING_ENTER)
        {
            Assert.Single(context.Session.DocumentSnapshot.Subtitles);
        }
        else
        {
            Assert.Equal(VideoPlaybackState.PLAYING, context.Controller.Snapshot.State);
            context.Session.Tick();
            Assert.Equal(0, context.ViewModel.Timeline.ViewStart);
        }
    }

    [AvaloniaTheory]
    [InlineData("Resize")]
    [InlineData("HeaderCollapse")]
    [InlineData("CommandCollapse")]
    public async Task LayoutChangesKeepPlaybackFollowEnabled(string layout)
    {
        await using var context = new MainWindowTestContext();
        await context.OpenMediaAsync();
        await context.Controller.PlayAsync();

        var timeline = UiTestActions.Find<SubtitleTimelineControl>(context.Window, "Timeline");
        if (layout == "Resize")
        {
            context.Window.Width += 100;
        }
        else
        {
            var trackId = context.Session.DocumentSnapshot.Tracks[0].Id;
            if (layout == "HeaderCollapse")
            {
                var rectangle = timeline.GetTrackHeaderRectangle(trackId)!.Value;
                var point = timeline.TranslatePoint(new(14, rectangle.Center.Y), context.Window)!.Value;
                context.Window.MouseDown(point, MouseButton.Left);
                context.Window.MouseUp(point, MouseButton.Left);
            }
            else
            {
                timeline.ToggleTrackCollapse(trackId);
            }
            Assert.True(timeline.IsTrackCollapsed(trackId));
        }
        context.Window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        context.Session.Tick();

        Assert.True(context.ViewModel.Timeline.IsPlaybackFollowEnabled);
    }
}
