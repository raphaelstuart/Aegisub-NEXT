using AegiNext.Core.Projects;

namespace AegiNext.Desktop.Tests.Workspace;

/// <summary>验证字幕范围位移草稿始终使用本地坐标，并保留完整动画目标和事务边界。</summary>
[Collection("Workspace session")]
public sealed class RangePositionDraftWorkflowTests
{
    /// <summary>范围位移不叠加整层 anchor，两个分量一次提交并一次撤销。</summary>
    [Fact]
    public async Task LocalOffsetDraftsCommitTogetherWithoutChangingLayerPlacementOrOtherRanges()
    {
        var document = Document();
        await using var context = new WorkspaceSessionTestContext(document);
        await context.InitializeAsync();
        var session = context.Session;
        session.SelectCue(document.Subtitles[0].Id);
        var range = document.Subtitles[0].AnimationRanges[0];
        session.ViewModel.Effects.Target = new(AnimationProperty.POSITION, TextRangeId: range.Id);
        var effects = session.ViewModel.Effects;

        Assert.True(effects.CanEditPosition);
        Assert.Equal(5, effects.PositionX);
        Assert.Equal(-7, effects.PositionY);
        effects.PositionXText = "18";
        effects.PositionYText = "-11";
        Assert.Same(document, context.Editor.Snapshot);
        Assert.False(context.Editor.CanUndo);
        Assert.True(session.TryCommitDrafts(false));

        var changed = context.Editor.Snapshot;
        Assert.Equal(new ScenePoint(18, -11), changed.Subtitles[0].AnimationRanges[0].Offset);
        Assert.Same(document.Subtitles[0].AnimationRanges[1], changed.Subtitles[0].AnimationRanges[1]);
        Assert.Same(document.Subtitles[0].Style, changed.Subtitles[0].Style);
        Assert.Equal(document.Layers[0].Transform, changed.Layers[0].Transform);
        Assert.Empty(changed.Layers[0].Tracks);
        Assert.True(context.Editor.Undo());
        Assert.Same(document, context.Editor.Snapshot);
        Assert.False(context.Editor.CanUndo);
        Assert.Equal(5, effects.PositionX);
        Assert.Equal(-7, effects.PositionY);
    }

    /// <summary>无效范围位移阻止切换，恢复 X 保留未提交 Y 且不引入整层坐标。</summary>
    [Fact]
    public async Task InvalidLocalComponentBlocksScopeChangeAndRestoreKeepsTheOtherRawDraft()
    {
        var document = Document();
        await using var context = new WorkspaceSessionTestContext(document);
        await context.InitializeAsync();
        var session = context.Session;
        session.SelectCue(document.Subtitles[0].Id);
        var range = document.Subtitles[0].AnimationRanges[0];
        var effects = session.ViewModel.Effects;
        effects.Target = new(AnimationProperty.POSITION, TextRangeId: range.Id);
        effects.PositionXText = "7e-";
        effects.PositionYText = "-12";

        effects.SelectedScope = effects.Scopes.Single(scope => scope.Id is null);
        session.RefreshEffectsInspectorTarget();

        Assert.Equal(range.Id, effects.Target.TextRangeId);
        Assert.Equal("7e-", effects.PositionXText);
        Assert.Equal("-12", effects.PositionYText);
        Assert.Equal("PositionXInput", effects.InvalidFieldKey);
        Assert.Same(document, context.Editor.Snapshot);
        Assert.False(context.Editor.CanUndo);
        effects.RestoreField("PositionXInput");
        Assert.Equal("5", effects.PositionXText);
        Assert.Equal("-12", effects.PositionYText);
        Assert.True(session.TryCommitDrafts(false));
        Assert.Equal(new ScenePoint(5, -12), context.Editor.Snapshot.Subtitles[0].AnimationRanges[0].Offset);
        Assert.Equal(document.Layers[0].Transform, context.Editor.Snapshot.Layers[0].Transform);
    }

    /// <summary>选中范围关键帧后，位置字段只更新该向量并保留反向曲线。</summary>
    [Fact]
    public async Task SelectedLocalPositionKeyframeUsesOffsetCoordinatesAndPreservesItsCurve()
    {
        var document = Document();
        var target = new AnimationTrackTarget(AnimationProperty.POSITION, TextRangeId: document.Subtitles[0].AnimationRanges[0].Id);
        var frame = new Keyframe(new(1), new ScenePoint(8, -10), KeyframeInterpolation.POWER)
        {
            Reverse = true, Exponent = 2.5, CurveStart = 0.2, CurveEnd = 0.8
        };
        var whole = new AnimationTrack(AnimationProperty.POSITION, [new(new(0), new ScenePoint(100, 200))]);
        document = document with { Layers = [document.Layers[0] with { Tracks = [whole, new(target, [frame])] }] };
        await using var context = new WorkspaceSessionTestContext(document);
        await context.InitializeAsync();
        var session = context.Session;
        session.SelectCue(document.Subtitles[0].Id);
        Assert.True(session.SelectKeyframe(new(document.Layers[0].Id, target, frame.Time, frame.Time)));
        var effects = session.ViewModel.Effects;
        Assert.Equal(8, effects.PositionX);
        Assert.Equal(-10, effects.PositionY);
        effects.PositionXText = "9";
        effects.PositionYText = "-15";
        Assert.True(session.TryCommitDrafts(false));

        var changed = Assert.Single(session.SelectedLayer!.Tracks.Single(track => track.Target == target).Keyframes);
        Assert.Equal(frame with { Value = new ScenePoint(9, -15) }, changed);
        Assert.Same(whole, session.SelectedLayer.Tracks.Single(track => track.Target == whole.Target));
        Assert.Equal(document.Subtitles[0].AnimationRanges, context.Editor.Snapshot.Subtitles[0].AnimationRanges);
        effects.PositionXText = "unfinished";
        effects.RestoreField("PositionXInput");
        Assert.Equal("9", effects.PositionXText);
        Assert.False(session.HasProjectDrafts);
    }

    /// <summary>范围动画开关使用完整身份，初始关键帧取范围基础位移。</summary>
    [Fact]
    public async Task LocalPositionAnimationToggleCreatesTheLocalBaseAndLeavesWholeLayerAnimationAlone()
    {
        var document = Document();
        var whole = new AnimationTrack(AnimationProperty.POSITION, [new(new(0), new ScenePoint(100, 200))]);
        document = document with { Layers = [document.Layers[0] with { Tracks = [whole] }] };
        await using var context = new WorkspaceSessionTestContext(document);
        await context.InitializeAsync();
        var session = context.Session;
        session.SelectCue(document.Subtitles[0].Id);
        var target = new AnimationTrackTarget(AnimationProperty.POSITION, TextRangeId: document.Subtitles[0].AnimationRanges[0].Id);
        session.ViewModel.Effects.Target = target;

        await session.SetEffectPropertyAnimationAsync(target, true);

        var enabled = context.Editor.Snapshot;
        Assert.Equal(new ScenePoint(5, -7), Assert.Single(session.SelectedLayer!.Tracks.Single(track => track.Target == target).Keyframes).Value.Vector);
        Assert.Same(whole, session.SelectedLayer.Tracks.Single(track => track.Target == whole.Target));
        await session.SetEffectPropertyAnimationAsync(target, false);
        Assert.Same(whole, Assert.Single(session.SelectedLayer.Tracks));
        Assert.True(context.Editor.Undo());
        Assert.Same(enabled, context.Editor.Snapshot);
        Assert.True(context.Editor.Undo());
        Assert.Same(document, context.Editor.Snapshot);
        Assert.False(context.Editor.CanUndo);
    }

    private static ProjectDocument Document()
    {
        var line = new SubtitleLine
        {
            Text = "ABCD", End = new(2), Style = new()
            {
                Position = new() { Anchor = new(0.25, 0.75), Offset = new(40, -20) }
            },
            AnimationRanges = [new(Guid.NewGuid(), 1, 2) { Offset = new(5, -7) }, new(Guid.NewGuid(), 0, 1) { Offset = new(2, 3) }]
        };
        return new() { Subtitles = [line], Layers = [new() { SubtitleId = line.Id, End = line.End, Transform = new() { Position = new(100, 200) } }] };
    }
}
