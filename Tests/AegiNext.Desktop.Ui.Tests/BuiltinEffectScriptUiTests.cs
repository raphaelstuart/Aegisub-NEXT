using AegiNext.Application;
using AegiNext.Core.Editing;
using AegiNext.Core.Effects;
using AegiNext.Core.Projects;
using AegiNext.Desktop.Shortcuts;
using Avalonia.Headless.XUnit;
using CommunityToolkit.Mvvm.Input;

namespace AegiNext.Desktop.Ui.Tests;

public sealed class BuiltinEffectScriptUiTests
{
    [AvaloniaTheory]
    [InlineData("fade-in-out")]
    [InlineData("pop-in")]
    [InlineData("slide-in")]
    [InlineData("fade-in")]
    [InlineData("fade-out")]
    [InlineData("pop-out")]
    [InlineData("slide-out")]
    [InlineData("letter-bounce")]
    [InlineData("letter-pulse")]
    public async Task PresetSelectorAppliesTheEmbeddedScriptWithOneUndo(string id)
    {
        await using var context = new MainWindowTestContext();
        await context.OpenMediaAsync();
        var cueId = context.Session.Editor.AddSubtitle(new(1), new(7, 5), "脚本字幕");
        context.Session.SelectCue(cueId);
        Assert.Equal(cueId, context.Session.SelectedCueId);
        context.Window.GetCommand(WorkbenchCommand.VIEW_EFFECTS).Execute(null);
        var before = context.Session.DocumentSnapshot;
        var template = BuiltinEffectScripts.Get(id);
        var expected = EffectScriptComposer.ComposeTarget(template.Script, before.Layers[0],
            before.Subtitles[0].Style, subtitle: before.Subtitles[0]);

        UiTestActions.SelectBuiltinPreset(context.Window, id);
        UiTestActions.Click(context.Window, "ApplyPresetButton");
        await Assert.IsAssignableFrom<IAsyncRelayCommand>(context.ViewModel.Effects.ApplyPresetCommand).ExecutionTask!;

        var actual = Assert.Single(context.Session.DocumentSnapshot.Layers);
        Assert.Equal(cueId, actual.Id);
        var expectedTracks = expected.Tracks.AsEnumerable();
        var actualTracks = actual.Tracks.AsEnumerable();
        if (template.Script.Version == 2)
        {
            var expectedSubtitle = Assert.IsType<SubtitleLine>(expected.Subtitle);
            var ranges = context.Session.DocumentSnapshot.Subtitles[0].AnimationRanges;
            Assert.Equal(expectedSubtitle.AnimationRanges.Select(range => (range.Utf16Start, range.Utf16Length, range.GeneratedOrigin)),
                ranges.Select(range => (range.Utf16Start, range.Utf16Length, range.GeneratedOrigin)));
            Assert.All(actual.Tracks, track => Assert.Contains(ranges, range => range.Id == track.Target.TextRangeId));
            expectedTracks = expectedTracks.OrderBy(track => expectedSubtitle.AnimationRanges.Single(range => range.Id == track.Target.TextRangeId).Utf16Start)
                .ThenBy(track => track.Property);
            actualTracks = actualTracks.OrderBy(track => ranges.Single(range => range.Id == track.Target.TextRangeId).Utf16Start)
                .ThenBy(track => track.Property);
            var restored = ProjectStore.Deserialize(ProjectStore.Serialize(context.Session.DocumentSnapshot));
            Assert.Equal(ranges.ToArray(), restored.Subtitles[0].AnimationRanges.ToArray());
            Assert.Equal(actual.Tracks.SelectMany(track => track.Keyframes), restored.Layers[0].Tracks.SelectMany(track => track.Keyframes));
        }
        Assert.Equal(expectedTracks.SelectMany(track => track.Keyframes), actualTracks.SelectMany(track => track.Keyframes));
        Assert.Equal(expectedTracks.Select(track => track.Property), actualTracks.Select(track => track.Property));
        Assert.All(actual.Tracks, track => Assert.Contains(
            new TimelineAnimationRowId(TimelineRowScope.TRACK, actual.TrackId, track.Property,
                track.Target.TextRangeId, track.Target.State), context.Session.TimelineViewState.CollapsedAnimationRows));
        Assert.True(context.Session.Editor.Undo());
        Assert.Same(before, context.Session.DocumentSnapshot);
    }

    [AvaloniaFact]
    public async Task ApplyingFadeInThenFadeOutThroughThePresetSelectorKeepsBothEdgesAndOneUndoPerApplication()
    {
        await using var context = new MainWindowTestContext();
        await context.OpenMediaAsync();
        var cueId = context.Session.Editor.AddSubtitle(new(1), new(5), "组合淡入淡出");
        context.Session.SelectCue(cueId);
        context.Window.GetCommand(WorkbenchCommand.VIEW_EFFECTS).Execute(null);

        UiTestActions.SelectBuiltinPreset(context.Window, "fade-in");
        UiTestActions.Click(context.Window, "ApplyPresetButton");
        await Assert.IsAssignableFrom<IAsyncRelayCommand>(context.ViewModel.Effects.ApplyPresetCommand).ExecutionTask!;
        var entrance = context.Session.DocumentSnapshot;
        Assert.Equal(0.75, Assert.Single(SceneEvaluator.Evaluate(entrance, new(23, 20))).Opacity, 12);
        Assert.Equal(1, Assert.Single(SceneEvaluator.Evaluate(entrance, new(97, 20))).Opacity, 12);

        UiTestActions.SelectBuiltinPreset(context.Window, "fade-out");
        UiTestActions.Click(context.Window, "ApplyPresetButton");
        await Assert.IsAssignableFrom<IAsyncRelayCommand>(context.ViewModel.Effects.ApplyPresetCommand).ExecutionTask!;

        var composed = context.Session.DocumentSnapshot;
        Assert.Equal(0.75, Assert.Single(SceneEvaluator.Evaluate(composed, new(23, 20))).Opacity, 12);
        Assert.Equal(1, Assert.Single(SceneEvaluator.Evaluate(composed, new(3))).Opacity, 12);
        Assert.Equal(0.75, Assert.Single(SceneEvaluator.Evaluate(composed, new(97, 20))).Opacity, 12);
        Assert.True(context.Session.Editor.Undo());
        Assert.Same(entrance, context.Session.DocumentSnapshot);
        Assert.Equal(0.75, Assert.Single(SceneEvaluator.Evaluate(context.Session.DocumentSnapshot, new(23, 20))).Opacity, 12);
        Assert.Equal(1, Assert.Single(SceneEvaluator.Evaluate(context.Session.DocumentSnapshot, new(97, 20))).Opacity, 12);
        Assert.True(context.Session.Editor.Redo());
        Assert.Same(composed, context.Session.DocumentSnapshot);
    }
}
