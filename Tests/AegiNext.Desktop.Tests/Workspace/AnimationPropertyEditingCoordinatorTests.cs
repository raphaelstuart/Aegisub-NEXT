using AegiNext.Core.Projects;

namespace AegiNext.Desktop.Tests.Workspace;

/// <summary>验证共享动画属性编辑状态、目标身份及草稿事务。</summary>
[Collection("Workspace session")]
public sealed class AnimationPropertyEditingCoordinatorTests
{
    /// <summary>同一属性目标复用状态，刷新保留无效草稿且不修改工程。</summary>
    [Fact]
    public async Task SameTargetReusesTheRowAndInspectorRefreshPreservesInvalidDraftWithoutWritingTheProject()
    {
        var document = CreateDocument();
        await using var context = new WorkspaceSessionTestContext(document);
        await context.InitializeAsync();
        var session = context.Session;
        session.SelectCue(document.Subtitles[0].Id);
        var target = new AnimationTrackTarget(AnimationProperty.FONT_SIZE);
        var row = session.PropertyEditing.GetRow(document.Layers[0].Id, target);
        var original = context.Editor.Snapshot;
        row.BeginEdit("effects");
        row.X.RawText = "unfinished";

        session.RefreshEffectsInspectorTarget();

        var refreshed = session.PropertyEditing.GetRow(document.Layers[0].Id, target);
        Assert.Same(row, refreshed);
        Assert.Same(row.X, refreshed.X);
        Assert.Equal("unfinished", refreshed.X.RawText);
        Assert.True(row.HasDraft);
        Assert.True(session.PropertyEditing.HasDrafts);
        Assert.Same(row, session.PropertyEditing.FindRow(row.XFieldKey));
        Assert.Same(original, context.Editor.Snapshot);
        Assert.False(context.Editor.CanUndo);
        row.Restore(row.XFieldKey);
        Assert.False(session.PropertyEditing.HasDrafts);
    }

    /// <summary>不同节点的同名属性具有独立完整身份，不共享草稿。</summary>
    [Fact]
    public async Task NodeTargetsUseDistinctRowsAndFieldKeysIncludingTheirNodeIdentity()
    {
        var first = new MaskNode { Position = new(10, 20) };
        var second = new MaskNode { Position = new(30, 40) };
        var document = CreateDocument(new VectorClipMask { Contours = [new() { Nodes = [first, second] }] });
        await using var context = new WorkspaceSessionTestContext(document);
        await context.InitializeAsync();
        var session = context.Session;
        session.SelectCue(document.Subtitles[0].Id);
        var firstTarget = new AnimationTrackTarget(AnimationProperty.MASK_NODE_POSITION, first.Id);
        var secondTarget = firstTarget with { NodeId = second.Id };
        var firstRow = session.PropertyEditing.GetRow(document.Layers[0].Id, firstTarget);
        var secondRow = session.PropertyEditing.GetRow(document.Layers[0].Id, secondTarget);

        Assert.NotSame(firstRow, secondRow);
        Assert.NotSame(firstRow.X, secondRow.X);
        Assert.NotEqual(firstRow.FieldKey, secondRow.FieldKey);
        Assert.NotEqual(firstRow.XFieldKey, secondRow.XFieldKey);
        Assert.Contains(first.Id.ToString("N"), firstRow.FieldKey);
        Assert.Contains(second.Id.ToString("N"), secondRow.FieldKey);
        Assert.Equal(firstTarget, firstRow.Target);
        Assert.Equal(secondTarget, secondRow.Target);
        firstRow.BeginEdit("masks");
        firstRow.X.RawText = "75";

        Assert.Equal("30", secondRow.X.RawText);
        Assert.Same(firstRow, session.PropertyEditing.FindRow(firstRow.XFieldKey));
        Assert.Same(secondRow, session.PropertyEditing.FindRow(secondRow.XFieldKey));
        Assert.Same(document, context.Editor.Snapshot);
        firstRow.Restore(firstRow.XFieldKey);
    }

    /// <summary>不同面板来源的多属性草稿合并提交，一次撤销恢复源快照。</summary>
    [Fact]
    public async Task TwoPropertyDraftsCommitOnceAndOneUndoRestoresBothValuesAndTheOriginalSnapshot()
    {
        var document = CreateDocument();
        await using var context = new WorkspaceSessionTestContext(document);
        await context.InitializeAsync();
        var session = context.Session;
        session.SelectCue(document.Subtitles[0].Id);
        var font = session.PropertyEditing.GetRow(document.Layers[0].Id, new(AnimationProperty.FONT_SIZE));
        var position = session.PropertyEditing.GetRow(document.Layers[0].Id, new(AnimationProperty.MASK_POSITION));
        font.BeginEdit("effects");
        font.X.RawText = "72";
        position.BeginEdit("masks");
        position.X.RawText = "12";
        position.Y.RawText = "34";
        var preview = session.PreviewDocument;

        Assert.Equal(72, preview.Subtitles[0].Style.FontSize);
        Assert.Equal(new ScenePoint(12, 34), preview.Layers[0].Mask!.Transform.Position);
        Assert.Same(document, context.Editor.Snapshot);
        Assert.True(session.TryCommitDrafts());

        Assert.Equal(72, context.Editor.Snapshot.Subtitles[0].Style.FontSize);
        Assert.Equal(new ScenePoint(12, 34), context.Editor.Snapshot.Layers[0].Mask!.Transform.Position);
        Assert.False(session.PropertyEditing.HasDrafts);
        Assert.Equal("Commit workspace drafts", context.Editor.UndoLabel);
        Assert.True(context.Editor.Undo());
        Assert.Same(document, context.Editor.Snapshot);
        Assert.False(context.Editor.CanUndo);
    }

    /// <summary>仅恢复向量X不能解除仍有Y草稿的冻结来源或绕过文档冲突。</summary>
    [Fact]
    public async Task RestoringOneVectorComponentKeepsTheRemainingDraftBoundToItsSource()
    {
        var document = CreateDocument();
        await using var context = new WorkspaceSessionTestContext(document);
        await context.InitializeAsync();
        var session = context.Session;
        session.SelectCue(document.Subtitles[0].Id);
        var row = session.PropertyEditing.GetRow(document.Layers[0].Id, new(AnimationProperty.MASK_POSITION));
        row.BeginEdit("effects");
        row.X.RawText = "unfinished";
        row.Y.RawText = "34";
        var frozenTarget = session.SceneEditing.DraftTarget;
        session.Details.EditText(0, 0, "Edited ");
        Assert.True(session.Details.TryCommit());
        var afterDetails = context.Editor.Snapshot;
        try
        {
            row.Restore(row.XFieldKey);

            Assert.Equal("0", row.X.RawText);
            Assert.Equal("34", row.Y.RawText);
            Assert.True(row.HasDraft);
            Assert.Same(frozenTarget, session.SceneEditing.DraftTarget);
            row.X.RawText = "12";
            Assert.False(session.TryCommitDrafts(false));
            Assert.Same(afterDetails, context.Editor.Snapshot);
            Assert.Equal("12", row.X.RawText);
            Assert.Equal("34", row.Y.RawText);
        }
        finally
        {
            row.Restore(row.XFieldKey);
            row.Restore(row.YFieldKey);
        }
    }

    /// <summary>节点删除后淘汰对应属性状态，清理过程不写入工程或新增撤销记录。</summary>
    [Fact]
    public async Task RemovingANodeEvictsItsCachedRowsWithoutAnotherProjectWrite()
    {
        var first = new MaskNode { Position = new(10, 20) };
        var second = new MaskNode { Position = new(30, 40) };
        var document = CreateDocument(new VectorClipMask { Contours = [new() { Nodes = [first, second] }] });
        await using var context = new WorkspaceSessionTestContext(document);
        await context.InitializeAsync();
        var session = context.Session;
        session.SelectCue(document.Subtitles[0].Id);
        var row = session.PropertyEditing.GetRow(document.Layers[0].Id, new(AnimationProperty.MASK_NODE_POSITION, first.Id));
        context.Editor.RemoveClipMaskNode(document.Layers[0].Id, first.Id);
        var removed = context.Editor.Snapshot;

        session.RefreshEffectsInspectorTarget();

        Assert.Null(session.PropertyEditing.FindRow(row.XFieldKey));
        Assert.Same(removed, context.Editor.Snapshot);
        Assert.False(session.PropertyEditing.HasDrafts);
        Assert.True(context.Editor.Undo());
        Assert.Same(document, context.Editor.Snapshot);
        Assert.False(context.Editor.CanUndo);
    }

    private static ProjectDocument CreateDocument(ClipMask? mask = null)
    {
        var subtitle = new SubtitleLine { End = new(5), Text = "Shared property editing" };
        return new()
        {
            Subtitles = [subtitle],
            Layers =
            [
                new()
                {
                    Kind = LayerKind.SUBTITLE,
                    SubtitleId = subtitle.Id,
                    End = subtitle.End,
                    Mask = mask ?? new RectangleClipMask { TopLeft = new(10, 20), BottomRight = new(400, 300) }
                }
            ]
        };
    }
}
