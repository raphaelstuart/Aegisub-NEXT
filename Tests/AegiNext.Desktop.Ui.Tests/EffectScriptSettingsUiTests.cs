using System.Collections.Immutable;
using AegiNext.Application.Presets;
using AegiNext.Core.Effects;
using AegiNext.Desktop.Controls;
using AegiNext.Desktop.Settings;
using AegiNext.Desktop.Settings.Effects;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SkiaSharp;

namespace AegiNext.Desktop.Ui.Tests;

public sealed class EffectScriptSettingsUiTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void AutomaticCompletionHasAnOpaqueThemeSurfaceAndVisibleBorder(bool dark)
    {
        using var environment = new UiTestEnvironment();
        var window = new SettingsWindow(new WorkbenchPreferences())
        {
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light
        };
        try
        {
            window.Show();
            window.SelectPage(SettingsPage.EFFECTS);
            UiTestActions.Click(window, "AddEffectScriptButton");
            var input = UiTestActions.Find<TextBox>(window, "ScriptTextInput");
            Assert.True(input.Focus());
            input.SelectAll();
            window.KeyTextInput("at 0 fill ");
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var popup = UiTestActions.Find<Popup>(window, "ScriptCompletionPopup");
            var frame = UiTestActions.Find<Border>(window, "ScriptCompletionFrame");
            Assert.True(popup.IsOpen);
            var surface = Color.Parse(dark ? "#1C222D" : "#FFFFFF");
            var border = Color.Parse(dark ? "#303949" : "#DDE2EA");
            Assert.Equal(surface, Assert.IsAssignableFrom<ISolidColorBrush>(frame.Background).Color);
            Assert.Equal(border, Assert.IsAssignableFrom<ISolidColorBrush>(frame.BorderBrush).Color);
            Assert.Equal(new Thickness(1), frame.BorderThickness);
            using var target = new RenderTargetBitmap(new((int)Math.Ceiling(frame.Bounds.Width),
                (int)Math.Ceiling(frame.Bounds.Height)), new(96, 96));
            target.Render(frame);
            using var stream = new MemoryStream();
            target.Save(stream, PngBitmapEncoderOptions.Default);
            stream.Position = 0;
            using var pixels = SKBitmap.Decode(stream);
            Assert.Equal(new SKColor(surface.R, surface.G, surface.B, surface.A), pixels.GetPixel(pixels.Width - 3, pixels.Height / 2));
            Assert.Equal(new SKColor(border.R, border.G, border.B, border.A), pixels.GetPixel(pixels.Width / 2, 0));
            Capture(window, dark ? "effect-script-completion-dark.png" : "effect-script-completion-light.png");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(Key.Tab)]
    [InlineData(Key.Enter)]
    public void CommittedCharactersAutomaticallyShowCaretCompletionAndReplaceOnlyThePrefix(Key acceptKey)
    {
        using var environment = new UiTestEnvironment();
        var window = new SettingsWindow(new WorkbenchPreferences());
        try
        {
            window.Show();
            window.SelectPage(SettingsPage.EFFECTS);
            UiTestActions.Click(window, "AddEffectScriptButton");
            var input = UiTestActions.Find<TextBox>(window, "ScriptTextInput");
            input.Focus();
            input.SelectAll();
            foreach (var character in "at 0 po")
            {
                window.KeyTextInput(character.ToString());
                Dispatcher.UIThread.RunJobs();
            }

            window.UpdateLayout();
            var popup = UiTestActions.Find<Popup>(window, "ScriptCompletionPopup");
            var list = UiTestActions.Find<ListBox>(window, "ScriptCompletions");
            Assert.True(popup.IsOpen);
            Assert.True(popup.IsUsingOverlayLayer);
            Assert.True(list.IsEffectivelyVisible);
            Assert.True(list.Bounds.Height > 0);
            Assert.Equal("position", ((EffectScriptCompletion)list.SelectedItem!).Insertion);
            Assert.True(input.IsFocused);
            var bounds = new Rect(list.TranslatePoint(default, window)!.Value, list.Bounds.Size);
            Assert.True(bounds.Intersects(new Rect(window.ClientSize)));
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(), block => block.Name == "ScriptSyntaxHint");

            UiTestActions.Press(window, acceptKey);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("at 0 position", input.Text);
            Assert.Equal(input.Text, window.ViewModel.Effects.Source);
            Assert.Equal(input.Text!.Length, input.CaretIndex);
            Assert.False(popup.IsOpen);
            Assert.True(input.IsFocused);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void CompletionClosesForEscFocusAndImePreeditWithoutChangingCommittedSource()
    {
        using var environment = new UiTestEnvironment();
        var window = new SettingsWindow(new WorkbenchPreferences());
        try
        {
            window.Show();
            window.SelectPage(SettingsPage.EFFECTS);
            UiTestActions.Click(window, "AddEffectScriptButton");
            var input = UiTestActions.Find<TextBox>(window, "ScriptTextInput");
            input.Focus();
            input.SelectAll();
            window.KeyTextInput("at 0 po");
            Dispatcher.UIThread.RunJobs();
            var popup = UiTestActions.Find<Popup>(window, "ScriptCompletionPopup");
            Assert.True(popup.IsOpen);
            UiTestActions.Press(window, Key.Escape);
            Dispatcher.UIThread.RunJobs();
            Assert.False(popup.IsOpen);
            Assert.Equal("at 0 po", input.Text);
            UiTestActions.Press(window, Key.Space, RawInputModifiers.Control);
            Assert.True(popup.IsOpen);

            var presenter = window.GetVisualDescendants().OfType<EffectScriptTextPresenter>().Single();
            presenter.PreeditText = "字幕";
            Assert.False(popup.IsOpen);
            Assert.Equal("at 0 po", input.Text);
            Assert.Contains("字幕", string.Concat(presenter.TextLayout.TextLines.SelectMany(line => line.TextRuns).Select(run => run.Text.ToString())), StringComparison.Ordinal);
            presenter.PreeditText = null;
            UiTestActions.Press(window, Key.Space, RawInputModifiers.Control);
            Assert.True(popup.IsOpen);
            UiTestActions.Find<TextBox>(window, "EffectScriptNameInput").Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.False(popup.IsOpen);
            Assert.Equal("at 0 po", input.Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void CompletionCanBeChosenWithThePointerWhileTheTextInputKeepsFocus()
    {
        using var environment = new UiTestEnvironment();
        var window = new SettingsWindow(new WorkbenchPreferences());
        try
        {
            window.Show();
            window.SelectPage(SettingsPage.EFFECTS);
            UiTestActions.Click(window, "AddEffectScriptButton");
            var input = UiTestActions.Find<TextBox>(window, "ScriptTextInput");
            input.Text = "at 0 sc";
            input.Focus();
            input.CaretIndex = input.Text.Length;
            UiTestActions.Press(window, Key.Space, RawInputModifiers.Control);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var list = UiTestActions.Find<ListBox>(window, "ScriptCompletions");
            var item = list.GetVisualDescendants().OfType<ListBoxItem>().Single();
            var point = item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("at 0 scale", input.Text);
            Assert.True(input.IsFocused);
            Assert.False(list.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void SettingsScriptPageHasItsOwnCompiledContextAndBuiltinReadOnlyEditor()
    {
        using var environment = new UiTestEnvironment();
        var window = new SettingsWindow(new WorkbenchPreferences());
        try
        {
            window.Show();
            window.SelectPage(SettingsPage.EFFECTS);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Same(window.ViewModel.Effects, UiTestActions.Find<EffectSettingsView>(window, "EffectsView").DataContext);
            var source = UiTestActions.Find<TextBox>(window, "ScriptTextInput");
            Assert.True(source.IsReadOnly);
            Assert.Contains("effect", source.Text, StringComparison.Ordinal);
            Assert.False(UiTestActions.Find<Button>(window, "SaveEffectScriptButton").IsEffectivelyEnabled);
            Assert.Null(window.FindControl<Button>("DuplicateEffectScriptButton"));
            UiTestActions.Click(window, "AddEffectScriptButton");
            Assert.False(source.IsReadOnly);
            Assert.True(window.ViewModel.Effects.IsDirty);
            Assert.Equal(BuiltinEffectScripts.Templates.Length, window.ViewModel.Effects.Effects.Length);
            Assert.Empty(window.ViewModel.Effects.SelectedIds);
            Assert.True(UiTestActions.Find<Button>(window, "SaveEffectScriptButton").IsEffectivelyEnabled);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void RealTextInputAndKeyboardCompletionReplaceThePrefixWithoutExtraSpaceOrClick()
    {
        using var environment = new UiTestEnvironment();
        var window = new SettingsWindow(new WorkbenchPreferences());
        try
        {
            window.Show();
            window.SelectPage(SettingsPage.EFFECTS);
            UiTestActions.Click(window, "AddEffectScriptButton");
            var source = UiTestActions.Find<TextBox>(window, "ScriptTextInput");
            source.Focus();
            source.SelectAll();
            window.KeyTextInput("at 0 po");
            source.CaretIndex = source.Text!.Length;
            UiTestActions.Press(window, Key.Space, RawInputModifiers.Control);
            var completion = UiTestActions.Find<ListBox>(window, "ScriptCompletions");
            Assert.True(completion.IsVisible);
            Assert.Equal("position", ((EffectScriptCompletion)completion.SelectedItem!).Insertion);
            UiTestActions.Press(window, Key.Enter);
            Assert.Equal("at 0 position", source.Text);
            Assert.Equal(source.Text, window.ViewModel.Effects.Source);
            Assert.False(completion.IsVisible);
            Assert.Equal(source.Text.Length, source.CaretIndex);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task InvalidScriptIsEditableAndLineColumnCanBeLocatedWithoutFocusLoop()
    {
        using var environment = new UiTestEnvironment();
        var window = new SettingsWindow(new WorkbenchPreferences());
        try
        {
            window.Show();
            window.SelectPage(SettingsPage.EFFECTS);
            UiTestActions.Click(window, "AddEffectScriptButton");
            var input = UiTestActions.Find<TextBox>(window, "ScriptTextInput");
            input.Text = "effect broken\n";
            UiTestActions.Click(window, "ValidateEffectScriptButton");
            Assert.NotNull(window.ViewModel.Effects.Error);
            Assert.Equal(1, window.ViewModel.Effects.DiagnosticLine);
            Assert.Equal("effect broken\n", input.Text);
            UiTestActions.Click(window, "ScriptLocateError");
            Assert.True(input.IsFocused);
            Assert.Equal(0, input.SelectionStart);
            window.SelectPage(SettingsPage.APPEARANCE);
            window.SelectPage(SettingsPage.EFFECTS);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("effect broken\n", input.Text);
            Assert.Null(window.FindControl<Button>("RestoreEffectScriptButton"));
            var draftId = window.ViewModel.Effects.Draft!.Id;
            var builtin = window.ViewModel.Effects.Effects.First(item => item.IsBuiltin);
            Assert.False(await window.ViewModel.Effects.SelectEffectsAsync(builtin.Id, [builtin.Id]));
            Assert.DoesNotContain(window.ViewModel.Effects.Effects, item => item.Id == draftId);
            Assert.Equal("effect broken\n", input.Text);
            Assert.False(input.IsReadOnly);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void SyntaxColorsHaveRealStyledRunsAndNativeCaretGeometryInBothThemes()
    {
        using var environment = new UiTestEnvironment();
        var window = new SettingsWindow(new WorkbenchPreferences());
        try
        {
            window.Show();
            window.SelectPage(SettingsPage.EFFECTS);
            foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                window.RequestedThemeVariant = theme;
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                var presenter = window.GetVisualDescendants().OfType<EffectScriptTextPresenter>().Single();
                var layout = presenter.TextLayout;
                var colors = layout.TextLines.SelectMany(line => line.TextRuns).Select(run => run.Properties?.ForegroundBrush)
                    .OfType<ISolidColorBrush>().Select(brush => brush.Color).Distinct().ToArray();
                Assert.True(colors.Length >= 4);
                var input = UiTestActions.Find<TextBox>(window, "ScriptTextInput");
                input.Focus();
                input.CaretIndex = 6;
                Assert.True(layout.HitTestTextPosition(6).Width >= 0);
                Assert.Equal(6, input.CaretIndex);
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void SaveAndExportForwardValidatedSourcesWithoutDependingOnCurrentSubtitle()
    {
        using var environment = new UiTestEnvironment();
        var window = new SettingsWindow(new WorkbenchPreferences());
        EffectScriptPreset? saved = null;
        EffectScriptPreset? exported = null;
        window.UpsertEffectRequested += (_, args) => saved = args.Preset;
        window.ExportEffectRequested += (_, args) => exported = Assert.Single(args.Presets);
        try
        {
            window.Show();
            window.UpdateSelectionAvailability(false);
            window.SelectPage(SettingsPage.EFFECTS);
            UiTestActions.Click(window, "AddEffectScriptButton");
            UiTestActions.Click(window, "SaveEffectScriptButton");
            Assert.NotNull(saved);
            window.UpdateEffects([saved], saved.Id);
            Assert.False(window.ViewModel.Effects.IsDirty);
            UiTestActions.Click(window, "ExportEffectScriptButton");
            Assert.Equal(saved, exported);
            Assert.NotNull(EffectScriptParser.Parse(exported!.Source));
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), button => button.Name == "ApplyEffectScriptButton");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PointerMultiSelectionExportsOnlySavedSelectedScriptsAndSurvivesRefresh(bool range)
    {
        using var environment = new UiTestEnvironment();
        var window = new SettingsWindow(new WorkbenchPreferences());
        var first = PersonalScript("multi-first");
        var second = PersonalScript("multi-second");
        var third = PersonalScript("multi-third");
        ImmutableArray<EffectScriptPreset> exported = [];
        window.ExportEffectRequested += (_, args) => exported = args.Presets;
        try
        {
            window.UpdateEffects([first, second, third], first.Id);
            window.Show();
            window.SelectPage(SettingsPage.EFFECTS);
            ClickScript(window, first.Id);
            var command = Avalonia.Application.Current!.PlatformSettings!.HotkeyConfiguration.CommandModifiers;
            var toggle = command.HasFlag(KeyModifiers.Meta) ? RawInputModifiers.Meta : RawInputModifiers.Control;
            ClickScript(window, third.Id, range ? RawInputModifiers.Shift : toggle);
            await window.ViewModel.Effects.SelectionCompletion;
            var expected = range ? new[] { first.Id, second.Id, third.Id } : [first.Id, third.Id];
            Assert.Equal(expected, window.ViewModel.Effects.SelectedIds.ToArray());
            Assert.False(UiTestActions.Find<Button>(window, "SaveEffectScriptButton").IsEffectivelyEnabled);
            Assert.True(UiTestActions.Find<Button>(window, "DeleteEffectScriptButton").IsEffectivelyEnabled);
            Assert.False(UiTestActions.Find<Button>(window, "ValidateEffectScriptButton").IsEffectivelyEnabled);
            Assert.True(UiTestActions.Find<TextBox>(window, "ScriptTextInput").IsReadOnly);

            window.RefreshLanguage();
            window.UpdateEffects([first, second, third]);
            UiTestActions.Click(window, "ExportEffectScriptButton");

            Assert.Equal(expected, exported.Select(item => item.Id).ToArray());
            var list = UiTestActions.Find<ListBox>(window, "EffectScriptList");
            Assert.Equal(expected, list.Selection.SelectedItems.OfType<EffectScriptSettingsItem>().Select(item => item.Id).OrderBy(id =>
                Array.IndexOf(expected, id)).ToArray());
            ClickScript(window, second.Id);
            await window.ViewModel.Effects.SelectionCompletion;
            Assert.Equal(new[] { second.Id }, window.ViewModel.Effects.SelectedIds.ToArray());
            Assert.False(UiTestActions.Find<TextBox>(window, "ScriptTextInput").IsReadOnly);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public async Task DirtyPointerSelectionAsksBeforeLeavingAndRollsBackOnCancellationOrFailedSave(int decision, bool leave)
    {
        using var environment = new UiTestEnvironment();
        var window = new SettingsWindow(new WorkbenchPreferences());
        var first = PersonalScript("pending-first");
        var second = PersonalScript("pending-second");
        var model = window.ViewModel.Effects;
        var saves = 0;
        var confirmations = 0;
        try
        {
            window.UpdateEffects([first, second], first.Id);
            window.Show();
            window.SelectPage(SettingsPage.EFFECTS);
            model.Source += "# pending edit\n";
            var pending = model.Source;
            model.ConfirmLeaveAsync = () =>
            {
                confirmations++;
                return Task.FromResult(decision);
            };
            model.SaveDraftAsync = _ =>
            {
                saves++;
                return Task.FromResult(false);
            };

            ClickScript(window, second.Id);
            await model.SelectionCompletion;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(1, confirmations);
            Assert.Equal(decision == 0 ? 1 : 0, saves);
            Assert.Equal(leave ? second.Id : first.Id, model.SelectedEffect!.Id);
            Assert.Equal(leave ? second.Source : pending, UiTestActions.Find<TextBox>(window, "ScriptTextInput").Text);
            var list = UiTestActions.Find<ListBox>(window, "EffectScriptList");
            Assert.Equal(model.SelectedEffect.Id,
                Assert.Single(list.Selection.SelectedItems.OfType<EffectScriptSettingsItem>()).Id);
            if (leave)
            {
                model.SelectEffects(first.Id, [first.Id]);
                Assert.Equal(first.Source, model.Source);
                Assert.False(model.IsDirty);
            }
        }
        finally
        {
            window.CloseImmediately();
        }
    }

    [AvaloniaFact]
    public void DeletingAnInvalidIndependentDraftRequestsDiscardBeforeClearingWithoutSaving()
    {
        using var environment = new UiTestEnvironment();
        var window = new SettingsWindow(new WorkbenchPreferences());
        var saves = 0;
        SettingsEffectDeleteEventArgs? deletion = null;
        window.UpsertEffectRequested += (_, _) => saves++;
        window.DeleteEffectRequested += (_, args) => deletion = args;
        try
        {
            window.Show();
            window.SelectPage(SettingsPage.EFFECTS);
            UiTestActions.Click(window, "AddEffectScriptButton");
            UiTestActions.Find<TextBox>(window, "ScriptTextInput").Text = "effect broken";
            var draft = Assert.IsType<EffectScriptPreset>(window.ViewModel.Effects.Draft);
            UiTestActions.Click(window, "DeleteEffectScriptButton");

            Assert.NotNull(deletion);
            Assert.True(deletion.IsDraftOnly);
            Assert.Empty(deletion.Ids);
            Assert.Equal(draft.Id, deletion.DraftId);
            Assert.Equal(draft.Id, window.ViewModel.Effects.Draft?.Id);
            Assert.Equal("effect broken", UiTestActions.Find<TextBox>(window, "ScriptTextInput").Text);
            window.ViewModel.Effects.DiscardDraft();

            Assert.Null(window.ViewModel.Effects.Draft);
            Assert.Equal(BuiltinEffectScripts.Templates.Length, window.ViewModel.Effects.Effects.Length);
            Assert.Empty(UiTestActions.Find<TextBox>(window, "ScriptTextInput").Text!);
            Assert.Equal(0, saves);
        }
        finally
        {
            window.Close();
        }
    }

    private static EffectScriptPreset PersonalScript(string id)
    {
        return new(Guid.NewGuid(), id, BuiltinEffectScripts.Get("fade-in").Source.Replace("fade-in", id, StringComparison.Ordinal));
    }

    private static void ClickScript(SettingsWindow window, Guid id, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        var list = UiTestActions.Find<ListBox>(window, "EffectScriptList");
        var item = list.Items.OfType<EffectScriptSettingsItem>().Single(value => value.Id == id);
        list.ScrollIntoView(item);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var container = Assert.IsAssignableFrom<ListBoxItem>(list.ContainerFromItem(item));
        var point = container.TranslatePoint(new Point(container.Bounds.Width / 2, container.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left, modifiers);
        window.MouseUp(point, MouseButton.Left, modifiers);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static void Capture(Window window, string name)
    {
        var directory = Environment.GetEnvironmentVariable("AEGINEXT_UI_CAPTURE_DIRECTORY");
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var image = window.CaptureRenderedFrame();
        Assert.NotNull(image);
        image.Save(Path.Combine(directory, name), PngBitmapEncoderOptions.Default);
    }
}
