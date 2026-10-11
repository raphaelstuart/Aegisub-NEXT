using System.Collections.Immutable;
using AegiNext.Core.Editing;
using AegiNext.Core.Projects;
using AegiNext.Core.Timing;

namespace AegiNext.Application.SubtitleFormats;

internal sealed class AssTextAnimationImport
{
    private readonly SubtitleLine original;
    private readonly Dictionary<string, AssTextAnimationChannel> channels;
    private readonly List<AssTextAnimationRun> runs = [];
    private readonly List<SubtitleFormatDiagnostic> diagnostics = [];
    private readonly HashSet<(string Code, string Message)> reported = [];
    private int revision;
    private int observedRevision = -1;
    private int observedCandidate;
    private bool observedKaraoke;
    private bool normalizedScale;
    private readonly bool sourceConstraints;

    internal AssTextAnimationImport(SubtitleLine original, SceneColor secondary, ScenePoint scale, double rotation, bool sourceConstraints = true)
    {
        this.original = original;
        this.sourceConstraints = sourceConstraints;
        channels = new()
        {
            ["fs"] = new(original.Style.FontSize), ["fsp"] = new(original.Style.LetterSpacing),
            ["bord"] = new(original.Style.StrokeWidth), ["blur"] = new(0),
            ["scale"] = new(scale), ["frz"] = new(-rotation),
            ["shadow"] = new(original.Style.ShadowOffset), ["1c"] = new(original.Style.Fill),
            ["2c"] = new(secondary), ["3c"] = new(original.Style.Stroke), ["4c"] = new(original.Style.ShadowColor)
        };
    }

    internal IEnumerable<SubtitleFormatDiagnostic> Diagnostics => diagnostics;

    internal bool HasShadowAnimation => !channels["shadow"].Snapshot(0).Operations.IsEmpty;

    internal AssRotationOriginGeometry RotationOriginGeometry() => AssRotationOriginGeometry.FromRuns(runs, original.End - original.Start);

    internal void Observe(int offset, int length, bool karaoke, int excludedCandidate)
    {
        if (runs.Count > 0 && observedRevision == revision && observedCandidate == excludedCandidate &&
            observedKaraoke == karaoke && runs[^1].Utf16Start + runs[^1].Utf16Length == offset)
        {
            runs[^1] = runs[^1] with { Utf16Length = runs[^1].Utf16Length + length };
            return;
        }
        observedRevision = revision;
        observedCandidate = excludedCandidate;
        observedKaraoke = karaoke;
        var next = new AssTextAnimationRun(offset, length, karaoke,
            channels.ToImmutableDictionary(pair => pair.Key, pair => pair.Value.Snapshot(excludedCandidate)));
        if (runs.Count > 0 && runs[^1].Utf16Start + runs[^1].Utf16Length == offset && runs[^1].Equivalent(next))
        {
            runs[^1] = runs[^1] with { Utf16Length = runs[^1].Utf16Length + length };
        }
        else
        {
            runs.Add(next);
        }
    }

    internal void Set(string name, SubtitleStyle style, SceneColor secondary, ScenePoint scale, double rotation, double blur)
    {
        revision++;
        switch (name)
        {
            case "fs": channels["fs"].Set(style.FontSize); break;
            case "fsp": channels["fsp"].Set(style.LetterSpacing); break;
            case "bord": channels["bord"].Set(style.StrokeWidth); break;
            case "blur": channels["blur"].Set(blur); break;
            case "fscx": channels["scale"].Set(scale, 1); break;
            case "fscy": channels["scale"].Set(scale, 2); break;
            case "fr":
            case "frz": channels["frz"].Set(-rotation); break;
            case "shad": channels["shadow"].Set(style.ShadowOffset); break;
            case "xshad": channels["shadow"].Set(style.ShadowOffset, 1); break;
            case "yshad": channels["shadow"].Set(style.ShadowOffset, 2); break;
            case "c":
            case "1c": channels["1c"].Set(style.Fill, 7); break;
            case "2c": channels["2c"].Set(secondary, 7); break;
            case "3c": channels["3c"].Set(style.Stroke, 7); break;
            case "4c": channels["4c"].Set(style.ShadowColor, 7); break;
            case "1a": channels["1c"].Set(style.Fill, 8); break;
            case "2a": channels["2c"].Set(secondary, 8); break;
            case "3a": channels["3c"].Set(style.Stroke, 8); break;
            case "4a": channels["4c"].Set(style.ShadowColor, 8); break;
            case "alpha":
                channels["1c"].Set(style.Fill, 8);
                channels["2c"].Set(secondary, 8);
                channels["3c"].Set(style.Stroke, 8);
                channels["4c"].Set(style.ShadowColor, 8);
                break;
            case "r":
                foreach (var key in channels.Keys)
                {
                    channels[key].Set(key switch
                    {
                        "fs" => style.FontSize, "fsp" => style.LetterSpacing, "bord" => style.StrokeWidth,
                        "blur" => 0, "scale" => scale, "frz" => -rotation, "shadow" => style.ShadowOffset,
                        "1c" => style.Fill, "2c" => secondary, "3c" => style.Stroke, _ => style.ShadowColor
                    });
                }
                break;
        }
    }

    internal void AddNumeric(string name, AssTransformTiming timing, double value, int candidate = 0, double? maximum = null)
    {
        revision++;
        var key = name is "fscx" or "fscy" ? "scale" : name == "fr" ? "frz" : name;
        AnimationValue target = name is "fscx" or "fscy" ? new ScenePoint(value, value) : name is "fr" or "frz" ? -value : value;
        channels[key].Add(new(timing, target, name == "fscx" ? 1 : name == "fscy" ? 2 : 0, KaraokeCandidate: candidate,
            ClampNonNegative: sourceConstraints && name is "fscx" or "fscy" or "bord" or "blur",
            Maximum: sourceConstraints ? maximum : null));
    }

    internal bool AddStyle(string name, string value, AssTransformTiming timing, double scaleY,
        AssResolutionContext resolution, double resetFontSize, int candidate = 0)
    {
        revision++;
        if (name == "fs")
        {
            var relative = value[0] is '+' or '-';
            var amount = AssFormatValues.Number(value);
            channels["fs"].Add(new(timing, relative ? 1 + amount / 10 : amount * scaleY,
                Mode: relative ? AnimationTransformMode.MULTIPLY_BY : AnimationTransformMode.INTERPOLATE_TO,
                NonPositiveFallback: sourceConstraints ? resetFontSize : null));
            return true;
        }
        if (name is "shad" or "xshad" or "yshad")
        {
            var amount = AssFormatValues.Number(value);
            var target = resolution.Shadow(amount);
            channels["shadow"].Add(new(timing, target, name == "xshad" ? 1 : name == "yshad" ? 2 : 0,
                KaraokeCandidate: candidate, ClampNonNegative: sourceConstraints && name == "shad"));
            return true;
        }
        if (name is "c" or "1c" or "2c" or "3c" or "4c")
        {
            channels[name == "c" ? "1c" : name].Add(new(timing, AssFormatValues.Color(value, SceneColor.White), 7, KaraokeCandidate: candidate));
            return true;
        }
        if (name is "alpha" or "1a" or "2a" or "3a" or "4a")
        {
            var target = SceneColor.White with { Alpha = AssFormatValues.Alpha(value) };
            if (name == "alpha")
            {
                for (var component = 1; component <= 4; component++)
                {
                    channels[component + "c"].Add(new(timing, target, 8, KaraokeCandidate: candidate));
                }
            }
            else
            {
                channels[name[0] + "c"].Add(new(timing, target, 8, KaraokeCandidate: candidate));
            }
            return true;
        }
        return false;
    }

    internal LayerTransform NormalizeTransform(LayerTransform transform)
    {
        for (var index = 0; index < runs.Count; index++)
        {
            var run = runs[index];
            var snapshot = run.Channels["scale"];
            var normalized = NormalizeScale(snapshot, transform.Scale);
            if (!normalized.Equivalent(snapshot))
            {
                normalizedScale = true;
                runs[index] = run with { Channels = run.Channels.SetItem("scale", normalized) };
            }
        }
        var mixed = runs.Count > 0 && runs.Skip(1).Any(run => !run.Channels["scale"].Equivalent(runs[0].Channels["scale"]));
        var staticGeometry = runs.Any(run => run.Channels["scale"].Initial.Vector != transform.Scale);
        if (!mixed && !staticGeometry)
        {
            return transform;
        }
        return transform with
        {
            Scale = new(ScaleNeedsUnitBasis(transform.Scale.X, 0) ? 1 : transform.Scale.X,
                ScaleNeedsUnitBasis(transform.Scale.Y, 1) ? 1 : transform.Scale.Y)
        };
    }

    internal (SubtitleLine Line, ImmutableArray<AnimationTrack> Tracks) Convert(SubtitleLine line,
        ImmutableArray<AnimationTrack> existing, LayerTransform transform, MediaTime contentOffset)
    {
        var ranges = ImmutableArray.CreateBuilder<SubtitleAnimationRange>();
        var tracks = existing.ToBuilder();
        var rangeIds = new Dictionary<int, Guid>();
        foreach (var key in channels.Keys)
        {
            var property = Property(key);
            var animated = runs.Any(run => !run.Channels[key].Operations.IsEmpty);
            var mixed = runs.Count > 0 && runs.Skip(1).Any(run => !run.Channels[key].Equivalent(runs[0].Channels[key]) ||
                key is "1c" or "2c" && run.Karaoke != runs[0].Karaoke);
            var staticGeometry = runs.Any(run => key == "scale" && run.Channels[key].Initial.Vector != transform.Scale ||
                key == "frz" && run.Channels[key].Initial.Scalar != transform.Rotation);
            var replaceScale = key == "scale" && (normalizedScale || mixed || staticGeometry);
            if (!animated && !staticGeometry || existing.Any(track => track.Property == property) && key != "blur" && !replaceScale)
            {
                continue;
            }
            if (replaceScale)
            {
                var wholeLineTarget = new AnimationTrackTarget(AnimationProperty.SCALE);
                for (var index = tracks.Count - 1; index >= 0; index--)
                {
                    if (tracks[index].Target == wholeLineTarget)
                    {
                        tracks.RemoveAt(index);
                    }
                }
            }
            if (key == "blur" && runs.Any(BorderCrossesZero))
            {
                Report("Ass.TransformAppearanceAnimation", "ASS 模糊动画跨越描边零值，作用对象随时间改变，已省略模糊动画并保留其他动画。");
                continue;
            }
            foreach (var runIndex in mixed || staticGeometry ? Enumerable.Range(0, runs.Count) : Enumerable.Range(0, Math.Min(runs.Count, 1)))
            {
                var run = runs[runIndex];
                var snapshot = run.Channels[key];
                if (snapshot.Operations.IsEmpty && !staticGeometry || key == "2c" && !run.Karaoke)
                {
                    continue;
                }
                Guid? rangeId = null;
                if (mixed || staticGeometry)
                {
                    if (!rangeIds.TryGetValue(runIndex, out var id))
                    {
                        if (rangeIds.Count >= 256)
                        {
                            Report("Ass.TextRangeBudget", "ASS 可见动画范围超过每字幕 256 个范围的预算，已省略额外范围动画并保留文字与静态样式。");
                            continue;
                        }
                        id = Guid.NewGuid();
                        rangeIds.Add(runIndex, id);
                    }
                    rangeId = id;
                }
                var state = key == "2c" ? SubtitleAnimationState.INACTIVE : key == "1c" && run.Karaoke
                    ? SubtitleAnimationState.ACTIVE : SubtitleAnimationState.NORMAL;
                var targetProperty = key == "blur" ? run.Channels["bord"].Initial.Scalar > 0 ? AnimationProperty.STROKE_BLUR : AnimationProperty.FILL_BLUR : property;
                if (snapshot.Operations.IsEmpty)
                {
                    continue;
                }
                if (!tracks.Any(track => track.Property == targetProperty && track.Target.TextRangeId == rangeId && track.Target.State == state))
                {
                    AddTrack(tracks, snapshot, targetProperty, rangeId, state, key, transform, contentOffset);
                }
                if (key == "blur")
                {
                    var blurTarget = new AnimationTrackTarget(targetProperty, TextRangeId: rangeId, State: state);
                    var blur = tracks.LastOrDefault(track => track.Target == blurTarget);
                    if (blur is not null)
                    {
                        tracks.Add(blur with
                        {
                            Target = blurTarget with { Property = AnimationProperty.SHADOW_BLUR },
                            Transforms = blur.Transforms.Select(operation => operation with { Id = Guid.NewGuid() }).ToImmutableArray()
                        });
                    }
                }
            }
        }
        foreach (var pair in rangeIds.OrderBy(pair => pair.Key))
        {
            var run = runs[pair.Key];
            var baseScale = run.Channels["scale"].Initial.Vector;
            ranges.Add(new(pair.Value, run.Utf16Start, run.Utf16Length)
            {
                Pivot = SubtitleAnimationPivot.SUBTITLE_ANCHOR,
                Scale = new(baseScale.X == transform.Scale.X ? 1 : baseScale.X / (transform.Scale.X == 0 ? 1 : transform.Scale.X),
                    baseScale.Y == transform.Scale.Y ? 1 : baseScale.Y / (transform.Scale.Y == 0 ? 1 : transform.Scale.Y)),
                Rotation = run.Channels["frz"].Initial.Scalar - transform.Rotation
            });
        }
        var result = line with { AnimationRanges = ranges.ToImmutable() };
        if (result.AnimationRanges.Any(range => range.Scale != new ScenePoint(1, 1) || range.Rotation != 0) ||
            tracks.Any(track => track.Target.TextRangeId is not null && track.Property is AnimationProperty.SCALE or AnimationProperty.ROTATION))
        {
            Report("Ass.TextRangeGeometry", "ASS 片段变换已保存为文字范围，原生排版后变换与 ASS 的字形、边缘、阴影及共享布局顺序可能不同。");
        }
        return (result, LayerAnimationTiming.Clip(new ProjectLayer
        {
            Start = original.Start, End = original.End, AnimationOffset = contentOffset, Tracks = tracks.ToImmutable()
        }).Tracks);
    }

    private AssTextAnimationSnapshot NormalizeScale(AssTextAnimationSnapshot snapshot, ScenePoint fallback)
    {
        var initial = snapshot.Initial;
        for (var component = 0; component < initial.ComponentCount; component++)
        {
            if (!ValidScale(initial.GetComponent(component), component))
            {
                var fallbackValue = component == 0 ? fallback.X : fallback.Y;
                initial = initial.WithComponent(component, ValidScale(fallbackValue, component) ? fallbackValue : 1);
                Report("Ass.TransformRange", "ASS 字形缩放基础值超出原生范围，已回退该轴并保留另一轴和其他内容。");
            }
        }
        var operations = ImmutableArray.CreateBuilder<AssTextAnimationOperation>();
        foreach (var operation in snapshot.Operations)
        {
            var value = operation.Value;
            var mask = operation.ComponentMask == 0 ? (1 << value.ComponentCount) - 1 : operation.ComponentMask;
            var remaining = mask;
            for (var component = 0; component < value.ComponentCount; component++)
            {
                if (!ValidScale(operation.ClampNonNegative ? Math.Max(value.GetComponent(component), 0) : value.GetComponent(component), component))
                {
                    var bit = 1 << component;
                    if ((mask & bit) != 0)
                    {
                        remaining &= ~bit;
                        Report("Ass.TransformRange", "ASS 字形缩放动画超出原生范围，已省略该轴操作并保留另一轴和其他内容。");
                    }
                    value = value.WithComponent(component, initial.GetComponent(component));
                }
            }
            if (remaining != 0)
            {
                operations.Add(operation with
                {
                    Value = value,
                    ComponentMask = remaining == mask ? operation.ComponentMask : remaining
                });
            }
        }
        return new(initial, operations.ToImmutable());
    }

    private static bool ValidScale(double value, int component) => double.IsFinite(value) && value >= 0 &&
        value <= AnimationPropertyMetadata.GetMaximum(AnimationProperty.SCALE, component);

    private bool ScaleNeedsUnitBasis(double basis, int component)
    {
        if (!double.IsFinite(basis) || basis == 0)
        {
            return true;
        }
        return runs.Any(run =>
        {
            var snapshot = run.Channels["scale"];
            return !ValidScale(snapshot.Initial.GetComponent(component) / basis, component) ||
                snapshot.Operations.Any(operation => !ValidScale((operation.ClampNonNegative
                    ? Math.Max(operation.Value.GetComponent(component), 0) : operation.Value.GetComponent(component)) / basis, component));
        });
    }

    private void AddTrack(ImmutableArray<AnimationTrack>.Builder tracks, AssTextAnimationSnapshot snapshot,
        AnimationProperty property, Guid? rangeId, SubtitleAnimationState state, string key, LayerTransform transform, MediaTime offset)
    {
        var appearanceScale = Math.Sqrt(Math.Abs(transform.Scale.X * transform.Scale.Y));
        if (appearanceScale == 0)
        {
            appearanceScale = 1;
        }
        AnimationValue ConvertValue(AnimationValue value, AnimationTransformMode mode)
        {
            if (mode == AnimationTransformMode.MULTIPLY_BY)
            {
                return value;
            }
            if (key is "bord" or "blur")
            {
                return value.Scalar / appearanceScale;
            }
            if (key == "shadow")
            {
                var rotation = AssTransformMath.SinCos(transform.Rotation);
                return new ScenePoint((rotation.Cosine * value.Vector.X + rotation.Sine * value.Vector.Y) /
                    (transform.Scale.X == 0 ? 1 : transform.Scale.X),
                    (-rotation.Sine * value.Vector.X + rotation.Cosine * value.Vector.Y) /
                    (transform.Scale.Y == 0 ? 1 : transform.Scale.Y));
            }
            if (key == "scale" && rangeId is not null)
            {
                return new ScenePoint(value.Vector.X / (transform.Scale.X == 0 ? 1 : transform.Scale.X),
                    value.Vector.Y / (transform.Scale.Y == 0 ? 1 : transform.Scale.Y));
            }
            return key == "frz" && rangeId is not null ? value.Scalar - transform.Rotation : value;
        }
        var track = new AnimationTrack(new AnimationTrackTarget(property, TextRangeId: rangeId, State: state), [])
        {
            InitialValue = ConvertValue(snapshot.Initial, AnimationTransformMode.INTERPOLATE_TO),
            ColorSpace = snapshot.Initial.IsColor ? AnimationColorSpace.SRGB : AnimationColorSpace.LINEAR_RGB,
            Transforms = snapshot.Operations.Select(operation => new AnimationTransformOperation(Guid.NewGuid(),
                operation.Timing.Start + offset, operation.Timing.End + offset, ConvertValue(operation.Value, operation.Mode), operation.Timing.Acceleration)
            {
                ComponentMask = operation.ComponentMask, Mode = operation.Mode
            }).ToImmutableArray()
        };
        var minimum = key == "fs" ? AnimationPropertyMetadata.GetMinimum(AnimationProperty.FONT_SIZE) : double.NegativeInfinity;
        if (AssSourceAnimationEvaluator.NeedsSampling(snapshot, minimum))
        {
            double[] Components(AnimationValue value) => Enumerable.Range(0, value.ComponentCount).Select(value.GetComponent).ToArray();
            var discontinuities = AssSourceAnimationEvaluator.Discontinuities(snapshot).ToArray();
            var samples = AssAnimationSampler.Sample(track, Components,
                offset, offset + original.End - original.Start,
                time => AssSourceAnimationEvaluator.Evaluate(snapshot, time - offset),
                time => AssSourceAnimationEvaluator.Evaluate(snapshot, time - offset, true),
                discontinuities.Select(time => time + offset));
            var belowFontFloor = key == "fs" && (samples.Frames.Any(frame => frame.Value.Scalar < minimum) ||
                discontinuities.Any(time => time >= MediaTime.Zero && time <= original.End - original.Start &&
                    AssSourceAnimationEvaluator.Evaluate(snapshot, time, true).Scalar < minimum));
            if (belowFontFloor)
            {
                Report("Ass.FontSizeRange", "ASS 连续字号经过小于原生 0.01 的正值，已钳制到原生下限并保留逐操作样式复位。");
            }
            track = track with
            {
                InitialValue = null, Transforms = [],
                Keyframes = samples.Frames.Select(frame => frame with
                {
                    Value = ConvertValue(key == "fs" ? Math.Max(frame.Value.Scalar, minimum) : frame.Value,
                        AnimationTransformMode.INTERPOLATE_TO)
                }).ToImmutableArray()
            };
            Report(samples.Limited ? "Ass.AnimationSamplingLimit" : "Ass.AnimationSampling",
                samples.Limited ? $"ASS {key} 逐操作约束采样达到 1 毫秒、断点量化或 4096 点限制，部分区间误差可能超过 1/255。" :
                    $"ASS {key} 插值后的逐操作约束已采样为原生动画，检查的源坐标分量误差不超过 1/255，最短间隔为 1 毫秒。");
        }
        else if (key == "shadow" && transform.Rotation != 0 && snapshot.Operations.Any(operation => operation.ComponentMask != 0))
        {
            var source = track with
            {
                InitialValue = snapshot.Initial,
                Transforms = snapshot.Operations.Select(operation => new AnimationTransformOperation(Guid.NewGuid(),
                    operation.Timing.Start + offset, operation.Timing.End + offset, operation.Value, operation.Timing.Acceleration)
                {
                    ComponentMask = operation.ComponentMask
                }).ToImmutableArray()
            };
            double[] Components(AnimationValue value)
            {
                var converted = ConvertValue(value, AnimationTransformMode.INTERPOLATE_TO).Vector;
                return [converted.X, converted.Y];
            }
            var samples = AssAnimationSampler.Sample(source, Components, offset, offset + original.End - original.Start);
            track = track with
            {
                InitialValue = null, Transforms = [],
                Keyframes = samples.Frames.Select(frame => frame with
                {
                    Value = ConvertValue(frame.Value, AnimationTransformMode.INTERPOLATE_TO)
                }).ToImmutableArray()
            };
            Report(samples.Limited ? "Ass.AnimationSamplingLimit" : "Ass.AnimationSampling",
                samples.Limited ? "旋转下独立阴影轴的转换采样达到 1 毫秒或 4096 点限制，部分区间误差可能超过 1/255。" :
                    "旋转下独立阴影轴已采样为原生向量，检查的分量误差不超过 1/255，最短间隔为 1 毫秒。");
        }
        track = LayerAnimationTiming.Clip(new ProjectLayer
        {
            Start = original.Start, End = original.End, AnimationOffset = offset, Tracks = [track]
        }).Tracks[0];
        try
        {
            var range = rangeId is { } id ? new SubtitleAnimationRange(id, 0, Math.Max(original.Text.Length, 1)) : null;
            var validationLine = original with { Text = original.Text.Length == 0 ? "a" : original.Text, AnimationRanges = range is null ? [] : [range] };
            ProjectValidator.Validate(new() { Subtitles = [validationLine], Layers = [new() { Id = original.Id, SubtitleId = original.Id, Start = original.Start, End = original.End, AnimationOffset = offset, Tracks = [track] }] });
            tracks.Add(track);
        }
        catch (InvalidDataException)
        {
            Report("Ass.TransformRange", $"ASS {key} 动画超出原生范围或时间预算，已舍弃该轨道并保留其他内容。");
        }
    }

    private static bool BorderCrossesZero(AssTextAnimationRun run)
    {
        var border = run.Channels["bord"];
        var values = border.Operations.Select(operation => operation.Value.Scalar).Prepend(border.Initial.Scalar).ToArray();
        return values.Any(value => value > 0) && values.Any(value => value <= 0);
    }

    private static AnimationProperty Property(string key) => key switch
    {
        "fs" => AnimationProperty.FONT_SIZE, "fsp" => AnimationProperty.LETTER_SPACING,
        "bord" => AnimationProperty.STROKE_WIDTH, "blur" => AnimationProperty.STROKE_BLUR,
        "scale" => AnimationProperty.SCALE, "frz" => AnimationProperty.ROTATION,
        "shadow" => AnimationProperty.SHADOW_OFFSET, "1c" or "2c" => AnimationProperty.FILL,
        "3c" => AnimationProperty.STROKE, _ => AnimationProperty.SHADOW_COLOR
    };

    private void Report(string code, string message)
    {
        if (reported.Add((code, message)))
        {
            diagnostics.Add(new(code, message, SubtitleId: original.Id));
        }
    }
}
