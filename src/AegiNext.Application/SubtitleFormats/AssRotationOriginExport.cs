using AegiNext.Core.Projects;

namespace AegiNext.Application.SubtitleFormats;

internal sealed record AssRotationOriginExport(ScenePoint Origin, ScenePoint PositionOffset, double PositionRotation)
{
    internal static AssRotationOriginExport? Create(ProjectLayer layer, SubtitleLine line, ScenePoint basis,
        ScenePoint alignmentDelta, ScenePoint outputScale, double outputRotation, ScenePoint position,
        AssLinearMove? move, bool convertedPath, bool needsMeasurement, bool hasMeasurement, out string reason)
    {
        reason = string.Empty;
        if (AssRotationOriginConversion.HasRangeGeometry(line, layer.Tracks))
        {
            reason = "文字范围包含独立位移、缩放或旋转";
            return null;
        }
        var geometry = layer.Tracks.Where(track => track.Target.TextRangeId is null &&
            track.Property is AnimationProperty.POSITION or AnimationProperty.SCALE or AnimationProperty.ROTATION).ToArray();
        if (geometry.Any(track => track.Target.State != SubtitleAnimationState.NORMAL))
        {
            reason = "独立文字状态的几何不能共用固定原点";
            return null;
        }
        var scale = layer.Transform.Scale;
        var rotation = layer.Transform.Rotation;
        var animatedRotation = false;
        foreach (var track in geometry)
        {
            var constant = AssRotationOriginConversion.TryConstant(track, out var value);
            if (track.Property == AnimationProperty.SCALE)
            {
                if (!constant)
                {
                    reason = "动态缩放需要原点与位置联动";
                    return null;
                }
                scale = value.Vector;
            }
            else if (track.Property == AnimationProperty.ROTATION)
            {
                if (constant)
                {
                    rotation = value.Scalar;
                }
                else
                {
                    animatedRotation = true;
                }
            }
            else if (move is null)
            {
                reason = "位置轨道不能完整转换为单段直线移动";
                return null;
            }
        }
        if (!double.IsFinite(scale.X) || !double.IsFinite(scale.Y) || scale.X is <= 0 or > 10000 ||
            scale.Y is <= 0 or > 10000 || scale != outputScale)
        {
            reason = "原点补偿需要完整保留的固定正缩放";
            return null;
        }
        if (!double.IsFinite(rotation) || !animatedRotation && !rotation.Equals(outputRotation))
        {
            reason = "整行旋转未完整保留";
            return null;
        }
        if (layer.MotionPath is not null && !convertedPath || move is { Approximate: true })
        {
            reason = "移动路径或速度不能精确转换为单段直线移动";
            return null;
        }
        if (animatedRotation && move is not null && move.First != move.Last)
        {
            reason = "旋转动画与移动同时存在，需要位置联动";
            return null;
        }
        if (needsMeasurement && (!hasMeasurement || HasUnmeasuredTypography(layer)))
        {
            reason = "未取得完整固定排版轴心，静态测量不能补偿字号或字距布局轨道";
            return null;
        }
        if (!AssRotationOriginConversion.ValidPoint(basis))
        {
            reason = "排版基准超出可转换的坐标范围";
            return null;
        }
        AssRotationOriginExport converted;
        if (!animatedRotation && move is not null)
        {
            var pivot = layer.Transform.Pivot;
            var shifted = new ScenePoint(scale.X * pivot.X, scale.Y * pivot.Y);
            var origin = new ScenePoint(basis.X + shifted.X, basis.Y + shifted.Y);
            var layoutDelta = new ScenePoint(alignmentDelta.X + pivot.X, alignmentDelta.Y + pivot.Y);
            var reference = AssRotationOriginConversion.Rotate(shifted, -rotation);
            var offset = new ScenePoint(basis.X + scale.X * layoutDelta.X - reference.X,
                basis.Y + scale.Y * layoutDelta.Y - reference.Y);
            converted = new(origin, offset, -rotation);
        }
        else
        {
            var origin = new ScenePoint(basis.X + position.X, basis.Y + position.Y);
            var offset = new ScenePoint(basis.X + scale.X * alignmentDelta.X,
                basis.Y + scale.Y * alignmentDelta.Y);
            converted = new(origin, offset, 0);
        }
        if (!AssRotationOriginConversion.ValidPoint(converted.Origin) ||
            !AssRotationOriginConversion.ValidPoint(converted.MapPosition(position)) ||
            move is not null && (!AssRotationOriginConversion.ValidPoint(converted.MapPosition(move.First)) ||
                !AssRotationOriginConversion.ValidPoint(converted.MapPosition(move.Last))))
        {
            reason = "原点补偿后的原点或定位超出可转换的坐标范围";
            return null;
        }
        return converted;
    }

    internal ScenePoint MapPosition(ScenePoint position)
    {
        var mapped = AssRotationOriginConversion.Rotate(position, PositionRotation);
        return new(PositionOffset.X + mapped.X, PositionOffset.Y + mapped.Y);
    }

    private static bool HasUnmeasuredTypography(ProjectLayer layer)
    {
        foreach (var track in layer.Tracks.Where(track =>
            track.Property is AnimationProperty.FONT_SIZE or AnimationProperty.LETTER_SPACING))
        {
            if (track.Property == AnimationProperty.FONT_SIZE || track.Target.TextRangeId is not null ||
                track.Target.State != SubtitleAnimationState.NORMAL || !AssRotationOriginConversion.TryConstant(track, out _))
            {
                return true;
            }
        }
        return false;
    }
}
