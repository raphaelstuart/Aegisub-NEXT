using System.Collections.Immutable;
using System.Text;
using AegiNext.Core.Projects;
using AegiNext.Core.Timing;

namespace AegiNext.Application.SubtitleFormats;

internal sealed class AssTextAnimationExport(SubtitleLine line, ProjectLayer layer,
    ImmutableArray<SubtitleFormatDiagnostic>.Builder diagnostics, bool projection = false)
{
    private readonly HashSet<string> reported = [];

    internal static bool Handles(AnimationTrack track) => track.Target.TextRangeId is not null ||
        track.Target.State != SubtitleAnimationState.NORMAL || track.Property is AnimationProperty.FONT_SIZE or
        AnimationProperty.SHADOW_OFFSET or AnimationProperty.SHADOW_BLUR or AnimationProperty.SHADOW_COLOR or
        AnimationProperty.FILL or AnimationProperty.STROKE ||
        track.Property is AnimationProperty.LETTER_SPACING or AnimationProperty.STROKE_WIDTH or AnimationProperty.FILL_BLUR or AnimationProperty.STROKE_BLUR &&
            AssNumericAnimation.FromTrack(track).Approximate ||
        track.Property == AnimationProperty.SCALE && track.IsOrdered && track.Transforms.Any(operation => operation.ComponentMask != 0);

    internal bool HasMatchingShadowBlur(AnimationProperty blurProperty)
    {
        var blur = layer.Tracks.FirstOrDefault(track => track.Property == blurProperty &&
            track.Target.TextRangeId is null && track.Target.State == SubtitleAnimationState.NORMAL);
        return blur is not null && layer.Tracks.Any(track =>
            track.Target == blur.Target with { Property = AnimationProperty.SHADOW_BLUR } && Equivalent(blur, track));
    }

    internal string Write(int offset, SubtitleStyle style, bool karaoke, MediaTime origin, ScenePoint scale, double rotation)
    {
        var selected = new Dictionary<(AnimationProperty Property, SubtitleAnimationState State), AnimationTrack>();
        foreach (var track in layer.Tracks.Where(track => (Handles(track) || projection &&
            track.Property is AnimationProperty.LETTER_SPACING or AnimationProperty.STROKE_WIDTH or AnimationProperty.FILL_BLUR or AnimationProperty.STROKE_BLUR) && track.Target.TextRangeId is null))
        {
            selected[(track.Property, track.Target.State)] = track;
        }
        var matching = line.AnimationRanges.Where(range => range.Utf16Start <= offset && offset < range.Utf16Start + range.Utf16Length).ToArray();
        if (matching.Any(range => range.Offset != default || layer.Tracks.Any(track =>
            track.Target.TextRangeId == range.Id && track.Property == AnimationProperty.POSITION)))
        {
            Report("Ass.RangeTranslation", "ASS 无法表达独立文字范围的排版后位移，已省略范围位移；原生范围与位移动画保留在工程中。");
        }
        foreach (var range in matching)
        {
            foreach (var track in layer.Tracks.Where(track => track.Target.TextRangeId == range.Id))
            {
                selected[(track.Property, track.Target.State)] = track;
            }
        }
        if (karaoke && (selected.ContainsKey((AnimationProperty.FILL, SubtitleAnimationState.INACTIVE)) ||
            KaraokeVisualStyleResolver.RangeStyleAt(line, offset, KaraokeVisualState.INACTIVE)?.Fill is not null))
        {
            selected.Remove((AnimationProperty.FILL, SubtitleAnimationState.NORMAL));
        }
        if (matching.Length > 1)
        {
            Report("Ass.RangeOverlap", "重叠文字范围的样式按范围顺序转换，叠加几何和轴心不能在 ASS 中精确表达，已省略重叠范围几何。");
            foreach (var property in new[] { AnimationProperty.SCALE, AnimationProperty.ROTATION })
            {
                if (selected.GetValueOrDefault((property, SubtitleAnimationState.NORMAL))?.Target.TextRangeId is not null)
                {
                    selected.Remove((property, SubtitleAnimationState.NORMAL));
                }
            }
        }
        var result = new StringBuilder();
        if (matching.Length == 1)
        {
            var range = matching[0];
            if (!projection && RemoveIdentityGeometryTrack(selected, AnimationProperty.SCALE, new ScenePoint(1, 1)))
            {
                range = range with { Scale = new(1, 1) };
            }
            if (!projection && RemoveIdentityGeometryTrack(selected, AnimationProperty.ROTATION, 0))
            {
                range = range with { Rotation = 0 };
            }
            matching[0] = range;
            var hasScale = range.Scale != new ScenePoint(1, 1) ||
                selected.GetValueOrDefault((AnimationProperty.SCALE, SubtitleAnimationState.NORMAL))?.Target.TextRangeId is not null;
            var hasRotation = range.Rotation != 0 ||
                selected.GetValueOrDefault((AnimationProperty.ROTATION, SubtitleAnimationState.NORMAL))?.Target.TextRangeId is not null;
            if (hasScale || hasRotation)
            {
                Report("Ass.TextRangeGeometry", "原生文字范围在排版后变换，ASS 在字形布局中应用缩放和旋转，边缘、阴影及片段位置可能不同。");
            }
            if (range.Scale.X < 0 || range.Scale.Y < 0)
            {
                Report("Ass.TransformScale", "ASS 不能保留文字范围的镜像，已省略负缩放分量并保留可转换的分量。");
            }
            if (range.Pivot != SubtitleAnimationPivot.SUBTITLE_ANCHOR && (hasScale || hasRotation))
            {
                Report("Ass.RangePivot", "文字范围使用独立中心轴心，ASS 只能采用字幕共享轴心；范围几何位置可能不同。");
            }
            if (projection || hasScale)
            {
                result.Append("\\fscx").Append(Number((range.Scale.X < 0 ? 1 : range.Scale.X) * scale.X * 100))
                    .Append("\\fscy").Append(Number((range.Scale.Y < 0 ? 1 : range.Scale.Y) * scale.Y * 100));
            }
            if (projection || hasRotation)
            {
                result.Append("\\frz").Append(Number(-(range.Rotation + rotation)));
            }
        }
        foreach (var pair in selected)
        {
            var track = pair.Value;
            if (track.Target.TextRangeId is not null && track.Property == AnimationProperty.POSITION)
            {
                continue;
            }
            var values = track.Keyframes.Select(frame => frame.Value).Concat(track.Transforms.Select(operation => operation.Value));
            if (track.InitialValue is { } initial)
            {
                values = values.Prepend(initial);
            }
            if (values.Any(value => value.IsColor && (value.Color.Red is < 0 or > 1 || value.Color.Green is < 0 or > 1 || value.Color.Blue is < 0 or > 1)))
            {
                Report("Ass.ColorRange", "ASS 8 位 sRGB 颜色限制了动画中的 HDR 或负颜色，已裁切并量化输出颜色。");
            }
            if (track.Property == AnimationProperty.SCALE && values.Any(value => value.Vector.X < 0 || value.Vector.Y < 0))
            {
                Report("Ass.TransformScale", "ASS 不能保留缩放动画的镜像阶段，已省略负缩放分量，其他可转换分量继续输出。");
            }
            if (track.Target.State != SubtitleAnimationState.NORMAL && !karaoke)
            {
                if (!projection)
                {
                    Report("Ass.DormantKaraokeAnimation", "未计时文字保存的 ACTIVE/INACTIVE 动画无法写入 ASS；已按普通正文导出，工程中的状态动画保持不变。");
                }
                continue;
            }
            var channel = track.Target.State == SubtitleAnimationState.INACTIVE ||
                karaoke && track.Property == AnimationProperty.FILL && track.Target.State == SubtitleAnimationState.NORMAL ? "2" : "1";
            if (track.Target.State != SubtitleAnimationState.NORMAL && track.Property != AnimationProperty.FILL)
            {
                Report("Ass.KaraokeAnimation", "ASS 描边、阴影和排版只有共享通道，不能保留激活与未激活状态的独立连续动画，已省略该状态轨道。");
                continue;
            }
            if (track.Property == AnimationProperty.SHADOW_BLUR)
            {
                var blurProperty = style.StrokeWidth > 0 ? AnimationProperty.STROKE_BLUR : AnimationProperty.FILL_BLUR;
                var blur = selected.GetValueOrDefault((blurProperty, track.Target.State)) ?? layer.Tracks.FirstOrDefault(candidate => candidate.Property == blurProperty && candidate.Target == track.Target with { Property = blurProperty });
                if (blur is null || !Equivalent(blur, track))
                {
                    Report("Ass.ShadowBlur", "ASS 模糊同时影响文字或描边及阴影，独立阴影模糊动画无法精确表达，已省略阴影模糊轨道。");
                }
                continue;
            }
            if (track.Property is AnimationProperty.FILL_BLUR or AnimationProperty.STROKE_BLUR &&
                (track.Property == AnimationProperty.FILL_BLUR ? style.StrokeWidth > 0 : style.StrokeWidth <= 0))
            {
                Report("Ass.BlurAnimation", "ASS 只有一个边缘模糊通道，已省略当前描边状态下不可见的独立模糊动画。");
                continue;
            }
            if (track.Property is AnimationProperty.FONT_SIZE or AnimationProperty.LETTER_SPACING)
            {
                Report("Ass.TextLayoutAnimation", "连续字号或字距按 ASS 标签导出；原生逐帧排版与 ASS 行布局、自动换行及锚点计算可能不同。");
            }
            var rangeGeometry = track.Target.TextRangeId is { } rangeId ? matching.FirstOrDefault(range => range.Id == rangeId) : null;
            var shadowRotation = rotation + (rangeGeometry?.Rotation ?? 0);
            AnimationValue OutputValue(AnimationValue value)
            {
                if (track.Property != AnimationProperty.SHADOW_OFFSET)
                {
                    return value;
                }
                var parts = AssTransformMath.SinCos(shadowRotation);
                var x = value.Vector.X * scale.X * (rangeGeometry?.Scale.X ?? 1);
                var y = value.Vector.Y * scale.Y * (rangeGeometry?.Scale.Y ?? 1);
                return new ScenePoint(parts.Cosine * x - parts.Sine * y, parts.Sine * x + parts.Cosine * y);
            }
            string Tags(AnimationValue value, int mask, AnimationTransformMode mode) => ValueTags(track.Property, OutputValue(value),
                mask, mode, channel, track.Target.TextRangeId is null ? new(1, 1) : scale,
                track.Target.TextRangeId is null ? 0 : rotation,
                track.Property == AnimationProperty.SHADOW_OFFSET ? 1 : Math.Sqrt(Math.Abs(scale.X * scale.Y)));
            double[] Components(AnimationValue value)
            {
                value = OutputValue(value);
                return Enumerable.Range(0, value.ComponentCount)
                    .Select(component => value.IsColor && component < 3 ? Encode(value.GetComponent(component)) : value.GetComponent(component)).ToArray();
            }
            var forceSampling = track.Property == AnimationProperty.SHADOW_OFFSET && shadowRotation != 0 &&
                (!track.IsOrdered || track.Transforms.Any(operation => operation.ComponentMask != 0));
            result.Append(AssTextAnimationTrackWriter.Write(track, Tags, Components, origin,
                layer.AnimationOffset + layer.End - layer.Start, line.Id, diagnostics, forceSampling));
        }
        return result.ToString();
    }

    private bool RemoveIdentityGeometryTrack(Dictionary<(AnimationProperty Property, SubtitleAnimationState State), AnimationTrack> selected,
        AnimationProperty property, AnimationValue identity)
    {
        var key = (property, SubtitleAnimationState.NORMAL);
        if (selected.GetValueOrDefault(key) is not { Target.TextRangeId: not null } track ||
            !AssMoveConversion.TryConstant(track, out var value) || value != identity)
        {
            return false;
        }
        selected.Remove(key);
        var whole = layer.Tracks.FirstOrDefault(candidate => candidate.Property == property &&
            candidate.Target.TextRangeId is null && candidate.Target.State == SubtitleAnimationState.NORMAL && Handles(candidate));
        if (whole is not null)
        {
            selected[key] = whole;
        }
        return true;
    }

    private string ValueTags(AnimationProperty property, AnimationValue value, int mask, AnimationTransformMode mode,
        string channel, ScenePoint scale, double rotation, double appearanceScale)
    {
        var result = new StringBuilder();
        bool Component(int index) => mask == 0 || (mask & (1 << index)) != 0;
        if (value.IsColor)
        {
            var color = value.Color;
            var number = property == AnimationProperty.FILL ? channel : property == AnimationProperty.STROKE ? "3" : "4";
            if (Component(0) || Component(1) || Component(2))
            {
                result.Append('\\').Append(number).Append('c').Append(AssFormatValues.Color(color, false));
            }
            if (Component(3))
            {
                result.Append('\\').Append(number).Append('a').Append(AssFormatValues.Alpha(color));
            }
            return result.ToString();
        }
        if (property == AnimationProperty.SCALE)
        {
            if (Component(0) && value.Vector.X >= 0)
            {
                result.Append("\\fscx").Append(Number(value.Vector.X * scale.X * 100));
            }
            if (Component(1) && value.Vector.Y >= 0)
            {
                result.Append("\\fscy").Append(Number(value.Vector.Y * scale.Y * 100));
            }
        }
        else if (property == AnimationProperty.SHADOW_OFFSET)
        {
            if (Component(0))
            {
                result.Append("\\xshad").Append(Number(value.Vector.X * appearanceScale));
            }
            if (Component(1))
            {
                result.Append("\\yshad").Append(Number(value.Vector.Y * appearanceScale));
            }
        }
        else
        {
            var tag = property switch
            {
                AnimationProperty.FONT_SIZE => "fs", AnimationProperty.LETTER_SPACING => "fsp",
                AnimationProperty.STROKE_WIDTH => "bord", AnimationProperty.FILL_BLUR or AnimationProperty.STROKE_BLUR => "blur",
                AnimationProperty.ROTATION => "frz", _ => null
            };
            if (tag is not null)
            {
                var amount = property switch
                {
                    AnimationProperty.FONT_SIZE when mode == AnimationTransformMode.MULTIPLY_BY => (value.Scalar - 1) * 10,
                    AnimationProperty.STROKE_WIDTH => value.Scalar * appearanceScale,
                    AnimationProperty.FILL_BLUR or AnimationProperty.STROKE_BLUR => value.Scalar * appearanceScale / AssBlurConversion.SigmaPerUnit,
                    AnimationProperty.ROTATION => -(value.Scalar + rotation), _ => value.Scalar
                };
                if (tag == "blur" && !projection)
                {
                    AssExportPrecision.AddBlurRange(amount, line.Id, diagnostics, reported);
                }
                result.Append('\\').Append(tag);
                if (mode == AnimationTransformMode.MULTIPLY_BY && amount >= 0)
                {
                    result.Append('+');
                }
                result.Append(Number(amount));
            }
        }
        return result.ToString();
    }

    private string Number(double amount)
    {
        AssExportPrecision.AddNumbers(line.Id, diagnostics, reported, amount);
        return AssFormatValues.Number(amount);
    }

    private static bool Equivalent(AnimationTrack first, AnimationTrack second)
    {
        if (first.IsOrdered != second.IsOrdered)
        {
            return false;
        }
        return first.IsOrdered ? first.InitialValue == second.InitialValue && first.Transforms.Length == second.Transforms.Length &&
            first.Transforms.Zip(second.Transforms).All(pair => pair.First with { Id = pair.Second.Id } == pair.Second) : first.Keyframes.SequenceEqual(second.Keyframes);
    }

    private static double Encode(double value)
    {
        var bounded = Math.Clamp(value, 0, 1);
        return bounded <= 0.0031308 ? bounded * 12.92 : 1.055 * Math.Pow(bounded, 1 / 2.4) - 0.055;
    }

    private void Report(string code, string message)
    {
        if (reported.Add(code))
        {
            diagnostics.Add(new(code, message, SubtitleId: line.Id));
        }
    }
}
