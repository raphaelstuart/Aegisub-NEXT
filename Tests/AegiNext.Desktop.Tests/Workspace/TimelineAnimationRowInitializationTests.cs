using AegiNext.Application;
using AegiNext.Core.Projects;
using AegiNext.Desktop.Workspace;

namespace AegiNext.Desktop.Tests.Workspace;

[Collection("Workspace session")]
public sealed class TimelineAnimationRowInitializationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewRowsStartCollapsedAndManualStateSurvivesEditsRemovalAndUndoRedo(bool collapsed)
    {
        var layer = new ProjectLayer { Kind = LayerKind.SHAPE, Shape = new(ShapeKind.RECTANGLE, 40, 20) };
        var document = new ProjectDocument { Layers = [layer] };
        await using var context = new WorkspaceSessionTestContext(document);
        await context.InitializeAsync();
        var session = context.Session;
        var opacity = new TimelineAnimationRowId(TimelineRowScope.TRACK, layer.TrackId, AnimationProperty.OPACITY);
        var rotation = opacity with { Property = AnimationProperty.ROTATION };

        context.Editor.SetKeyframe(layer.Id, AnimationProperty.OPACITY, new(new(0), 0.5));

        Assert.Equal(opacity, Assert.Single(session.TimelineViewState.CollapsedAnimationRows));
        Assert.Empty(context.Editor.Snapshot.TimelineViewState.CollapsedAnimationRows);
        session.SetTimelineAnimationRowCollapsed(opacity, collapsed);
        context.Editor.SetKeyframe(layer.Id, AnimationProperty.OPACITY, new(new(0), 0.75));
        Assert.Equal(collapsed, session.TimelineViewState.CollapsedAnimationRows.Contains(opacity));
        context.Editor.SetKeyframe(layer.Id, AnimationProperty.ROTATION, new(new(0), 30));
        Assert.Contains(rotation, session.TimelineViewState.CollapsedAnimationRows);
        Assert.Equal(collapsed, session.TimelineViewState.CollapsedAnimationRows.Contains(opacity));

        Assert.True(context.Editor.Undo());
        Assert.True(context.Editor.Undo());
        Assert.True(context.Editor.Undo());
        Assert.Same(document, context.Editor.Snapshot);
        Assert.False(context.Editor.CanUndo);
        Assert.True(context.Editor.Redo());
        Assert.Equal(collapsed, session.TimelineViewState.CollapsedAnimationRows.Contains(opacity));
        Assert.True(context.Editor.Redo());
        Assert.True(context.Editor.Redo());
        Assert.Contains(rotation, session.TimelineViewState.CollapsedAnimationRows);
        Assert.Equal(collapsed, session.TimelineViewState.CollapsedAnimationRows.Contains(opacity));
        Assert.False(context.Editor.CanRedo);
    }

    [Fact]
    public async Task ExistingRowsKeepTheirStateWhileNewTrackRangeAndStateRowsStartCollapsed()
    {
        var otherTrack = new ProjectTrack { Name = "Other" };
        var range = new SubtitleAnimationRange(Guid.NewGuid(), 0, 1);
        var cue = new SubtitleLine { End = new(3), Text = "AB", AnimationRanges = [range] };
        var layer = new ProjectLayer
        {
            Id = cue.Id, SubtitleId = cue.Id, Start = cue.Start, End = cue.End,
            Tracks = [new(AnimationProperty.STROKE_WIDTH, [new(new(0), 0.5)])]
        };
        var otherLayer = new ProjectLayer
        {
            TrackId = otherTrack.Id, Kind = LayerKind.SHAPE, Shape = new(ShapeKind.RECTANGLE, 40, 20)
        };
        var document = new ProjectDocument
        {
            Tracks = [ProjectTrack.Default, otherTrack], Subtitles = [cue], Layers = [layer, otherLayer]
        };
        await using var context = new WorkspaceSessionTestContext(document);
        await context.InitializeAsync();
        var existing = new TimelineAnimationRowId(TimelineRowScope.TRACK, layer.TrackId, AnimationProperty.STROKE_WIDTH);
        var rangeTarget = new AnimationTrackTarget(AnimationProperty.STROKE_WIDTH, TextRangeId: range.Id);
        var stateTarget = new AnimationTrackTarget(AnimationProperty.STROKE_WIDTH, State: SubtitleAnimationState.INACTIVE);

        context.Editor.SetKeyframe(layer.Id, AnimationProperty.STROKE_WIDTH, new(new(0), 0.75));
        context.Editor.SetKeyframe(otherLayer.Id, AnimationProperty.STROKE_WIDTH, new(new(0), 0.5));
        context.Editor.SetKeyframe(layer.Id, rangeTarget, new(new(0), 0.5));
        context.Editor.SetKeyframe(layer.Id, stateTarget, new(new(0), 0.5));

        var rows = context.Session.TimelineViewState.CollapsedAnimationRows;
        Assert.Equal(3, rows.Length);
        Assert.DoesNotContain(existing, rows);
        Assert.Contains(existing with { OwnerId = otherTrack.Id }, rows);
        Assert.Contains(existing with { TextRangeId = range.Id }, rows);
        Assert.Contains(existing with { State = SubtitleAnimationState.INACTIVE }, rows);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SavingAndReopeningPreservesNewRowStateWithoutMarkingTheProjectDirty(bool collapsed)
    {
        var layer = new ProjectLayer { Kind = LayerKind.SHAPE, Shape = new(ShapeKind.RECTANGLE, 40, 20) };
        await using var context = new WorkspaceSessionTestContext(new() { Layers = [layer] });
        await context.InitializeAsync();
        var session = context.Session;
        var row = new TimelineAnimationRowId(TimelineRowScope.TRACK, layer.TrackId, AnimationProperty.OPACITY);
        context.Editor.SetKeyframe(layer.Id, AnimationProperty.OPACITY, new(new(0), 0.5));
        Assert.Contains(row, session.TimelineViewState.CollapsedAnimationRows);
        session.SetTimelineAnimationRowCollapsed(row, collapsed);
        var content = context.Editor.Snapshot;
        var persisted = session.CreatePersistenceSnapshot(content);
        var path = Path.Combine(context.DirectoryPath, "new-animation.aeginext");
        await ProjectStore.SaveAsync(persisted, path);
        session.AcceptProjectSave(content, persisted);

        Assert.Equal(ProjectOpenStatus.OPENED, (await session.OpenProjectAsync(path)).Status);

        Assert.Equal(collapsed, session.TimelineViewState.CollapsedAnimationRows.Contains(row));
        Assert.False(session.HasUnsavedChanges);
        Assert.False(context.Editor.CanUndo);
        context.Editor.SetKeyframe(layer.Id, AnimationProperty.OPACITY, new(new(0), 0.75));
        Assert.Equal(collapsed, session.TimelineViewState.CollapsedAnimationRows.Contains(row));
        Assert.True(context.Editor.Undo());
        Assert.False(session.HasUnsavedChanges);
    }
}
