using System.Collections.Immutable;
using AegiNext.Core.Editing;
using AegiNext.Core.Projects;
using AegiNext.Core.Timing;

namespace AegiNext.Core.Effects;

/// <summary>按脚本实际声明的完整属性区间组合动画，保留未声明区间的既有曲线。</summary>
public static class EffectScriptComposer
{
    /// <summary>编译并组合目标的全部轨道；不连续接合、有序目标或超出预算时整体失败，不修改输入。</summary>
    public static ImmutableArray<AnimationTrack> Compose(EffectScript script, ProjectLayer target, SubtitleStyle? subtitleStyle = null,
        AnimationTrackTarget? targetContext = null, SubtitleLine? subtitle = null)
    {
        var compilation = EffectScriptCompiler.CompileWithCoverage(script, target, subtitleStyle, true, targetContext, subtitle);
        return ComposeCompilation(compilation, target);
    }

    /// <summary>组合完整目标结果，保留未声明的既有动画并同步返回生成后的字幕范围。</summary>
    public static EffectScriptCompilation ComposeTarget(EffectScript script, ProjectLayer target, SubtitleStyle? subtitleStyle = null,
        AnimationTrackTarget? targetContext = null, SubtitleLine? subtitle = null)
    {
        var compilation = EffectScriptCompiler.CompileTarget(script, target, subtitleStyle, true, targetContext, subtitle);
        var prepared = compilation.PreparedLayer ?? target;
        var tracks = ComposeCompilation(compilation, prepared);
        EffectScriptCompiler.ValidateScopedTrackBudget(tracks);
        return compilation with { Tracks = tracks, PreparedLayer = prepared };
    }

    internal static ImmutableArray<AnimationTrack> ComposeCompilation(EffectScriptCompilation compilation, ProjectLayer target)
    {
        try
        {
            var (minimum, maximum) = LayerAnimationTiming.GetRange(target);
            var original = target.Tracks.ToDictionary(track => track.Target);
            var intervals = compilation.Intervals.ToLookup(interval => interval.Target);
            foreach (var added in compilation.Tracks)
            {
                if (!original.TryGetValue(added.Target, out var existing))
                {
                    original.Add(added.Target, added);
                    continue;
                }

                if (existing.IsOrdered)
                {
                    throw new EffectScriptException("目标轨道采用有序变换；请显式清除该轨道后再应用关键帧脚本。");
                }

                original[added.Target] = Merge(existing, added, intervals[added.Target], minimum, maximum);
            }

            var result = original.Values.OrderBy(track => track.Target.Property).ThenBy(track => track.Target.NodeId)
                .ThenBy(track => track.Target.TextRangeId).ThenBy(track => track.Target.State).ToImmutableArray();
            return result.SequenceEqual(target.Tracks) ? target.Tracks : result;
        }
        catch (OverflowException error)
        {
            throw new EffectScriptException("组合动画的精确时间超出有理数表示范围。", innerException: error);
        }
    }

    private static AnimationTrack Merge(AnimationTrack existing, AnimationTrack added, IEnumerable<EffectScriptInterval> intervals,
        MediaTime minimum, MediaTime maximum)
    {
        var ranges = new List<EffectScriptInterval>();
        foreach (var interval in intervals.OrderBy(interval => interval.Start))
        {
            if (ranges.Count > 0 && interval.Start <= ranges[^1].End)
            {
                var previous = ranges[^1];
                ranges[^1] = previous with { End = interval.End > previous.End ? interval.End : previous.End };
            }
            else
            {
                ranges.Add(interval);
            }
        }

        if (existing.ColorSpace != added.ColorSpace)
        {
            if (ranges.Count == 1 && ranges[0].Start == minimum && ranges[0].End == maximum)
            {
                return added;
            }
            var interval = ranges[0];
            throw new EffectScriptException("脚本采用线性 RGB；已有轨道采用不同色空间，部分覆盖无法保留两种插值。请覆盖完整时长或清除该目标轨道。",
                interval.Line, interval.Column);
        }

        var frames = ImmutableArray.CreateBuilder<Keyframe>();
        var cursor = minimum;
        foreach (var interval in ranges)
        {
            if (interval.Start > minimum || interval.Start == interval.End)
            {
                RequireContinuous(existing, added, interval.Start, interval);
            }

            if (interval.End < maximum)
            {
                RequireContinuous(existing, added, interval.End, interval);
            }

            if (interval.Start == interval.End)
            {
                continue;
            }

            if (cursor < interval.Start)
            {
                Append(frames, AnimationTrackSlicer.Slice(existing, cursor, interval.Start));
            }

            Append(frames, AnimationTrackSlicer.Slice(added, interval.Start, interval.End));
            cursor = interval.End;
        }

        if (frames.Count == 0)
        {
            return existing;
        }

        if (cursor < maximum)
        {
            Append(frames, AnimationTrackSlicer.Slice(existing, cursor, maximum));
        }

        var combined = frames.ToImmutable();
        if (combined.Length > AnimationPropertyMetadata.GetMaximumTrackEntries(existing.Property))
        {
            throw new EffectScriptException("组合后的属性轨道超出分量时间并集预算。");
        }

        return SameKeyframes(existing.Keyframes, combined) ? existing : existing with { Keyframes = combined };
    }

    private static void RequireContinuous(AnimationTrack existing, AnimationTrack added, MediaTime time, EffectScriptInterval interval)
    {
        if (!SceneEvaluator.EvaluateTrack(existing, time).Equals(SceneEvaluator.EvaluateTrack(added, time)))
        {
            throw new EffectScriptException($"{interval.Target.Property} 在 {time} 与已有动画接合值不一致；请调整预设端点或清除该目标轨道。",
                interval.Line, interval.Column);
        }
    }

    private static void Append(ImmutableArray<Keyframe>.Builder frames, AnimationTrack slice)
    {
        foreach (var frame in slice.Keyframes)
        {
            if (frames.Count > 0 && frames[^1].Time == frame.Time)
            {
                frames[^1] = frame;
            }
            else
            {
                frames.Add(frame);
            }
        }
    }

    private static bool SameKeyframes(ImmutableArray<Keyframe> first, ImmutableArray<Keyframe> second)
    {
        if (first.Length != second.Length)
        {
            return false;
        }

        for (var index = 0; index < first.Length; index++)
        {
            var a = first[index];
            var b = second[index];
            if (a.Time != b.Time || a.Value != b.Value || a.Interpolation != b.Interpolation ||
                a.CurveStart != b.CurveStart || a.CurveEnd != b.CurveEnd || a.Exponent != b.Exponent || a.Reverse != b.Reverse ||
                !a.ComponentCurves.SequenceEqual(b.ComponentCurves))
            {
                return false;
            }
        }

        return true;
    }
}
