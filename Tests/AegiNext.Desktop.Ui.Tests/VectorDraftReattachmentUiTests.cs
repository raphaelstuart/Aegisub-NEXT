using AegiNext.Core.Projects;
using AegiNext.Desktop.Controls;
using AegiNext.Desktop.Panels.Masks;
using AegiNext.Desktop.Workspace;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AegiNext.Desktop.Ui.Tests;

/// <summary>验证向量输入重新挂载后的原始草稿、字段身份和标题拖拽行为。</summary>
public sealed class VectorDraftReattachmentUiTests
{
    /// <summary>跨窗口保留无效分量草稿，标题拖拽只修改另一分量。</summary>
    [AvaloniaFact]
    public void DataTemplateReattachmentPreservesAuthoritativeRawAndTitleDragChangesOnlyItsComponent()
    {
        var target = new AnimationTrackTarget(AnimationProperty.MASK_RECTANGLE_TOP_LEFT);
        var x = new MaskNumericField("X", "Workbench.MASK_RECTANGLE_TOP_LEFT", target, 0, -1000, 1000);
        var y = new MaskNumericField("Y", "Workbench.MASK_RECTANGLE_TOP_LEFT", target, 1, -1000, 1000);
        x.Draft.Load(10);
        y.Draft.Load(20);
        var model = new MaskVectorField(x, y);
        var content = new ContentControl
        {
            Content = model,
            ContentTemplate = new FuncDataTemplate<MaskVectorField>((field, _) => new VectorDraftInput
            {
                XFieldKey = "RectangleCorner_X",
                YFieldKey = "RectangleCorner_Y",
                XInputName = "X",
                YInputName = "Y",
                [!VectorDraftInput.XProperty] = new Binding("X.Draft.Value") { Source = field, Mode = BindingMode.TwoWay },
                [!VectorDraftInput.YProperty] = new Binding("Y.Draft.Value") { Source = field, Mode = BindingMode.TwoWay },
                [!VectorDraftInput.XTextProperty] = new Binding("X.Draft.RawText") { Source = field, Mode = BindingMode.TwoWay },
                [!VectorDraftInput.YTextProperty] = new Binding("Y.Draft.RawText") { Source = field, Mode = BindingMode.TwoWay }
            })
        };
        var first = new Window { Width = 500, Height = 180, Content = content };
        var second = new Window { Width = 360, Height = 180 };
        first.Show();
        try
        {
            first.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var vector = Assert.Single(content.GetVisualDescendants().OfType<VectorDraftInput>());
            var xInput = Assert.Single(vector.GetVisualDescendants().OfType<NumericDraftInput>(), input => input.Name == "X");
            var yInput = Assert.Single(vector.GetVisualDescendants().OfType<NumericDraftInput>(), input => input.Name == "Y");
            Assert.Equal("10", xInput.RawText);
            Assert.Equal("20", yInput.RawText);
            var text = Assert.Single(xInput.GetVisualDescendants().OfType<TextBox>());
            Assert.True(text.Focus());
            UiTestActions.Press(first, Key.A, RawInputModifiers.Control);
            first.KeyTextInput("invalid X");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("invalid X", x.Draft.RawText);
            first.Content = null;
            second.Content = content;
            second.Show();
            second.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("invalid X", x.Draft.RawText);
            Assert.Equal("20", y.Draft.RawText);
            vector = Assert.Single(content.GetVisualDescendants().OfType<VectorDraftInput>());
            xInput = Assert.Single(vector.GetVisualDescendants().OfType<NumericDraftInput>(), input => input.Name == "X");
            yInput = Assert.Single(vector.GetVisualDescendants().OfType<NumericDraftInput>(), input => input.Name == "Y");
            Assert.Equal("invalid X", vector.XText);
            Assert.Equal("20", vector.YText);
            Assert.Equal("invalid X", xInput.RawText);
            Assert.Equal("20", yInput.RawText);
            Assert.Equal("invalid X", Assert.Single(xInput.GetVisualDescendants().OfType<TextBox>()).Text);
            Assert.Equal("RectangleCorner_X", vector.XFieldKey);
            Assert.Equal("RectangleCorner_Y", vector.YFieldKey);
            Assert.False(yInput.ShowButtonSpinner);
            Assert.True(vector.FocusField("RectangleCorner_Y"));
            var label = Assert.Single(vector.GetVisualDescendants().OfType<NumericDragLabel>(), candidate => ReferenceEquals(candidate.Input, yInput));
            var point = label.TranslatePoint(new Point(label.Bounds.Width / 2, label.Bounds.Height / 2), second)!.Value;
            second.MouseDown(point, MouseButton.Left);
            second.MouseMove(new(point.X + 1, point.Y));
            second.MouseUp(new(point.X + 1, point.Y), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("21", y.Draft.RawText);
            Assert.Equal("invalid X", x.Draft.RawText);
        }
        finally
        {
            first.Close();
            second.Close();
        }
    }
}
