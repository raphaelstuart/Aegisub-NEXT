using System.Collections.Immutable;
using System.Text.Json;
using AegiNext.Desktop.Settings;
using AegiNext.Desktop.Settings.Shortcuts;
using AegiNext.Desktop.Shortcuts;
using Avalonia.Input;

namespace AegiNext.Desktop.Tests.Shortcuts;

public sealed class ClipBoundaryShortcutPreferencesTests
{
    [Theory]
    [InlineData(false, Key.Q, "SEEK_CLIP_START")]
    [InlineData(false, Key.W, "SEEK_CLIP_END")]
    [InlineData(true, Key.Q, "SEEK_CLIP_START")]
    [InlineData(true, Key.W, "SEEK_CLIP_END")]
    public void DefaultsUseExactShiftModifierOnBothPlatformsAndProtectTextInput(bool mac, Key key, string expected)
    {
        var router = new ShortcutRouter(ShortcutDefaults.CreateBindings(), mac);

        Assert.True(router.TryResolve(key, KeyModifiers.Shift, false, out var command));
        Assert.Equal(expected, command.ToString());
        Assert.False(router.TryResolve(key, KeyModifiers.Shift, true, out _));
        Assert.False(router.TryResolve(key, KeyModifiers.Control, false, out _));
        Assert.False(router.TryResolve(key, KeyModifiers.Meta, false, out _));
        Assert.False(router.TryResolve(key, KeyModifiers.Control | KeyModifiers.Shift, false, out _));
        Assert.False(router.TryResolve(key, KeyModifiers.Control | KeyModifiers.Alt, true, out _));
        Assert.True(router.TryResolve(key, KeyModifiers.None, false, out var audition));
        Assert.Equal(key == Key.Q ? WorkbenchCommand.AUDITION_BEFORE_SUBTITLE : WorkbenchCommand.AUDITION_AFTER_SUBTITLE, audition);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Shift+Q")]
    [InlineData("Shift+W")]
    public async Task PreviousCompleteSettingsGainBindingsWithoutReplacingCustomOrDisabledShortcuts(string occupied)
    {
        using var directory = new TemporaryWorkbenchDirectory();
        var previous = ShortcutDefaults.CreateBindings().Where(binding => binding.Command <= WorkbenchCommand.MERGE_PROJECT)
            .Select(binding => binding.Command switch
            {
                WorkbenchCommand.PLAY_PAUSE => binding with { Gesture = occupied },
                WorkbenchCommand.SAVE_PROJECT => binding with { Gesture = string.Empty },
                _ => binding
            }).ToImmutableArray();
        var original = new WorkbenchPreferences { ShortcutBindings = previous, Language = "zh-CN", Volume = 0.25f };
        var json = JsonSerializer.Serialize(original);
        var path = Path.Combine(directory.Path, "preferences.json");
        await File.WriteAllTextAsync(path, json);
        using var store = new WorkbenchPreferencesStore(directory.Path);

        var restored = store.Load();

        Assert.Null(store.LoadError);
        Assert.Equal(previous.AsEnumerable(), restored.ShortcutBindings.Take(previous.Length));
        var additions = restored.ShortcutBindings.Skip(previous.Length)
            .Where(binding => binding.Command <= WorkbenchCommand.SEEK_CLIP_END).ToArray();
        Assert.Equal(2, additions.Length);
        Assert.Equal("SEEK_CLIP_START", additions[0].Command.ToString());
        Assert.Equal("SEEK_CLIP_END", additions[1].Command.ToString());
        foreach (var addition in additions)
        {
            var gesture = addition.Command.ToString() == "SEEK_CLIP_START" ? "Shift+Q" : "Shift+W";
            Assert.Equal(gesture == occupied ? string.Empty : gesture, addition.Gesture);
        }
        Assert.Equal(original.Language, restored.Language);
        Assert.Equal(original.Volume, restored.Volume);
        Assert.Same(restored, WorkbenchPreferencesMigration.Upgrade(restored));
        Assert.Equal(json, await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData(Key.Q)]
    [InlineData(Key.W)]
    public async Task RebindingAndDisablingRemainEffectiveAfterRestart(Key key)
    {
        var router = new ShortcutRouter(ShortcutDefaults.CreateBindings());
        Assert.True(router.TryResolve(key, KeyModifiers.Shift, false, out var command));
        var model = new ShortcutSettingsViewModel(ShortcutDefaults.CreateBindings());
        model.SelectedRow = Assert.Single(model.Rows, row => row.Command == command);
        ImmutableArray<ShortcutBinding> saved = default;
        model.Changed += (_, args) => saved = args.Bindings;
        model.CaptureGesture("Shift+F6");
        Assert.Equal("Shift+F6", Assert.Single(saved, binding => binding.Command == command).Gesture);
        model.ClearCommand.Execute(null);
        using var directory = new TemporaryWorkbenchDirectory();
        using var store = new WorkbenchPreferencesStore(directory.Path);
        await store.SaveAsync(new() { ShortcutBindings = saved });

        var restored = store.Load();

        Assert.Null(store.LoadError);
        Assert.Equal(string.Empty, Assert.Single(restored.ShortcutBindings, binding => binding.Command == command).Gesture);
        Assert.False(new ShortcutRouter(restored.ShortcutBindings).TryResolve(key, KeyModifiers.Shift, false, out _));
    }
}
