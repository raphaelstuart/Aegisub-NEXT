using AegiNext.Core.Effects;
using AegiNext.Core.Projects;

namespace AegiNext.Application.Tests;

public sealed class EffectScriptScopeEditingTests
{
    [Fact]
    public void BatchBakesEachSubtitlesGroupsAndTracksAsOneUndoTransaction()
    {
        var initial = new ProjectEditor();
        var first = initial.AddSubtitle(new(1), new(3), "AB");
        var second = initial.AddSubtitle(new(4), new(8), "XYZ");
        var untouched = initial.AddSubtitle(new(9), new(10), "Keep");
        initial.UpdateLayer(first, layer => layer with { AnimationOffset = new(2) });
        initial.UpdateSubtitle(first, line => line with { InlineSpans = [new(1, 1, new() { FontSize = 50 })] });
        var original = initial.Snapshot;
        var editor = new ProjectEditor(original);
        var changes = 0;
        editor.Changed += (_, _) => changes++;

        editor.ApplyEffectScript([first, second], Script());

        var applied = editor.Snapshot;
        Assert.Equal(2, applied.Subtitles[0].AnimationRanges.Length);
        Assert.Equal(3, applied.Subtitles[1].AnimationRanges.Length);
        Assert.All(applied.Subtitles.Take(2).SelectMany(line => line.AnimationRanges), range =>
        {
            Assert.Equal("scope-edit", range.GeneratedOrigin!.EffectId);
            Assert.Equal("pulse", range.GeneratedOrigin.ScopeName);
            Assert.Equal(new ScenePoint(1, 1), range.Scale);
        });
        Assert.Equal(2, applied.Layers[0].Tracks.Length);
        Assert.Equal(new AegiNext.Core.Timing.MediaTime(2), applied.Layers[0].Tracks[0].Keyframes[0].Time);
        Assert.Same(original.Subtitles[0].Style, applied.Subtitles[0].Style);
        Assert.Equal(original.Subtitles[0].InlineSpans, applied.Subtitles[0].InlineSpans);
        Assert.Same(original.Layers.Single(layer => layer.Id == untouched), applied.Layers.Single(layer => layer.Id == untouched));
        Assert.Equal(1, changes);
        ProjectValidator.Validate(applied);
        var restored = ProjectStore.Deserialize(ProjectStore.Serialize(applied));
        Assert.Equal(applied.Subtitles.SelectMany(line => line.AnimationRanges), restored.Subtitles.SelectMany(line => line.AnimationRanges));
        Assert.True(editor.Undo());
        Assert.Same(original, editor.Snapshot);
        Assert.False(editor.CanUndo);
        Assert.True(editor.Redo());
        Assert.Same(applied, editor.Snapshot);
    }

    [Fact]
    public void ReapplicationReusesMatchingIdsAndExclusivelyReplacesGeneratedResults()
    {
        var initial = new ProjectEditor();
        var id = initial.AddSubtitle(new(0), new(2), "ABCD");
        var manual = new SubtitleAnimationRange(Guid.NewGuid(), 0, 4) { Rotation = 15 };
        var other = new SubtitleAnimationRange(Guid.NewGuid(), 0, 1)
        {
            GeneratedOrigin = new("another-effect", "pulse", null, "grapheme")
        };
        initial.SetSubtitleAnimationRange(id, manual);
        initial.SetSubtitleAnimationRange(id, other);
        initial.ApplyEffectScript(id, Script());
        var generated = initial.Snapshot.Subtitles[0].AnimationRanges.Where(range => range.GeneratedOrigin?.EffectId == "scope-edit").ToArray();
        initial.SetSubtitleAnimationRange(id, generated[1] with { Scale = new(2, 2), Offset = new(5, 0), Pivot = SubtitleAnimationPivot.SUBTITLE_ANCHOR });
        initial.SetKeyframe(id, new AnimationTrackTarget(AnimationProperty.ROTATION, TextRangeId: generated[1].Id), new(new(0), 45));
        initial.MoveSubtitleAnimationRange(id, generated[1].Id, 0);
        var original = initial.Snapshot;
        var editor = new ProjectEditor(original);

        editor.ApplyEffectScript(id, Script());

        var result = editor.Snapshot;
        var regenerated = result.Subtitles[0].AnimationRanges.Where(range => range.GeneratedOrigin?.EffectId == "scope-edit").ToArray();
        Assert.Equal(generated.Select(range => range.Id).Order(), regenerated.Select(range => range.Id).Order());
        Assert.Equal(generated[1].Id, result.Subtitles[0].AnimationRanges[0].Id);
        Assert.All(regenerated, range =>
        {
            Assert.Equal(new ScenePoint(1, 1), range.Scale);
            Assert.Equal(default, range.Offset);
            Assert.Equal(SubtitleAnimationPivot.RANGE_CENTER, range.Pivot);
        });
        Assert.Contains(manual, result.Subtitles[0].AnimationRanges);
        Assert.Contains(other, result.Subtitles[0].AnimationRanges);
        Assert.DoesNotContain(result.Layers[0].Tracks, track => track.Target.TextRangeId == generated[1].Id && track.Property == AnimationProperty.ROTATION);

        editor.ApplyEffectScript(id, Script("chunk(2)"));
        Assert.Equal(4, editor.Snapshot.Subtitles[0].AnimationRanges.Length);
        Assert.DoesNotContain(editor.Snapshot.Subtitles[0].AnimationRanges, range => generated.Any(previous => previous.Id == range.Id));
        Assert.Equal(2, editor.Snapshot.Layers[0].Tracks.Length);
        ProjectValidator.Validate(editor.Snapshot);
    }

    [Fact]
    public void CurrentRangeGroupsRemainInsideTheirParentAndParentDeletionCascades()
    {
        var initial = new ProjectEditor();
        var id = initial.AddSubtitle(new(0), new(2), "ABCDE");
        var parent = new SubtitleAnimationRange(Guid.NewGuid(), 1, 3);
        initial.SetSubtitleAnimationRange(id, parent);
        var original = initial.Snapshot;
        var editor = new ProjectEditor(original);

        editor.ApplyEffectScript(id, Script(), new(AnimationProperty.FILL, TextRangeId: parent.Id));

        var groups = editor.Snapshot.Subtitles[0].AnimationRanges.Where(range => range.Id != parent.Id).ToArray();
        Assert.Equal(3, groups.Length);
        Assert.All(groups, range =>
        {
            Assert.Equal(parent.Id, range.GeneratedOrigin!.ParentRangeId);
            Assert.InRange(range.Utf16Start, 1, 3);
        });
        editor.RemoveSubtitleAnimationRange(id, parent.Id);
        Assert.Empty(editor.Snapshot.Subtitles[0].AnimationRanges);
        Assert.Empty(editor.Snapshot.Layers[0].Tracks);
        ProjectValidator.Validate(editor.Snapshot);
    }

    [Fact]
    public void GroupUnitReusesTheCurrentManualRangeWhileSubtitleTargetIgnoresIt()
    {
        var initial = new ProjectEditor();
        var id = initial.AddSubtitle(new(0), new(2), "AB");
        var parent = new SubtitleAnimationRange(Guid.NewGuid(), 0, 1) { Scale = new(2, 3) };
        initial.SetSubtitleAnimationRange(id, parent);
        var editor = new ProjectEditor(initial.Snapshot);
        var context = new AnimationTrackTarget(AnimationProperty.FILL, TextRangeId: parent.Id);

        editor.ApplyEffectScript(id, Script("group"), context);

        Assert.Equal(parent, Assert.Single(editor.Snapshot.Subtitles[0].AnimationRanges));
        Assert.Equal(parent.Id, Assert.Single(editor.Snapshot.Layers[0].Tracks).Target.TextRangeId);
        editor.ApplyEffectScript(id, Script("group", "subtitle"), context);
        Assert.Contains(editor.Snapshot.Layers[0].Tracks, track => track.Target.TextRangeId is null && track.Property == AnimationProperty.SCALE);
    }

    [Theory]
    [InlineData("range")]
    [InlineData("mixed-font")]
    public void AnyBatchFailureLeavesRangesTracksHistoryAndNotificationsUntouched(string failure)
    {
        var initial = new ProjectEditor();
        var first = initial.AddSubtitle(new(0), new(2), "AB");
        var second = initial.AddSubtitle(new(3), new(5), "C");
        var script = Script(target: "range(1, 2)");
        if (failure == "mixed-font")
        {
            initial.UpdateSubtitle(second, line => line with { Text = "CD" });
            initial.UpdateSubtitle(second, line => line with { InlineSpans = [new(1, 1, new() { FontSize = 50 })] });
            script = EffectScriptParser.Parse(Source("chunk(2)").Replace("scale", "font-size", StringComparison.Ordinal)
                .Replace("factor(1.25, 1.25)", "factor(1.25)", StringComparison.Ordinal));
        }
        var original = initial.Snapshot;
        var editor = new ProjectEditor(original);
        editor.UpdateLayer(first, layer => layer with { Name = "Redo fixture" });
        Assert.True(editor.Undo());
        var changes = 0;
        editor.Changed += (_, _) => changes++;

        Assert.Throws<EffectScriptException>(() => editor.ApplyEffectScript([first, second], script));

        Assert.Same(original, editor.Snapshot);
        Assert.False(editor.CanUndo);
        Assert.True(editor.CanRedo);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void EditingTextRemapsExistingBakedGroupsAndReapplyingRegroupsThem()
    {
        var initial = new ProjectEditor();
        var id = initial.AddSubtitle(new(0), new(2), "ABCD");
        initial.ApplyEffectScript(id, Script("chunk(2)"));
        var identities = initial.Snapshot.Subtitles[0].AnimationRanges.Select(range => range.Id).ToArray();
        var editor = new ProjectEditor(initial.Snapshot);

        editor.ReplaceSubtitleTextRange(id, 1, 0, "X");

        Assert.Equal(identities, editor.Snapshot.Subtitles[0].AnimationRanges.Select(range => range.Id));
        Assert.Equal(2, editor.Snapshot.Subtitles[0].AnimationRanges.Length);
        editor.ApplyEffectScript(id, Script("chunk(2)"));
        Assert.Equal(3, editor.Snapshot.Subtitles[0].AnimationRanges.Length);
        Assert.Equal(3, editor.Snapshot.Layers[0].Tracks.Length);
        ProjectValidator.Validate(editor.Snapshot);
    }

    [Fact]
    public void EmptyGroupedTextDoesNotCreateAnUndoEntry()
    {
        var initial = new ProjectEditor();
        var id = initial.AddSubtitle(new(0), new(2), "   ");
        var original = initial.Snapshot;
        var editor = new ProjectEditor(original);

        editor.ApplyEffectScript(id, Script());

        Assert.Same(original, editor.Snapshot);
        Assert.False(editor.CanUndo);
    }

    private static EffectScript Script(string unit = "grapheme", string target = "current") => EffectScriptParser.Parse(Source(unit, target));

    private static string Source(string unit = "grapheme", string target = "current") => $"""
        effect "scope-edit" version 2
        short-clip compress
        scope pulse {target}
            unit {unit}
            stagger 60ms
            segment pop fixed 150ms pingpong
                at 0 scale base ease-in-out
                at 1 scale factor(1.25, 1.25)
            end
            segment rest flex 1
            end
        end
        """;
}
