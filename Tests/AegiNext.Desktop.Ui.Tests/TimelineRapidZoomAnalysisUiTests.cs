using System.Text.Json;
using AegiNext.Core.Timing;
using AegiNext.Desktop.Controllers;
using AegiNext.Desktop.Controls;
using AegiNext.Desktop.Editing;
using AegiNext.Desktop.Layouts;
using AegiNext.Desktop.Workspace;
using AegiNext.Media.Analysis;
using AegiNext.Media.Audio;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Dock.Model.Controls;
using Dock.Model.Core;
using SkiaSharp;

namespace AegiNext.Desktop.Ui.Tests;

/// <summary>通过真实滚轮输入验证持久化缓存缩放后的双层发布和 Skia 像素覆盖。</summary>
public sealed class TimelineRapidZoomAnalysisUiTests
{
    private static JsonSerializerOptions CaptureJsonOptions { get; } = new()
    {
        WriteIndented = true
    };

    /// <summary>不同片长从窄视口连续缩小到全片，在普通和高 DPI 宽矮时间线中完整绘制缓存双层。</summary>
    [AvaloniaTheory]
    [InlineData(30, 1d)]
    [InlineData(60, 1d)]
    [InlineData(120, 1d)]
    [InlineData(30, 2d)]
    [InlineData(60, 2d)]
    [InlineData(120, 2d)]
    public async Task RapidControlWheelZoomOutPublishesBothCompleteLayersWithoutSeekingOrEditing(int duration, double scaling)
    {
        var playback = new UiAuditionAudioSource();
        var output = new UiAuditionAudioOutput();
        var video = new PreviewTestSource(1, 0, duration * 1000L);
        await using var context = new MainWindowTestContext(
            (_, _, initial, _) => Task.FromResult(new AudioPlaybackSession(playback, output, initial)),
            videoSourceFactory: () => video,
            mediaProbe: (_, _) => Task.FromResult(new VideoPreviewMedia(0, MediaTime.Zero, new(duration), 1,
                VideoWidth: 1, VideoHeight: 1)));
        context.Window.Width = scaling > 1 ? 1464 : 760;
        context.Window.Height = scaling > 1 ? 650 : 680;
        context.Window.SetRenderScaling(scaling);
        ExpandTimelinePane(context);
        context.Window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        context.Window.UpdateLayout();
        await context.Controller.OpenAsync($"rapid-zoom-{duration}.media", TestContext.Current.CancellationToken);
        context.Session.Tick();
        var timeline = UiTestActions.Find<SubtitleTimelineControl>(context.Window, "Timeline");
        var model = context.ViewModel.Timeline;
        Assert.Equal(scaling, model.RenderScaling);
        Assert.True(timeline.Viewport.Width > 0 && timeline.Viewport.Height > 0);
        Assert.Equal(24, timeline.RulerHeight);
        Assert.True(timeline.Viewport.Height >= 240, "频谱像素验收需要完整的标尺高度和足够的音频绘制区域。");
        if (scaling > 1)
        {
            Assert.InRange(timeline.Bounds.Height, 250, 300);
            Assert.True(timeline.Bounds.Width > 1300, "高 DPI 用例需要覆盖截图相近的宽矮时间线。");
        }
        Assert.True(TimelineDrawingCache.CanCache(timeline.Bounds.Size, scaling, 5));
        model.Viewport = timeline.Viewport with
        {
            StartSeconds = duration / 2.0,
            PixelsPerSecond = timeline.Viewport.Width / 32
        };
        model.SuspendPlaybackFollow();
        var source = new UiRapidZoomAudioSource((long)duration * WaveformAnalyzer.SAMPLE_RATE);
        using var coordinator = new AnalysisCoordinator(context.Session,
            (path, _, mapping, mediaDuration, directory, options, budget) => new(_ => source, mapping, mediaDuration, 8L * 1024 * 1024,
                cacheDirectory: directory, cacheIdentity: path,
                detailSourceFactory: _ => new UiRapidZoomAudioSource((long)duration * WaveformAnalyzer.SAMPLE_RATE)));
        try
        {
            await coordinator.StartAsync("synthetic-48k-mono.media");
            await coordinator.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            var narrowWaveform = Assert.IsType<WaveformData>(model.Waveform);
            Assert.NotNull(model.Spectrogram);
            var originalDocument = context.Session.DocumentSnapshot;
            var originalPosition = context.Session.ProjectPosition;
            var playbackSeeks = playback.SeekCount;
            var videoSeeks = video.SeekCount;
            var warmFramesRead = source.FramesRead;
            var pointer = timeline.TranslatePoint(new Point(timeline.HeaderWidth + timeline.Viewport.Width / 2,
                timeline.RulerHeight + timeline.Viewport.Height * 0.85), context.Window)!.Value;
            var analysisSeeks = source.SeekCount;
            var wheelEvents = 1;
            context.Window.MouseWheel(pointer, new(0, -6), RawInputModifiers.Control);
            while (timeline.VisibleDuration < duration)
            {
                Assert.True(wheelEvents < 40, "真实 Ctrl+滚轮没有按预期扩大视口。");
                var scale = timeline.PixelsPerSecond;
                context.Window.MouseWheel(pointer, new(0, -2), RawInputModifiers.Control);
                Assert.True(timeline.PixelsPerSecond < scale);
                wheelEvents++;
            }
            var finalViewport = timeline.Viewport;
            var finalPlan = Assert.IsType<WaveformViewportPlan>(WaveformViewportPlanner.Create(finalViewport, model.RenderScaling, new(duration)));
            Assert.Equal(0, finalViewport.StartSeconds);
            Assert.True(finalViewport.VisibleDuration >= duration);
            Assert.Equal(MediaTime.Zero, finalPlan.Visible.Start);
            Assert.True(finalPlan.Visible.End >= new MediaTime(duration));
            await coordinator.Completion.WaitAsync(TimeSpan.FromSeconds(45), TestContext.Current.CancellationToken);
            await Task.Delay(150, TestContext.Current.CancellationToken);
            context.Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Assert.False(timeline.IsViewportDrawingDeferred);
            var waveform = Assert.IsType<WaveformData>(model.Waveform);
            var spectrum = Assert.IsType<SpectrogramData>(model.Spectrogram);
            Assert.Equal(finalPlan.Analysis, waveform.Request);
            Assert.True(waveform.SamplesPerBucket > narrowWaveform.SamplesPerBucket);
            Assert.Equal(finalViewport, model.Viewport);
            Assert.Equal(string.Empty, model.AnalysisStatus);
            Assert.Null(model.WaveformOverview);
            Assert.Null(model.SpectrogramOverview);
            Assert.True(waveform.Start <= finalPlan.Visible.Start && waveform.End >= finalPlan.Visible.End);
            Assert.True(spectrum.Start <= finalPlan.Visible.Start && spectrum.End >= finalPlan.Visible.End);
            var row = (int)(Math.Log(UiRapidZoomAudioSource.TONE_HERTZ / 40.0) / Math.Log(8000.0 / 40) * spectrum.Height);
            for (var bucket = 0; bucket < waveform.BucketCount; bucket++)
            {
                Assert.True(waveform.Peaks.Span[bucket * 2] < -0.7F && waveform.Peaks.Span[bucket * 2 + 1] > 0.7F);
            }
            for (var column = 0; column < spectrum.Width; column++)
            {
                var center = spectrum.Start + spectrum.ColumnDuration * column + spectrum.ColumnDuration / 2;
                var level = spectrum.Levels.Span[row * spectrum.Width + column];
                if (center < MediaTime.Zero || center >= new MediaTime(duration))
                {
                    Assert.Equal(0, level);
                }
                else
                {
                    Assert.True(level > 150, $"媒体内频谱列 {column}（中心 {center}）没有预期的音频能量。");
                }
            }
            Assert.Equal(originalPosition, context.Session.ProjectPosition);
            Assert.Equal(playbackSeeks, playback.SeekCount);
            Assert.Equal(videoSeeks, video.SeekCount);
            Assert.Same(originalDocument, context.Session.DocumentSnapshot);
            Assert.Equal((long)duration * WaveformAnalyzer.SAMPLE_RATE, warmFramesRead);
            Assert.Equal(warmFramesRead, source.FramesRead);
            Assert.Equal(analysisSeeks, source.SeekCount);
            Assert.Equal(0, source.CancelCount);
            timeline.SetAudioGraphPalette(new()
            {
                UseClassicSpectrum = false,
                AdaptToTheme = false,
                Low = "#000000",
                Mid = "#0000FF",
                High = "#0000FF",
                Waveform = "#FF0000FF"
            });
            using var image = Capture(timeline, scaling);
            Assert.InRange(timeline.CachedDrawingBytes, 1, TimelineDrawingCache.MAX_CONTROL_BYTES);
            var coverage = CountCoverage(image, timeline, duration, scaling);
            Save(context, timeline, image, duration, scaling, wheelEvents, source, finalViewport, waveform, spectrum, coverage);
            Assert.True(coverage.WaveformColumns >= coverage.Columns * 0.95,
                $"最终视口仅 {coverage.WaveformColumns}/{coverage.Columns} 个像素列绘制了波形。");
            Assert.True(coverage.SpectrumColumns >= coverage.Columns * 0.95,
                $"最终视口仅 {coverage.SpectrumColumns}/{coverage.Columns} 个像素列绘制了频谱能量。");
        }
        finally
        {
            await coordinator.ClearAsync();
        }
        Assert.Equal(1, source.DisposeCount);
    }

    private static void ExpandTimelinePane(MainWindowTestContext context)
    {
        var pane = Assert.IsAssignableFrom<IToolDock>(context.Window.Layouts.PanelAdapters[WorkbenchPanelIds.TIMELINE].Owner);
        var split = Assert.IsAssignableFrom<IProportionalDock>(pane.Owner);
        Assert.Equal(Orientation.Vertical, split.Orientation);
        Assert.NotNull(split.VisibleDockables);
        var siblings = split.VisibleDockables.Where(value => value is not ISplitter && !ReferenceEquals(value, pane)).ToArray();
        var remainingProportion = siblings.Sum(value => value.Proportion);
        Assert.True(remainingProportion > 0);
        foreach (var sibling in siblings)
        {
            sibling.Proportion = 0.4 * sibling.Proportion / remainingProportion;
        }
        pane.Proportion = 0.6;
    }

    private static SKBitmap Capture(SubtitleTimelineControl timeline, double scaling)
    {
        var pixels = new PixelSize((int)Math.Ceiling(timeline.Bounds.Width * scaling),
            (int)Math.Ceiling(timeline.Bounds.Height * scaling));
        using var target = new RenderTargetBitmap(pixels);
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

    private static (int Columns, int WaveformColumns, int SpectrumColumns) CountCoverage(SKBitmap image,
        SubtitleTimelineControl timeline, int duration, double scaling)
    {
        var first = (int)Math.Ceiling((timeline.HeaderWidth + 3) * scaling);
        var end = Math.Min(duration, timeline.ViewStart + timeline.VisibleDuration);
        var last = Math.Min(image.Width - (int)Math.Ceiling(3 * scaling),
            (int)Math.Floor((timeline.HeaderWidth + (end - timeline.ViewStart) * timeline.PixelsPerSecond - 3) * scaling));
        var waveformY = (int)Math.Floor((timeline.RulerHeight + timeline.Viewport.Height / 2) * scaling);
        var spectrumTop = (int)Math.Ceiling((timeline.RulerHeight + 1) * scaling);
        var spectrumBottom = (int)Math.Floor((timeline.RulerHeight + timeline.Viewport.Height * 0.18) * scaling);
        var waveformColumns = 0;
        var spectrumColumns = 0;
        Assert.True(last > first && spectrumBottom > spectrumTop);
        for (var x = first; x < last; x++)
        {
            var waveColor = image.GetPixel(x, waveformY);
            if (waveColor.Red > 180 && waveColor.Green < 80 && waveColor.Blue < 80)
            {
                waveformColumns++;
            }
            for (var y = spectrumTop; y < spectrumBottom; y++)
            {
                var spectrumColor = image.GetPixel(x, y);
                if (spectrumColor.Blue > 180 && spectrumColor.Red < 80 && spectrumColor.Green < 80)
                {
                    spectrumColumns++;
                    break;
                }
            }
        }
        return (last - first, waveformColumns, spectrumColumns);
    }

    private static void Save(MainWindowTestContext context, SubtitleTimelineControl timeline, SKBitmap image, int duration, double scaling, int wheelEvents,
        UiRapidZoomAudioSource source, TimelineViewport viewport, WaveformData waveform, SpectrogramData spectrum,
        (int Columns, int WaveformColumns, int SpectrumColumns) coverage)
    {
        var directory = Environment.GetEnvironmentVariable("AEGINEXT_UI_CAPTURE_DIRECTORY");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }
        Assert.True(Path.IsPathFullyQualified(directory));
        Directory.CreateDirectory(directory);
        var name = $"timeline-rapid-zoom-{duration}s" + (scaling > 1 ? $"-{scaling:0}x-wide" : string.Empty);
        using (var encoded = image.Encode(SKEncodedImageFormat.Png, 100))
        using (var stream = File.Create(Path.Combine(directory, name + ".png")))
        {
            encoded.SaveTo(stream);
        }
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var frame = context.Window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(directory, name + "-window.png"), PngBitmapEncoderOptions.Default);
        var values = new Dictionary<string, object>
        {
            ["SyntheticMediaDurationSeconds"] = duration,
            ["VisibleStartSeconds"] = viewport.StartSeconds,
            ["VisibleDurationSeconds"] = viewport.VisibleDuration,
            ["PixelsPerSecond"] = viewport.PixelsPerSecond,
            ["RenderScaling"] = scaling,
            ["WindowWidth"] = context.Window.Bounds.Width,
            ["WindowHeight"] = context.Window.Bounds.Height,
            ["TimelineWidth"] = timeline.Bounds.Width,
            ["TimelineHeight"] = timeline.Bounds.Height,
            ["HeaderWidth"] = timeline.HeaderWidth,
            ["RulerHeight"] = timeline.RulerHeight,
            ["AudioViewportWidth"] = timeline.Viewport.Width,
            ["AudioViewportHeight"] = timeline.Viewport.Height,
            ["CapturePixelWidth"] = image.Width,
            ["CapturePixelHeight"] = image.Height,
            ["DrawingPath"] = TimelineDrawingCache.CanCache(timeline.Bounds.Size, scaling, 5) ? "cache" : "direct",
            ["CachedDrawingBytes"] = timeline.CachedDrawingBytes,
            ["AudioLeftPixel"] = (int)Math.Ceiling(timeline.HeaderWidth * scaling),
            ["MediaRightPixel"] = (int)Math.Floor((timeline.HeaderWidth + (duration - viewport.StartSeconds) * viewport.PixelsPerSecond) * scaling),
            ["WaveformCenterPixelY"] = (int)Math.Floor((timeline.RulerHeight + viewport.Height / 2) * scaling),
            ["SpectrumTopPixelY"] = (int)Math.Ceiling((timeline.RulerHeight + 1) * scaling),
            ["SpectrumBottomPixelY"] = (int)Math.Floor((timeline.RulerHeight + viewport.Height * 0.18) * scaling),
            ["WheelEvents"] = wheelEvents,
            ["SamplesPerBucket"] = waveform.SamplesPerBucket,
            ["SpectrumColumns"] = spectrum.Width,
            ["FramesRead"] = source.FramesRead,
            ["AnalysisSeeks"] = source.SeekCount,
            ["PixelColumns"] = coverage.Columns,
            ["WaveformPixelColumns"] = coverage.WaveformColumns,
            ["SpectrumPixelColumns"] = coverage.SpectrumColumns
        };
        File.WriteAllText(Path.Combine(directory, name + ".json"), JsonSerializer.Serialize(values, CaptureJsonOptions));
    }
}
