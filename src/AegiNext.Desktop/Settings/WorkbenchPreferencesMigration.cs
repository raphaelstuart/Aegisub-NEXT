using System.Collections.Immutable;
using AegiNext.Desktop.Shortcuts;

namespace AegiNext.Desktop.Settings;

internal static class WorkbenchPreferencesMigration
{
    internal static WorkbenchPreferences Upgrade(WorkbenchPreferences value)
    {
        if (value.Version != 1 || value.ShortcutBindings.IsDefault)
        {
            return value;
        }

        ShortcutConfiguration.Validate(value.ShortcutBindings);
        var present = value.ShortcutBindings.Select(binding => binding.Command).ToHashSet();
        var commands = Enum.GetValues<WorkbenchCommand>();
        var legacy = commands.Where(command => command <= WorkbenchCommand.VIEW_TIMELINE);
        var previous = commands.Where(command => command <= WorkbenchCommand.LAYOUT_RESTORE_DEFAULT);
        var current = commands.Where(command => command <= WorkbenchCommand.VIEW_LOG);
        var subtitleDetails = commands.Where(command => command <= WorkbenchCommand.OPEN_SUBTITLE_DETAILS);
        var focusCommands = commands.Where(command => command <= WorkbenchCommand.END_TEXT_INPUT);
        var projectCommands = commands.Where(command => command <= WorkbenchCommand.CLOSE_PROJECT);
        var aboutCommands = commands.Where(command => command <= WorkbenchCommand.OPEN_ABOUT);
        var maskCommands = commands.Where(command => command <= WorkbenchCommand.VIEW_MASKS);
        var clipCommands = commands.Where(command => command <= WorkbenchCommand.PASTE_CLIPS);
        var timingCommands = commands.Where(command => command <= WorkbenchCommand.INSERT_SUBTITLE_LINE_BREAK);
        var processorCommands = commands.Where(command => command <= WorkbenchCommand.APPLY_TIMING_POST_PROCESSOR);
        var mergeCommands = commands.Where(command => command <= WorkbenchCommand.MERGE_PROJECT);
        var clipBoundaryCommands = commands.Where(command => command <= WorkbenchCommand.SEEK_CLIP_END);
        if (!present.SetEquals(clipBoundaryCommands) && !present.SetEquals(mergeCommands) && !present.SetEquals(processorCommands) && !present.SetEquals(timingCommands) && !present.SetEquals(clipCommands) && !present.SetEquals(maskCommands) && !present.SetEquals(aboutCommands) && !present.SetEquals(legacy) && !present.SetEquals(previous) && !present.SetEquals(current) && !present.SetEquals(subtitleDetails) && !present.SetEquals(focusCommands) && !present.SetEquals(projectCommands))
        {
            return value;
        }

        var occupied = value.ShortcutBindings.Select(binding => ShortcutConfiguration.Parse(binding.Gesture, OperatingSystem.IsMacOS()))
            .Where(gesture => gesture is not null).Select(gesture => (gesture!.Key, gesture.KeyModifiers)).ToHashSet();
        var additions = ShortcutDefaults.CreateBindings().Where(binding => !present.Contains(binding.Command))
            .Select(binding =>
            {
                var gesture = ShortcutConfiguration.Parse(binding.Gesture, OperatingSystem.IsMacOS());
                return gesture is not null && !occupied.Add((gesture.Key, gesture.KeyModifiers))
                    ? binding with { Gesture = string.Empty }
                    : binding;
            });
        return value with { ShortcutBindings = value.ShortcutBindings.Concat(additions).ToImmutableArray() };
    }
}
