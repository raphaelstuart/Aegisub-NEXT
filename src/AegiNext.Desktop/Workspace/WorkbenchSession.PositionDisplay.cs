using AegiNext.Core.Projects;

namespace AegiNext.Desktop.Workspace;

internal sealed partial class WorkbenchSession
{
    private ScenePoint? InspectorPosition(ProjectLayer? layer, Rendering.LayerPlacementResolution placement)
    {
        if (layer is null)
        {
            return null;
        }
        var position = InspectorVector(layer, AnimationProperty.POSITION, layer.Transform.Position);
        if (SceneEditing.Target.TextRangeId is not null)
        {
            return position;
        }
        return placement.BasePosition is { } basePosition
            ? new(basePosition.X + position.X, basePosition.Y + position.Y) : null;
    }
}
