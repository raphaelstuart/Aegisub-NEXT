using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using AegiNext.Core.Timing;
using AegiNext.Desktop.Controllers;
using AegiNext.Desktop.Controls;
using AegiNext.Desktop.Rendering;
using AegiNext.Desktop.Workspace;
using AegiNext.Media.Decoding;

namespace AegiNext.Desktop.Ui.Tests;

internal sealed class NativeScrubMetrics
{
    private const int SAMPLE_LIMIT = 8192;
    private static readonly JsonSerializerOptions jsonOptions = new() { WriteIndented = true };
    private readonly Lock gate = new();
    private readonly List<(int Round, long Time, string Stage, double Milliseconds, MediaTime? Target)> stages = [];
    private readonly List<(int Round, long Time, int Number, string Kind, MediaTime Target, double Milliseconds, double Lateness)> inputs = [];
    private readonly List<(int Round, long Time, VideoPreviewSnapshot Snapshot, int Width, int Height, long Quality, bool Interactive)> frames = [];
    private readonly List<(int Round, long Time, VideoPreviewSnapshot Snapshot, int Width, int Height, long Quality,
        bool Interactive, MediaTime? RequestedPosition, MediaTime? SourceFrameTime, MediaTime? SourceFrameEnd,
        MediaTime? CompositionTime)> transientFrames = [];
    private readonly List<(int Round, PreviewInteractionEvent Event)> interactions = [];
    private readonly List<(int Round, long Time, long Requested, long Presented, long? Generation, bool Completed)> draws = [];
    private readonly List<object> rounds = [];
    private readonly List<double> releaseLatencies = [];
    private int round;
    private int sequence;
    private int discardedSamples;

    internal int PresentationCount
    {
        get
        {
            lock (gate)
            {
                return frames.Count;
            }
        }
    }

    internal void BeginRound(int value)
    {
        lock (gate)
        {
            round = value;
        }
    }

    internal void RecordStage(string stage, long started, MediaTime? target = null)
    {
        RecordDuration(stage, started, Stopwatch.GetElapsedTime(started).TotalMilliseconds, target);
    }

    internal void RecordDuration(string stage, long started, double milliseconds, MediaTime? target = null)
    {
        lock (gate)
        {
            if (Accept(stages.Count))
            {
                stages.Add((round, started, stage, milliseconds, target));
            }
        }
    }

    internal void RecordNativeDelta(long started, VideoDecodeSessionInfo before, VideoDecodeSessionInfo after)
    {
        if (after.DecodeNanoseconds >= before.DecodeNanoseconds)
        {
            RecordDuration("NativeDecode", started, (after.DecodeNanoseconds - before.DecodeNanoseconds) / 1_000_000d);
        }
        if (after.DownloadNanoseconds >= before.DownloadNanoseconds)
        {
            RecordDuration("NativeDownload", started, (after.DownloadNanoseconds - before.DownloadNanoseconds) / 1_000_000d);
        }
    }

    internal void RecordInput(string kind, long started, MediaTime target, long due)
    {
        lock (gate)
        {
            if (Accept(inputs.Count))
            {
                inputs.Add((round, started, ++sequence, kind, target, Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    Stopwatch.GetElapsedTime(due, started).TotalMilliseconds));
            }
        }
    }

    internal void RecordPresentation(VideoPreviewUpdate update, PreviewFrameRecord? identity)
    {
        if (update.Frame is not { } frame)
        {
            return;
        }
        lock (gate)
        {
            if (update.IsTransientPreview)
            {
                if (Accept(transientFrames.Count))
                {
                    transientFrames.Add((round, Stopwatch.GetTimestamp(), update.Snapshot, frame.Width, frame.Height,
                        identity?.QualityRevision ?? -1, identity?.Interactive ?? false, update.RequestedPosition,
                        update.SourceFrameTime, update.SourceFrameEnd, update.CompositionTime));
                }
                return;
            }
            if (Accept(frames.Count))
            {
                frames.Add((round, Stopwatch.GetTimestamp(), update.Snapshot, frame.Width, frame.Height,
                    identity?.QualityRevision ?? -1, identity?.Interactive ?? false));
            }
        }
    }

    internal void RecordInteraction(PreviewInteractionEvent value)
    {
        lock (gate)
        {
            if (Accept(interactions.Count))
            {
                interactions.Add((round, value));
            }
        }
    }

    internal void RecordDraw(EffectCanvasControl canvas, VideoPreviewSnapshot snapshot)
    {
        lock (gate)
        {
            if (Accept(draws.Count))
            {
                draws.Add((round, Stopwatch.GetTimestamp(), canvas.PreviewSequence, canvas.PresentedPreviewSequence,
                    snapshot.PresentedGeneration, canvas.PreviewCompletion.IsCompleted));
            }
        }
    }

    internal void FinishRound(long started, long released, long completed, MediaTime target, bool stillInteractive,
        int presentationsDuringMovement, VideoPreviewSnapshot snapshot)
    {
        lock (gate)
        {
            if (round > 0)
            {
                releaseLatencies.Add(Stopwatch.GetElapsedTime(released, completed).TotalMilliseconds);
            }
            rounds.Add(new
            {
                Round = round, DurationMilliseconds = Stopwatch.GetElapsedTime(started, completed).TotalMilliseconds,
                ReleaseToExactMilliseconds = Stopwatch.GetElapsedTime(released, completed).TotalMilliseconds,
                Target = Seconds(target), HeldAfter400MillisecondsStillInteractive = stillInteractive,
                PresentationsDuringMovement = presentationsDuringMovement,
                TransientPresentations = transientFrames.Count(value => value.Round == round),
                FinalFrameStart = Seconds(snapshot.PresentedFrameTime), FinalFrameEnd = Seconds(snapshot.PresentedFrameEnd),
                FinalPosition = Seconds(snapshot.Position), FinalState = snapshot.State.ToString()
            });
        }
    }

    internal async Task<string> WriteReportAsync(string directory, string fixture, string mediaPath, VideoDecodeMode mode,
        bool animated, int repetitions, VideoDecodeSessionInfo? decoder, ProjectPreviewConverter? converter,
        (uint Decoders, uint Frames, uint Converters) initial, (uint Decoders, uint Frames, uint Converters) final,
        string? failure)
    {
        object report;
        lock (gate)
        {
            var measured = frames.Where(value => value.Round > 0).ToArray();
            var measuredTransient = transientFrames.Where(value => value.Round > 0).ToArray();
            var inputLatencies = new List<double>();
            var acceptLatencies = new List<double>();
            var transientInputLatencies = new List<double>();
            var transientAcceptLatencies = new List<double>();
            foreach (var delivery in interactions.Where(value => value.Round > 0 && value.Event.Stage is "delivered" or "cached-delivered"))
            {
                var isTransient = delivery.Event.Stage == "cached-delivered";
                var input = interactions.LastOrDefault(value => value.Event.Stage == "input" &&
                    value.Event.Session == delivery.Event.Session && value.Event.Sequence == delivery.Event.Sequence &&
                    value.Event.Timestamp <= delivery.Event.Timestamp);
                if (input.Event.Timestamp != 0)
                {
                    var destination = isTransient ? transientInputLatencies : inputLatencies;
                    destination.Add(Stopwatch.GetElapsedTime(input.Event.Timestamp, delivery.Event.Timestamp).TotalMilliseconds);
                }
                var accepted = interactions.LastOrDefault(value => value.Event.Stage == "accepted" &&
                    value.Event.Session == delivery.Event.Session && value.Event.Sequence == delivery.Event.Sequence &&
                    value.Event.Timestamp <= delivery.Event.Timestamp);
                if (accepted.Event.Timestamp != 0)
                {
                    var destination = isTransient ? transientAcceptLatencies : acceptLatencies;
                    destination.Add(Stopwatch.GetElapsedTime(accepted.Event.Timestamp, delivery.Event.Timestamp).TotalMilliseconds);
                }
            }
            var intervals = measured.GroupBy(value => value.Round).SelectMany(group => group.Zip(group.Skip(1),
                (left, right) => Stopwatch.GetElapsedTime(left.Time, right.Time).TotalMilliseconds));
            var allFrames = measured.Select(value => (value.Round, value.Time))
                .Concat(measuredTransient.Select(value => (value.Round, value.Time))).OrderBy(value => value.Time).ToArray();
            var allIntervals = allFrames.GroupBy(value => value.Round).SelectMany(group => group.Zip(group.Skip(1),
                (left, right) => Stopwatch.GetElapsedTime(left.Time, right.Time).TotalMilliseconds));
            var cacheProperty = converter?.GetType().GetProperty("BackgroundCacheStatistics", BindingFlags.Instance | BindingFlags.NonPublic);
            report = new
            {
                Runtime = RuntimeInformation.RuntimeIdentifier, OS = RuntimeInformation.OSDescription,
                RecordedAtUtc = DateTimeOffset.UtcNow, Fixture = fixture, MediaPath = mediaPath,
                RequestedMode = mode.ToString(), AnimatedSubtitles = animated, Repetitions = repetitions,
                RequestedPointerRateHz = 120, RequestedRenderBarrierRateHz = 60,
                ActualDecoder = decoder, Cache = cacheProperty?.GetValue(converter),
                Measurement = "Real MainWindow/WorkbenchSession pointer dispatch, native navigator and production project converter. " +
                    "Skia Headless render barriers run at up to 60 Hz; sequence/completion observations are not proof that each accepted frame reached the canvas, a native GPU or a display scanout. " +
                    "ConvertAndCompose includes conversion, cache lookup and subtitle composition. Native stages are cumulative native counter deltas. " +
                    "Round zero records cold opening and warmup; rounds one onward are measured. Missing cache diagnostics in the baseline are null. " +
                    "Input and accepted request identities come from the workspace diagnostics; pointer scheduling lateness is reported instead of assuming 120 Hz was achieved. " +
                    "The original PresentedFrames, Presentations, DeliveredFrameIntervals, InputToDelivered and AcceptedToDelivered remain exact-only for baseline comparison. " +
                    "Transient cache deliveries are reported separately and included only in fields prefixed All. Their source interval and requested position come from the update, not the unchanged exact snapshot. " +
                    "ReleaseToExact still waits for an exact delivery with the selected quality revision.",
                DiscardedDiagnosticSamples = discardedSamples,
                PresentedFrames = measured.Length,
                TransientPresentations = measuredTransient.Length,
                AllPresentations = allFrames.Length,
                TransientIntervalsContainingRequestedPosition = measuredTransient.Count(value =>
                    value.SourceFrameTime <= value.RequestedPosition && value.RequestedPosition < value.SourceFrameEnd),
                ValidExactPresentedIntervals = measured.Count(value => value.Snapshot.PresentedFrameTime <= value.Snapshot.PresentedAtPosition &&
                    value.Snapshot.PresentedAtPosition < value.Snapshot.PresentedFrameEnd),
                MaximumPreparedFrames = measured.Select(value => value.Snapshot.PreparedFrameCount).DefaultIfEmpty().Max(),
                MaximumPendingFrames = measured.Select(value => value.Snapshot.PreparationPendingCount).DefaultIfEmpty().Max(),
                MaximumPreparedBytes = measured.Select(value => value.Snapshot.PreparedBytes).DefaultIfEmpty().Max(),
                MaximumPendingBytes = measured.Select(value => value.Snapshot.PreparationPendingBytes).DefaultIfEmpty().Max(),
                Stages = stages.Where(value => value.Round > 0).GroupBy(value => value.Stage).ToDictionary(group => group.Key,
                    group => Summarize(group.Select(value => value.Milliseconds))),
                OpeningAndWarmupStages = stages.Where(value => value.Round == 0).GroupBy(value => value.Stage).ToDictionary(group => group.Key,
                    group => Summarize(group.Select(value => value.Milliseconds))),
                InputHandler = Summarize(inputs.Where(value => value.Round > 0).Select(value => value.Milliseconds)),
                PointerScheduleLateness = Summarize(inputs.Where(value => value.Round > 0 && value.Kind == "move").Select(value => value.Lateness)),
                InputToDelivered = Summarize(inputLatencies), AcceptedToDelivered = Summarize(acceptLatencies),
                TransientInputToDelivered = Summarize(transientInputLatencies),
                TransientAcceptedToDelivered = Summarize(transientAcceptLatencies),
                AllInputToDelivered = Summarize(inputLatencies.Concat(transientInputLatencies)),
                AllAcceptedToDelivered = Summarize(acceptLatencies.Concat(transientAcceptLatencies)),
                AllDeliveredFrameIntervals = Summarize(allIntervals),
                DeliveredFrameIntervals = Summarize(intervals), ReleaseToExact = Summarize(releaseLatencies), Rounds = rounds,
                NativeResourcesBefore = new { initial.Decoders, initial.Frames, initial.Converters },
                NativeResourcesAfter = new { final.Decoders, final.Frames, final.Converters },
                Inputs = inputs.Select(value => new { value.Round, Timestamp = value.Time, value.Number, value.Kind,
                    Target = Seconds(value.Target), HandlerMilliseconds = value.Milliseconds, ScheduleLatenessMilliseconds = value.Lateness }),
                Presentations = frames.Select(value => new { value.Round, Timestamp = value.Time,
                    value.Width, value.Height, value.Quality, value.Interactive, value.Snapshot.Epoch, value.Snapshot.PresentedGeneration,
                    Position = Seconds(value.Snapshot.PresentedAtPosition), FrameStart = Seconds(value.Snapshot.PresentedFrameTime),
                    FrameEnd = Seconds(value.Snapshot.PresentedFrameEnd) }),
                TransientPresentationMetadata = transientFrames.Select(value => new
                {
                    value.Round, Timestamp = value.Time, value.Width, value.Height, value.Quality, value.Interactive,
                    value.Snapshot.Epoch, IsTransientPreview = true,
                    RequestedPosition = Seconds(value.RequestedPosition),
                    SourceFrameTime = Seconds(value.SourceFrameTime), SourceFrameEnd = Seconds(value.SourceFrameEnd),
                    CompositionTime = Seconds(value.CompositionTime),
                    LatestExactGeneration = value.Snapshot.PresentedGeneration,
                    LatestExactPosition = Seconds(value.Snapshot.PresentedAtPosition),
                    LatestExactFrameStart = Seconds(value.Snapshot.PresentedFrameTime),
                    LatestExactFrameEnd = Seconds(value.Snapshot.PresentedFrameEnd)
                }),
                InteractionEvents = interactions.Select(value => new { value.Round, value.Event }),
                RenderBarrierObservations = draws.Select(value => new { value.Round, Timestamp = value.Time,
                    value.Requested, value.Presented, LatestControllerGeneration = value.Generation, value.Completed }),
                Failure = failure
            };
        }
        Directory.CreateDirectory(directory);
        var label = Environment.GetEnvironmentVariable("AEGINEXT_SCRUB_REPORT_LABEL") ?? "current";
        Assert.DoesNotContain(Path.DirectorySeparatorChar, label);
        Assert.DoesNotContain(Path.AltDirectorySeparatorChar, label);
        var path = Path.Combine(directory, $"scrub-{label}-{fixture}-{mode}-{(animated ? "animated" : "empty")}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, jsonOptions),
            CancellationToken.None);
        return path;
    }

    private bool Accept(int count)
    {
        if (count < SAMPLE_LIMIT)
        {
            return true;
        }
        discardedSamples++;
        return false;
    }

    private static double? Seconds(MediaTime? value) => value is { } time ? (double)time.Numerator / time.Denominator : null;

    private static object Summarize(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        return new
        {
            Count = ordered.Length,
            P50Milliseconds = ordered.Length == 0 ? (double?)null : ordered[(int)Math.Ceiling(ordered.Length * 0.5) - 1],
            P95Milliseconds = ordered.Length == 0 ? (double?)null : ordered[(int)Math.Ceiling(ordered.Length * 0.95) - 1],
            MaximumMilliseconds = ordered.Length == 0 ? (double?)null : ordered[^1]
        };
    }
}
