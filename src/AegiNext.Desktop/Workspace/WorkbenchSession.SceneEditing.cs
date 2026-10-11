using AegiNext.Core.Projects;
using AegiNext.Core.Editing;
using AegiNext.Core.Timing;
using AegiNext.Desktop.Controls;
using AegiNext.Desktop.Editing;
using AegiNext.Desktop.I18n;

namespace AegiNext.Desktop.Workspace;

internal sealed partial class WorkbenchSession
{
    private MediaTime? inspectorTime;
    private Guid? inspectorLayerId;

    internal SceneEditingState SceneEditing { get; } = new();

    internal AnimationEditTarget? AnimationTarget => SelectedLayer is { } layer
        ? SceneEditing.GestureTarget ?? SceneEditing.DraftTarget ?? new(layer.Id, SelectedKeyTime ?? LayerAnimationTiming.ClampTime(layer, ProjectPosition - layer.Start + layer.AnimationOffset), SelectedKeyTime is not null, SceneEditing.Target)
        : null;

    internal MediaTime EditingPosition => SelectedLayer is { } layer && (SceneEditing.GestureTarget?.LocalTime ?? SelectedKeyTime) is { } time
        ? layer.Start + time - layer.AnimationOffset : ProjectPosition;

    internal event EventHandler? SceneGestureCancellationRequested;

    internal void CancelSceneGesture()
    {
        CancelCanvasGesture();
        MaskEditing.CancelGesture();
        SceneGestureCancellationRequested?.Invoke(this, EventArgs.Empty);
    }

    private void FreezeDraftTarget()
    {
        if (SceneEditing.DraftTarget is not null || SelectedLayer is not { } layer)
        {
            return;
        }
        var local = SelectedKeyTime ?? (inspectorTime ?? ProjectPosition) - layer.Start + layer.AnimationOffset;
        SceneEditing.DraftTarget = new(layer.Id, LayerAnimationTiming.ClampTime(layer, local), SelectedKeyTime is not null, SceneEditing.Target);
        _ = RunCommandAsync(PauseForSceneEditAsync);
    }

    internal Task PauseForSceneEditAsync() => controller.Snapshot.State == AegiNext.Media.Playback.VideoPlaybackState.PLAYING
        ? controller.PauseAsync() : Task.CompletedTask;

    internal bool BeginCanvasGesture()
    {
        if (!TryCommitDrafts() || AnimationTarget is not { } target)
        {
            return false;
        }
        SceneEditing.GestureTarget = target with { Target = new AnimationTrackTarget(AnimationProperty.POSITION) };
        _ = RunCommandAsync(PauseForSceneEditAsync);
        return true;
    }

    internal void CancelCanvasGesture() => SceneEditing.GestureTarget = null;

    internal Task CommitCanvasAsync(CanvasLayerEditEventArgs value) => RunCommandAsync(() => EditAsync(() =>
    {
        var layer = SelectedLayer;
        var target = SceneEditing.GestureTarget ?? AnimationTarget;
        SceneEditing.GestureTarget = null;
        if (layer is null || target is null || layer.Id != value.LayerId)
        {
            return;
        }
        var document = editor.Snapshot;
        var prepared = WorkspaceDraftOperations.UpdateLayer(document, layer.Id, item => item with { MotionPath = value.Path });
        var transform = layer.Transform;
        foreach (var (property, changed, original) in new[]
        {
            (AnimationProperty.POSITION, value.Transform.Position, transform.Position),
            (AnimationProperty.SCALE, value.Transform.Scale, transform.Scale)
        })
        {
            if (changed != original)
            {
                var evaluated = AnimationEditOperations.Value(layer, property, target, original);
                prepared = AnimationEditOperations.SetValue(prepared, target, property,
                    new ScenePoint(evaluated.X + changed.X - original.X, evaluated.Y + changed.Y - original.Y));
            }
        }
        if (value.Transform.Rotation != transform.Rotation)
        {
            var evaluated = AnimationEditOperations.Value(layer, AnimationProperty.ROTATION, target, transform.Rotation);
            prepared = AnimationEditOperations.SetValue(prepared, target, AnimationProperty.ROTATION,
                evaluated + value.Transform.Rotation - transform.Rotation);
        }
        if (prepared != document)
        {
            editor.Apply("Edit scene in video preview", _ => prepared);
        }
    }));

    private void RefreshAnimatedInspectorAtTime()
    {
        if (effectsDirty || stylesDirty || inspectorTime == EditingPosition && inspectorLayerId == SelectedLayerId)
        {
            return;
        }
        inspectorTime = EditingPosition;
        inspectorLayerId = SelectedLayerId;
        using var updateLease = BeginWorkbenchUpdate();
        try
        {
            RefreshInspector();
        }
        finally
        {
            updateLease.Dispose();
        }
    }

    internal void RefreshMaskPreview() => RefreshEditingPreview();

    private void RefreshEditingPreview()
    {
        var document = PreviewDocument;
        var layer = document.Layers.FirstOrDefault(value => value.Id == SelectedLayerId);
        ViewModel.Preview.Scene = new(document, layer, EditingPosition, SceneEditing.Mode,
            ProjectDirectory, SelectedKeyTime is not null || SceneEditing.GestureTarget is not null, playback.UsesInteractiveQuality,
            preferences.PreviewQuality);
        Volatile.Write(ref previewState, CreatePreviewState(document));
    }

    private double InspectorValue(ProjectLayer layer, AnimationProperty property, double fallback)
    {
        var identity = SceneEditing.Target with { Property = property, NodeId = null,
            State = AnimationPropertyMetadata.IsSubtitleVisualProperty(property) ? SceneEditing.Target.State : SubtitleAnimationState.NORMAL };
        if ((identity.TextRangeId is not null && Panels.Effects.EffectsPanelViewModel.IsRangeProperty(property) ||
             identity.State != SubtitleAnimationState.NORMAL && Panels.Effects.EffectsPanelViewModel.IsAppearanceProperty(property)) &&
            layer.SubtitleId is { } id)
        {
            fallback = SubtitleAnimationEvaluation.GetBaseValue(layer, DocumentSnapshot.Subtitles.Single(line => line.Id == id), identity).Scalar;
        }
        return AnimationTarget is { } target ? AnimationEditOperations.Value(layer, property, target, fallback) : fallback;
    }

    private ScenePoint InspectorVector(ProjectLayer layer, AnimationProperty property, ScenePoint fallback)
    {
        var identity = SceneEditing.Target with { Property = property, NodeId = null,
            State = AnimationPropertyMetadata.IsSubtitleVisualProperty(property) ? SceneEditing.Target.State : SubtitleAnimationState.NORMAL };
        if (identity.TextRangeId is not null && Panels.Effects.EffectsPanelViewModel.IsRangeProperty(property) && layer.SubtitleId is { } id)
        {
            fallback = SubtitleAnimationEvaluation.GetBaseValue(layer, DocumentSnapshot.Subtitles.Single(line => line.Id == id), identity).Vector;
        }
        return AnimationTarget is { } target ? AnimationEditOperations.Value(layer, property, target, fallback) : fallback;
    }

    private SceneColor InspectorColor(ProjectLayer layer, SceneColor color, bool stroke) =>
        AnimationTarget is { } target
            ? AnimationEditOperations.Value(layer, stroke ? AnimationProperty.STROKE : AnimationProperty.FILL, target, color)
            : color;

    private SceneColor PrepareColor(ref ProjectDocument document, ProjectLayer layer, SceneColor requested, SceneColor original, bool stroke)
    {
        var displayed = InspectorColor(layer, original, stroke);
        var property = stroke ? AnimationProperty.STROKE : AnimationProperty.FILL;
        if (requested == displayed)
        {
            return original;
        }
        if (AnimationTarget is { } target && (target.IsKeyframe || layer.Tracks.Any(track => track.Property == property)))
        {
            document = AnimationEditOperations.SetValue(document, target, property, requested);
            return original;
        }
        return requested;
    }

    private double PrepareStrokeWidth(ref ProjectDocument document, ProjectLayer layer, double baseValue, string text)
    {
        var original = InspectorValue(layer, AnimationProperty.STROKE_WIDTH, baseValue);
        var requested = ReadNumber(text, original, "StrokeWidth", "StrokeWidthInput");
        if (requested != original && AnimationTarget is { } target && (target.IsKeyframe || layer.Tracks.Any(track => track.Property == AnimationProperty.STROKE_WIDTH)))
        {
            document = AnimationEditOperations.SetValue(document, target, AnimationProperty.STROKE_WIDTH, requested);
            return baseValue;
        }
        return requested == original ? baseValue : requested;
    }

    private double PrepareStyleAnimationNumber(ref ProjectDocument document, ProjectLayer layer,
        AnimationProperty property, double baseValue, string text, string label)
    {
        var original = InspectorValue(layer, property, baseValue);
        var requested = ReadNumber(text, original, label, label + "Input");
        if (requested != original && AnimationTarget is { } target &&
            (target.IsKeyframe || layer.Tracks.Any(track => track.Property == property)))
        {
            document = AnimationEditOperations.SetValue(document, target, property, requested);
            return baseValue;
        }
        return requested == original ? baseValue : requested;
    }

    private void RefreshEditingTargetLabel()
    {
        ViewModel.Effects.EditTargetLabel = AnimationTarget is { } target
            ? $"{Localization.Get("Workbench." + (target.IsKeyframe ? "EditKeyframeTarget" : "EditPlayheadTarget"))} {TimelineTimeText.Format(target.LocalTime)}"
            : Localization.Get("Workbench.NoSelection");
    }
}
