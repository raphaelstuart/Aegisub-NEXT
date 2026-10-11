using System.Diagnostics;
using AegiNext.Core.Projects;
using AegiNext.Core.Timing;
using AegiNext.Desktop.Controls;
using AegiNext.Desktop.Controllers;
using AegiNext.Desktop.Settings;
using AegiNext.Media.Decoding;
using AegiNext.Media.Playback;
using AegiNext.Media.Preview;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;

namespace AegiNext.Desktop.Ui.Tests;

/// <summary>通过真实窗口手势、工作区、原生解码和工程合成测量往返拖动；显式启用才运行。</summary>
public sealed class NativeTimelineScrubBenchmarks(ITestOutputHelper output)
{
    /// <summary>记录软件及自动后端在空字幕和动画字幕场景的冷启动与五轮暖态拖动。</summary>
    [AvaloniaTheory]
    [InlineData(VideoDecodeMode.Software, false)]
    [InlineData(VideoDecodeMode.Auto, false)]
    [InlineData(VideoDecodeMode.Software, true)]
    [InlineData(VideoDecodeMode.Auto, true)]
    public async Task RealTimelinePointerScrubbingReportsBoundedNativePipeline(VideoDecodeMode mode, bool animated)
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("AEGINEXT_RUN_SCRUB_BENCHMARKS") == "1",
            "需要 AEGINEXT_RUN_SCRUB_BENCHMARKS=1、匹配的原生媒体库及 ffmpeg/ffprobe。输出位于 artifacts/validation/scrub。");
        var directory = ArtifactDirectory();
        var repetitions = int.TryParse(Environment.GetEnvironmentVariable("AEGINEXT_SCRUB_REPETITIONS"), out var supplied) ? supplied : 5;
        Assert.InRange(repetitions, 1, 5);
        foreach (var fixture in await NativeScrubFixture.CreateAsync(directory))
        {
            await MeasureAsync(fixture, directory, mode, animated, repetitions);
        }
    }

    private async Task MeasureAsync(NativeScrubFixture fixture, string directory, VideoDecodeMode mode, bool animated, int repetitions)
    {
        var before = Resources();
        var metrics = new NativeScrubMetrics();
        var context = new NativeScrubWindowContext(metrics);
        string? failure = null;
        try
        {
            context.Window.Session.UpdatePreferences(context.Window.Session.Preferences with { PreviewQuality = PreviewQuality.HIGH });
            await context.Controller.SwitchDecodeModeAsync(mode);
            var opening = Stopwatch.GetTimestamp();
            await context.Window.OpenMediaAsync(fixture.Path, true);
            await WaitAsync(() => context.Controller.Snapshot.PresentedGeneration is not null, context);
            metrics.RecordStage("ColdOpenToFirstAcceptedFrame", opening);
            Assert.Null(context.Controller.Snapshot.Error);
            var duration = context.Controller.Snapshot.Duration;
            Assert.True(duration is { } length && length >= new MediaTime(4), "Scrub fixture must contain at least four seconds of video.");
            if (animated)
            {
                InstallAnimation(context);
                await context.Controller.RefreshPausedPreviewAsync();
            }
            var timeline = UiTestActions.Find<SubtitleTimelineControl>(context.Window, "Timeline");
            var seconds = Math.Min(10, (double)duration!.Value.Numerator / duration.Value.Denominator);
            context.Window.Session.ViewModel.Timeline.Viewport = timeline.Viewport with
            {
                StartSeconds = 0, PixelsPerSecond = timeline.Viewport.Width / seconds
            };
            context.Window.Session.ViewModel.Timeline.SuspendPlaybackFollow();
            context.Window.UpdateLayout();
            await RunRoundAsync(context, metrics, timeline, seconds, 0);
            for (var round = 1; round <= repetitions; round++)
            {
                await RunRoundAsync(context, metrics, timeline, seconds, round);
            }
            Assert.Null(context.RenderingError);
            Assert.Null(context.Controller.Snapshot.Error);
            Assert.True(metrics.PresentationCount > 0);
        }
        catch (Exception error)
        {
            failure = error.ToString();
            throw;
        }
        finally
        {
            await context.DisposeAsync();
            var after = Resources();
            var report = await metrics.WriteReportAsync(directory, fixture.Name, fixture.Path, mode, animated, repetitions,
                context.Source?.LastSessionInfo, context.Converter, before, after, failure);
            output.WriteLine(report);
            Assert.Equal(before, after);
        }
    }

    private static async Task RunRoundAsync(NativeScrubWindowContext context, NativeScrubMetrics metrics,
        SubtitleTimelineControl timeline, double seconds, int round)
    {
        metrics.BeginRound(round);
        var window = context.Window;
        var canvas = UiTestActions.Find<EffectCanvasControl>(window, "EffectCanvas");
        var started = Stopwatch.GetTimestamp();
        var nextRender = started;
        var from = seconds * 0.1;
        var range = seconds * 0.7;
        Pointer("down", from, started);
        Assert.True(timeline.HasActiveDrag);
        var presentationsAtStart = metrics.PresentationCount;
        await MoveAsync(180, index => from + range * (index <= 90 ? index / 90d : (180 - index) / 90d));
        var presentedDuringMovement = metrics.PresentationCount - presentationsAtStart;
        var hold = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(hold) < TimeSpan.FromMilliseconds(400))
        {
            RenderIfDue();
            await Task.Delay(4, TestContext.Current.CancellationToken);
        }
        var stillInteractive = context.Window.Session.GetPreviewState().IsInteractive;
        await MoveAsync(60, index => from + range * index / 60);
        var released = Stopwatch.GetTimestamp();
        Pointer("up", from + range, released);
        Assert.False(timeline.HasActiveDrag);
        var target = timeline.Position;
        var mediaTarget = target + (context.Window.Session.DocumentSnapshot.Media?.MediaOrigin ?? MediaTime.Zero);
        await WaitAsync(() => IsExact(context.Controller.Snapshot, mediaTarget) &&
            !context.Window.Session.GetPreviewState().IsInteractive &&
            context.LastPresentedIdentity is { Interactive: false } identity &&
            identity.QualityRevision == context.Window.Session.GetPreviewState().QualityRevision, context, () => RenderIfDue());
        RenderIfDue(true);
        var completed = Stopwatch.GetTimestamp();
        metrics.FinishRound(started, released, completed, mediaTarget, stillInteractive, presentedDuringMovement, context.Controller.Snapshot);
        Assert.Equal(VideoPlaybackState.PAUSED, context.Controller.Snapshot.State);

        async Task MoveAsync(int count, Func<int, double> position)
        {
            var movementStarted = Stopwatch.GetTimestamp();
            for (var index = 1; index <= count; index++)
            {
                var due = movementStarted + (long)(index * Stopwatch.Frequency / 120d);
                var remaining = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), due);
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining, TestContext.Current.CancellationToken);
                }
                Pointer("move", position(index), due);
                RenderIfDue();
            }
        }

        void Pointer(string kind, double value, long due)
        {
            var point = timeline.TranslatePoint(new Point(timeline.HeaderWidth + (value - timeline.ViewStart) * timeline.PixelsPerSecond,
                timeline.RulerHeight / 2), window) ?? throw new InvalidOperationException("Timeline is not attached to the benchmark window.");
            var inputStarted = Stopwatch.GetTimestamp();
            switch (kind)
            {
                case "down":
                    window.MouseDown(point, MouseButton.Left);
                    break;
                case "up":
                    window.MouseUp(point, MouseButton.Left);
                    break;
                default:
                    window.MouseMove(point);
                    break;
            }
            metrics.RecordInput(kind, inputStarted, timeline.Position, due);
        }

        void RenderIfDue(bool force = false)
        {
            if (force || Stopwatch.GetTimestamp() >= nextRender)
            {
                var drawStarted = Stopwatch.GetTimestamp();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                metrics.RecordStage("HeadlessRenderBarrier", drawStarted);
                metrics.RecordDraw(canvas, context.Controller.Snapshot);
                nextRender = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 60;
            }
        }
    }

    private static bool IsExact(VideoPreviewSnapshot snapshot, MediaTime target) =>
        snapshot.Position == target && snapshot.PresentedAtPosition == target &&
        snapshot.PresentedFrameTime <= target && target < snapshot.PresentedFrameEnd;

    private static async Task WaitAsync(Func<bool> complete, NativeScrubWindowContext context, Action? pump = null)
    {
        var started = Stopwatch.GetTimestamp();
        while (!complete())
        {
            Assert.Null(context.Controller.Snapshot.Error);
            Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(30),
                $"Native preview did not settle. Snapshot={context.Controller.Snapshot}; Session={context.Window.Session.LastError}");
            pump?.Invoke();
            await Task.Delay(4, TestContext.Current.CancellationToken);
        }
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background, TestContext.Current.CancellationToken);
    }

    private static void InstallAnimation(NativeScrubWindowContext context)
    {
        var document = context.Window.Session.DocumentSnapshot;
        var line = new SubtitleLine
        {
            Text = "Scrubbing preview 字幕动画", Start = MediaTime.Zero, End = new(10),
            Style = new() { FontFamily = "Noto Sans", FontSize = 64 }
        };
        var layer = new ProjectLayer
        {
            Id = line.Id, SubtitleId = line.Id, Kind = LayerKind.SUBTITLE, Start = line.Start, End = line.End,
            Tracks = [new(AnimationProperty.POSITION, [new(MediaTime.Zero, new ScenePoint(80, 80)), new(new(10), new ScenePoint(800, 500))]),
                new(AnimationProperty.OPACITY, [new(MediaTime.Zero, 0.2), new(new(5), 1), new(new(10), 0.2)])]
        };
        context.Window.Session.Editor.Reset(document with { Subtitles = [line], Layers = [layer] });
    }

    private static (uint Decoders, uint Frames, uint Converters) Resources() =>
        (FfmpegVideoDecoder.GetLiveDecoderCount(), FfmpegVideoDecoder.GetLiveFrameCount(), SdrVideoConverter.LiveConverterCount);

    private static string ArtifactDirectory()
    {
        var supplied = Environment.GetEnvironmentVariable("AEGINEXT_SCRUB_ARTIFACT_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(supplied))
        {
            Assert.True(Path.IsPathFullyQualified(supplied));
            return supplied;
        }
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "AegiNext.sln")))
        {
            root = root.Parent ?? throw new DirectoryNotFoundException("Cannot locate the scrub validation artifact directory.");
        }
        return Path.Combine(root.FullName, "artifacts", "validation", "scrub");
    }
}
