using AegiNext.Application.Presets;
using AegiNext.Core.Effects;
using AegiNext.Core.Projects;
using AegiNext.Desktop.Controls;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;

namespace AegiNext.Desktop.Ui.Tests;

public sealed class BatchEffectPresetUiTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimelineMultiSelectionAppliesPresetAcrossTracksWithOneUndo(bool personalPreset)
    {
        await using var context = new MainWindowTestContext();
        await context.Session.EffectScripts.Completion;
        var otherTrack = new ProjectTrack { Name = "Other track" };
        var first = new SubtitleLine { Start = new(1), End = new(3), Text = "First" };
        var second = new SubtitleLine { Start = new(4), End = new(8), Text = "Second" };
        var untouched = new SubtitleLine { Start = new(9), End = new(10), Text = "Untouched" };
        var document = new ProjectDocument
        {
            Tracks = [ProjectTrack.Default, otherTrack],
            Subtitles = [first, second, untouched],
            Layers = [Layer(first), Layer(second) with { TrackId = otherTrack.Id, Opacity = 0.5, AnimationOffset = new(1) }, Layer(untouched)]
        };
        context.Session.Editor.Reset(document);
        var timeline = PrepareTimeline(context);
        SelectClip(context, timeline, first.Id);
        SelectClip(context, timeline, second.Id, OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control);
        var selected = new[] { first.Id, second.Id }.Order().ToArray();
        Assert.Equal(selected, context.ViewModel.Timeline.SelectedLayerIds.Order());
        Assert.Equal(second.Id, context.Session.SelectedLayerId);
        if (personalPreset)
        {
            var source = BuiltinEffectScripts.Get("fade-in-out").Source.Replace("fade-in-out", "batch-personal-fade", StringComparison.Ordinal);
            var preset = new EffectScriptPreset(Guid.NewGuid(), "Batch personal fade", source);
            await context.Session.EffectScripts.UpsertAsync(preset);
            UiTestActions.Find<ComboBox>(context.Window, "PresetCombo").SelectedIndex = Array.IndexOf(context.ViewModel.Effects.Presets, preset.Name);
        }
        else
        {
            UiTestActions.SelectBuiltinPreset(context.Window, "fade-in-out");
        }
        var changes = 0;
        context.Session.Editor.Changed += (_, _) => changes++;

        UiTestActions.Click(context.Window, "ApplyPresetButton");
        await Assert.IsAssignableFrom<IAsyncRelayCommand>(context.ViewModel.Effects.ApplyPresetCommand).ExecutionTask!;

        var applied = context.Session.DocumentSnapshot;
        foreach (var id in selected)
        {
            var layer = applied.Layers.Single(value => value.Id == id);
            var track = Assert.Single(layer.Tracks);
            Assert.Equal(AnimationProperty.OPACITY, track.Property);
            Assert.Equal(layer.AnimationOffset, track.Keyframes[0].Time);
            Assert.Equal(layer.End - layer.Start + layer.AnimationOffset, track.Keyframes[^1].Time);
            Assert.Equal(layer.Opacity, track.Keyframes.Max(frame => frame.Value.Scalar));
        }
        Assert.Same(document.Layers[2], applied.Layers[2]);
        Assert.Equal(1, changes);
        Assert.Equal(second.Id, context.Session.SelectedLayerId);
        Assert.Equal(selected, context.ViewModel.Timeline.SelectedLayerIds.Order());
        Assert.Equal(selected, context.ViewModel.Effects.SelectedIds.Order());
        Assert.True(context.Session.Editor.Undo());
        Assert.Same(document, context.Session.DocumentSnapshot);
        Assert.False(context.Session.Editor.CanUndo);
        Assert.Equal(selected, context.ViewModel.Timeline.SelectedLayerIds.Order());
        Assert.True(context.Session.Editor.Redo());
        Assert.Same(applied, context.Session.DocumentSnapshot);
    }

    [AvaloniaFact]
    public async Task SubtitleRowMultiSelectionUsesTheSameApplyButton()
    {
        await using var context = new MainWindowTestContext();
        await context.Session.EffectScripts.Completion;
        var first = new SubtitleLine { Start = new(0), End = new(2), Text = "First" };
        var second = new SubtitleLine { Start = new(3), End = new(7), Text = "Second" };
        var document = new ProjectDocument { Subtitles = [first, second], Layers = [Layer(first), Layer(second)] };
        context.Session.Editor.Reset(document);
        Assert.True(context.Session.SelectSubtitleRows(first.Id, [first.Id, second.Id]));
        UiTestActions.SelectBuiltinPreset(context.Window, "pop-in");

        UiTestActions.Click(context.Window, "ApplyPresetButton");
        await Assert.IsAssignableFrom<IAsyncRelayCommand>(context.ViewModel.Effects.ApplyPresetCommand).ExecutionTask!;

        Assert.All(context.Session.DocumentSnapshot.Layers, layer => Assert.Contains(layer.Tracks, track => track.Property == AnimationProperty.SCALE));
        Assert.Equal(first.Id, context.Session.SelectedLayerId);
        Assert.Equal(new[] { first.Id, second.Id }, context.Session.SelectedSubtitleIds);
        Assert.True(context.Session.Editor.Undo());
        Assert.Same(document, context.Session.DocumentSnapshot);
        Assert.False(context.Session.Editor.CanUndo);
    }

    [AvaloniaFact]
    public async Task RejectedClipLeavesTheWholeSelectionAndProjectUntouched()
    {
        await using var context = new MainWindowTestContext();
        await context.Session.EffectScripts.Completion;
        var first = new SubtitleLine { Start = new(0), End = new(2), Text = "Long" };
        var second = new SubtitleLine { Start = new(3), End = new(31, 10), Text = "Short" };
        var document = new ProjectDocument { Subtitles = [first, second], Layers = [Layer(first), Layer(second)] };
        context.Session.Editor.Reset(document);
        context.Session.SelectLayer(first.Id, [first.Id, second.Id]);
        var source = BuiltinEffectScripts.Get("fade-in-out").Source.Replace("fade-in-out", "batch-reject-short", StringComparison.Ordinal)
            .Replace("short-clip compress", "short-clip reject", StringComparison.Ordinal);
        var preset = new EffectScriptPreset(Guid.NewGuid(), "Reject short clips", source);
        await context.Session.EffectScripts.UpsertAsync(preset);
        UiTestActions.Find<ComboBox>(context.Window, "PresetCombo").SelectedIndex = Array.IndexOf(context.ViewModel.Effects.Presets, preset.Name);

        UiTestActions.Click(context.Window, "ApplyPresetButton");
        await Assert.IsAssignableFrom<IAsyncRelayCommand>(context.ViewModel.Effects.ApplyPresetCommand).ExecutionTask!;

        Assert.IsType<EffectScriptException>(context.Session.LastError);
        Assert.Same(document, context.Session.DocumentSnapshot);
        Assert.False(context.Session.Editor.CanUndo);
        Assert.False(context.Session.Editor.HasUnsavedChanges);
        Assert.Equal(first.Id, context.Session.SelectedLayerId);
        Assert.Equal(new[] { first.Id, second.Id }.Order(), context.ViewModel.Effects.SelectedIds.Order());
    }

    private static ProjectLayer Layer(SubtitleLine cue) => new()
    {
        Id = cue.Id, Kind = LayerKind.SUBTITLE, SubtitleId = cue.Id, Start = cue.Start, End = cue.End
    };

    private static SubtitleTimelineControl PrepareTimeline(MainWindowTestContext context)
    {
        var timeline = UiTestActions.Find<SubtitleTimelineControl>(context.Window, "Timeline");
        context.ViewModel.Timeline.IsSnapEnabled = false;
        context.ViewModel.Timeline.IsStepEnabled = false;
        timeline.PixelsPerSecond = 60;
        timeline.ViewStart = 0;
        Flush(context.Window);
        return timeline;
    }

    private static void SelectClip(MainWindowTestContext context, SubtitleTimelineControl timeline, Guid id,
        RawInputModifiers modifiers = RawInputModifiers.None)
    {
        var point = timeline.TranslatePoint(timeline.GetClipRectangle(id)!.Value.Center, context.Window)!.Value;
        Assert.Same(timeline, context.Window.InputHitTest(point));
        context.Window.MouseDown(point, MouseButton.Left, modifiers);
        context.Window.MouseUp(point, MouseButton.Left, modifiers);
        Flush(context.Window);
    }

    private static void Flush(Window window)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }
}
