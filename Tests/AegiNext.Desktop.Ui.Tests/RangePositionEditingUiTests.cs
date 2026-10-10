using AegiNext.Core.Projects;
using AegiNext.Desktop.Controls;
using AegiNext.Desktop.I18n;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;

namespace AegiNext.Desktop.Ui.Tests;

/// <summary>验证实际特效面板显示范围本地位移，并复用既有输入和动画操作。</summary>
public sealed class RangePositionEditingUiTests
{
    /// <summary>本地位移行正常显示，真实键盘的 Esc 仅恢复当前分量。</summary>
    [AvaloniaFact]
    public async Task LocalPositionRowEditsAndRestoresRawComponentsAndHidesForAppearanceStates()
    {
        await using var context = new MainWindowTestContext();
        await context.OpenMediaAsync();
        await PrepareAsync(context);
        var effects = context.ViewModel.Effects;
        var window = context.Window;
        var source = context.Session.DocumentSnapshot;
        var range = source.Subtitles[0].AnimationRanges[0];
        var row = UiTestActions.Find<Grid>(window, "PositionRow");
        var vector = UiTestActions.Find<VectorDraftInput>(window, "PositionInput");
        var label = UiTestActions.Find<TextBlock>(window, "PositionLabel");
        var x = UiTestActions.Find<NumericDraftInput>(window, "PositionXInput");
        var text = Assert.Single(x.GetVisualDescendants().OfType<TextBox>());
        Assert.True(row.IsEffectivelyVisible);
        Assert.True(vector.IsEffectivelyEnabled);
        Assert.Equal(Localization.Get("Workbench.LocalPositionVector"), label.Text);
        Assert.Equal(Localization.Get("Workbench.LocalPositionHint"), ToolTip.GetTip(row));
        Assert.Equal("5", x.RawText);
        Assert.False(UiTestActions.Find<Button>(window, "ResetPositionButton").IsEffectivelyVisible);
        try
        {
            Assert.True(text.Focus());
            UiTestActions.Press(window, Key.A, RawInputModifiers.Control);
            window.KeyTextInput("7e-");
            effects.PositionYText = "9";
            Assert.False(context.Session.TryCommitDrafts(false));
            Assert.Same(source, context.Session.DocumentSnapshot);
            Assert.Equal("7e-", x.RawText);
            UiTestActions.Press(window, Key.Escape);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("5", x.RawText);
            Assert.Equal("9", effects.PositionYText);
            Assert.True(context.Session.TryCommitDrafts(false));
            Assert.Equal(new ScenePoint(5, 9), context.Session.SelectedCue!.AnimationRanges[0].Offset);
            Assert.True(context.Session.Editor.Undo());
            Assert.Same(source, context.Session.DocumentSnapshot);

            effects.SelectedState = effects.States.Single(state => state.State == SubtitleAnimationState.INACTIVE);
            Dispatcher.UIThread.RunJobs();
            Assert.False(row.IsEffectivelyVisible);
            effects.SelectedState = effects.States.Single(state => state.State == SubtitleAnimationState.NORMAL);
            effects.SelectedScope = effects.Scopes.Single(scope => scope.Id is null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(row.IsEffectivelyVisible);
            Assert.Equal(Localization.Get("Workbench.PositionVector"), label.Text);
            Assert.Null(ToolTip.GetTip(row));
            Assert.True(UiTestActions.Find<Button>(window, "ResetPositionButton").IsEffectivelyVisible);
            Assert.Equal(range.Offset, context.Session.SelectedCue!.AnimationRanges[0].Offset);
        }
        finally
        {
            effects.RestoreField("PositionXInput");
            effects.RestoreField("PositionYInput");
            context.Session.TryCommitDrafts(false);
        }
    }

    /// <summary>位置行的动画开关和重置操作仅作用于完整范围目标。</summary>
    [AvaloniaFact]
    public async Task RangePositionButtonsToggleAndResetOnlyTheScopedTrack()
    {
        await using var context = new MainWindowTestContext();
        await context.OpenMediaAsync();
        await PrepareAsync(context);
        var session = context.Session;
        var range = session.SelectedCue!.AnimationRanges[0];
        var target = new AnimationTrackTarget(AnimationProperty.POSITION, TextRangeId: range.Id);
        session.Editor.SetKeyframe(session.SelectedLayer!.Id, AnimationProperty.POSITION, new(new(0), new ScenePoint(100, 200)));
        session.Editor.SetKeyframe(session.SelectedLayer.Id, target, new(new(0), new ScenePoint(20, 30)) { Reverse = true });
        var source = session.DocumentSnapshot;
        var whole = source.Layers[0].Tracks.Single(track => track.Target.TextRangeId is null);
        var toggle = UiTestActions.Find<ToggleButton>(context.Window, "PositionAnimationToggle");
        var key = UiTestActions.Find<Button>(context.Window, "PositionKeyframeButton");
        var reset = UiTestActions.Find<Button>(context.Window, "PositionResetAnimationButton");
        Assert.True(toggle.IsChecked);
        Assert.Equal(AnimationProperty.POSITION, key.CommandParameter);
        Assert.True(reset.IsEffectivelyEnabled);
        var toggleCommand = Assert.IsAssignableFrom<IAsyncRelayCommand>(toggle.Command);

        UiTestActions.Click(context.Window, "PositionAnimationToggle");
        await toggleCommand.ExecutionTask!;

        Assert.Same(whole, Assert.Single(session.SelectedLayer!.Tracks));
        Assert.True(session.Editor.Undo());
        Assert.Same(source, session.DocumentSnapshot);
        var resetCommand = Assert.IsAssignableFrom<IAsyncRelayCommand>(reset.Command);
        UiTestActions.Click(context.Window, "PositionResetAnimationButton");
        await resetCommand.ExecutionTask!;
        var frame = Assert.Single(session.SelectedLayer.Tracks.Single(track => track.Target == target).Keyframes);
        Assert.Equal(range.Offset, frame.Value.Vector);
        Assert.True(frame.Reverse);
        Assert.Same(whole, session.SelectedLayer.Tracks.Single(track => track.Target == whole.Target));
        Assert.True(session.Editor.Undo());
        Assert.Same(source, session.DocumentSnapshot);
    }

    private static async Task PrepareAsync(MainWindowTestContext context)
    {
        await UiTestActions.CreateSubtitleAsync(context, text: "ABCD");
        var session = context.Session;
        var range = new SubtitleAnimationRange(Guid.NewGuid(), 1, 2) { Offset = new(5, -7) };
        session.Editor.SetSubtitleAnimationRange(session.SelectedCue!.Id, range);
        session.Editor.UpdateLayer(session.SelectedLayer!.Id, layer => layer with { Transform = layer.Transform with { Position = new(100, 200) } });
        context.ViewModel.Effects.SelectedScope = context.ViewModel.Effects.Scopes.Single(scope => scope.Id == range.Id);
        context.ViewModel.Effects.TransformExpanded = true;
        Dispatcher.UIThread.RunJobs();
        context.Window.UpdateLayout();
    }
}
