using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AegiNext.Desktop.Menus;
using AegiNext.Desktop.Settings;
using AegiNext.Desktop.Shortcuts;
using AegiNext.Desktop.Updates;

namespace AegiNext.Desktop.Tests.Updates;

public sealed class UpdatePreferencesTests
{
    [Fact]
    public void DefaultsCheckAutomaticallyAndIncludePrereleases()
    {
        var preferences = new WorkbenchPreferences();

        preferences.Validate();

        Assert.True(preferences.AutoCheckUpdates);
        Assert.Equal(UpdateChannel.INCLUDE_PRERELEASE, preferences.UpdateChannel);
        Assert.Equal(50, (int)WorkbenchCommand.SEEK_CLIP_END);
        Assert.Equal(51, (int)WorkbenchCommand.CHECK_UPDATES);
        Assert.Equal(13, (int)SettingsPage.SUBTITLE_COLOR_TAGS);
        Assert.Equal(14, (int)SettingsPage.UPDATES);
    }

    [Theory]
    [InlineData(false, UpdateChannel.STABLE)]
    [InlineData(true, UpdateChannel.INCLUDE_PRERELEASE)]
    public async Task SaveAndTransferRoundTripsPreserveTheUpdateSettings(bool automatic, UpdateChannel channel)
    {
        using var directory = new TemporaryWorkbenchDirectory();
        using var store = new WorkbenchPreferencesStore(directory.Path);
        var preferences = new WorkbenchPreferences { AutoCheckUpdates = automatic, UpdateChannel = channel };

        await store.SaveAsync(preferences);
        var loaded = store.Load();
        var imported = WorkbenchPreferencesStore.Deserialize(WorkbenchPreferencesStore.Serialize(preferences));

        Assert.Null(store.LoadError);
        Assert.Equal(preferences, loaded);
        Assert.Equal(preferences, imported);
    }

    [Fact]
    public async Task PreviousSettingsLoadAndImportWithDefaultsAndAnUnboundCheckCommand()
    {
        using var directory = new TemporaryWorkbenchDirectory();
        var previous = new WorkbenchPreferences
        {
            Language = "zh-CN",
            Volume = 0.375f,
            ShortcutBindings = ShortcutDefaults.CreateBindings()
                .Where(binding => binding.Command <= WorkbenchCommand.SEEK_CLIP_END)
                .Select(binding => binding.Command switch
                {
                    WorkbenchCommand.OPEN_PROJECT => binding with { Gesture = "CmdOrCtrl+Alt+O" },
                    WorkbenchCommand.SAVE_PROJECT => binding with { Gesture = string.Empty },
                    _ => binding
                }).ToImmutableArray()
        };
        var json = JsonNode.Parse(JsonSerializer.Serialize(previous))!.AsObject();
        json.Remove(nameof(WorkbenchPreferences.AutoCheckUpdates));
        json.Remove(nameof(WorkbenchPreferences.UpdateChannel));
        var text = json.ToJsonString();
        var path = Path.Combine(directory.Path, "preferences.json");
        await File.WriteAllTextAsync(path, text);
        using var store = new WorkbenchPreferencesStore(directory.Path);

        var loaded = store.Load();
        var imported = WorkbenchPreferencesStore.Deserialize(Encoding.UTF8.GetBytes(text));

        Assert.Null(store.LoadError);
        Assert.Equal(loaded, imported);
        Assert.True(loaded.AutoCheckUpdates);
        Assert.Equal(UpdateChannel.INCLUDE_PRERELEASE, loaded.UpdateChannel);
        Assert.Equal(previous.Language, loaded.Language);
        Assert.Equal(previous.Volume, loaded.Volume);
        Assert.Equal(previous.ShortcutBindings.AsEnumerable(),
            loaded.ShortcutBindings.Where(binding => binding.Command <= WorkbenchCommand.SEEK_CLIP_END));
        Assert.Equal(string.Empty, Assert.Single(loaded.ShortcutBindings,
            binding => binding.Command == WorkbenchCommand.CHECK_UPDATES).Gesture);
        Assert.Equal(text, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public void InvalidChannelsAreRejectedByValidationAndSettingsImport()
    {
        var preferences = new WorkbenchPreferences { UpdateChannel = (UpdateChannel)99 };

        Assert.Throws<InvalidDataException>(preferences.Validate);
        Assert.Throws<InvalidDataException>(() =>
            WorkbenchPreferencesStore.Deserialize(JsonSerializer.SerializeToUtf8Bytes(preferences)));
    }

    [Fact]
    public void EqualityIncludesBothUpdateSettingsAndRemainsConsistentForEqualSnapshots()
    {
        var preferences = new WorkbenchPreferences();
        var equal = preferences with { ShortcutBindings = preferences.ShortcutBindings.ToArray().ToImmutableArray() };

        Assert.Equal(preferences, equal);
        Assert.Equal(preferences.GetHashCode(), equal.GetHashCode());
        Assert.NotEqual(preferences, preferences with { AutoCheckUpdates = false });
        Assert.NotEqual(preferences, preferences with { UpdateChannel = UpdateChannel.STABLE });
    }

    [Fact]
    public void HelpMenuContainsCheckUpdatesAndItsDefaultShortcutIsUnbound()
    {
        var help = Assert.Single(WorkbenchMenuCatalog.Groups, group => group.Key == "Help");

        Assert.Contains(WorkbenchCommand.CHECK_UPDATES, help.Commands);
        Assert.Contains(WorkbenchCommand.OPEN_ABOUT, help.Commands);
        Assert.Equal(string.Empty, Assert.Single(ShortcutDefaults.CreateBindings(),
            binding => binding.Command == WorkbenchCommand.CHECK_UPDATES).Gesture);
    }
}
