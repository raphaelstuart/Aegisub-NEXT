using AegiNext.Core.Projects;
using AegiNext.Core.Timing;

namespace AegiNext.Application.SubtitleFormats;

internal sealed record AssRotationOriginGeometry(bool HasContent, bool HasCommonScale, bool HasCommonRotation,
    ScenePoint? ConstantScale, double? ConstantRotation, bool IsValid)
{
    internal bool IsComplete { get; init; } = true;

    internal bool HasScaleAnimation => ConstantScale is null;

    internal bool HasRotationAnimation => ConstantRotation is null;

    internal static AssRotationOriginGeometry FromRuns(IReadOnlyList<AssTextAnimationRun> runs, MediaTime duration)
    {
        if (runs.Count == 0)
        {
            return new(false, true, true, null, null, true);
        }
        var scales = runs.Select(run => run.Channels["scale"]).ToArray();
        var rotations = runs.Select(run => run.Channels["frz"]).ToArray();
        var constantScale = CommonConstant(scales, duration, out var scale);
        var constantRotation = CommonConstant(rotations, duration, out var rotation);
        return new(true,
            constantScale || scales.All(snapshot => snapshot.Equivalent(scales[0])),
            constantRotation || rotations.All(snapshot => snapshot.Equivalent(rotations[0])),
            constantScale ? scale.Vector : null, constantRotation ? rotation.Scalar : null,
            scales.All(snapshot => Valid(snapshot, true)) && rotations.All(snapshot => Valid(snapshot, false)));
    }

    internal static bool ContainsGeometry(IEnumerable<AssOverrideTag> tags)
    {
        var pending = new Stack<AssOverrideTag>(tags);
        while (pending.TryPop(out var tag))
        {
            if (tag.Name is "fsc" or "fscx" or "fscy" or "fr" or "frz")
            {
                return true;
            }
            if (tag.Name == "t")
            {
                foreach (var child in AssOverrideTags.Parse(AssOverrideTags.Arguments(tag.Value)[^1]))
                {
                    pending.Push(child);
                }
            }
        }
        return false;
    }

    private static bool CommonConstant(AssTextAnimationSnapshot[] snapshots, MediaTime duration,
        out AnimationValue value)
    {
        if (!AssGeometryAnimationWindow.TryConstant(snapshots[0], duration, out value))
        {
            return false;
        }
        var expected = value;
        return snapshots.Skip(1).All(snapshot =>
            AssGeometryAnimationWindow.TryConstant(snapshot, duration, out var actual) && actual == expected);
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
