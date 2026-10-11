using AegiNext.Core.Projects;
using AegiNext.Core.Timing;

namespace AegiNext.Application.SubtitleFormats;

internal static class AssMoveConversion
{
    private const double TOLERANCE = 1e-9;

    internal static bool TryConstant(AnimationTrack track, out AnimationValue value)
    {
        value = track.IsOrdered ? track.InitialValue!.Value : track.Keyframes[0].Value;
        if (!track.IsOrdered)
        {
            var baseline = value;
            return track.Keyframes.All(key => key.Value == baseline);
        }
        foreach (var operation in track.Transforms)
        {
            for (var component = 0; component < value.ComponentCount; component++)
            {
                if (operation.ComponentMask != 0 && (operation.ComponentMask & (1 << component)) == 0)
                {
                    continue;
                }
                var target = operation.Value.GetComponent(component);
                var initial = value.GetComponent(component);
                if (operation.Mode == AnimationTransformMode.MULTIPLY_BY ? initial != 0 && !target.Equals(1d) : !target.Equals(initial))
                {
                    return false;
                }
            }
        }
        return true;
    }

    internal static AssLinearMove? FromTrack(AnimationTrack track)
    {
        if (TryConstant(track, out var constant))
        {
            return new(constant.Vector, constant.Vector, MediaTime.Zero, MediaTime.Zero);
        }
        if (track.IsOrdered)
        {
            if (track.Transforms.Length != 1 || track.Transforms[0] is not { Acceleration: > 0 } operation || operation.Start >= operation.End)
            {
                return null;
            }
            var initial = track.InitialValue!.Value.Vector;
            var target = operation.Value.Vector;
            var destination = new ScenePoint(operation.ComponentMask == 0 || (operation.ComponentMask & 1) != 0 ? target.X : initial.X,
                operation.ComponentMask == 0 || (operation.ComponentMask & 2) != 0 ? target.Y : initial.Y);
            return new(initial, destination, operation.Start, operation.End,
                !operation.Acceleration.Equals(1d));
        }
        var keys = track.Keyframes;
        var first = 0;
        var last = keys.Length - 1;
        while (first < last && keys[first].Value == keys[first + 1].Value)
        {
            first++;
        }
        while (last > first && keys[last].Value == keys[last - 1].Value)
        {
            last--;
        }
        var start = keys[first].Value.Vector;
        var end = keys[last].Value.Vector;
        var previous = 0d;
        var approximate = false;
        var duration = keys[last].Time - keys[first].Time;
        for (var index = first; index < last; index++)
        {
            var key = keys[index];
            var next = keys[index + 1];
            var current = key.Value.Vector;
            var destination = next.Value.Vector;
            if (!TryProgress(start, end, destination, out var progress) || progress < previous - TOLERANCE ||
                !StraightCurve(key, destination, out var linear))
            {
                return null;
            }
            var elapsed = next.Time - keys[first].Time;
            var timeFraction = ((double)elapsed.Numerator / elapsed.Denominator) / ((double)duration.Numerator / duration.Denominator);
            approximate |= Math.Abs(progress - timeFraction) > TOLERANCE || current != destination && !linear;
            previous = progress;
        }
        return new(start, end, keys[first].Time, keys[last].Time, approximate);
    }

    internal static AssLinearMove? FromPath(MotionPath motion, ScenePoint position)
    {
        if (motion.OrientToPath || motion.Path.Closed || motion.Path.Segments.Length != 1)
        {
            return null;
        }
        var start = motion.Path.Start;
        var segment = motion.Path.Segments[0];
        if (!TryProgress(start, segment.End, segment.Control1, out var first) ||
            !TryProgress(start, segment.End, segment.Control2, out var second) || second < first - TOLERANCE)
        {
            return null;
        }
        return new(new(position.X + start.X, position.Y + start.Y),
            new(position.X + segment.End.X, position.Y + segment.End.Y), MediaTime.Zero, motion.Duration,
            start != segment.End && (Math.Abs(first - 1d / 3) > TOLERANCE || Math.Abs(second - 2d / 3) > TOLERANCE));
    }

    private static bool StraightCurve(Keyframe key, ScenePoint destination, out bool linear)
    {
        var start = key.Value.Vector;
        var changesX = !start.X.Equals(destination.X);
        var changesY = !start.Y.Equals(destination.Y);
        var x = key.GetCurve(0);
        var y = key.GetCurve(1);
        linear = (!changesX || IsLinear(x)) && (!changesY || IsLinear(y));
        return (!changesX || IsContinuous(x)) && (!changesY || IsContinuous(y)) &&
            (!changesX || !changesY || linear || x == y);
    }

    private static bool IsLinear(AnimationCurve curve)
    {
        return curve.Interpolation == KeyframeInterpolation.LINEAR ||
            curve.Interpolation == KeyframeInterpolation.POWER && curve.Exponent.Equals(1d);
    }

    private static bool IsContinuous(AnimationCurve curve)
    {
        return curve.Interpolation != KeyframeInterpolation.HOLD &&
            (curve.Interpolation != KeyframeInterpolation.POWER || curve.Exponent > 0);
    }

    private static bool TryProgress(ScenePoint start, ScenePoint end, ScenePoint point, out double progress)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var lengthSquared = dx * dx + dy * dy;
        if (lengthSquared == 0)
        {
            progress = 0;
            return point == start;
        }
        progress = ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSquared;
        var x = start.X + dx * progress;
        var y = start.Y + dy * progress;
        return progress >= -TOLERANCE && progress <= 1 + TOLERANCE &&
            Math.Abs(point.X - x) <= TOLERANCE * Math.Max(1, Math.Abs(dx)) &&
            Math.Abs(point.Y - y) <= TOLERANCE * Math.Max(1, Math.Abs(dy));
    }
}
