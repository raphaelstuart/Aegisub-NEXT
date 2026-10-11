using System.Collections.Immutable;
using AegiNext.Desktop.I18n;
using AegiNext.Desktop.Menus;
using AegiNext.Desktop.Settings;
using AegiNext.Desktop.Shortcuts;

namespace AegiNext.Desktop.Tests;

public sealed class ProjectMergeMenuTests
{
    [Fact]
    public void MergeIsAvailableInFileMenuWithoutChangingExistingCommandIdentities()
    {
        var file = WorkbenchMenuCatalog.Groups.Single(group => group.Key == "File");
        Assert.Contains(WorkbenchCommand.MERGE_PROJECT, file.Commands);
        Assert.Equal(1, (int)WorkbenchCommand.OPEN_PROJECT);
        Assert.Equal(47, (int)WorkbenchCommand.APPLY_TIMING_POST_PROCESSOR);
        Assert.Equal(48, (int)WorkbenchCommand.MERGE_PROJECT);
    }

    [Fact]
    public void PreviousBindingsRetainCustomGesturesAndGainAnUnboundMergeCommand()
    {
        var previous = ShortcutDefaults.CreateBindings()
            .Where(binding => binding.Command <= WorkbenchCommand.APPLY_TIMING_POST_PROCESSOR)
            .Select(binding => binding.Command == WorkbenchCommand.OPEN_PROJECT
                ? binding with { Gesture = "CmdOrCtrl+Alt+O" } : binding).ToImmutableArray();
        var upgraded = WorkbenchPreferencesMigration.Upgrade(new() { ShortcutBindings = previous });
        upgraded.Validate();
        Assert.Equal(previous, upgraded.ShortcutBindings.Where(binding => binding.Command <= WorkbenchCommand.APPLY_TIMING_POST_PROCESSOR));
        Assert.Equal(string.Empty, upgraded.ShortcutBindings.Single(binding => binding.Command == WorkbenchCommand.MERGE_PROJECT).Gesture);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("zh-CN")]
    public void MergeLabelsAndDiagnosticsExistInEachBuiltinLanguage(string language)
    {
        var catalog = LocalizationCatalog.Load(Path.Combine(AppContext.BaseDirectory, "i18n"));
        var strings = catalog.GetLanguage(language).Strings;
        foreach (var key in new[]
        {
            "Settings.MERGE_PROJECT", "Workbench.MergeProjects", "Workbench.ProjectMergeCurrentSource",
            "Workbench.ProjectMergeDuplicateSource", "Workbench.ProjectMergeChanged",
            "Workbench.ProjectMergeCanvasMismatch", "Workbench.ProjectMergeWhiteMismatch",
            "Workbench.ProjectMergeSourceFailed", "WorkflowLog.ProjectsMerged"
        })
        {
            Assert.True(strings.TryGetValue(key, out var value), key);
            Assert.False(string.IsNullOrWhiteSpace(value));
        }
    }
}
