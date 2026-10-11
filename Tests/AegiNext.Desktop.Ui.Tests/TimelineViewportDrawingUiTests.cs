using AegiNext.Core.Projects;
using AegiNext.Core.Timing;
using AegiNext.Desktop.Controls;
using AegiNext.Media.Analysis;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using SkiaSharp;

namespace AegiNext.Desktop.Ui.Tests;

public sealed class TimelineViewportDrawingUiTests
{
    [AvaloniaTheory]
    [InlineData(1d, false)]
    [InlineData(1.5d, false)]
    [InlineData(2d, false)]
    [InlineData(1d, true)]
    [InlineData(1.5d, true)]
    [InlineData(2d, true)]
    public async Task ContinuousNavigationDrawsImmediatelyAndCachesTheSamePixelsAfterSettling(double scaling, bool dark)
    {
        using var environment = new UiTestEnvironment();
        var cue = new SubtitleLine { Start = new(1), End = new(4), Text = "字幕 ABC 123" };
        var layer = new ProjectLayer
        {
            SubtitleId = cue.Id, Start = cue.Start, End = cue.End,
            Tracks = [new(AnimationProperty.OPACITY, [new(new(0), 0.2), new(new(2), 0.8)])]
        };
        using var timeline = new SubtitleTimelineControl();
        timeline.SetDocument(new() { Subtitles = [cue], Layers = [layer] }, cue.Id, layer);
        timeline.SetSpectrogram(new(128, 128, MediaTime.Zero, new(1, 8),
            [.. Enumerable.Range(0, 128 * 128).Select(index => (byte)(index % 256))]));
        var waveform = new WaveformData(new(MediaTime.Zero, 8192, 128),
            [.. Enumerable.Range(0, 128).SelectMany(index => new[] { -0.2f - index % 5 * 0.1f, 0.3f + index % 7 * 0.1f })]);
        timeline.SetWaveform(waveform, null, waveform.End);
        var window = new Window
        {
            Width = 640, Height = 240, Content = timeline,
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light
        };
        window.Show();
        try
        {
            window.SetRenderScaling(scaling);
            window.UpdateLayout();
            timeline.SetViewport(new(0, 100), 120);
            using (Capture(timeline, scaling))
            {
                Assert.InRange(timeline.CachedDrawingBytes, 1, TimelineDrawingCache.MAX_CONTROL_BYTES);
            }

            var point = new Point(timeline.HeaderWidth + 100, timeline.RulerHeight + 10);
            for (var index = 0; index < 6; index++)
            {
                var previous = timeline.Viewport;
                window.MouseWheel(point, index % 2 == 0 ? new(-1, 0) : new(0, 1),
                    index % 2 == 0 ? RawInputModifiers.None : RawInputModifiers.Control);
                await Task.Delay(30, TestContext.Current.CancellationToken);
                Dispatcher.UIThread.RunJobs();
                using var frame = Capture(timeline, scaling);
                Assert.NotEqual(previous, timeline.Viewport);
                Assert.Equal(0, timeline.CachedDrawingBytes);
                timeline.Position = new(index + 1, 10);
            }

            using var navigating = Capture(timeline, scaling);
            await Task.Delay(150, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
            using var settled = Capture(timeline, scaling);
            Assert.InRange(timeline.CachedDrawingBytes, 1, TimelineDrawingCache.MAX_CONTROL_BYTES);
            AssertEquivalent(navigating, settled);
            var builds = timeline.StaticDrawingBuildCount;
            for (var frame = 0; frame < 4; frame++)
            {
                timeline.Position = new(frame + 1);
                using var image = Capture(timeline, scaling);
                Assert.Equal(builds, timeline.StaticDrawingBuildCount);
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task DetachingDuringNavigationReleasesDrawingAndReattachmentCanCacheImmediately()
    {
        using var environment = new UiTestEnvironment();
        using var timeline = new SubtitleTimelineControl();
        var window = new Window { Width = 640, Height = 240, Content = timeline };
        window.Show();
        try
        {
            window.UpdateLayout();
            timeline.SetViewport(new(0, 100), 120);
            window.MouseWheel(new(timeline.HeaderWidth + 100, timeline.RulerHeight + 10), new(-1, 0));
            using (Capture(timeline, 1))
            {
                Assert.Equal(0, timeline.CachedDrawingBytes);
            }
            window.Content = null;
            Assert.Equal(0, timeline.CachedDrawingBytes);
            Assert.False(timeline.IsViewportDrawingDeferred);
            window.Content = timeline;
            window.UpdateLayout();
            using (Capture(timeline, 1))
            {
                Assert.InRange(timeline.CachedDrawingBytes, 1, TimelineDrawingCache.MAX_CONTROL_BYTES);
            }
            var builds = timeline.StaticDrawingBuildCount;
            await Task.Delay(150, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
            using var settled = Capture(timeline, 1);
            Assert.Equal(builds, timeline.StaticDrawingBuildCount);
            window.Content = null;
            Assert.Equal(0, timeline.CachedDrawingBytes);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void DisposingDuringNavigationStopsDeferredDrawing()
    {
        using var environment = new UiTestEnvironment();
        var timeline = new SubtitleTimelineControl();
        var window = new Window { Width = 640, Height = 240, Content = timeline };
        window.Show();
        try
        {
            window.UpdateLayout();
            timeline.SetViewport(new(0, 100), 120);
            window.MouseWheel(new(timeline.HeaderWidth + 100, timeline.RulerHeight + 10), new(-1, 0));
            Assert.True(timeline.IsViewportDrawingDeferred);
            timeline.Dispose();
            Assert.False(timeline.IsViewportDrawingDeferred);
            Assert.Equal(0, timeline.CachedDrawingBytes);
        }
        finally
        {
            window.Close();
            timeline.Dispose();
        }
    }

    private static SKBitmap Capture(SubtitleTimelineControl timeline, double scaling)
    {
        using var target = new RenderTargetBitmap(new((int)Math.Ceiling(timeline.Bounds.Width * scaling),
            (int)Math.Ceiling(timeline.Bounds.Height * scaling)));
        using (var drawing = target.CreateDrawingContext())
        {
            using var transform = drawing.PushTransform(Matrix.CreateScale(scaling, scaling));
            timeline.Render(drawing);
        }
        using var stream = new MemoryStream();
        target.Save(stream, PngBitmapEncoderOptions.Default);
        stream.Position = 0;
        return SKBitmap.Decode(stream);
    }

    private static void AssertEquivalent(SKBitmap expected, SKBitmap actual)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        for (var y = 0; y < expected.Height; y++)
        {
            for (var x = 0; x < expected.Width; x++)
            {
                var first = expected.GetPixel(x, y);
                var second = actual.GetPixel(x, y);
                var difference = Math.Max(Math.Abs(first.Alpha - second.Alpha), Math.Max(Math.Abs(first.Red - second.Red),
                    Math.Max(Math.Abs(first.Green - second.Green), Math.Abs(first.Blue - second.Blue))));
                Assert.True(difference <= 3, $"Pixel ({x}, {y}): navigating {first}, settled {second}.");
            }
        }
    }
}
