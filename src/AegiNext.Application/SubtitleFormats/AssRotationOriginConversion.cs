using System.Collections.Immutable;
using AegiNext.Core.Projects;
using AegiNext.Core.Timing;

namespace AegiNext.Application.SubtitleFormats;

internal static class AssRotationOriginConversion
{
    internal static bool TryImport(ScenePoint origin, ScenePoint? basePosition, SubtitleLine line,
        LayerTransform transform, ImmutableArray<AnimationTrack> placementTracks, ImmutableArray<AnimationTrack> numericTracks,
        AssRotationOriginGeometry sourceGeometry, double canvasScaleX, double canvasScaleY,
        MediaTime contentOffset, MediaTime duration, out LayerTransform convertedTransform,
        out ImmutableArray<AnimationTrack> convertedPlacementTracks, out ImmutableArray<AnimationTrack> convertedNumericTracks,
        out string reason)
    {
        convertedTransform = transform;
        convertedPlacementTracks = placementTracks;
        convertedNumericTracks = numericTracks;
        reason = string.Empty;
        if (!ValidPoint(origin))
        {
            reason = "旋转原点超出原生坐标范围";
            return false;
        }
        if (basePosition is not { } basis)
        {
            reason = "自动定位或未保留的定位不能精确补偿旋转原点";
            return false;
        }
        if (!sourceGeometry.HasContent || !sourceGeometry.IsValid)
        {
            reason = "源文字缩放或旋转超出可转换的原生范围";
            return false;
        }
        if (!sourceGeometry.IsComplete)
        {
            reason = "部分源几何变换未保留，不能精确补偿旋转原点";
            return false;
        }
        if (!sourceGeometry.HasCommonScale || !sourceGeometry.HasCommonRotation || HasRangeGeometry(line, numericTracks))
        {
            reason = "文字范围的位移、缩放或旋转不能共用此整行原点补偿";
            return false;
        }
        var scale = transform.Scale;
        var rotation = transform.Rotation;
        var animatedRotation = false;
        var normalizedTracks = numericTracks.ToBuilder();
        for (var index = 0; index < numericTracks.Length; index++)
        {
            var track = numericTracks[index];
            if (track.Target.TextRangeId is not null || track.Property is not (AnimationProperty.SCALE or AnimationProperty.ROTATION))
            {
                continue;
            }
            if (track.Target.State != SubtitleAnimationState.NORMAL)
            {
                reason = "独立文字状态的几何不能共用此整行原点补偿";
                return false;
            }
            var constant = AssGeometryAnimationWindow.TryConstant(track, contentOffset, contentOffset + duration, out var value);
            if (track.Property == AnimationProperty.SCALE)
            {
                if (!constant)
                {
                    reason = "动态缩放需要旋转原点与位置联动，当前转换不支持该组合";
                    return false;
                }
                scale = value.Vector;
            }
            else if (constant)
            {
                rotation = value.Scalar;
            }
            else
            {
                animatedRotation = true;
            }
            if (constant && track.IsOrdered)
            {
                normalizedTracks[index] = track with
                {
                    Keyframes = [new(contentOffset, value)], InitialValue = null, Transforms = []
                };
            }
        }
        if (sourceGeometry.HasScaleAnimation)
        {
            reason = "动态缩放需要旋转原点与位置联动，当前转换不支持该组合";
            return false;
        }
        if (sourceGeometry.ConstantScale != scale || sourceGeometry.ConstantRotation is { } expectedRotation &&
            (animatedRotation || expectedRotation != rotation))
        {
            reason = "源可见窗口内的固定几何与导入结果不一致，不能精确补偿旋转原点";
            return false;
        }
        if (sourceGeometry.HasRotationAnimation && !animatedRotation)
        {
            reason = "源旋转动画未完整保留，不能精确补偿旋转原点";
            return false;
        }
        if (!double.IsFinite(scale.X) || !double.IsFinite(scale.Y) || scale.X is <= 0 or > 10000 || scale.Y is <= 0 or > 10000)
        {
            reason = "旋转原点补偿仅支持固定正缩放，零缩放轴不能使用此轴心换算";
            return false;
        }
        if (!canvasScaleX.Equals(canvasScaleY) && (rotation != 0 || animatedRotation))
        {
            reason = "非等比画布重采样与旋转组合产生剪切，不能精确补偿旋转原点";
            return false;
        }
        if (placementTracks.Any(track => track.Property != AnimationProperty.POSITION || track.Target.TextRangeId is not null ||
            track.Target.State != SubtitleAnimationState.NORMAL || track.IsOrdered) || placementTracks.Length > 1)
        {
            reason = "旋转原点补偿仅支持单段整行直线移动";
            return false;
        }
        if (animatedRotation && placementTracks.Any(track => !TryConstant(track, out var value) || value.Vector != default))
        {
            reason = "旋转动画与移动同时存在，需要位置联动，当前转换不支持该组合";
            return false;
        }
        var delta = new ScenePoint(origin.X - basis.X, origin.Y - basis.Y);
        var pivot = new ScenePoint(delta.X / scale.X, delta.Y / scale.Y);
        if (!ValidPoint(delta) || !ValidPoint(pivot))
        {
            reason = "旋转原点补偿后的位移或局部轴心超出原生坐标范围";
            return false;
        }
        var converted = ImmutableArray.CreateBuilder<AnimationTrack>(placementTracks.Length);
        foreach (var track in placementTracks)
        {
            var keys = ImmutableArray.CreateBuilder<Keyframe>(track.Keyframes.Length);
            foreach (var key in track.Keyframes)
            {
                var moved = Rotate(key.Value.Vector, rotation);
                var position = new ScenePoint(delta.X + moved.X, delta.Y + moved.Y);
                if (!ValidPoint(position))
                {
                    reason = "旋转原点补偿后的移动坐标超出原生范围";
                    return false;
                }
                keys.Add(key with { Value = position });
            }
            converted.Add(track with { Keyframes = keys.MoveToImmutable() });
        }
        convertedTransform = transform with { Position = delta, Pivot = pivot };
        convertedPlacementTracks = converted.MoveToImmutable();
        convertedNumericTracks = normalizedTracks.ToImmutable();
        return true;
    }

    internal static bool ValidPoint(ScenePoint point) => double.IsFinite(point.X) && double.IsFinite(point.Y) &&
        Math.Abs(point.X) <= 1e9 && Math.Abs(point.Y) <= 1e9;

    internal static ScenePoint Rotate(ScenePoint point, double degrees)
    {
        var parts = AssTransformMath.SinCos(degrees);
        return new(point.X * parts.Cosine - point.Y * parts.Sine, point.X * parts.Sine + point.Y * parts.Cosine);
    }

    internal static bool HasRangeGeometry(SubtitleLine line, IEnumerable<AnimationTrack> tracks)
    {
        var geometryTracks = tracks.Where(track => track.Target.TextRangeId is not null &&
            track.Property is AnimationProperty.POSITION or AnimationProperty.SCALE or AnimationProperty.ROTATION).ToArray();
        foreach (var range in line.AnimationRanges)
        {
            var position = range.Offset;
            var scale = range.Scale;
            var rotation = range.Rotation;
            foreach (var track in geometryTracks.Where(track => track.Target.TextRangeId == range.Id))
            {
                if (!TryConstant(track, out var value) || track.Target.State != SubtitleAnimationState.NORMAL)
                {
                    return true;
                }
                switch (track.Property)
                {
                    case AnimationProperty.POSITION:
                        position = value.Vector;
                        break;
                    case AnimationProperty.SCALE:
                        scale = value.Vector;
                        break;
                    case AnimationProperty.ROTATION:
                        rotation = value.Scalar;
                        break;
                }
            }
            if (position != default || scale != new ScenePoint(1, 1) || rotation != 0)
            {
                return true;
            }
        }
        return false;
    }

    internal static bool TryConstant(AnimationTrack track, out AnimationValue value)
    {
        return AssMoveConversion.TryConstant(track, out value);
    }
}
