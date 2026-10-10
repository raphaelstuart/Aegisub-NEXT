using System.Collections.Immutable;
using AegiNext.Core.Projects;

namespace AegiNext.Application.Tests;

public sealed class GeneratedRangeLifecycleTests
{
    [Theory]
    [InlineData("clipboard")]
    [InlineData("project-merge")]
    [InlineData("split")]
    public void CloningRemapsGeneratedParentIdentitiesAndPreservesOffsets(string operation)
    {
        var source = Document();
        ProjectDocument result;
        if (operation == "clipboard")
        {
            var captured = ProjectEditingOperations.CaptureClips(source, [source.Layers[0].Id], source.Layers[0].Id);
            result = ProjectEditingOperations.PasteClips(source, captured, new(4)).Document;
        }
        else if (operation == "project-merge")
        {
            result = ProjectEditingOperations.MergeProjects(new(), [new(source, "Source", "/tmp")]).Document;
        }
        else
        {
            result = ProjectEditingOperations.SplitSubtitle(source, source.Subtitles[0].Id, new(2), 2);
        }

        var copied = result.Subtitles[^1];
        var parent = copied.AnimationRanges.Single(range => range.GeneratedOrigin is null);
        var child = copied.AnimationRanges.Single(range => range.GeneratedOrigin is not null);
        Assert.NotEqual(source.Subtitles[0].AnimationRanges[0].Id, parent.Id);
        Assert.NotEqual(source.Subtitles[0].AnimationRanges[1].Id, child.Id);
        Assert.Equal(parent.Id, child.GeneratedOrigin!.ParentRangeId);
        Assert.Equal(new ScenePoint(0, -12), child.Offset);
        Assert.Equal(child.Id, result.Layers[^1].Tracks[0].Target.TextRangeId);
        ProjectValidator.Validate(result);
    }

    [Fact]
    public void RemovingAParentRangeDeletesAllDescendantsAndTheirTracksWithOneUndo()
    {
        var original = Document();
        var line = original.Subtitles[0];
        var child = line.AnimationRanges[1];
        var grandchild = new SubtitleAnimationRange(Guid.NewGuid(), 3, 1)
        {
            GeneratedOrigin = new("other-pulse", "third", child.Id, "grapheme")
        };
        var unrelated = new SubtitleAnimationRange(Guid.NewGuid(), 0, 1);
        original = original with { Subtitles = [line with { AnimationRanges = line.AnimationRanges.Add(grandchild).Add(unrelated) }] };
        var editor = new ProjectEditor(original);

        editor.RemoveSubtitleAnimationRange(line.Id, line.AnimationRanges[0].Id);

        Assert.Equal(unrelated, Assert.Single(editor.Snapshot.Subtitles[0].AnimationRanges));
        Assert.Empty(editor.Snapshot.Layers[0].Tracks);
        Assert.True(editor.Undo());
        Assert.Same(original, editor.Snapshot);
        Assert.False(editor.CanUndo);
    }

    [Fact]
    public void TextDeletionPrunesGeneratedDescendantsWhoseParentNoLongerExists()
    {
        var original = Document();
        var line = original.Subtitles[0];
        var parent = line.AnimationRanges[0] with { Utf16Start = 0, Utf16Length = 1 };
        original = original with { Subtitles = [line with { AnimationRanges = [parent, line.AnimationRanges[1]] }] };

        var result = ProjectEditingOperations.ReplaceSubtitleTextRange(original, line.Id, 0, 1, string.Empty);

        Assert.Empty(result.Subtitles[0].AnimationRanges);
        Assert.Empty(result.Layers[0].Tracks);
        ProjectValidator.Validate(result);
    }

    [Fact]
    public void NeutralGeneratedRangesCanMergeWithoutDanglingParentReferences()
    {
        var source = Document();
        var first = new SubtitleLine { Text = "A", End = new(1) };
        var second = source.Subtitles[0] with
        {
            Start = new(1), End = new(5),
            AnimationRanges = source.Subtitles[0].AnimationRanges.Select(range => range with { Offset = default }).ToImmutableArray()
        };
        var document = new ProjectDocument
        {
            Subtitles = [first, second],
            Layers = [new() { SubtitleId = first.Id, End = first.End }, new() { SubtitleId = second.Id, Start = second.Start, End = second.End }]
        };

        var result = ProjectEditingOperations.MergeSubtitles(document, first.Id, second.Id);

        var parent = result.Subtitles[0].AnimationRanges.Single(range => range.GeneratedOrigin is null);
        var child = result.Subtitles[0].AnimationRanges.Single(range => range.GeneratedOrigin is not null);
        Assert.Equal(parent.Id, child.GeneratedOrigin!.ParentRangeId);
        Assert.Equal(2, parent.Utf16Start);
        ProjectValidator.Validate(result);
    }

    private static ProjectDocument Document()
    {
        var parent = new SubtitleAnimationRange(Guid.NewGuid(), 0, 4);
        var child = new SubtitleAnimationRange(Guid.NewGuid(), 2, 2)
        {
            Offset = new(0, -12), GeneratedOrigin = new("pulse", "pairs", parent.Id, "chunk-2")
        };
        var line = new SubtitleLine { Text = "ABCD", End = new(4), AnimationRanges = [parent, child] };
        return new()
        {
            Subtitles = [line],
            Layers = [new() { SubtitleId = line.Id, End = line.End, Tracks =
                [new(new AnimationTrackTarget(AnimationProperty.POSITION, TextRangeId: child.Id), [new(new(0), new ScenePoint(0, 0))])] }]
        };
    }
}
