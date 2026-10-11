using System.Collections.Immutable;
using System.Globalization;
using AegiNext.Core.Projects;
using AegiNext.Core.Timing;

namespace AegiNext.Application.SubtitleFormats;

internal sealed class AssEventConversionContext
{
    private readonly ProjectLayer layer;
    private readonly AssTextAnimationExport textAnimation;
    private readonly SubtitleLine line;
    private readonly ImmutableArray<SubtitleFormatDiagnostic>.Builder diagnostics;
    private readonly HashSet<AnimationProperty> consumed = [];
    private readonly HashSet<string> reported = [];
    private readonly ScenePoint scale;
    private readonly double rotation;
    private readonly (double Sine, double Cosine) rotationParts;
    private readonly ScenePoint placementOffset;
    private readonly ScenePoint position;
    private readonly AssLinearMove? move;
    private readonly AssRotationOriginExport? rotationOrigin;
    private readonly AssOpacityEnvelope? opacity;
    private readonly bool hasPlacement;
    private readonly bool convertedPath;
    private readonly double? letterSpacing;
    private readonly double? fillBlur;
    private readonly double? strokeBlur;
    private readonly double? strokeWidth;
    private readonly Dictionary<(AnimationProperty Property, int Component), AssNumericAnimation> numeric = [];

    internal AssEventConversionContext(ProjectDocument document, ProjectLayer layer, SubtitleLine line,
        ISubtitlePlacementMeasurer? measurer, ImmutableArray<SubtitleFormatDiagnostic>.Builder diagnostics)
    {
        var originalLayer = layer;
        textAnimation = new(line, layer, diagnostics);
        layer = layer with { Tracks = layer.Tracks.Where(track => !AssTextAnimationExport.Handles(track)).ToImmutableArray() };
        this.layer = layer;
        this.line = line;
        this.diagnostics = diagnostics;
        letterSpacing = Constant(AnimationProperty.LETTER_SPACING)?.Scalar;
        fillBlur = Constant(AnimationProperty.FILL_BLUR)?.Scalar;
        strokeBlur = Constant(AnimationProperty.STROKE_BLUR)?.Scalar;
        strokeWidth = Constant(AnimationProperty.STROKE_WIDTH)?.Scalar;
        var alignmentPivot = AssTextParser.Pivot(line.Style.Alignment);
        var stylePosition = line.Style.Position ?? SubtitlePosition.FromAlignment(line.Style.Alignment, line.Style.Margins);
        var automaticPlacement = line.Style.Position is null && layer.Transform == new LayerTransform() &&
            layer.MotionPath is null && !layer.Tracks.Any(track => track.Property is AnimationProperty.POSITION or AnimationProperty.SCALE or AnimationProperty.ROTATION);
        var spacingAnimation = Dynamic(AnimationProperty.LETTER_SPACING);
        if (spacingAnimation is not null)
        {
            if (stylePosition.Pivot == alignmentPivot && (automaticPlacement || line.Style.Position is not null || line.Style.WrapMode == SubtitleWrapMode.NO_WRAP))
            {
                AddNumeric(spacingAnimation);
                letterSpacing = spacingAnimation.Initial;
            }
            else
            {
                Report("Ass.TransformPivotAnimation", "动态字距会改变自动定位或自定义轴心的补偿位置，ASS 无法保持该位移，已仅省略字距动画。");
            }
        }
        foreach (var property in new[] { AnimationProperty.STROKE_WIDTH, AnimationProperty.FILL_BLUR, AnimationProperty.STROKE_BLUR })
        {
            if (Dynamic(property) is { } animation)
            {
                if (KaraokeOverrides(property))
                {
                    Report("Ass.KaraokeAnimation", "卡拉 OK 的独立视觉覆盖与整行描边或模糊动画同时存在，ASS 不能保留其覆盖顺序，已仅省略冲突的整行视觉动画。");
                }
                else
                {
                    AddNumeric(animation);
                }
            }
        }
        var opacityTrack = layer.Tracks.FirstOrDefault(track => track.Property == AnimationProperty.OPACITY);
        opacity = opacityTrack is null ? new(layer.Opacity, []) : AssOpacityConversion.FromTrack(opacityTrack);
        if (opacityTrack is not null)
        {
            if (opacity is null)
            {
                Report("Ass.OpacityAnimation", "ASS 淡入淡出不能保留这条多段或重叠透明度动画，导出时已省略该动画。");
            }
            else
            {
                consumed.Add(AnimationProperty.OPACITY);
            }
        }
        if (opacity is { Approximate: true })
        {
            Report("Ass.OpacityApproximation", "ASS 淡入淡出仅支持线性变化，已保留透明度端点、延迟和起止时间，将缓动近似为线性。");
        }
        var transform = layer.Transform;
        scale = Constant(AnimationProperty.SCALE)?.Vector ?? transform.Scale;
        rotation = Constant(AnimationProperty.ROTATION)?.Scalar ?? transform.Rotation;
        var scaleXAnimation = Dynamic(AnimationProperty.SCALE, 0);
        var scaleYAnimation = Dynamic(AnimationProperty.SCALE, 1);
        var rotationAnimation = Dynamic(AnimationProperty.ROTATION);
        if (scale.X < 0 || scale.Y < 0)
        {
            Report("Ass.TransformScale", "ASS 导出已省略负缩放分量；项目中的镜像变换保持不变。");
            scale = new(scale.X >= 0 ? scale.X : 1, scale.Y >= 0 ? scale.Y : 1);
        }
        position = transform.Position;
        var positionTrack = layer.Tracks.FirstOrDefault(track => track.Property == AnimationProperty.POSITION);
        if (positionTrack is not null && AssMoveConversion.FromTrack(positionTrack) is { } converted)
        {
            consumed.Add(AnimationProperty.POSITION);
            move = converted;
            position = converted.First;
        }
        if (layer.MotionPath is { } path && (move is null || move.First == move.Last) &&
            !layer.Tracks.Any(track => track.Property == AnimationProperty.PATH_PROGRESS) &&
            AssMoveConversion.FromPath(path, position) is { } pathMove)
        {
            move = pathMove;
            position = pathMove.First;
            convertedPath = true;
        }
        if (move is { Approximate: true })
        {
            Report("Ass.MoveApproximation", "ASS move 仅支持匀速直线，已保留移动路径和起止时间，将速度变化近似为匀速。");
        }
        hasPlacement = line.Style.Position is not null || position != default || transform.Pivot != default ||
            scale != new ScenePoint(1, 1) || !rotation.Equals(0d) || move is not null && move.First != move.Last ||
            scaleXAnimation is not null || rotationAnimation is not null;
        var basis = new ScenePoint(stylePosition.Anchor.X * document.Width + stylePosition.Offset.X,
            stylePosition.Anchor.Y * document.Height + stylePosition.Offset.Y);
        var delta = new ScenePoint(-transform.Pivot.X, -transform.Pivot.Y);
        var needsMeasurement = line.Style.Position is null || stylePosition.Pivot != alignmentPivot;
        if (hasPlacement && needsMeasurement && measurer is not null)
        {
            var measuredLine = letterSpacing is { } spacing ? line with
            {
                Style = line.Style with { LetterSpacing = spacing },
                InlineSpans = line.InlineSpans.Select(span => span with
                {
                    Style = span.Style with { LetterSpacing = spacing }
                }).ToImmutableArray()
            } : line;
            var metrics = measurer.Measure(document, measuredLine);
            basis = metrics.BasePosition;
            delta = new(metrics.BoundsOrigin.X + alignmentPivot.X * metrics.BoundsSize.X - metrics.Pivot.X - transform.Pivot.X,
                metrics.BoundsOrigin.Y + alignmentPivot.Y * metrics.BoundsSize.Y - metrics.Pivot.Y - transform.Pivot.Y);
        }
        else if (hasPlacement && needsMeasurement)
        {
            Report("Ass.PlacementMeasurement", "未提供字体排版测量，定位使用九宫格边距近似；无法补偿真实字形边界和自定义文字轴心。");
        }
        if (hasPlacement)
        {
            rotationOrigin = AssRotationOriginExport.Create(originalLayer, line, basis, delta, scale, rotation,
                position, move, convertedPath, needsMeasurement, measurer is not null, out var originReason);
            if (rotationOrigin is not null)
            {
                AssExportPrecision.AddNumbers(line.Id, diagnostics, rotationOrigin.Origin.X, rotationOrigin.Origin.Y);
            }
            else if (transform.Pivot != default)
            {
                Report("Ass.RotationOrigin", $"当前几何组合不能生成可逆的 ASS org 补偿（{originReason}），已采用原有位置、轴心与动画转换。");
            }
        }
        if (AcceptScale(scaleXAnimation, delta.X, needsMeasurement && measurer is null && stylePosition.Pivot != alignmentPivot))
        {
            scale = scale with { X = scaleXAnimation!.Initial };
        }
        if (AcceptScale(scaleYAnimation, delta.Y, needsMeasurement && measurer is null && stylePosition.Pivot != alignmentPivot))
        {
            scale = scale with { Y = scaleYAnimation!.Initial };
        }
        if (scaleXAnimation is not null && (!numeric.ContainsKey((AnimationProperty.SCALE, 0)) || !numeric.ContainsKey((AnimationProperty.SCALE, 1))))
        {
            consumed.Remove(AnimationProperty.SCALE);
        }
        if (rotationAnimation is not null)
        {
            if (rotationOrigin is not null || rotationAnimation.Operations.IsEmpty || Math.Abs(delta.X * scale.X) < 1e-9 && Math.Abs(delta.Y * scale.Y) < 1e-9 &&
                !(needsMeasurement && measurer is null && stylePosition.Pivot != alignmentPivot))
            {
                AddNumeric(rotationAnimation);
                rotation = rotationAnimation.Initial;
            }
            else
            {
                Report("Ass.TransformPivotAnimation", "旋转动画需要随时间改变自定义轴心的补偿位置，ASS 无法保持该位移，已仅省略旋转动画。");
            }
        }
        rotationParts = AssTransformMath.SinCos(rotation);
        if (scale != new ScenePoint(1, 1) || numeric.ContainsKey((AnimationProperty.SCALE, 0)) || numeric.ContainsKey((AnimationProperty.SCALE, 1)))
        {
            Report("Ass.TransformLayout", "ASS 的缩放在自动换行前生效，项目在排版后缩放；长句的换行和文字边界可能不同。");
        }
        if (!ExactScale(scale.X) || !ExactScale(scale.Y))
        {
            Report("Ass.NumberPrecision", "导出的 ASS 缩放百分比保留最多 9 位小数，部分缩放数值已取近似值。");
        }
        var compensation = TransformVector(delta);
        placementOffset = new(basis.X + compensation.X, basis.Y + compensation.Y);
        if (layer.Tracks.Any(track => !AnimationPropertyMetadata.IsMaskProperty(track.Property) && !consumed.Contains(track.Property)) ||
            layer.MotionPath is not null && !convertedPath || layer.Blend != BlendMode.NORMAL)
        {
            Report("Subtitle.Composition", "字幕格式不能保留部分项目合成或动画；已保留可以转换的位置、缩放、旋转和透明度。");
        }
    }

    internal string GeometryTags => scale == new ScenePoint(1, 1) && rotation.Equals(0d) ? string.Empty :
        "\\fscx" + AssFormatValues.Number(scale.X * 100) + "\\fscy" + AssFormatValues.Number(scale.Y * 100) +
        "\\frz" + AssFormatValues.Number(rotation == 0 ? 0 : -rotation);

    internal string TextAnimationTags(int offset, SubtitleStyle style, bool karaoke, MediaTime origin) =>
        textAnimation.Write(offset, style, karaoke, origin, scale, rotation);

    internal SubtitleStyle ApplyTypographyAnimations(SubtitleStyle style)
    {
        return style with
        {
            LetterSpacing = Initial(AnimationProperty.LETTER_SPACING, letterSpacing ?? style.LetterSpacing),
            FillBlur = Initial(AnimationProperty.FILL_BLUR, fillBlur ?? style.FillBlur),
            StrokeBlur = Initial(AnimationProperty.STROKE_BLUR, strokeBlur ?? style.StrokeBlur),
            StrokeWidth = Initial(AnimationProperty.STROKE_WIDTH, strokeWidth ?? style.StrokeWidth)
        };
    }

    internal MediaTime EventOrigin(AssMaskSample sample, MediaTime timeOffset)
    {
        return AssEventClock.Origin(line.Start, sample.Start, layer.AnimationOffset, timeOffset);
    }

    internal string AnimationTags(SubtitleStyle style, MediaTime origin)
    {
        var result = new System.Text.StringBuilder();
        var appearanceScale = Math.Sqrt(scale.X * scale.Y);
        var hasScale = numeric.ContainsKey((AnimationProperty.SCALE, 0)) || numeric.ContainsKey((AnimationProperty.SCALE, 1));
        var hasRotation = numeric.ContainsKey((AnimationProperty.ROTATION, 0));
        foreach (var animation in numeric.Values)
        {
            var tag = animation.Property switch
            {
                AnimationProperty.LETTER_SPACING => "\\fsp",
                AnimationProperty.STROKE_WIDTH => "\\bord",
                AnimationProperty.FILL_BLUR when style.StrokeWidth <= 0 => "\\blur",
                AnimationProperty.STROKE_BLUR when style.StrokeWidth > 0 => "\\blur",
                AnimationProperty.SCALE => animation.Component == 0 ? "\\fscx" : "\\fscy",
                AnimationProperty.ROTATION => "\\frz",
                _ => null
            };
            if (tag is null)
            {
                Report(animation.Property == AnimationProperty.FILL_BLUR ? "Ass.FillBlurAnimation" : "Ass.StrokeBlurAnimation",
                    "ASS 单一模糊通道无法同时保留这段文字的独立填充与描边模糊动画，已仅省略当前未选中的模糊动画。");
                continue;
            }
            var factor = animation.Property switch
            {
                AnimationProperty.SCALE => 100,
                AnimationProperty.ROTATION => -1,
                AnimationProperty.STROKE_WIDTH => appearanceScale,
                AnimationProperty.FILL_BLUR or AnimationProperty.STROKE_BLUR => appearanceScale / AssBlurConversion.SigmaPerUnit,
                _ => 1
            };
            result.Append(animation.Write(tag, factor, origin, line.Id, diagnostics));
        }
        var uniform = UniformScaleAnimation();
        if (uniform is not null && style.StrokeWidth > 0 && !KaraokeOverrides(AnimationProperty.STROKE_WIDTH) && !numeric.ContainsKey((AnimationProperty.STROKE_WIDTH, 0)))
        {
            result.Append(uniform.Write("\\bord", style.StrokeWidth, origin, line.Id, diagnostics));
        }
        var blurProperty = style.StrokeWidth > 0 ? AnimationProperty.STROKE_BLUR : AnimationProperty.FILL_BLUR;
        if (uniform is not null && AssBlurConversion.Sigma(style) > 0 && !KaraokeOverrides(blurProperty) && !numeric.ContainsKey((blurProperty, 0)))
        {
            result.Append(uniform.Write("\\blur", AssBlurConversion.Sigma(style) / AssBlurConversion.SigmaPerUnit, origin, line.Id, diagnostics));
        }
        if ((hasScale || hasRotation) && (style.ShadowColor.Alpha > 0 && style.ShadowOffset != default ||
            hasScale && (uniform is null || numeric.ContainsKey((AnimationProperty.STROKE_WIDTH, 0)) ||
                numeric.ContainsKey((blurProperty, 0)) || KaraokeOverrides(AnimationProperty.STROKE_WIDTH) || KaraokeOverrides(blurProperty)) &&
            (style.StrokeWidth > 0 || style.FillBlur > 0 || style.StrokeBlur > 0 || style.ShadowBlur > 0)))
        {
            Report("Ass.TransformAppearanceAnimation", "缩放或旋转动画已保留，但 ASS 无法同步保留独立描边、模糊或阴影的全部变换补偿，部分外观按初始变换近似。");
        }
        if (style.ShadowColor.Alpha > 0 && (numeric.ContainsKey((blurProperty, 0)) && !textAnimation.HasMatchingShadowBlur(blurProperty) ||
            uniform is not null && !style.ShadowBlur.Equals(AssBlurConversion.Sigma(style))))
        {
            Report("Ass.ShadowBlur", "ASS 的模糊动画同时改变阴影模糊，无法独立保留项目的阴影模糊外观。");
        }
        if (numeric.TryGetValue((AnimationProperty.STROKE_WIDTH, 0), out var border) &&
            (border.Initial == 0 || border.Operations.Any(operation => operation.Value == 0)) &&
            (border.Initial > 0 || border.Operations.Any(operation => operation.Value > 0)) &&
            (style.FillBlur > 0 || style.StrokeBlur > 0))
        {
            Report("Ass.BlurAnimation", "描边动画可能切换 ASS 模糊所作用的通道，独立填充与描边模糊已按初始描边状态选择。");
        }
        return result.ToString();
    }

    private AssNumericAnimation? UniformScaleAnimation()
    {
        if (numeric.TryGetValue((AnimationProperty.SCALE, 0), out var x) &&
            numeric.TryGetValue((AnimationProperty.SCALE, 1), out var y) && x.Initial.Equals(y.Initial) &&
            x.Operations.SequenceEqual(y.Operations))
        {
            return x;
        }
        return null;
    }

    private double Initial(AnimationProperty property, double fallback)
    {
        return numeric.TryGetValue((property, 0), out var animation) ? animation.Initial : fallback;
    }

    private AssNumericAnimation? Dynamic(AnimationProperty property, int component = 0)
    {
        var track = layer.Tracks.FirstOrDefault(candidate => candidate.Property == property);
        return track is null || consumed.Contains(property) ? null : AssNumericAnimation.FromTrack(track, component);
    }

    private void AddNumeric(AssNumericAnimation animation)
    {
        numeric.Add((animation.Property, animation.Component), animation);
        consumed.Add(animation.Property);
        if (animation.Approximate)
        {
            Report("Ass.TransformCurveApproximation", "ASS 数值变换不能直接保留部分缓动或已裁剪的曲线相位，已保留端点和时间并近似为线性。");
        }
    }

    private bool AcceptScale(AssNumericAnimation? animation, double delta, bool unknownPivot)
    {
        if (animation is null)
        {
            return false;
        }
        if (animation.HasNegative)
        {
            Report("Ass.TransformScale", "ASS 不支持这条缩放分量中的负值，已仅省略该分量动画，零缩放仍可转换。");
            return false;
        }
        if (!animation.Operations.IsEmpty && (Math.Abs(delta) >= 1e-9 || unknownPivot))
        {
            Report("Ass.TransformPivotAnimation", "缩放动画需要随时间改变自定义轴心的补偿位置，ASS 无法保持该位移，已仅省略冲突的缩放分量动画。");
            return false;
        }
        AddNumeric(animation);
        return true;
    }

    private bool KaraokeOverrides(AnimationProperty property)
    {
        if (line.KaraokeStyle is not null && !line.Karaoke.IsEmpty)
        {
            return true;
        }
        return line.Karaoke.Any(segment => segment.HighlightKind == KaraokeHighlightKind.OUTLINE_STEP) ||
            line.KaraokeStyleSpans.Any(span => line.Karaoke.Any(segment => span.Utf16Start < segment.Utf16Start + segment.Utf16Length &&
                segment.Utf16Start < span.Utf16Start + span.Utf16Length) && (Overrides(span.ActiveStyle) || Overrides(span.InactiveStyle)));

        bool Overrides(KaraokeVisualStyleOverride? visual)
        {
            return property switch
            {
                AnimationProperty.STROKE_WIDTH => visual?.StrokeWidth.HasValue == true,
                AnimationProperty.FILL_BLUR => visual?.FillBlur.HasValue == true || visual?.StrokeWidth.HasValue == true,
                AnimationProperty.STROKE_BLUR => visual?.StrokeBlur.HasValue == true || visual?.StrokeWidth.HasValue == true,
                _ => false
            };
        }
    }

    internal SubtitleStyle ConvertStyle(SubtitleStyle style)
    {
        if (scale == new ScenePoint(1, 1) && rotation.Equals(0d))
        {
            return style;
        }
        if (!scale.X.Equals(scale.Y) && style.StrokeWidth > 0 && style.Stroke.Alpha > 0)
        {
            Report("Ass.TransformAppearance", "非等比缩放的描边已按两轴缩放的几何平均值近似，文字缩放和阴影方向仍保留。");
        }
        if (!scale.X.Equals(scale.Y) && (style.FillBlur > 0 || style.StrokeBlur > 0 || style.ShadowBlur > 0))
        {
            Report("Ass.TransformAppearance", "非等比缩放的模糊已按两轴缩放的几何平均值近似，水平与垂直扩散范围不能同时保持。");
        }
        AssExportPrecision.AddNumbers(line.Id, diagnostics, -rotation);
        return style with
        {
            StrokeWidth = style.StrokeWidth * Math.Sqrt(scale.X * scale.Y),
            FillBlur = style.FillBlur * Math.Sqrt(scale.X * scale.Y),
            StrokeBlur = style.StrokeBlur * Math.Sqrt(scale.X * scale.Y),
            ShadowBlur = style.ShadowBlur * Math.Sqrt(scale.X * scale.Y),
            ShadowOffset = TransformVector(style.ShadowOffset)
        };
    }

    internal string PlacementTags(AssMaskSample sample, MediaTime timeOffset)
    {
        var alignment = "{\\an" + AssFormatValues.Alignment(line.Style.Alignment).ToString(CultureInfo.InvariantCulture);
        if (!hasPlacement)
        {
            return alignment + "}";
        }
        if (rotationOrigin is not null)
        {
            alignment += "\\org(" + Point(rotationOrigin.Origin) + ")";
        }
        var origin = new MediaTime((sample.Start + timeOffset).ToTimestamp(new(1, 100), MediaTimeRounding.FLOOR).Value, 100) -
            timeOffset - line.Start + layer.AnimationOffset;
        var end = new MediaTime((sample.End + timeOffset).ToTimestamp(new(1, 100), MediaTimeRounding.CEILING).Value, 100) -
            timeOffset - line.Start + layer.AnimationOffset;
        var startTime = move is null || move.Start < origin ? origin : move.Start;
        var endTime = move is null || move.End > end ? end : move.End;
        if (move is null || move.First == move.Last || startTime >= endTime)
        {
            var point = Place(move?.Evaluate(origin) ?? position);
            AssExportPrecision.AddNumbers(line.Id, diagnostics, point.X, point.Y);
            return alignment + "\\pos(" + Point(point) + ")}";
        }
        var first = Place(move.Evaluate(startTime));
        var last = Place(move.Evaluate(endTime));
        var startMs = Milliseconds(startTime - origin);
        var endMs = Math.Max(startMs + 1, Milliseconds(endTime - origin));
        if (new MediaTime(startMs, 1000) != startTime - origin || new MediaTime(endMs, 1000) != endTime - origin)
        {
            Report("Ass.MoveTimeQuantization", "ASS 移动时刻已取整到毫秒，极短移动至少保留 1 毫秒。");
        }
        AssExportPrecision.AddNumbers(line.Id, diagnostics, first.X, first.Y, last.X, last.Y);
        return alignment + "\\move(" + Point(first) + "," + Point(last) + "," +
            startMs.ToString(CultureInfo.InvariantCulture) + "," + endMs.ToString(CultureInfo.InvariantCulture) + ")}";
    }

    internal string OpacityTags(AssMaskSample sample, MediaTime timeOffset)
    {
        if (opacity is null)
        {
            return string.Empty;
        }
        var origin = new MediaTime((sample.Start + timeOffset).ToTimestamp(new(1, 100), MediaTimeRounding.FLOOR).Value, 100) -
            timeOffset - line.Start + layer.AnimationOffset;
        var end = new MediaTime((sample.End + timeOffset).ToTimestamp(new(1, 100), MediaTimeRounding.CEILING).Value, 100) -
            timeOffset - line.Start + layer.AnimationOffset;
        var tags = AssOpacityConversion.WriteTags(opacity, origin, end, line.Id, diagnostics);
        if (tags.Length > 0 && opacity.Clip(origin, end).HasPartialOpacity)
        {
            Report("Ass.OpacityComposition", "ASS 淡入淡出在字形组成部分绘制时应用透明度，项目在整层绘制后应用；填充、描边或阴影重叠区域可能不同。");
        }
        return tags.Length == 0 ? string.Empty : "{" + tags + "}";
    }

    private AnimationValue? Constant(AnimationProperty property)
    {
        var track = layer.Tracks.FirstOrDefault(candidate => candidate.Property == property);
        if (track is null || !AssMoveConversion.TryConstant(track, out var value))
        {
            return null;
        }
        consumed.Add(property);
        return value;
    }

    private ScenePoint Place(ScenePoint point)
    {
        if (rotationOrigin is not null)
        {
            return rotationOrigin.MapPosition(point);
        }
        return new(placementOffset.X + point.X, placementOffset.Y + point.Y);
    }

    private ScenePoint TransformVector(ScenePoint point)
    {
        var x = point.X * scale.X;
        var y = point.Y * scale.Y;
        return new(rotationParts.Cosine * x - rotationParts.Sine * y, rotationParts.Sine * x + rotationParts.Cosine * y);
    }

    private void Report(string code, string message)
    {
        if (reported.Add(code))
        {
            diagnostics.Add(new(code, message, SubtitleId: line.Id));
        }
    }

    private static long Milliseconds(MediaTime time) => time.ToTimestamp(new(1, 1000), MediaTimeRounding.TO_EVEN).Value;
    private static string Point(ScenePoint point) => AssFormatValues.Number(point.X) + "," + AssFormatValues.Number(point.Y);

    private static bool ExactScale(double value)
    {
        var serialized = AssFormatValues.Number(AssFormatValues.Number(value * 100));
        return serialized.Equals(value * 100) || (serialized / 100).Equals(value);
    }
}
