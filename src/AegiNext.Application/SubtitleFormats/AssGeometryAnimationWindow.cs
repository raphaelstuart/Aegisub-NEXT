using AegiNext.Core.Editing;
using AegiNext.Core.Projects;
using AegiNext.Core.Timing;

namespace AegiNext.Application.SubtitleFormats;

internal static class AssGeometryAnimationWindow
{
    internal static bool TryConstant(AssTextAnimationSnapshot snapshot, MediaTime duration, out AnimationValue value)
    {
        value = snapshot.Initial;
        if (AssSourceAnimationEvaluator.NeedsSampling(snapshot))
        {
            return false;
        }
        foreach (var operation in snapshot.Operations)
        {
            if (!ApplyConstant(ref value, operation.Value, operation.ComponentMask, operation.Mode,
                operation.Timing.Start, operation.Timing.End, operation.Timing.Acceleration, MediaTime.Zero, duration))
            {
                return false;
            }
        }
        value = AssSourceAnimationEvaluator.Evaluate(snapshot, MediaTime.Zero);
        return true;
    }

    internal static bool TryConstant(AnimationTrack track, MediaTime minimum, MediaTime maximum, out AnimationValue value)
    {
        if (!track.IsOrdered)
        {
            return AssMoveConversion.TryConstant(track, out value);
        }
        value = track.InitialValue!.Value;
        foreach (var operation in track.Transforms)
        {
            if (!ApplyConstant(ref value, operation.Value, operation.ComponentMask, operation.Mode,
                operation.Start, operation.End, operation.Acceleration, minimum, maximum))
            {
                return false;
            }
        }
        value = SceneEvaluator.EvaluateTrack(track, minimum);
        return true;
    }

    private static bool ApplyConstant(ref AnimationValue value, AnimationValue target, int mask, AnimationTransformMode mode,
        MediaTime start, MediaTime end, double acceleration, MediaTime minimum, MediaTime maximum)
    {
        if (start >= maximum)
        {
            return true;
        }
        var completed = end <= minimum || acceleration == 0 && start <= minimum;
        for (var component = 0; component < value.ComponentCount; component++)
        {
            if (mask != 0 && (mask & (1 << component)) == 0)
            {
                continue;
            }
            var initial = value.GetComponent(component);
            var destination = target.GetComponent(component);
            if (completed)
            {
                value = value.WithComponent(component, mode == AnimationTransformMode.MULTIPLY_BY
                    ? initial * destination : initial + (destination - initial));
            }
            else if (mode == AnimationTransformMode.MULTIPLY_BY ? initial != 0 && destination != 1 : destination != initial)
            {
                return false;
            }
        }
        return true;
    }
}
