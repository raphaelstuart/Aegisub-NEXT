using System.Collections.Immutable;
using AegiNext.Core.Editing;
using AegiNext.Core.Projects;
using AegiNext.Core.Timing;

namespace AegiNext.Application.SubtitleFormats;

internal sealed class AssGeometryParser
{
    private readonly SubtitleLine original;
    private readonly IReadOnlyDictionary<string, AssStyleDefinition> styles;
    private readonly double canvasScaleX;
    private readonly double canvasScaleY;
    private readonly bool sourceConstraints;
    private readonly List<SubtitleFormatDiagnostic> diagnostics = [];
    private AssStyleDefinition baseline;
    private ScenePoint currentScale;
    private double currentRotation;
    private ScenePoint observedScale;
    private double observedRotation;
    private bool observed;
    private bool mixedScaleX;
    private bool mixedScaleY;
    private bool mixedRotation;
    private bool hasPlacement;
    private ScenePoint? placement;
    private AnimationTrack? move;

    internal AssGeometryParser(SubtitleLine original, IReadOnlyDictionary<string, AssStyleDefinition> styles,
        double canvasScaleX, double canvasScaleY, bool sourceConstraints = true)
    {
        this.original = original;
        this.styles = styles;
        this.canvasScaleX = canvasScaleX;
        this.canvasScaleY = canvasScaleY;
        this.sourceConstraints = sourceConstraints;
        baseline = OriginalDefinition();
        currentScale = baseline.Scale;
        currentRotation = baseline.Rotation;
    }

    internal IEnumerable<SubtitleFormatDiagnostic> Diagnostics => diagnostics;
    internal ScenePoint CurrentScale => currentScale;
    internal double CurrentRotation => currentRotation;
    internal ScenePoint? Placement => placement;

    internal void Reset(string name)
    {
        baseline = name.Length > 0 && styles.TryGetValue(name, out var style) ? style : OriginalDefinition();
        currentScale = baseline.Scale;
        currentRotation = baseline.Rotation;
    }

    internal void Apply(string name, string value)
    {
        switch (name)
        {
            case "fscx":
                currentScale = currentScale with { X = value.Length == 0 ? baseline.Scale.X : Scale(value) };
                break;
            case "fscy":
                currentScale = currentScale with { Y = value.Length == 0 ? baseline.Scale.Y : Scale(value) };
                break;
            case "fr":
            case "frz":
                currentRotation = value.Length == 0 ? baseline.Rotation : AssFormatValues.Number(value);
                break;
        }
    }

    private double Scale(string value)
    {
        var scale = AssFormatValues.Number(value) / 100;
        return sourceConstraints ? Math.Max(scale, 0) : scale;
    }

    internal void Observe()
    {
        if (!observed)
        {
            observedScale = currentScale;
            observedRotation = currentRotation;
            observed = true;
            return;
        }
        mixedScaleX |= !observedScale.X.Equals(currentScale.X);
        mixedScaleY |= !observedScale.Y.Equals(currentScale.Y);
        mixedRotation |= !observedRotation.Equals(currentRotation);
    }

    internal LayerTransform Transform()
    {
        if (!observed)
        {
            Observe();
        }
        var x = Component(observedScale.X, mixedScaleX, 1, 10000, "水平缩放", true);
        var y = Component(observedScale.Y, mixedScaleY, 1, 10000, "垂直缩放", true);
        var rotation = Component(observedRotation, mixedRotation, 0, 1e9, "旋转", false);
        if (!x.Equals(1d) || !y.Equals(1d))
        {
            Report("Ass.TransformLayout", "ASS 字形缩放已导入原生片段变换；两种排版器的换行时机和字形锚点不同，自动换行或位置可能改变。", 0, 0);
        }
        if (!canvasScaleX.Equals(canvasScaleY) && rotation != 0)
        {
            Report("Ass.TransformLayout", "ASS 旋转与画布非等比重采样的组合可能产生剪切，原生缩放和旋转不能完整表达，已近似保留旋转。", 0, 0);
        }
        return new() { Scale = new(x, y), Rotation = -rotation };
    }

    internal bool TryPlacement(string name, string value, int sourceStart, int sourceLength, out ScenePoint position)
    {
        position = new();
        if (hasPlacement)
        {
            Report("Ass.DuplicatePlacement", "ASS 同一行重复的位置或移动标签已忽略，采用首个值。", sourceStart, sourceLength);
            return false;
        }
        var arguments = AssOverrideTags.Arguments(value);
        if (name == "pos" && arguments.Length != 2 || name == "move" && arguments.Length is not (4 or 6))
        {
            throw new InvalidDataException(name == "pos" ? "ASS pos 必须有两个坐标。" : "ASS move 必须有四个坐标以及可选的两个时间。");
        }
        hasPlacement = true;
        position = new(AssFormatValues.Number(arguments[0]) * canvasScaleX, AssFormatValues.Number(arguments[1]) * canvasScaleY);
        if (!ValidPoint(position))
        {
            Report("Ass.UnsupportedTransform", "ASS 定位超出原生坐标范围，已跳过该定位。", sourceStart, sourceLength);
            return false;
        }
        if (name == "pos")
        {
            placement = position;
            return true;
        }
        var target = new ScenePoint(AssFormatValues.Number(arguments[2]) * canvasScaleX, AssFormatValues.Number(arguments[3]) * canvasScaleY);
        var delta = new ScenePoint(target.X - position.X, target.Y - position.Y);
        if (!ValidPoint(target) || !ValidPoint(delta))
        {
            Report("Ass.UnsupportedTransform", "ASS 移动坐标或位移超出原生范围，已跳过该移动。", sourceStart, sourceLength);
            return false;
        }
        var start = MediaTime.Zero;
        var end = original.End - original.Start;
        if (arguments.Length == 6)
        {
            var first = AssFormatValues.Number(arguments[4]);
            var last = AssFormatValues.Number(arguments[5]);
            if (first != 0 || last != 0)
            {
                if (first < long.MinValue || first >= long.MaxValue || last < long.MinValue || last >= long.MaxValue)
                {
                    Report("Ass.MoveTiming", "ASS 移动时间超出可保存的范围，已跳过该移动。", sourceStart, sourceLength);
                    return false;
                }
                start = new((long)first, 1000);
                end = new((long)last, 1000);
                if (!first.Equals((long)first) || !last.Equals((long)last))
                {
                    Report("Ass.MoveTiming", "ASS 移动时间已按整数毫秒解析，小数毫秒被舍弃。", sourceStart, sourceLength);
                }
            }
        }
        if (end <= start)
        {
            Report("Ass.MoveTiming", "ASS 逆序或瞬时移动不能表示为连续位置轨道，已跳过该移动。", sourceStart, sourceLength);
            return false;
        }
        if (delta != new ScenePoint())
        {
            move = new(AnimationProperty.POSITION,
            [
                new(start, AnimationValue.FromVector(new())),
                new(end, AnimationValue.FromVector(delta))
            ]);
        }
        placement = position;
        return true;
    }

    internal ImmutableArray<AnimationTrack> Tracks(MediaTime contentOffset)
    {
        if (move is null)
        {
            return [];
        }
        var rebased = move with
        {
            Keyframes = move.Keyframes.Select(frame => frame with { Time = frame.Time + contentOffset }).ToImmutableArray()
        };
        return LayerAnimationTiming.Clip(new ProjectLayer
        {
            Start = original.Start, End = original.End, AnimationOffset = contentOffset, Tracks = [rebased]
        }).Tracks;
    }

    private AssStyleDefinition OriginalDefinition()
    {
        return styles.TryGetValue(original.StyleName, out var style) ? style : new(original.StyleName, original.Style, SceneColor.White);
    }

    private double Component(double value, bool mixed, double fallback, double maximum, string name, bool positive)
    {
        if (mixed)
        {
            Report("Ass.InlineTransform", $"ASS 行内{name}不一致，无法保存为整行片段变换，已跳过该分量。", 0, 0);
            return fallback;
        }
        if (!double.IsFinite(value) || Math.Abs(value) > maximum || positive && value < 0)
        {
            Report("Ass.UnsupportedTransform", $"ASS {name}不符合可转换的原生范围，已跳过该分量。", 0, 0);
            return fallback;
        }
        return value;
    }

    private static bool ValidPoint(ScenePoint point) => double.IsFinite(point.X) && double.IsFinite(point.Y) &&
        Math.Abs(point.X) <= 1e9 && Math.Abs(point.Y) <= 1e9;

    private void Report(string code, string message, int start, int length)
    {
        diagnostics.Add(new(code, message, start, length, original.Id));
    }
}
