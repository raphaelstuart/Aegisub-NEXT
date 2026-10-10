using AegiNext.Application.Presets;
using AegiNext.Core.Effects;
using AegiNext.Desktop.Controls;
using AegiNext.Desktop.I18n;
using AegiNext.Desktop.Settings;
using AegiNext.Desktop.Settings.Effects;
using AegiNext.Desktop.Styling;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Material.Icons;
using Material.Icons.Avalonia;
using SkiaSharp;

namespace AegiNext.Desktop.Ui.Tests;

public sealed class EffectScriptPageLayoutUiTests
{
    private static readonly string[] toolbarButtons =
    [
        "AddEffectScriptButton", "DeleteEffectScriptButton", "ImportEffectScriptButton", "ExportEffectScriptButton",
        "ValidateEffectScriptButton", "SaveEffectScriptButton"
    ];

    [AvaloniaTheory]
    [InlineData(860, 580, "en-US", false)]
    [InlineData(860, 580, "en-US", true)]
    [InlineData(860, 580, "zh-CN", false)]
    [InlineData(860, 580, "zh-CN", true)]
    [InlineData(1200, 820, "en-US", false)]
    [InlineData(1200, 820, "en-US", true)]
    [InlineData(1200, 820, "zh-CN", false)]
    [InlineData(1200, 820, "zh-CN", true)]
    public void ScriptPageKeepsAllActionsInTheTopToolbarAndUsesItsRemainingHeight(double width, double height,
        string language, bool dark)
    {
        using var environment = new UiTestEnvironment();
        Localization.SetLanguage(language);
        var window = new SettingsWindow(new() { Language = language, Theme = dark ? WorkbenchTheme.DARK : WorkbenchTheme.LIGHT })
        {
            Width = width, Height = height
        };
        var saved = new List<EffectScriptPreset>();
        var deleted = new List<Guid>();
        var imports = 0;
        var exports = 0;
        window.UpsertEffectRequested += (_, args) =>
        {
            saved.Add(args.Preset);
            window.UpdateEffects(saved, args.Preset.Id);
        };
        window.DeleteEffectRequested += (_, args) =>
        {
            deleted.Add(args.Id);
            window.UpdateEffects(saved.Where(preset => preset.Id != args.Id));
        };
        window.ImportEffectRequested += (_, _) => imports++;
        window.ExportEffectRequested += (_, _) => exports++;
        try
        {
            window.Show();
            window.SelectPage(SettingsPage.EFFECTS);
            Flush(window);
            AssertPageGeometry(window);
            var input = UiTestActions.Find<TextBox>(window, "ScriptTextInput");
            Assert.True(input.IsReadOnly);
            Assert.False(UiTestActions.Find<Button>(window, "SaveEffectScriptButton").IsEffectivelyEnabled);
            Assert.False(UiTestActions.Find<Button>(window, "DeleteEffectScriptButton").IsEffectivelyEnabled);
            Capture(window, $"effect-script-page-builtin-{width:0}x{height:0}-{language}-{(dark ? "dark" : "light")}.png");
            ClickToolbar(window, "ValidateEffectScriptButton");
            Assert.Equal(Localization.Get("Settings.ScriptValid"), window.ViewModel.Effects.ValidationStatus);
            Assert.Equal(window.ViewModel.Effects.ValidationStatus,
                ToolTip.GetTip(UiTestActions.Find<Button>(window, "ValidateEffectScriptButton")));
            AssertPageGeometry(window);
            ClickToolbar(window, "AddEffectScriptButton");
            Assert.False(input.IsReadOnly);
            Assert.True(window.ViewModel.Effects.IsDirty);
            Assert.Equal(BuiltinEffectScripts.Templates.Length, window.ViewModel.Effects.Effects.Length);
            Assert.Empty(window.ViewModel.Effects.SelectedIds);
            ClickToolbar(window, "SaveEffectScriptButton");
            Assert.Single(saved);
            Assert.False(window.ViewModel.Effects.IsDirty);
            AssertPageGeometry(window);
            Capture(window, $"effect-script-page-custom-{width:0}x{height:0}-{language}-{(dark ? "dark" : "light")}.png");
            ClickToolbar(window, "ExportEffectScriptButton");
            ClickToolbar(window, "ImportEffectScriptButton");
            Assert.Equal(1, exports);
            Assert.Equal(1, imports);
            ClickToolbar(window, "DeleteEffectScriptButton");
            Assert.Equal(saved[0].Id, Assert.Single(deleted));
            ClickToolbar(window, "AddEffectScriptButton");
            Assert.False(input.IsReadOnly);
            Assert.True(window.ViewModel.Effects.IsDirty);
            AssertPageGeometry(window);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData("Import")]
    [InlineData("ImportFont")]
    [InlineData("IMPORT_ASS")]
    [InlineData("IMPORT_SUBTITLES")]
    public void SharedImportIconUsesLibraryKindsAndVisibleLibraryGeometry(string key)
    {
        var import = Assert.IsType<MaterialIcon>(WorkbenchIcon.Create(key, 24));
        var export = Assert.IsType<MaterialIcon>(WorkbenchIcon.Create("Export", 24));
        Assert.Equal(MaterialIconKind.Import, import.Kind);
        Assert.Equal(MaterialIconKind.Export, export.Kind);
        Assert.Same(MaterialIconDataProvider.Get<Geometry>(MaterialIconKind.Import), import.Drawing.Geometry);
        Assert.Same(MaterialIconDataProvider.Get<Geometry>(MaterialIconKind.Export), export.Drawing.Geometry);
        var icons = new[] { import, export };
        var window = new Window
        {
            Width = 120,
            Height = 80,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { import, export }
            }
        };

        try
        {
            window.Show();
            Flush(window);
            foreach (var icon in icons)
            {
                var geometry = Assert.IsAssignableFrom<Geometry>(icon.Drawing.Geometry);
                Assert.True(geometry.Bounds.Width > 0);
                Assert.True(geometry.Bounds.Height > 0);
                Assert.Equal(new Size(24, 24), icon.Bounds.Size);
                using var target = new RenderTargetBitmap(new(24, 24), new(96, 96));
                target.Render(icon);
                using var stream = new MemoryStream();
                target.Save(stream, PngBitmapEncoderOptions.Default);
                stream.Position = 0;
                using var pixels = SKBitmap.Decode(stream);
                Assert.NotNull(pixels);
                Assert.Contains(pixels.Pixels, pixel => pixel.Alpha > 0);
            }
        }
        finally
        {
            window.Close();
        }
    }

    private static void AssertPageGeometry(SettingsWindow window)
    {
        Flush(window);
        var page = UiTestActions.Find<EffectSettingsView>(window, "EffectsView");
        var toolbar = UiTestActions.Find<WrapPanel>(window, "EffectLibraryToolbar");
        var buttons = toolbar.GetLogicalDescendants().OfType<Button>().ToArray();
        Assert.Equal(toolbarButtons, buttons.Select(button => button.Name));
        var toolbarBounds = BoundsIn(toolbar, window);
        var save = buttons[^1];
        Assert.Contains("accent", save.Classes);
        Assert.Null(window.FindControl<Button>("DuplicateEffectScriptButton"));
        foreach (var button in buttons)
        {
            var bounds = BoundsIn(button, window);
            Assert.True(button.IsEffectivelyVisible);
            Assert.True(toolbarBounds.Contains(bounds));
            Assert.True(new Rect(window.ClientSize).Contains(bounds));
        }

        for (var first = 0; first < buttons.Length; first++)
        {
            for (var second = first + 1; second < buttons.Length; second++)
            {
                Assert.False(BoundsIn(buttons[first], window).Intersects(BoundsIn(buttons[second], window)));
            }
        }

        Assert.DoesNotContain(page.GetVisualDescendants().OfType<Button>(), button => button.Name == "RestoreEffectScriptButton");
        var editor = UiTestActions.Find<EffectScriptEditor>(window, "EffectScriptEditor");
        var editorGrid = Assert.IsType<Grid>(editor.GetVisualParent());
        Assert.Equal(3, editorGrid.RowDefinitions.Count);
        Assert.Equal(2, Grid.GetRow(editor));
        Assert.Equal(window.ViewModel.Effects.EditorStatus, Assert.Single(editorGrid.Children.OfType<TextBlock>()).Text);
        Assert.DoesNotContain(editorGrid.Children, child => Grid.GetRow(child) > Grid.GetRow(editor));
        var editorBounds = BoundsIn(editor, page);
        var list = UiTestActions.Find<ListBox>(window, "EffectScriptList");
        Assert.InRange(Math.Abs(editorBounds.Bottom - BoundsIn(list, page).Bottom), 0, 1);
        Assert.InRange(Math.Abs(editorBounds.Bottom - page.Bounds.Height), 0, 1);
        Assert.True(editorBounds.Height >= 180);
        var input = UiTestActions.Find<TextBox>(window, "ScriptTextInput");
        Assert.InRange(editorBounds.Bottom - BoundsIn(input, page).Bottom, 0, 8);
        var lineNumbers = UiTestActions.Find<ScriptLineNumberMargin>(window, "ScriptLineNumbers");
        Assert.True(lineNumbers.IsEffectivelyVisible);
        Assert.True(lineNumbers.Bounds.Width > 0 && lineNumbers.Bounds.Height >= 180);
        Assert.True(new Rect(window.ClientSize).Contains(BoundsIn(lineNumbers, window)));
    }

    private static void ClickToolbar(SettingsWindow window, string name)
    {
        var button = UiTestActions.Find<Button>(window, name);
        Assert.Contains(UiTestActions.Find<WrapPanel>(window, "EffectLibraryToolbar"), button.GetVisualAncestors());
        Assert.True(button.IsEffectivelyEnabled);
        var point = button.TranslatePoint(new(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        var hit = Assert.IsAssignableFrom<Visual>(window.InputHitTest(point));
        Assert.True(ReferenceEquals(hit, button) || hit.GetVisualAncestors().Contains(button));
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Flush(window);
    }

    private static Rect BoundsIn(Control control, Visual owner)
    {
        return new(control.TranslatePoint(default, owner)!.Value, control.Bounds.Size);
    }

    private static void Capture(SettingsWindow window, string name)
    {
        var directory = Environment.GetEnvironmentVariable("AEGINEXT_UI_CAPTURE_DIRECTORY");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        Assert.True(Path.IsPathFullyQualified(directory));
        Directory.CreateDirectory(directory);
        Flush(window);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(directory, name), PngBitmapEncoderOptions.Default);
    }

    private static void Flush(Window window)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }
}
