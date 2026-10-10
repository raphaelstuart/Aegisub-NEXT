using System.Collections.Immutable;
using AegiNext.Core.Effects;
using AegiNext.Core.Projects;

namespace AegiNext.Desktop.Tests.Workspace;

/// <summary>验证工作台异步应用分组脚本时的批量事务和失败原子性。</summary>
[Collection("Workspace session")]
public sealed class GroupedEffectWorkflowTests
{
    /// <summary>批量生成范围与轨道一次提交，一次撤销恢复应用前快照。</summary>
    [Fact]
    public async Task WorkbenchBatchApplicationCommitsAllGeneratedRangesAndTracksWithOneUndo()
    {
        var document = Document("AB", "CD", "Unselected");
        await using var context = new WorkspaceSessionTestContext(document);
        await context.InitializeAsync();
        var session = context.Session;
        Assert.True(session.SelectSubtitleRows(document.Subtitles[0].Id, document.Subtitles.Take(2).Select(line => line.Id)));

        await session.EffectScripts.ApplyAsync(Source("current", "grapheme"));

        var applied = context.Editor.Snapshot;
        Assert.Null(session.LastError);
        Assert.False(session.IsProjectBusy);
        for (var index = 0; index < 2; index++)
        {
            var line = applied.Subtitles[index];
            Assert.Equal(2, line.AnimationRanges.Length);
            Assert.All(line.AnimationRanges, range => Assert.Equal("grouped-workflow", range.GeneratedOrigin!.EffectId));
            Assert.Equal(2, applied.Layers[index].Tracks.Length);
            Assert.All(applied.Layers[index].Tracks, track => Assert.Contains(line.AnimationRanges, range => range.Id == track.Target.TextRangeId));
        }
        Assert.Same(document.Subtitles[2], applied.Subtitles[2]);
        Assert.Same(document.Layers[2], applied.Layers[2]);
        Assert.Equal("Apply effect script", context.Editor.UndoLabel);
        Assert.True(context.Editor.Undo());
        Assert.Same(document, context.Editor.Snapshot);
        Assert.False(context.Editor.CanUndo);
        Assert.True(context.Editor.Redo());
        Assert.Same(applied, context.Editor.Snapshot);
    }

    /// <summary>后续目标范围越界时，不提交先前目标已准备的范围或轨道。</summary>
    [Fact]
    public async Task LaterTargetFailureLeavesNoGeneratedRangesTracksOrUndoEntry()
    {
        var document = Document("ABCD", "A");
        await using var context = new WorkspaceSessionTestContext(document);
        await context.InitializeAsync();
        var session = context.Session;
        Assert.True(session.SelectSubtitleRows(document.Subtitles[0].Id, document.Subtitles.Select(line => line.Id)));

        await Assert.ThrowsAsync<EffectScriptException>(() => session.EffectScripts.ApplyAsync(Source("range(2, 2)", "grapheme")));

        Assert.Same(document, context.Editor.Snapshot);
        Assert.False(context.Editor.CanUndo);
        Assert.False(session.IsProjectBusy);
        Assert.All(context.Editor.Snapshot.Subtitles, line => Assert.Empty(line.AnimationRanges));
        Assert.All(context.Editor.Snapshot.Layers, layer => Assert.Empty(layer.Tracks));
    }

    private static string Source(string target, string unit)
    {
        return $$"""
            effect "grouped-workflow" version 2
            short-clip compress
            scope letters {{target}}
                unit {{unit}}
                stagger 50ms
                segment pulse fixed 100ms pingpong
                    at 0 scale base power(2)
                    at 1 scale factor(1.25, 1.25)
                end
                segment rest flex 1
                end
            end
            """;
    }

    private static ProjectDocument Document(params string[] texts)
    {
        var lines = texts.Select((text, index) => new SubtitleLine
        {
            Text = text, Start = new(index * 3), End = new(index * 3 + 2)
        }).ToImmutableArray();
        return new()
        {
            Subtitles = lines,
            Layers = lines.Select(line => new ProjectLayer { SubtitleId = line.Id, Start = line.Start, End = line.End }).ToImmutableArray()
        };
    }
}
