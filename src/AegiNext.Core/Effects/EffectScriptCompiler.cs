using System.Collections.Immutable;
using AegiNext.Core.Editing;
using AegiNext.Core.Projects;
using AegiNext.Core.Timing;

namespace AegiNext.Core.Effects;

/// <summary>按目标片段精确分配固定和自由时间，生成可持久化的既有动画轨道。</summary>
public static partial class EffectScriptCompiler
{
    /// <summary>以目标内容时钟编译脚本；使用应用前基础值，共享端点冲突或越界值整体失败。</summary>
    public static ImmutableArray<AnimationTrack> Compile(EffectScript script, ProjectLayer target, SubtitleStyle? subtitleStyle = null,
        AnimationTrackTarget? targetContext = null, SubtitleLine? subtitle = null)
    {
        return CompileWithCoverage(script, target, subtitleStyle, false, targetContext, subtitle).Tracks;
    }

    internal static EffectScriptCompilation CompileWithCoverage(EffectScript script, ProjectLayer target, SubtitleStyle? subtitleStyle,
        bool preserveExistingAnimation, AnimationTrackTarget? targetContext, SubtitleLine? subtitle)
    {
        EffectScriptValidator.Validate(script);
        ArgumentNullException.ThrowIfNull(target);
        if (script.Version != 1)
        {
            throw new EffectScriptException("版本 2 会生成字幕范围，请使用 CompileTarget 接口保留完整编译结果。");
        }
        try
        {
            return CompileCore(script, target, subtitleStyle ?? subtitle?.Style, preserveExistingAnimation, targetContext, subtitle);
        }
        catch (OverflowException error)
        {
            throw new EffectScriptException("脚本的精确时间超出有理数表示范围，请简化时长、权重或段内位置。", innerException: error);
        }
    }

    private static EffectScriptCompilation CompileCore(EffectScript script, ProjectLayer target, SubtitleStyle? subtitleStyle,
        bool preserveExistingAnimation, AnimationTrackTarget? targetContext, SubtitleLine? subtitle)
    {
        if (targetContext is { } context)
        {
            try
            {
                SubtitleAnimationTargetValidation.ValidateIdentity(context);
            }
            catch (InvalidDataException error)
            {
                throw new EffectScriptException(error.Message, innerException: error);
            }
            if (context.NodeId.HasValue)
            {
                throw new EffectScriptException("脚本上下文只指定文字范围和视觉状态；蒙版节点由脚本选择器指定。");
            }
            if ((context.TextRangeId.HasValue || context.State != SubtitleAnimationState.NORMAL) &&
                (target.Kind != LayerKind.SUBTITLE || subtitle is null || target.SubtitleId != subtitle.Id))
            {
                throw new EffectScriptException("文字范围和视觉状态脚本需要目标字幕及其应用前内容。");
            }
        }
        if (target.SubtitleId.HasValue && subtitleStyle is null &&
            script.Segments.Any(segment => segment.Keyframes.Any(frame => frame.Property is EffectScriptProperty.STROKE_WIDTH or EffectScriptProperty.FILL or EffectScriptProperty.STROKE)))
        {
            throw new EffectScriptException("字幕颜色和描边属性需要应用前的字幕样式。");
        }
        var (origin, end) = LayerAnimationTiming.GetRange(target);
        var duration = end - origin;
        if (duration <= MediaTime.Zero)
        {
            throw new EffectScriptException("片段中没有可应用特效的时间。");
        }

        var fixedTotal = script.Segments.Aggregate(MediaTime.Zero, (total, segment) => total + (segment.FixedDuration ?? MediaTime.Zero));
        var flexTotal = script.Segments.Sum(segment => segment.FlexWeight);
        var compress = duration < fixedTotal;
        if (compress && script.ShortClipPolicy == EffectScriptShortClipPolicy.REJECT)
        {
            throw new EffectScriptException($"片段时长 {duration} 小于固定段总时长 {fixedTotal}，该脚本拒绝应用。");
        }

        var remaining = compress ? MediaTime.Zero : duration - fixedTotal;
        var tracks = new Dictionary<AnimationTrackTarget, List<Keyframe>>();
        var intervals = ImmutableArray.CreateBuilder<EffectScriptInterval>();
        var existingTargets = preserveExistingAnimation ? target.Tracks.Select(track => track.Target).ToHashSet() : [];
        var cursor = origin;
        foreach (var segment in script.Segments)
        {
            var length = segment.FixedDuration is { } fixedLength
                ? compress ? EffectScriptTiming.Scale(fixedLength, duration, fixedTotal) : fixedLength
                : EffectScriptTiming.Scale(remaining, EffectScriptTiming.Scale(new(1), segment.FlexWeight), EffectScriptTiming.Scale(new(1), flexTotal));
            foreach (var frame in segment.Keyframes)
            {
                var time = cursor + EffectScriptTiming.Scale(length, frame.Progress);
                var animationTarget = ResolveTarget(frame, target, targetContext, subtitle);
                var needsBase = frame.Value.Kind != EffectScriptValueKind.ABSOLUTE ||
                    !tracks.ContainsKey(animationTarget) && time > origin && !existingTargets.Contains(animationTarget);
                Add(tracks, animationTarget, frame, time,
                    ResolveBaseValue(frame, animationTarget, target, subtitleStyle, subtitle, needsBase), origin,
                    existingTargets.Contains(animationTarget));
                if (frame.Progress == 0)
                {
                    intervals.Add(new(animationTarget, cursor, cursor + length, frame.Line, frame.Column));
                }
            }

            cursor += length;
        }

        var compiled = tracks.OrderBy(pair => pair.Key.Property).ThenBy(pair => pair.Key.NodeId)
            .ThenBy(pair => pair.Key.TextRangeId).ThenBy(pair => pair.Key.State).Select(pair =>
        {
            var frames = pair.Value;
            if (frames[^1].Time < end)
            {
                frames[^1] = frames[^1] with { Interpolation = KeyframeInterpolation.HOLD };
                frames.Add(new(end, frames[^1].Value, KeyframeInterpolation.HOLD));
            }

            return new AnimationTrack(pair.Key, frames.ToImmutableArray());
        }).ToImmutableArray();
        return new(compiled, intervals.ToImmutable());
    }

    private static AnimationTrackTarget ResolveTarget(EffectScriptKeyframe frame, ProjectLayer layer,
        AnimationTrackTarget? targetContext, SubtitleLine? subtitle)
    {
        var property = EffectScriptPropertyMetadata.GetAnimationProperty(frame.Property);
        if (AnimationPropertyMetadata.IsSubtitleOnlyProperty(property) &&
            (layer.Kind != LayerKind.SUBTITLE || !layer.SubtitleId.HasValue))
        {
            throw new EffectScriptException("字幕排版、分通道模糊和阴影属性需要字幕片段。", frame.Line, frame.Column);
        }
        if (!AnimationPropertyMetadata.IsNodeProperty(property))
        {
            var animationTarget = new AnimationTrackTarget(property, TextRangeId: targetContext?.TextRangeId,
                State: targetContext?.State ?? SubtitleAnimationState.NORMAL);
            if (animationTarget.TextRangeId.HasValue || animationTarget.State != SubtitleAnimationState.NORMAL)
            {
                try
                {
                    SubtitleAnimationTargetValidation.Validate(animationTarget, subtitle, layer.Mask);
                }
                catch (InvalidDataException error)
                {
                    throw new EffectScriptException(error.Message, frame.Line, frame.Column, error);
                }
            }
            return animationTarget;
        }

        if (targetContext is { } context && (context.TextRangeId.HasValue || context.State != SubtitleAnimationState.NORMAL))
        {
            throw new EffectScriptException("蒙版属性不能应用到文字范围或卡拉 OK 视觉状态。", frame.Line, frame.Column);
        }

        var selector = frame.NodeSelector!.Value;
        if (layer.Mask is not VectorClipMask vector || selector.ContourNumber > vector.Contours.Length ||
            selector.NodeNumber > vector.Contours[selector.ContourNumber - 1].Nodes.Length)
        {
            throw new EffectScriptException($"蒙版轮廓 {selector.ContourNumber} 的节点 {selector.NodeNumber} 不存在。", frame.Line, frame.Column);
        }

        return new(property, vector.Contours[selector.ContourNumber - 1].Nodes[selector.NodeNumber - 1].Id);
    }

    private static AnimationValue ResolveBaseValue(EffectScriptKeyframe frame, AnimationTrackTarget animationTarget,
        ProjectLayer layer, SubtitleStyle? subtitleStyle, SubtitleLine? subtitle, bool needsBase)
    {
        if (AnimationPropertyMetadata.IsSubtitleOnlyProperty(animationTarget.Property) && subtitleStyle is null)
        {
            throw new EffectScriptException("字幕排版、分通道模糊和阴影属性需要应用前的字幕样式。", frame.Line, frame.Column);
        }
        if (animationTarget.TextRangeId.HasValue || animationTarget.State != SubtitleAnimationState.NORMAL)
        {
            if (needsBase && !SubtitleAnimationEvaluation.IsBaseValueUniform(layer, subtitle, animationTarget))
            {
                throw new EffectScriptException($"{frame.Property} 的基础值在目标文字范围内混合，请统一该属性或使用从片段起点开始的显式数值。",
                    frame.Line, frame.Column);
            }
            return SubtitleAnimationEvaluation.GetBaseValue(layer, subtitle, animationTarget);
        }
        if (AnimationPropertyMetadata.IsMaskProperty(animationTarget.Property))
        {
            if (layer.Mask is not { } mask)
            {
                throw new EffectScriptException("目标 Clip 尚无蒙版；脚本只动画现有几何。", frame.Line, frame.Column);
            }

            try
            {
                return ClipMaskAnimation.GetBaseValue(mask, animationTarget);
            }
            catch (InvalidDataException error)
            {
                throw new EffectScriptException(error.Message, frame.Line, frame.Column, error);
            }
        }

        return animationTarget.Property switch
        {
            AnimationProperty.POSITION => layer.Transform.Position,
            AnimationProperty.SCALE => layer.Transform.Scale,
            AnimationProperty.ROTATION => layer.Transform.Rotation,
            AnimationProperty.OPACITY => layer.Opacity,
            AnimationProperty.BLUR => layer.Blur,
            AnimationProperty.STROKE_WIDTH => subtitleStyle?.StrokeWidth ?? layer.StrokeWidth,
            AnimationProperty.LETTER_SPACING => subtitleStyle!.LetterSpacing,
            AnimationProperty.FILL_BLUR => subtitleStyle!.FillBlur,
            AnimationProperty.STROKE_BLUR => subtitleStyle!.StrokeBlur,
            AnimationProperty.FONT_SIZE => subtitleStyle!.FontSize,
            AnimationProperty.SHADOW_OFFSET => subtitleStyle!.ShadowOffset,
            AnimationProperty.SHADOW_BLUR => subtitleStyle!.ShadowBlur,
            AnimationProperty.SHADOW_COLOR => subtitleStyle!.ShadowColor,
            AnimationProperty.FILL => subtitleStyle?.Fill ?? layer.Fill,
            AnimationProperty.STROKE => subtitleStyle?.Stroke ?? layer.Stroke,
            AnimationProperty.PATH_PROGRESS => 0,
            _ => throw new EffectScriptException("未知脚本属性。", frame.Line, frame.Column)
        };
    }

    private static double Resolve(EffectScriptValueKind kind, double baseValue, double literal)
    {
        return kind switch
        {
            EffectScriptValueKind.BASE => baseValue,
            EffectScriptValueKind.OFFSET => baseValue + literal,
            EffectScriptValueKind.FACTOR => baseValue * literal,
            _ => literal
        };
    }

    private static void Add(Dictionary<AnimationTrackTarget, List<Keyframe>> tracks, AnimationTrackTarget target,
        EffectScriptKeyframe source, MediaTime time, AnimationValue baseValue, MediaTime origin, bool preserveUnmentionedTime,
        bool reverse = false)
    {
        var value = ResolveValue(source, target, baseValue);
        if (!tracks.TryGetValue(target, out var frames))
        {
            frames = [];
            tracks.Add(target, frames);
            if (time > origin)
            {
                frames.Add(new(origin, baseValue, KeyframeInterpolation.HOLD));
            }
        }

        var key = new Keyframe(time, value, source.Interpolation) { Exponent = source.Exponent, Reverse = reverse };
        if (frames.Count > 0 && frames[^1].Time == time)
        {
            if (!frames[^1].Value.Equals(value))
            {
                throw new EffectScriptException($"{source.Property} 的共享端点存在不同值，零时长段也不能产生跳变。", source.Line, source.Column);
            }

            frames[^1] = key;
        }
        else
        {
            if (source.Progress == 0 && frames.Count > 0)
            {
                if (!preserveUnmentionedTime && !frames[^1].Value.Equals(value))
                {
                    throw new EffectScriptException($"{source.Property} 在未声明区间后发生跳变，请显式声明连续关键帧。", source.Line, source.Column);
                }

                frames[^1] = frames[^1] with { Interpolation = KeyframeInterpolation.HOLD, Reverse = false };
            }

            frames.Add(key);
        }
    }

    private static AnimationValue ResolveValue(EffectScriptKeyframe source, AnimationTrackTarget target, AnimationValue baseValue)
    {
        var value = baseValue;
        if (source.Value.Kind != EffectScriptValueKind.BASE)
        {
            var literal = source.Value.Literal!.Value;
            for (var component = 0; component < value.ComponentCount; component++)
            {
                value = value.WithComponent(component, Resolve(source.Value.Kind, baseValue.GetComponent(component), literal.GetComponent(component)));
            }
        }

        for (var component = 0; component < value.ComponentCount; component++)
        {
            var number = value.GetComponent(component);
            if (!double.IsFinite(number) || number < AnimationPropertyMetadata.GetMinimum(target.Property, component) ||
                number > AnimationPropertyMetadata.GetMaximum(target.Property, component))
            {
                throw new EffectScriptException($"{source.Property} 的求值结果超出项目允许范围。", source.Line, source.Column);
            }
        }

        return value;
    }
}
