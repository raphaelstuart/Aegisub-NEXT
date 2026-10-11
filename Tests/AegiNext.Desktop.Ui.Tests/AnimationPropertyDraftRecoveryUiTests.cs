using AegiNext.Core.Projects;
using AegiNext.Core.Timing;
using AegiNext.Desktop.Controls;
using AegiNext.Desktop.Panels.Effects;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AegiNext.Desktop.Ui.Tests;

/// <summary>验证属性草稿冲突后的键盘恢复可以开启新的编辑事务。</summary>
public sealed class AnimationPropertyDraftRecoveryUiTests
{
    /// <summary>详情独立提交后，Esc结束失效草稿，重新输入可提交并单独撤销。</summary>
    [AvaloniaFact]
    public async Task EscapeAfterDetailsCommitAllowsANewPropertyEditWithItsOwnUndo()
    {
        await using var context = new MainWindowTestContext();
        var session = context.Session;
        var subtitleId = session.Editor.AddSubtitle(MediaTime.Zero, new(5), "Subtitle");
        session.SelectCue(subtitleId);
        context.ViewModel.Effects.TypographyExpanded = true;
        var row = Assert.Single(context.ViewModel.Effects.TypographyRows,
            row => row.Target.Property == AnimationProperty.FONT_SIZE);
        using var panel = new EffectsPanelView(context.ViewModel.Effects, session);
        var host = new Window { Width = 650, Height = 920, Content = panel };
        host.Show();
        try
        {
            host.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            host.UpdateLayout();
            var view = Assert.Single(host.GetVisualDescendants().OfType<EffectPropertyRowView>(),
                view => ReferenceEquals(view.DataContext, row));
            var input = Assert.Single(view.GetVisualDescendants().OfType<NumericDraftInput>(), input => input.IsVisible);
            input.BringIntoView();
            host.UpdateLayout();
            var text = Assert.Single(input.GetVisualDescendants().OfType<TextBox>());
            Assert.True(text.Focus());
            UiTestActions.Press(host, Key.A, RawInputModifiers.Control);
            host.KeyTextInput("unfinished");
            UiTestActions.Press(host, Key.Enter);
            Assert.Equal("unfinished", row.X.RawText);
            Assert.True(row.HasDraft);

            session.Details.EditText(0, 0, "Edited ");
            Assert.True(session.Details.TryCommit());
            var afterDetails = session.DocumentSnapshot;
            var originalFontSize = session.SelectedCue!.Style.FontSize;
            Assert.Equal("Edited Subtitle", session.SelectedCue.Text);
            Assert.False(session.TryCommitDrafts(false));

            Assert.True(text.Focus());
            UiTestActions.Press(host, Key.Escape);
            Assert.False(row.HasDraft);
            Assert.Same(afterDetails, session.DocumentSnapshot);
            UiTestActions.Press(host, Key.A, RawInputModifiers.Control);
            host.KeyTextInput("72");
            Assert.Equal("72", row.X.RawText);
            UiTestActions.Press(host, Key.Enter);

            Assert.False(row.HasDraft, session.LastError?.Message);
            Assert.Equal(72, session.SelectedCue.Style.FontSize);
            Assert.Equal("Edited Subtitle", session.SelectedCue.Text);
            Assert.Equal("Commit workspace drafts", session.Editor.UndoLabel);
            Assert.True(session.Editor.Undo());
            Assert.Same(afterDetails, session.DocumentSnapshot);
            Assert.Equal(originalFontSize, session.SelectedCue!.Style.FontSize);
            Assert.Equal("Edited Subtitle", session.SelectedCue.Text);
        }
        finally
        {
            row.Restore(row.XFieldKey);
            host.Close();
        }
    }
}
