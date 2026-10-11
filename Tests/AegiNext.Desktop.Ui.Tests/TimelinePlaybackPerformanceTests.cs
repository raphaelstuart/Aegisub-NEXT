using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using AegiNext.Application;
using AegiNext.Core.Projects;
using AegiNext.Core.Timing;
using AegiNext.Desktop.Controllers;
using AegiNext.Desktop.Controls;
using AegiNext.Desktop.Shortcuts;
using AegiNext.Desktop.Views;
using AegiNext.Desktop.Workspace;
using AegiNext.Media.Decoding;
using AegiNext.Media.Playback;
using AegiNext.Media.Preview;
using AegiNext.Media.Probing;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace AegiNext.Desktop.Ui.Tests;

/// <summary>在显式启用时采集真实原生解码与 Headless 时间线交互的性能证据。</summary>
public sealed class TimelinePlaybackPerformanceTests
{
    private const int SUBTITLE_COUNT = 3000;
    private const int TRACK_COUNT = 12;
    private const int TIMING_SAMPLE_COUNT = 12;
    private static readonly JsonSerializerOptions jsonOptions = new() { WriteIndented = true };

    /// <summary>控制可选性能采样；普通测试不创建媒体或运行时间基准。</summary>
    public static bool IsPerformanceMeasurementEnabled =>
        Environment.GetEnvironmentVariable("AEGINEXT_RUN_TIMELINE_PERFORMANCE_TESTS") == "1";

    /// <summary>测量连续播放、真实滚轮路由和 F8 入口，保留原生资源回收与有效呈现区间断言。</summary>
    [AvaloniaFact(SkipUnless = nameof(IsPerformanceMeasurementEnabled),
        Skip = "Requires AEGINEXT_RUN_TIMELINE_PERFORMANCE_TESTS=1 and an absolute AEGINEXT_TIMELINE_PERFORMANCE_OUTPUT JSON path.")]
    public async Task NativePlaybackWithTimelineScrollingAndTimingEntryProducesRepeatableMeasurements()
    {
        var reportPath = Environment.GetEnvironmentVariable("AEGINEXT_TIMELINE_PERFORMANCE_OUTPUT")
            ?? throw new InvalidOperationException("AEGINEXT_TIMELINE_PERFORMANCE_OUTPUT must name an absolute JSON path.");
        Assert.True(Path.IsPathFullyQualified(reportPath));
        using var environment = new UiTestEnvironment();
        var mediaPath = await CreateMediaAsync(environment.DirectoryPath);
        var document = CreateDocument();
        var projectPath = Path.Combine(environment.DirectoryPath, "timeline-performance.aeginext");
        await ProjectStore.SaveAsync(document, projectPath, TestContext.Current.CancellationToken);
        var initialResources = ReadResources();
        var dispatches = new ConcurrentQueue<(string Phase, long Timestamp, double Milliseconds)>();
        var pendingDispatches = new ConcurrentQueue<Action>();
        var presentations = new ConcurrentQueue<(string Phase, long Timestamp, VideoPreviewSnapshot Snapshot)>();
        var currentPhase = "Opening";
        var phaseReports = new List<object>();
        var timingDispatchMilliseconds = new List<double>();
        var timingCompletionMilliseconds = new List<double>();
        var timingRenderMilliseconds = new List<double>();
        var timingDispatchAllocatedBytes = new List<long>();
        var exitMilliseconds = new List<double>();
        bool? playbackFollowEnabledAfterScroll = null;
        object? playingTimingEntry = null;
        VideoFrameNavigator? navigator = null;
        VideoPreviewController? controller = null;
        VideoPreviewSnapshot? endingSnapshot = null;
        VideoDecodeSessionInfo? endingDecoder = null;
        string? failure = null;
        MainWindow? window = null;
        SubtitleTimelineControl? timeline = null;
        await using var session = new WorkbenchSession(new StartupTestDialogService(),
            present =>
            {
                controller = new(VideoPreviewProbe.ProbeAsync,
                    (path, index, clock, options) => new(token =>
                    {
                        navigator = VideoFrameNavigator.Open(path, index, options, token);
                        return navigator;
                    }, TimeProvider.System, externalPosition: clock),
                    () => new SdrVideoConverter(new(1920, 1080)),
                    DispatchMeasuredAsync, update =>
                    {
                        present(update);
                        if (update.Frame is not null)
                        {
                            presentations.Enqueue((Volatile.Read(ref currentPhase), Stopwatch.GetTimestamp(), update.Snapshot));
                        }
                    }, audioFactory: null);
                return controller;
            }, dispatch: DispatchMeasuredAsync, preferencesStore: new(environment.DirectoryPath), initialPreferences: new()
            {
                Language = "en-US",
                Projects = new()
                {
                    WorkspaceRoot = environment.DirectoryPath, AutoSaveEnabled = false, BackupEnabled = false
                }
            });
        try
        {
            await PumpUntilCompletedAsync(session.Styles.Completion);
            var openingProject = session.OpenProjectAsync(projectPath);
            await PumpUntilCompletedAsync(openingProject);
            Assert.Equal(ProjectOpenStatus.OPENED, openingProject.Result.Status);
            window = new(session) { Width = 1440, Height = 900 };
            window.Show();
            Render(window, timeline);
            timeline = UiTestActions.Find<SubtitleTimelineControl>(window, "Timeline");
            session.ViewModel.Timeline.PixelsPerSecond = 160;
            await PumpUntilCompletedAsync(window.OpenMediaAsync(mediaPath, true));
            await WaitUntilAsync(() => controller!.Snapshot.PresentedFrameTime is not null, TimeSpan.FromSeconds(15), PumpUi);
            await PumpUntilCompletedAsync(session.WaitForProjectIdleAsync());
            Assert.True(session.SelectTrack(document.Tracks[0].Id));
            Assert.True(timeline.Focus());
            Render(window, timeline);
            var point = timeline.TranslatePoint(new(timeline.HeaderWidth + 100, timeline.RulerHeight + 25), window)!.Value;
            Assert.Same(timeline, window.InputHitTest(point));
            await PumpUntilCompletedAsync(controller!.PlayAsync());
            await Task.Delay(300, TestContext.Current.CancellationToken);
            await MeasurePlaybackPhaseAsync("PlayingStationary", false);
            await MeasurePlaybackPhaseAsync("PlayingScroll", true);
            playbackFollowEnabledAfterScroll = session.ViewModel.Timeline.IsPlaybackFollowEnabled;
            await MeasurePlaybackPhaseAsync("PlayingZoom", false, true);

            await PumpUntilCompletedAsync(controller.PauseAsync());
            Volatile.Write(ref currentPhase, "TimingEntryPaused");
            for (var index = 0; index <= TIMING_SAMPLE_COUNT; index++)
            {
                await PumpUntilCompletedAsync(controller.SeekAsync(new MediaTime(4 + index, 4)));
                session.Tick();
                Render(window, timeline);
                await Task.Delay(25, TestContext.Current.CancellationToken);
                Assert.Equal(VideoPlaybackState.PAUSED, controller.Snapshot.State);
                await MeasureTimingEntryAsync(index > 0, false);
            }
            await PumpUntilCompletedAsync(controller.SeekAsync(new(1)));
            await PumpUntilCompletedAsync(controller.PlayAsync());
            await Task.Delay(80, TestContext.Current.CancellationToken);
            Volatile.Write(ref currentPhase, "TimingEntryPlaying");
            playingTimingEntry = await MeasureTimingEntryAsync(false, true);
            await PumpUntilCompletedAsync(controller.PauseAsync());
            session.InvalidateTimingSession();
            endingSnapshot = controller.Snapshot;
            endingDecoder = navigator?.SessionInfo;
            Assert.Null(endingSnapshot.Error);
            Assert.Null(session.LastError);
            Assert.Equal(SUBTITLE_COUNT + TIMING_SAMPLE_COUNT + 2, session.DocumentSnapshot.Subtitles.Length);
            Assert.All(presentations, sample =>
            {
                Assert.True(sample.Snapshot.PresentedFrameTime <= sample.Snapshot.PresentedAtPosition);
                Assert.True(sample.Snapshot.PresentedAtPosition < sample.Snapshot.PresentedFrameEnd);
            });

            async Task MeasurePlaybackPhaseAsync(string phase, bool scroll, bool zoom = false)
            {
                Volatile.Write(ref currentPhase, phase);
                var beforeDiagnostics = controller.PipelineDiagnostics;
                var wheelMilliseconds = new List<double>();
                var renderMilliseconds = new List<double>();
                var refreshMilliseconds = new List<double>();
                var dispatchMilliseconds = new List<double>();
                var wheelAllocatedBytes = new List<long>();
                var refreshAllocatedBytes = new List<long>();
                var uiAllocatedBytes = new List<long>();
                var started = Stopwatch.GetTimestamp();
                var processAllocatedBefore = GC.GetTotalAllocatedBytes(true);
                var iteration = 0;
                while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(2))
                {
                    var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                    var dispatchStarted = Stopwatch.GetTimestamp();
                    PumpUi();
                    dispatchMilliseconds.Add(Stopwatch.GetElapsedTime(dispatchStarted).TotalMilliseconds);
                    if (scroll || zoom)
                    {
                        var wheelAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                        var wheelStarted = Stopwatch.GetTimestamp();
                        var verticalDelta = iteration % 5 == 0 ? (iteration % 60 < 30 ? -1 : 1) : 0;
                        window.MouseWheel(point, zoom ? new Vector(0, iteration % 24 < 12 ? 1 : -1) : new Vector(-1, verticalDelta),
                            zoom ? RawInputModifiers.Control : RawInputModifiers.None);
                        wheelMilliseconds.Add(Stopwatch.GetElapsedTime(wheelStarted).TotalMilliseconds);
                        wheelAllocatedBytes.Add(GC.GetAllocatedBytesForCurrentThread() - wheelAllocatedBefore);
                    }
                    var refreshStarted = Stopwatch.GetTimestamp();
                    var refreshAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                    session.Tick();
                    refreshMilliseconds.Add(Stopwatch.GetElapsedTime(refreshStarted).TotalMilliseconds);
                    refreshAllocatedBytes.Add(GC.GetAllocatedBytesForCurrentThread() - refreshAllocatedBefore);
                    var renderStarted = Stopwatch.GetTimestamp();
                    Render(window, timeline);
                    renderMilliseconds.Add(Stopwatch.GetElapsedTime(renderStarted).TotalMilliseconds);
                    uiAllocatedBytes.Add(GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
                    iteration++;
                    var remaining = TimeSpan.FromMilliseconds(iteration * 1000d / 60) - Stopwatch.GetElapsedTime(started);
                    if (remaining > TimeSpan.Zero)
                    {
                        await Task.Delay(remaining, TestContext.Current.CancellationToken);
                    }
                }
                var ended = Stopwatch.GetTimestamp();
                var elapsed = Stopwatch.GetElapsedTime(started, ended).TotalSeconds;
                var afterDiagnostics = controller.PipelineDiagnostics;
                var playing = presentations.Where(sample => sample.Phase == phase &&
                    sample.Timestamp >= started && sample.Timestamp <= ended &&
                    sample.Snapshot.State == VideoPlaybackState.PLAYING).ToArray();
                var intervals = playing.Zip(playing.Skip(1), (first, second) =>
                    Stopwatch.GetElapsedTime(first.Timestamp, second.Timestamp).TotalMilliseconds);
                phaseReports.Add(new
                {
                    Phase = phase,
                    Seconds = elapsed,
                    UiIterations = iteration,
                    WheelEvents = wheelMilliseconds.Count,
                    PresentedFrames = playing.Length,
                    PresentedFramesPerSecond = playing.Length / elapsed,
                    ExpiredBeforeDispatch = ReadCounter(afterDiagnostics, "ExpiredBeforeDispatch") - ReadCounter(beforeDiagnostics, "ExpiredBeforeDispatch"),
                    ExpiredInCallback = ReadCounter(afterDiagnostics, "ExpiredInCallback") - ReadCounter(beforeDiagnostics, "ExpiredInCallback"),
                    ProcessAllocatedBytes = GC.GetTotalAllocatedBytes(true) - processAllocatedBefore,
                    UiThreadAllocatedBytes = uiAllocatedBytes.Sum(),
                    WheelAllocatedBytes = wheelAllocatedBytes.Sum(),
                    SessionRefreshAllocatedBytes = refreshAllocatedBytes.Sum(),
                    WheelDispatch = Summarize(wheelMilliseconds),
                    SessionRefresh = Summarize(refreshMilliseconds),
                    DispatchPump = Summarize(dispatchMilliseconds),
                    RenderTick = Summarize(renderMilliseconds),
                    UiQueue = Summarize(dispatches.Where(sample => sample.Phase == phase &&
                        sample.Timestamp >= started && sample.Timestamp <= ended).Select(sample => sample.Milliseconds)),
                    PresentedFrameInterval = Summarize(intervals),
                    PipelineBefore = beforeDiagnostics,
                    PipelineAfter = afterDiagnostics
                });
                Assert.Equal(VideoPlaybackState.PLAYING, controller.Snapshot.State);
                Assert.True(playing.Length >= 10, $"{phase} only presented {playing.Length} frames in {elapsed:F2} seconds. {afterDiagnostics}");
            }

            async Task<object> MeasureTimingEntryAsync(bool record, bool playing)
            {
                await PumpUntilCompletedAsync(session.WaitForProjectIdleAsync());
                var subtitleCount = session.DocumentSnapshot.Subtitles.Length;
                Assert.True(window.GetCommand(WorkbenchCommand.TIMING_ENTER).CanExecute(null));
                var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                var started = Stopwatch.GetTimestamp();
                UiTestActions.Press(window, Key.F8);
                var keyElapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                var keyAllocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
                await WaitUntilAsync(() => session.DocumentSnapshot.Subtitles.Length == subtitleCount + 1,
                    TimeSpan.FromSeconds(10), PumpUi);
                await PumpUntilCompletedAsync(session.WaitForProjectIdleAsync());
                var completionElapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                var renderStarted = Stopwatch.GetTimestamp();
                Render(window, timeline);
                var renderElapsed = Stopwatch.GetElapsedTime(renderStarted).TotalMilliseconds;
                var stateAfterEntry = controller.Snapshot.State;
                var positionAfterEntry = controller.Snapshot.Position;
                var canExit = window.GetCommand(WorkbenchCommand.TIMING_EXIT).CanExecute(null);
                if (!playing)
                {
                    Assert.True(canExit);
                }
                double? exitElapsed = null;
                if (canExit)
                {
                    var exitStarted = Stopwatch.GetTimestamp();
                    UiTestActions.Press(window, Key.F9);
                    await PumpUntilCompletedAsync(session.WaitForProjectIdleAsync());
                    exitElapsed = Stopwatch.GetElapsedTime(exitStarted).TotalMilliseconds;
                }
                if (record)
                {
                    timingDispatchMilliseconds.Add(keyElapsed);
                    timingCompletionMilliseconds.Add(completionElapsed);
                    timingRenderMilliseconds.Add(renderElapsed);
                    timingDispatchAllocatedBytes.Add(keyAllocated);
                    exitMilliseconds.Add(exitElapsed!.Value);
                }
                return new
                {
                    PlaybackStateBeforeEntry = playing ? "PLAYING" : "PAUSED",
                    KeyDispatchMilliseconds = keyElapsed,
                    TransactionCompletionMilliseconds = completionElapsed,
                    FirstRenderTickAfterCompletionMilliseconds = renderElapsed,
                    KeyDispatchAllocatedBytes = keyAllocated,
                    PlaybackStateAfterEntry = stateAfterEntry.ToString(),
                    PositionAfterEntry = positionAfterEntry.ToString(),
                    TimingExitAvailable = canExit,
                    TimingExitMilliseconds = exitElapsed
                };
            }
        }
        catch (Exception error)
        {
            failure = error.ToString();
            throw;
        }
        finally
        {
            if (controller is not null)
            {
                endingSnapshot ??= controller.Snapshot;
                endingDecoder ??= navigator?.SessionInfo;
                await PumpUntilCompletedAsync(controller.CloseAsync());
            }
            if (window is not null)
            {
                window.Close();
                await WaitUntilAsync(() => !window.IsVisible, TimeSpan.FromSeconds(10), PumpUi);
                await PumpUntilCompletedAsync(window.DisposeAsync().AsTask());
                Assert.False(window.IsVisible);
            }
            var finalResources = ReadResources();
            var report = new
            {
                RecordedAtUtc = DateTimeOffset.UtcNow,
                RuntimeIdentifier = RuntimeInformation.RuntimeIdentifier,
                OperatingSystem = RuntimeInformation.OSDescription,
                Configuration = "Run the same harness in Release before and after the change.",
                MeasurementBoundary = "Real FFmpeg/native decoder and SDR converter, system playback clock, actual MainWindow and timeline wheel routing, Skia Headless render ticks and explicit RenderTargetBitmap.Render of the timeline on each measured iteration. Controller and workspace callbacks use a test-owned queue drained on the real UI thread, bounded to queue.Count captured at each pump entry. Measurement loops do not drain the Avalonia dispatcher. This is a manually pumped Headless bridge, not macOS DispatcherPriority.Render scheduling. Does not measure physical touchpad, OS window compositor or native display refresh. Subtitle scene composition is not included in the injected SDR converter.",
                Fixture = new { Codec = "h264", Width = 1920, Height = 1080, FramesPerSecond = 60, DurationSeconds = 30, AudioStreams = 0 },
                Project = new { SubtitleCount = SUBTITLE_COUNT, TrackCount = TRACK_COUNT, WindowWidth = 1440, WindowHeight = 900, TimelinePixelsPerSecond = 160 },
                NativeBackend = endingDecoder,
                PlaybackPhases = phaseReports,
                PlaybackFollowEnabledAfterScroll = playbackFollowEnabledAfterScroll,
                TimingEntry = new
                {
                    PlaybackState = "PAUSED",
                    SamplePreparation = "Pause, seek to 1 + sampleIndex / 4 seconds and settle outside the measured interval.",
                    WarmupSamples = 1,
                    Samples = timingDispatchMilliseconds.Count,
                    KeyDispatch = Summarize(timingDispatchMilliseconds),
                    TransactionCompletion = Summarize(timingCompletionMilliseconds),
                    FirstRenderTickAfterCompletion = Summarize(timingRenderMilliseconds),
                    KeyDispatchAllocatedBytes = timingDispatchAllocatedBytes,
                    TimingExit = Summarize(exitMilliseconds)
                },
                PlayingTimingEntry = playingTimingEntry,
                EndingState = endingSnapshot?.State.ToString(),
                EndingPosition = endingSnapshot?.Position.ToString(),
                NativeResourcesBefore = initialResources,
                NativeResourcesAfter = finalResources,
                Failure = failure
            };
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize((object)report, jsonOptions), TestContext.Current.CancellationToken);
            Assert.Equal(initialResources, finalResources);
        }

        void PumpUi()
        {
            Assert.True(Dispatcher.UIThread.CheckAccess());
            var count = pendingDispatches.Count;
            for (var index = 0; index < count && pendingDispatches.TryDequeue(out var callback); index++)
            {
                callback();
            }
        }

        async Task DispatchMeasuredAsync(Action action, CancellationToken token)
        {
            var phase = Volatile.Read(ref currentPhase);
            var started = Stopwatch.GetTimestamp();
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            pendingDispatches.Enqueue(() =>
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    Assert.True(Dispatcher.UIThread.CheckAccess());
                    dispatches.Enqueue((phase, Stopwatch.GetTimestamp(),
                        Stopwatch.GetElapsedTime(started).TotalMilliseconds));
                    action();
                    completion.TrySetResult();
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    completion.TrySetCanceled(token);
                }
                catch (Exception error)
                {
                    completion.TrySetException(error);
                }
            });
            await completion.Task.WaitAsync(token);
        }

        async Task PumpUntilCompletedAsync(Task task)
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cancellation.CancelAfter(TimeSpan.FromSeconds(30));
            while (!task.IsCompleted)
            {
                PumpUi();
                await Task.Delay(1, cancellation.Token);
            }
            await task;
        }
    }

    private static ProjectDocument CreateDocument()
    {
        var tracks = Enumerable.Range(0, TRACK_COUNT).Select(index => new ProjectTrack
        {
            Id = index == 0 ? ProjectTrack.DEFAULT_TRACK_ID : Guid.NewGuid(),
            Name = index == 0 ? "Timing samples" : $"Track {index:00}"
        }).ToImmutableArray();
        var subtitles = Enumerable.Range(0, SUBTITLE_COUNT).Select(index => new SubtitleLine
        {
            Start = new(index / (TRACK_COUNT - 1) * 6),
            End = new(index / (TRACK_COUNT - 1) * 6 + 3),
            Text = $"字幕 {index:0000} ABC 123"
        }).ToImmutableArray();
        return new()
        {
            Name = "Timeline playback performance",
            FrameRate = new(60, 1),
            Tracks = tracks,
            Subtitles = subtitles,
            Layers = subtitles.Select((line, index) => new ProjectLayer
            {
                TrackId = tracks[index % (TRACK_COUNT - 1) + 1].Id, Id = line.Id, SubtitleId = line.Id, Kind = LayerKind.SUBTITLE, Start = line.Start, End = line.End
            }).ToImmutableArray()
        };
    }

    private static async Task<string> CreateMediaAsync(string directory)
    {
        var path = Path.Combine(directory, "1080p60-bt709.mp4");
        var arguments = new[]
        {
            "-v", "error", "-nostdin", "-f", "lavfi", "-i", "testsrc2=size=1920x1080:rate=60", "-an",
            "-frames:v", "1800", "-c:v", "libx264", "-profile:v", "high", "-preset", "ultrafast", "-crf", "18",
            "-threads", "1", "-g", "120", "-bf", "2", "-pix_fmt", "yuv420p",
            "-vf", "setsar=1/1,setparams=range=limited:color_primaries=bt709:color_trc=bt709:colorspace=bt709",
            "-color_range", "tv", "-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709",
            "-video_track_timescale", "60000", "-y", path
        };
        var result = await ProbeProcessRunner.RunAsync(MediaToolchain.ResolveFfmpeg(), arguments,
            TimeSpan.FromSeconds(60), 1024 * 1024, 1024 * 1024, TestContext.Current.CancellationToken);
        Assert.True(result.ExitCode == 0, result.StandardError);
        return path;
    }

    private static void Render(MainWindow window, SubtitleTimelineControl? timeline)
    {
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        if (timeline is not null)
        {
            using var bitmap = new RenderTargetBitmap(new((int)Math.Ceiling(timeline.Bounds.Width),
                (int)Math.Ceiling(timeline.Bounds.Height)), new(96, 96));
            bitmap.Render(timeline);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout, Action pump)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(timeout);
        while (!predicate())
        {
            pump();
            await Task.Delay(1, cancellation.Token);
        }
    }

    private static Dictionary<string, uint> ReadResources()
    {
        return new()
        {
            ["Decoders"] = FfmpegVideoDecoder.GetLiveDecoderCount(),
            ["Frames"] = FfmpegVideoDecoder.GetLiveFrameCount(),
            ["Converters"] = SdrVideoConverter.LiveConverterCount
        };
    }

    private static long ReadCounter(string diagnostics, string name)
    {
        var match = Regex.Match(diagnostics, $"(?:^|; ){Regex.Escape(name)}=([0-9]+)(?:;|$)");
        Assert.True(match.Success, $"Missing {name} in {diagnostics}");
        return long.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static object Summarize(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        return new
        {
            Samples = ordered.Length,
            P50Milliseconds = ordered.Length == 0 ? (double?)null : ordered[(int)Math.Ceiling(ordered.Length * 0.5) - 1],
            P95Milliseconds = ordered.Length == 0 ? (double?)null : ordered[(int)Math.Ceiling(ordered.Length * 0.95) - 1],
            MaximumMilliseconds = ordered.Length == 0 ? (double?)null : ordered[^1]
        };
    }
}
