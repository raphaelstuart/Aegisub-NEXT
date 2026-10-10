using System.Collections.Immutable;
using System.Numerics;
using AegiNext.Core.Editing;
using AegiNext.Core.Projects;
using AegiNext.Core.Timing;

namespace AegiNext.Core.Effects;

public static partial class EffectScriptCompiler
{
    /// <summary>编译完整目标结果；版本 2 同时生成静态字幕范围，输出轨道只包含脚本声明的目标。</summary>
    public static EffectScriptCompilation CompileTarget(EffectScript script, ProjectLayer target, SubtitleStyle? subtitleStyle = null,
        AnimationTrackTarget? targetContext = null, SubtitleLine? subtitle = null)
    {
        return CompileTarget(script, target, subtitleStyle, false, targetContext, subtitle);
    }

    internal static EffectScriptCompilation CompileTarget(EffectScript script, ProjectLayer target, SubtitleStyle? subtitleStyle,
        bool preserveExistingAnimation, AnimationTrackTarget? targetContext, SubtitleLine? subtitle)
    {
        EffectScriptValidator.Validate(script);
        ArgumentNullException.ThrowIfNull(target);
        if (script.Version == 1)
        {
            return CompileWithCoverage(script, target, subtitleStyle, preserveExistingAnimation, targetContext, subtitle) with
            {
                PreparedLayer = target,
                Subtitle = subtitle
            };
        }
        try
        {
            return CompileV2(script, target, subtitleStyle ?? subtitle?.Style, preserveExistingAnimation, targetContext, subtitle);
        }
        catch (OverflowException error)
        {
            throw new EffectScriptException("脚本的精确时间超出有理数表示范围，请简化时长、权重或段内位置。", innerException: error);
        }
    }

    private static EffectScriptCompilation CompileV2(EffectScript script, ProjectLayer target, SubtitleStyle? style,
        bool preserveExisting, AnimationTrackTarget? context, SubtitleLine? subtitle)
    {
        var (origin, end) = LayerAnimationTiming.GetRange(target);
        var duration = end - origin;
        if (duration <= MediaTime.Zero)
        {
            throw new EffectScriptException("片段中没有可应用特效的时间。");
        }
        var (plans, prepared, preparedSubtitle) = PrepareScopes(script, target, context, subtitle);
        var working = preserveExisting ? prepared : prepared with { Tracks = [] };
        var intervals = ImmutableArray.CreateBuilder<EffectScriptInterval>();
        var compilations = new List<EffectScriptCompilation>();
        var touched = new HashSet<AnimationTrackTarget>();
        foreach (var plan in plans)
        {
            if (plan.RangeIds.IsEmpty)
            {
                continue;
            }
            var timing = AllocateScope(script, plan, duration);
            var budgetLayer = prepared with { Tracks = prepared.Tracks.Concat(compilations.SelectMany(result => result.Tracks)).ToImmutableArray() };
            var compiled = CompileScope(plan, timing, target, preparedSubtitle, plan.Generated ? preparedSubtitle : subtitle,
                style, working, budgetLayer, origin, end);
            RejectScopeOverlap(intervals, compiled.Intervals);
            intervals.AddRange(compiled.Intervals);
            touched.UnionWith(compiled.Tracks.Select(track => track.Target));
            compilations.Add(compiled);
            ValidateScopedTrackBudget(budgetLayer.Tracks.Concat(compiled.Tracks));
        }
        var added = AggregateScopes(compilations, working, origin, end);
        var complete = new EffectScriptCompilation(added, intervals.ToImmutable());
        var composed = EffectScriptComposer.ComposeCompilation(complete, working);
        return new(composed.Where(track => touched.Contains(track.Target)).ToImmutableArray(), complete.Intervals)
        {
            PreparedLayer = prepared,
            Subtitle = preparedSubtitle
        };
    }

    private static ImmutableArray<AnimationTrack> AggregateScopes(List<EffectScriptCompilation> compilations,
        ProjectLayer working, MediaTime origin, MediaTime end)
    {
        var sources = new List<(AnimationTrack Track, EffectScriptInterval Interval)>();
        foreach (var compilation in compilations)
        {
            var byTarget = compilation.Tracks.ToDictionary(track => track.Target);
            foreach (var interval in compilation.Intervals)
            {
                sources.Add((byTarget[interval.Target], interval));
            }
        }
        var existing = working.Tracks.Select(track => track.Target).ToHashSet();
        return sources.GroupBy(source => source.Track.Target).OrderBy(group => group.Key.Property).ThenBy(group => group.Key.NodeId)
            .ThenBy(group => group.Key.TextRangeId).ThenBy(group => group.Key.State).Select(group =>
            {
                var frames = new List<Keyframe>();
                foreach (var (track, interval) in group.OrderBy(source => source.Interval.Start).ThenBy(source => source.Interval.End))
                {
                    if (frames.Count == 0 && interval.Start > origin)
                    {
                        frames.Add(new(origin, track.Keyframes[0].Value, KeyframeInterpolation.HOLD));
                    }
                    var slice = AnimationTrackSlicer.Slice(track, interval.Start, interval.End);
                    foreach (var frame in slice.Keyframes)
                    {
                        if (frames.Count > 0 && frames[^1].Time == frame.Time)
                        {
                            if (!frames[^1].Value.Equals(frame.Value))
                            {
                                throw new EffectScriptException($"{interval.Target.Property} 的不同作用域共享端点存在不同值。", interval.Line, interval.Column);
                            }
                            frames[^1] = frame;
                        }
                        else
                        {
                            if (frame.Time == interval.Start && frames.Count > 0)
                            {
                                if (!existing.Contains(group.Key) && !frames[^1].Value.Equals(frame.Value))
                                {
                                    throw new EffectScriptException($"{interval.Target.Property} 在未声明区间后发生跳变。", interval.Line, interval.Column);
                                }
                                frames[^1] = frames[^1] with { Interpolation = KeyframeInterpolation.HOLD, Reverse = false };
                            }
                            frames.Add(frame);
                        }
                    }
                }
                HoldUntil(frames, end);
                if (frames.Count > AnimationPropertyMetadata.GetMaximumTrackEntries(group.Key.Property))
                {
                    var interval = group.First().Interval;
                    throw new EffectScriptException("多个作用域的属性轨道超出分量时间并集预算。", interval.Line, interval.Column);
                }
                return new AnimationTrack(group.Key, frames.ToImmutableArray());
            }).ToImmutableArray();
    }

    private static EffectScriptCompilation CompileScope(EffectScriptScopePlan plan, EffectScriptScopeTiming timing, ProjectLayer sourceLayer,
        SubtitleLine? targetSubtitle, SubtitleLine? baseSubtitle, SubtitleStyle? style, ProjectLayer working, ProjectLayer prepared,
        MediaTime origin, MediaTime end)
    {
        var tracks = new Dictionary<AnimationTrackTarget, List<Keyframe>>();
        var intervals = ImmutableArray.CreateBuilder<EffectScriptInterval>();
        var existingTargets = working.Tracks.Select(track => track.Target).ToHashSet();
        for (var group = 0; group < plan.RangeIds.Length; group++)
        {
            var context = new AnimationTrackTarget(AnimationProperty.FILL, TextRangeId: plan.RangeIds[group], State: plan.Scope.State);
            var streams = timing.Segments.Select(segment => segment.Segment.Keyframes
                .GroupBy(frame => ResolveTarget(frame, sourceLayer, context, targetSubtitle))
                .Select(flow => new EffectScriptPropertyStream(flow.Key, flow.ToImmutableArray())).ToImmutableArray()).ToImmutableArray();
            var start = origin + timing.Delay + EffectScriptTiming.Scale(timing.Stagger, group);
            PreflightKeys(timing.Segments, streams, start, origin, end);
            var targets = prepared.Tracks.Select(track => track.Target).Concat(existingTargets).Concat(tracks.Keys)
                .Concat(streams.SelectMany(segment => segment).Select(flow => flow.Target)).Distinct();
            if (targets.Count(target => target.TextRangeId.HasValue || target.State != SubtitleAnimationState.NORMAL) > MAXIMUM_SCOPED_TRACKS)
            {
                throw new EffectScriptException($"字幕范围和视觉状态轨道总数超过 {MAXIMUM_SCOPED_TRACKS} 条的预算。", plan.Scope.Line, plan.Scope.Column);
            }
            var cursor = start;
            for (var segmentIndex = 0; segmentIndex < timing.Segments.Length; segmentIndex++)
            {
                var segment = timing.Segments[segmentIndex];
                foreach (var flow in streams[segmentIndex])
                {
                    EmitStream(tracks, flow, segment, cursor, origin, sourceLayer, style, baseSubtitle, existingTargets.Contains(flow.Target));
                    var frame = flow.Frames[0];
                    intervals.Add(new(flow.Target, cursor, cursor + segment.Length, frame.Line, frame.Column));
                }
                cursor += segment.Length;
            }
        }
        var compiled = tracks.OrderBy(pair => pair.Key.Property).ThenBy(pair => pair.Key.NodeId)
            .ThenBy(pair => pair.Key.TextRangeId).ThenBy(pair => pair.Key.State).Select(pair =>
            {
                HoldUntil(pair.Value, end);
                return new AnimationTrack(pair.Key, pair.Value.ToImmutableArray());
            }).ToImmutableArray();
        return new(compiled, intervals.ToImmutable());
    }

    private static void PreflightKeys(ImmutableArray<EffectScriptSegmentTiming> timings,
        ImmutableArray<ImmutableArray<EffectScriptPropertyStream>> streams, MediaTime start, MediaTime origin, MediaTime end)
    {
        var counts = new Dictionary<AnimationTrackTarget, BigInteger>();
        var lastTimes = new Dictionary<AnimationTrackTarget, MediaTime>();
        var cursor = start;
        for (var index = 0; index < timings.Length; index++)
        {
            var timing = timings[index];
            var legs = timing.Segment.PingPong ? 2 : 1;
            foreach (var flow in streams[index])
            {
                var points = timing.LegLength == MediaTime.Zero ? BigInteger.One :
                    BigInteger.One + timing.Cycles * legs * (flow.Frames.Length - 1);
                if (points > AnimationPropertyMetadata.GetMaximumTrackEntries(flow.Target.Property))
                {
                    var frame = flow.Frames[0];
                    throw new EffectScriptException($"{frame.Property} 重复展开后的关键帧超出分量时间并集预算。", frame.Line, frame.Column);
                }
                var activeLength = timing.Cycles.IsZero ? MediaTime.Zero :
                    Multiply(timing.LegLength, timing.Cycles * legs);
                if (activeLength < timing.Length)
                {
                    points++;
                }
                if (!counts.TryGetValue(flow.Target, out var count))
                {
                    count = cursor > origin ? BigInteger.One : BigInteger.Zero;
                }
                else if (lastTimes[flow.Target] == cursor)
                {
                    points--;
                }
                count += points;
                counts[flow.Target] = count;
                lastTimes[flow.Target] = cursor + timing.Length;
                var allowance = lastTimes[flow.Target] < end ? 1 : 0;
                if (count + allowance > AnimationPropertyMetadata.GetMaximumTrackEntries(flow.Target.Property))
                {
                    var frame = flow.Frames[0];
                    throw new EffectScriptException($"{frame.Property} 重复展开后的关键帧超出分量时间并集预算。", frame.Line, frame.Column);
                }
            }
            cursor += timing.Length;
        }
    }

    private static MediaTime Multiply(MediaTime time, BigInteger factor)
    {
        var top = time.Numerator * factor;
        var bottom = new BigInteger(time.Denominator);
        var divisor = BigInteger.GreatestCommonDivisor(top, bottom);
        return new(checked((long)(top / divisor)), checked((long)(bottom / divisor)));
    }

    private static void EmitStream(Dictionary<AnimationTrackTarget, List<Keyframe>> tracks, EffectScriptPropertyStream flow,
        EffectScriptSegmentTiming timing, MediaTime cursor, MediaTime origin, ProjectLayer sourceLayer, SubtitleStyle? style,
        SubtitleLine? baseSubtitle, bool preserveExisting)
    {
        if (!timing.Segment.PingPong && (timing.Segment.CycleDuration.HasValue || timing.Segment.RepeatCount > 1))
        {
            var first = flow.Frames[0];
            var last = flow.Frames[^1];
            var firstValue = ResolveValue(first, flow.Target,
                ResolveBaseValue(first, flow.Target, sourceLayer, style, baseSubtitle, first.Value.Kind != EffectScriptValueKind.ABSOLUTE));
            var lastValue = ResolveValue(last, flow.Target,
                ResolveBaseValue(last, flow.Target, sourceLayer, style, baseSubtitle, last.Value.Kind != EffectScriptValueKind.ABSOLUTE));
            if (!firstValue.Equals(lastValue))
            {
                throw new EffectScriptException($"{last.Property} 的正向重复或循环必须闭合；首尾求值需相同，或使用 pingpong。", last.Line, last.Column);
            }
        }
        var cycles = checked((int)timing.Cycles);
        var legs = timing.Segment.PingPong ? 2 : 1;
        for (var cycle = 0; cycle < cycles; cycle++)
        {
            for (var leg = 0; leg < legs; leg++)
            {
                var reversed = leg == 1;
                var legStart = cursor + EffectScriptTiming.Scale(timing.LegLength, cycle * (decimal)legs + leg);
                for (var index = 0; index < flow.Frames.Length; index++)
                {
                    var sourceIndex = reversed ? flow.Frames.Length - 1 - index : index;
                    var source = flow.Frames[sourceIndex];
                    if (reversed)
                    {
                        var outgoing = sourceIndex > 0 ? flow.Frames[sourceIndex - 1] : null;
                        source = source with
                        {
                            Progress = 1 - source.Progress,
                            Interpolation = outgoing?.Interpolation ?? KeyframeInterpolation.HOLD,
                            Exponent = outgoing?.Exponent ?? 1
                        };
                    }
                    var time = legStart + EffectScriptTiming.Scale(timing.LegLength, source.Progress);
                    var needsBase = source.Value.Kind != EffectScriptValueKind.ABSOLUTE ||
                        !tracks.ContainsKey(flow.Target) && time > origin && !preserveExisting;
                    Add(tracks, flow.Target, source, time, ResolveBaseValue(source, flow.Target, sourceLayer, style, baseSubtitle, needsBase),
                        origin, preserveExisting, reversed && sourceIndex > 0);
                }
            }
        }
        HoldUntil(tracks[flow.Target], cursor + timing.Length);
    }

    private static void HoldUntil(List<Keyframe> frames, MediaTime end)
    {
        if (frames[^1].Time < end)
        {
            frames[^1] = frames[^1] with { Interpolation = KeyframeInterpolation.HOLD, Reverse = false };
            frames.Add(new(end, frames[^1].Value, KeyframeInterpolation.HOLD));
        }
    }

    private static void RejectScopeOverlap(ImmutableArray<EffectScriptInterval>.Builder previous,
        ImmutableArray<EffectScriptInterval> added)
    {
        var byTarget = previous.ToLookup(interval => interval.Target);
        foreach (var interval in added)
        {
            if (byTarget[interval.Target].Any(other => interval.Start < other.End && other.Start < interval.End))
            {
                throw new EffectScriptException($"不同作用域对 {interval.Target.Property} 的相同完整目标声明了重叠时间区间。", interval.Line, interval.Column);
            }
        }
    }
}
