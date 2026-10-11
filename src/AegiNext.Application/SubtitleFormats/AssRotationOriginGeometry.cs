using AegiNext.Core.Projects;

namespace AegiNext.Application.SubtitleFormats;

internal sealed record AssRotationOriginGeometry(bool HasContent, bool HasCommonScale, bool HasCommonRotation,
    bool HasScaleAnimation, bool HasRotationAnimation, bool IsValid)
{
    internal static AssRotationOriginGeometry FromRuns(IReadOnlyList<AssTextAnimationRun> runs)
    {
        if (runs.Count == 0)
        {
            return new(false, true, true, false, false, true);
        }
        var scales = runs.Select(run => run.Channels["scale"]).ToArray();
        var rotations = runs.Select(run => run.Channels["frz"]).ToArray();
        var scaleAnimation = scales.Any(Changes);
        var rotationAnimation = rotations.Any(Changes);
        return new(true,
            scales.All(snapshot => snapshot.Initial == scales[0].Initial && (!scaleAnimation || snapshot.Equivalent(scales[0]))),
            rotations.All(snapshot => snapshot.Initial == rotations[0].Initial && (!rotationAnimation || snapshot.Equivalent(rotations[0]))),
            scaleAnimation, rotationAnimation,
            scales.All(snapshot => Valid(snapshot, true)) && rotations.All(snapshot => Valid(snapshot, false)));
    }

    private static bool Changes(AssTextAnimationSnapshot snapshot)
    {
        foreach (var operation in snapshot.Operations)
        {
            for (var component = 0; component < snapshot.Initial.ComponentCount; component++)
            {
                if (operation.ComponentMask != 0 && (operation.ComponentMask & (1 << component)) == 0)
                {
                    continue;
                }
                var initial = snapshot.Initial.GetComponent(component);
                var target = operation.Value.GetComponent(component);
                if (operation.ClampNonNegative)
                {
                    target = Math.Max(target, 0);
                }
                if (operation.Mode == AnimationTransformMode.MULTIPLY_BY ? initial != 0 && !target.Equals(1d) : !target.Equals(initial))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool Valid(AssTextAnimationSnapshot snapshot, bool scale)
    {
        for (var component = 0; component < snapshot.Initial.ComponentCount; component++)
        {
            if (!Valid(snapshot.Initial.GetComponent(component), scale))
            {
                return false;
            }
        }
        foreach (var operation in snapshot.Operations)
        {
            for (var component = 0; component < snapshot.Initial.ComponentCount; component++)
            {
                if (operation.ComponentMask != 0 && (operation.ComponentMask & (1 << component)) == 0)
                {
                    continue;
                }
                var value = operation.Value.GetComponent(component);
                if (operation.ClampNonNegative)
                {
                    value = Math.Max(value, 0);
                }
                if (!Valid(value, scale))
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static bool Valid(double value, bool scale) => double.IsFinite(value) &&
        (scale ? value is >= 0 and <= 10000 : Math.Abs(value) <= 1e9);
}
