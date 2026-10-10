using System.Reflection;
using AegiNext.Application.Presets;
using AegiNext.Core.Effects;
using AegiNext.Core.Presets;
using AegiNext.Desktop.Settings;
using AegiNext.Desktop.Settings.Effects;
using AegiNext.Desktop.Startup;
using AegiNext.Desktop.Workspace;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace AegiNext.Desktop.Ui.Tests;

public sealed class SettingsLibrarySelectionUiTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExportWritesOnlySelectedSavedFilesAndImportAcceptsMultipleFiles(bool effects)
    {
        using var environment = new UiTestEnvironment();
        var context = new DesktopApplicationContext(new WorkbenchPreferencesStore(environment.DirectoryPath));
        var output = Path.Combine(environment.DirectoryPath, "selected-export");
        Directory.CreateDirectory(output);
        var dialogs = new StartupTestDialogService { FolderPath = output };
        using var coordinator = new SettingsWindowCoordinator(context, _ => dialogs);
        var owner = new Window();
        try
        {
            await context.Initialization;
            var ids = new Guid[3];
            for (var index = 0; index < ids.Length; index++)
            {
                ids[index] = Guid.NewGuid();
                if (effects)
                {
                    var source = $"effect \"selection-{index}\" version 1\nshort-clip compress\nsegment stay flex 1\n    at 0 opacity base\n    at 1 opacity base\nend\n";
                    await context.RunEffectOperationAsync(() => context.EffectScriptLibrary.UpsertAsync(new(ids[index], $"Effect {index}", source)));
                }
                else
                {
                    await context.RunStyleOperationAsync(() => context.StyleLibrary.UpsertAsync(new(ids[index], $"Style {index}", new())));
                }
            }
            owner.Show();
            await coordinator.OpenAsync(owner, page: effects ? SettingsPage.EFFECTS : SettingsPage.STYLES);
            var window = coordinator.Window!;
            if (effects)
            {
                window.ViewModel.Effects.SelectEffects(ids[0], [ids[0], ids[2]]);
            }
            else
            {
                window.ViewModel.Styles.SelectStyles(ids[0], [ids[0], ids[2]]);
            }
            UiTestActions.Click(window, effects ? "ExportEffectScriptButton" : "ExportStylesButton");
            await context.Completion;
            var files = Directory.GetFiles(output);
            Assert.Equal(2, files.Length);
            Assert.Equal(1, dialogs.FolderRequests);
            Assert.Equal(0, dialogs.SaveCount);
            Assert.Equal(0, dialogs.PresetConfirmationRequests);
            if (effects)
            {
                var sources = await Task.WhenAll(files.Select(path => File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)));
                Assert.DoesNotContain(sources, source => source.Contains("selection-1", StringComparison.Ordinal));
                Assert.Contains(sources, source => source.Contains("selection-0", StringComparison.Ordinal));
                await context.RunEffectOperationAsync(() => context.EffectScriptLibrary.RemoveAsync(ids[0]));
                await context.RunEffectOperationAsync(() => context.EffectScriptLibrary.RemoveAsync(ids[2]));
            }
            else
            {
                var exported = await Task.WhenAll(files.Select(path => SubtitleStylePresetStore.LoadAsync(path, TestContext.Current.CancellationToken)));
                Assert.Equal(new[] { ids[0], ids[2] }.Order(), exported.SelectMany(value => value.Presets).Select(value => value.Id).Order());
                await context.RunStyleOperationAsync(() => context.StyleLibrary.RemoveAsync(ids[0]));
                await context.RunStyleOperationAsync(() => context.StyleLibrary.RemoveAsync(ids[2]));
            }
            dialogs.OpenPaths = files;
            UiTestActions.Click(window, effects ? "ImportEffectScriptButton" : "ImportStylesButton");
            await context.Completion;
            Assert.Equal(3, effects ? context.EffectScriptLibrary.Snapshot.Presets.Length : context.StyleLibrary.Snapshot.Presets.Length);
            Assert.False(window.ViewModel.HasError);
        }
        finally
        {
            coordinator.Dispose();
            owner.Close();
            await context.DisposeAsync();
        }
    }

    [AvaloniaTheory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public async Task PageSwitchHonorsSaveDiscardAndCancel(bool effects, int choice)
    {
        using var environment = new UiTestEnvironment();
        var context = new DesktopApplicationContext(new WorkbenchPreferencesStore(environment.DirectoryPath));
        var dialogs = new StartupTestDialogService { PresetChoice = choice };
        using var coordinator = new SettingsWindowCoordinator(context, _ => dialogs);
        var owner = new Window();
        try
        {
            await context.Initialization;
            owner.Show();
            await coordinator.OpenAsync(owner, page: effects ? SettingsPage.EFFECTS : SettingsPage.STYLES);
            var window = coordinator.Window!;
            var originalPage = window.CurrentPage;
            if (effects)
            {
                window.ViewModel.Effects.AddCommand.Execute(null);
                window.ViewModel.Effects.Name = "Changed 脚本 123";
                Assert.Null(window.ViewModel.Effects.SelectedEffect);
                Assert.Equal(BuiltinEffectScripts.Templates.Length, window.ViewModel.Effects.Effects.Length);
            }
            else
            {
                window.ViewModel.Styles.AddCommand.Execute(null);
                window.ViewModel.Styles.Name = "Changed 样式 123";
                Assert.Empty(window.ViewModel.Styles.Styles);
            }
            UiTestActions.SelectSettingsPage(window, SettingsPage.COLORS);
            await window.ViewModel.NavigationCompletion;
            Assert.Equal(1, dialogs.PresetConfirmationRequests);
            Assert.Equal(choice == 2 ? originalPage : SettingsPage.COLORS, window.CurrentPage);
            Assert.Equal(window.CurrentPage, UiTestActions.Find<ListBox>(window, "Navigation").SelectedValue);
            var count = effects ? context.EffectScriptLibrary.Snapshot.Presets.Length : context.StyleLibrary.Snapshot.Presets.Length;
            Assert.Equal(choice == 0 ? 1 : 0, count);
            Assert.Equal(choice == 2, effects ? window.ViewModel.Effects.IsDirty : window.ViewModel.Styles.IsDirty);
            if (choice == 1)
            {
                Assert.Null(effects ? (object?)window.ViewModel.Effects.Draft : window.ViewModel.Styles.Draft);
            }
        }
        finally
        {
            coordinator.Dispose();
            owner.Close();
            await context.DisposeAsync();
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidDraftCannotLeaveWhenSaveIsChosenAndDiscardCanClose(bool effects)
    {
        using var environment = new UiTestEnvironment();
        var context = new DesktopApplicationContext(new WorkbenchPreferencesStore(environment.DirectoryPath));
        var dialogs = new StartupTestDialogService { PresetChoice = 0 };
        using var coordinator = new SettingsWindowCoordinator(context, _ => dialogs);
        var owner = new Window();
        try
        {
            await context.Initialization;
            owner.Show();
            await coordinator.OpenAsync(owner, page: effects ? SettingsPage.EFFECTS : SettingsPage.STYLES);
            var window = coordinator.Window!;
            if (effects)
            {
                window.ViewModel.Effects.AddCommand.Execute(null);
                window.ViewModel.Effects.Source = "effect broken";
            }
            else
            {
                window.ViewModel.Styles.AddCommand.Execute(null);
                window.ViewModel.Styles.FontSizeText = "7e-";
            }
            window.SelectPage(SettingsPage.COLORS);
            await window.ViewModel.NavigationCompletion;
            Assert.Equal(effects ? SettingsPage.EFFECTS : SettingsPage.STYLES, window.CurrentPage);
            Assert.NotNull(effects ? window.ViewModel.Effects.Error : window.ViewModel.Styles.Error);
            window.Close();
            await window.CloseCompletion;
            Assert.True(window.IsVisible);
            Assert.Same(window, coordinator.Window);
            dialogs.PresetChoice = 1;
            window.Close();
            await window.CloseCompletion;
            Assert.Null(coordinator.Window);
            Assert.Empty(context.StyleLibrary.Snapshot.Presets);
            Assert.Empty(context.EffectScriptLibrary.Snapshot.Presets);
        }
        finally
        {
            coordinator.Dispose();
            owner.Close();
            await context.DisposeAsync();
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StorageFailureKeepsNewDraftOutOfListAndCanBeRetried(bool effects)
    {
        using var environment = new UiTestEnvironment();
        var context = new DesktopApplicationContext(new WorkbenchPreferencesStore(environment.DirectoryPath));
        var dialogs = new StartupTestDialogService { PresetChoice = 0 };
        using var coordinator = new SettingsWindowCoordinator(context, _ => dialogs);
        var owner = new Window();
        try
        {
            await context.Initialization;
            owner.Show();
            await coordinator.OpenAsync(owner, page: effects ? SettingsPage.EFFECTS : SettingsPage.STYLES);
            var window = coordinator.Window!;
            if (effects)
            {
                window.ViewModel.Effects.AddCommand.Execute(null);
                window.ViewModel.Effects.Name = "Retry Script 中文 123";
            }
            else
            {
                window.ViewModel.Styles.AddCommand.Execute(null);
                window.ViewModel.Styles.Name = "Retry Style 中文 123";
            }
            var path = Path.Combine(environment.DirectoryPath, effects ? "effect-scripts.json" : "subtitle-styles.aegistyles");
            Directory.CreateDirectory(path);
            window.SelectPage(SettingsPage.COLORS);
            await window.ViewModel.NavigationCompletion;
            Assert.Equal(effects ? SettingsPage.EFFECTS : SettingsPage.STYLES, window.CurrentPage);
            Assert.True(window.ViewModel.HasError);
            Assert.True(effects ? window.ViewModel.Effects.IsDirty : window.ViewModel.Styles.IsDirty);
            Assert.Empty(context.StyleLibrary.Snapshot.Presets);
            Assert.Empty(context.EffectScriptLibrary.Snapshot.Presets);
            Directory.Delete(path);
            window.SelectPage(SettingsPage.COLORS);
            await window.ViewModel.NavigationCompletion;
            Assert.Equal(SettingsPage.COLORS, window.CurrentPage);
            Assert.False(window.ViewModel.HasError);
            Assert.Equal(1, effects ? context.EffectScriptLibrary.Snapshot.Presets.Length : context.StyleLibrary.Snapshot.Presets.Length);
        }
        finally
        {
            coordinator.Dispose();
            owner.Close();
            await context.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task ReentrantNavigationAndCloseWaitForTheOriginalDecision()
    {
        using var environment = new UiTestEnvironment();
        var context = new DesktopApplicationContext(new WorkbenchPreferencesStore(environment.DirectoryPath));
        var decision = new TaskCompletionSource<int>();
        var dialogs = new StartupTestDialogService { PendingPresetDecision = decision };
        using var coordinator = new SettingsWindowCoordinator(context, _ => dialogs);
        var owner = new Window();
        try
        {
            await context.Initialization;
            owner.Show();
            await coordinator.OpenAsync(owner, page: SettingsPage.STYLES);
            var window = coordinator.Window!;
            window.ViewModel.Styles.AddCommand.Execute(null);
            window.SelectPage(SettingsPage.EFFECTS);
            var navigation = window.ViewModel.NavigationCompletion;
            Assert.False(navigation.IsCompleted);
            window.SelectPage(SettingsPage.COLORS);
            Assert.Same(navigation, window.ViewModel.NavigationCompletion);
            window.Close();
            Assert.False(window.CloseCompletion.IsCompleted);
            Assert.Equal(1, dialogs.PresetConfirmationRequests);
            decision.SetResult(1);
            await navigation;
            await window.CloseCompletion;
            Assert.Null(coordinator.Window);
            Assert.Equal(1, dialogs.PresetConfirmationRequests);
            Assert.Empty(context.StyleLibrary.Snapshot.Presets);
        }
        finally
        {
            decision.TrySetResult(2);
            coordinator.Dispose();
            owner.Close();
            await context.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task OwnerCloseCanBeCancelledAndResumesAfterTheDraftIsDiscarded()
    {
        using var environment = new UiTestEnvironment();
        var context = new DesktopApplicationContext(new WorkbenchPreferencesStore(environment.DirectoryPath));
        using var coordinator = new SettingsWindowCoordinator(context);
        var owner = new Window();
        try
        {
            await context.Initialization;
            owner.Show();
            await coordinator.OpenAsync(owner, page: SettingsPage.EFFECTS);
            var settings = coordinator.Window!;
            settings.ViewModel.Effects.AddCommand.Execute(null);
            owner.Close();
            UiTestActions.Click(Assert.Single(settings.OwnedWindows), "CancelButton");
            await settings.CloseCompletion;
            Assert.True(owner.IsVisible);
            Assert.True(settings.IsVisible);
            owner.Close();
            UiTestActions.Click(Assert.Single(settings.OwnedWindows), "DiscardButton");
            await settings.CloseCompletion;
            Dispatcher.UIThread.RunJobs();
            Assert.False(owner.IsVisible);
            Assert.False(settings.IsVisible);
            Assert.Empty(context.EffectScriptLibrary.Snapshot.Presets);
        }
        finally
        {
            coordinator.Dispose();
            owner.Close();
            await context.DisposeAsync();
        }
    }

    [AvaloniaTheory]
    [InlineData("en-US", false)]
    [InlineData("zh-CN", true)]
    public async Task ActualUnsavedDialogOffersCancelDiscardAndSave(string language, bool dark)
    {
        using var environment = new UiTestEnvironment();
        var context = new DesktopApplicationContext(new WorkbenchPreferencesStore(environment.DirectoryPath),
            new() { Language = language, Theme = dark ? WorkbenchTheme.DARK : WorkbenchTheme.LIGHT });
        using var coordinator = new SettingsWindowCoordinator(context);
        var owner = new Window();
        try
        {
            await context.Initialization;
            owner.Show();
            await coordinator.OpenAsync(owner, page: SettingsPage.STYLES);
            var window = coordinator.Window!;
            window.ViewModel.Styles.AddCommand.Execute(null);
            window.ViewModel.Styles.Name = "字幕 Style 123";
            window.SelectPage(SettingsPage.EFFECTS);
            var dialog = Assert.Single(window.OwnedWindows);
            Assert.True(UiTestActions.Find<Button>(dialog, "SaveButton").IsVisible);
            Capture(dialog, $"unsaved-presets-dialog-{language}.png");
            UiTestActions.Click(dialog, "CancelButton");
            await window.ViewModel.NavigationCompletion;
            Assert.Equal(SettingsPage.STYLES, window.CurrentPage);
            Assert.True(window.ViewModel.Styles.IsDirty);
            window.SelectPage(SettingsPage.EFFECTS);
            UiTestActions.Click(Assert.Single(window.OwnedWindows), "DiscardButton");
            await window.ViewModel.NavigationCompletion;
            Assert.Equal(SettingsPage.EFFECTS, window.CurrentPage);
            Assert.Null(window.ViewModel.Styles.Draft);
            window.ViewModel.Effects.AddCommand.Execute(null);
            window.Close();
            UiTestActions.Click(Assert.Single(window.OwnedWindows), "SaveButton");
            await window.CloseCompletion;
            Assert.Null(coordinator.Window);
            Assert.Single(context.EffectScriptLibrary.Snapshot.Presets);
        }
        finally
        {
            coordinator.Dispose();
            owner.Close();
            await context.DisposeAsync();
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LeftListsSupportControlAndShiftSelectionAndKeepIdsOnRefresh(bool effects)
    {
        var window = new SettingsWindow(new WorkbenchPreferences());
        try
        {
            var ids = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();
            if (effects)
            {
                window.UpdateEffects(ids.Select((id, index) => new EffectScriptPreset(id, $"特效 Script {index}",
                    $"effect \"selection-{index}\" version 1\nshort-clip compress\nsegment stay flex 1\n    at 0 opacity base\n    at 1 opacity base\nend\n")));
            }
            else
            {
                window.UpdateStyles(ids.Select((id, index) => new SubtitleStylePreset(id, $"样式 Style {index}", new())));
            }
            window.SelectPage(effects ? SettingsPage.EFFECTS : SettingsPage.STYLES);
            window.Show();
            var list = UiTestActions.Find<ListBox>(window, effects ? "EffectScriptList" : "StyleList");
            var start = effects ? BuiltinEffectScripts.Templates.Length : 0;
            ClickItem(window, list, start);
            ClickItem(window, list, start + 2, RawInputModifiers.Control);
            Assert.Equal(2, list.Selection.SelectedItems.Count);
            Assert.Equal(new[] { ids[0], ids[2] }, (effects ? window.ViewModel.Effects.SelectedIds : window.ViewModel.Styles.SelectedIds).ToArray());
            ClickItem(window, list, start);
            ClickItem(window, list, start + 2, RawInputModifiers.Shift);
            Assert.Equal(3, list.Selection.SelectedItems.Count);
            Assert.Equal(ids, (effects ? window.ViewModel.Effects.SelectedIds : window.ViewModel.Styles.SelectedIds).ToArray());
            window.RefreshLanguage();
            Assert.Equal(3, list.Selection.SelectedItems.Count);
            Capture(window, effects ? "effects-multiple-selection.png" : "styles-multiple-selection.png");
            if (effects)
            {
                Assert.True(window.ViewModel.Effects.IsReadOnly);
                Assert.False(window.ViewModel.Effects.SaveCommand.CanExecute(null));
            }
            else
            {
                Assert.False(window.ViewModel.Styles.CanEdit);
                Assert.False(window.ViewModel.Styles.SaveCommand.CanExecute(null));
            }
            await Task.CompletedTask;
        }
        finally
        {
            window.Close();
        }
    }

    private static void Capture(Window window, string name)
    {
        var directory = Environment.GetEnvironmentVariable("AEGINEXT_UI_CAPTURE_DIRECTORY");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }
        Assert.True(Path.IsPathFullyQualified(directory));
        Directory.CreateDirectory(directory);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(directory, name), PngBitmapEncoderOptions.Default);
    }

    private static void ClickItem(Window window, ListBox list, int index, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        var item = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(index));
        item.BringIntoView();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        var point = item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left, modifiers);
        window.MouseUp(point, MouseButton.Left, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task NativePickerRequestsMultipleInputsAndOneExportFolder()
    {
        var owner = new Window();
        try
        {
            owner.Show();
            var files = DispatchProxy.Create<IStorageProvider, RecordingStorageProvider>();
            var fileService = new WindowWorkbenchDialogService(owner, () => files);
            Assert.Empty(await fileService.OpenFilesAsync("ImportStyles", "StyleFiles", ["*.aegistyles"]));
            Assert.True(((RecordingStorageProvider)(object)files).OpenOptions!.AllowMultiple);
            var folders = DispatchProxy.Create<IStorageProvider, RecordingFolderStorageProvider>();
            var folderService = new WindowWorkbenchDialogService(owner, () => folders);
            Assert.Null(await folderService.OpenFolderAsync("ExportStyles"));
            Assert.False(((RecordingFolderStorageProvider)(object)folders).Options!.AllowMultiple);
        }
        finally
        {
            owner.Close();
        }
    }
}
