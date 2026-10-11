using AegiNext.Desktop.Shortcuts;

namespace AegiNext.Desktop.Settings.Shortcuts;

/// <summary>快捷键设置的功能分区与显示顺序，不改变命令路由或配置存储。</summary>
internal static class ShortcutSettingsSections
{
    internal static ShortcutSettingsListItem[] CreateItems(IEnumerable<ShortcutSettingRow> rows)
    {
        var remaining = rows.ToDictionary(row => row.Command);
        var items = new List<ShortcutSettingsListItem>();
        AddSection("Settings.ShortcutSectionProjectFiles",
        [
            WorkbenchCommand.NEW_PROJECT, WorkbenchCommand.OPEN_PROJECT, WorkbenchCommand.MERGE_PROJECT,
            WorkbenchCommand.CLOSE_PROJECT,
            WorkbenchCommand.SAVE_PROJECT, WorkbenchCommand.SAVE_PROJECT_AS, WorkbenchCommand.OPEN_MEDIA,
            WorkbenchCommand.IMPORT_SUBTITLES, WorkbenchCommand.IMPORT_ASS,
            WorkbenchCommand.EXPORT_SUBTITLES, WorkbenchCommand.EXPORT_ASS, WorkbenchCommand.EXPORT_VIDEO
        ]);
        AddSection("Settings.ShortcutSectionEditing",
        [
            WorkbenchCommand.UNDO, WorkbenchCommand.REDO, WorkbenchCommand.COPY_CLIPS,
            WorkbenchCommand.PASTE_CLIPS, WorkbenchCommand.END_TEXT_INPUT
        ]);
        AddSection("Settings.ShortcutSectionSubtitlesTiming",
        [
            WorkbenchCommand.ADD_SUBTITLE, WorkbenchCommand.DELETE_SUBTITLE, WorkbenchCommand.SPLIT_SUBTITLE,
            WorkbenchCommand.MERGE_SUBTITLE, WorkbenchCommand.OPEN_SUBTITLE_DETAILS,
            WorkbenchCommand.ADVANCE_SUBTITLE_ROW, WorkbenchCommand.INSERT_SUBTITLE_LINE_BREAK,
            WorkbenchCommand.TIMING_ENTER, WorkbenchCommand.TIMING_EXIT, WorkbenchCommand.APPLY_TIMING_POST_PROCESSOR
        ]);
        AddSection("Settings.ShortcutSectionPlaybackAudition",
        [
            WorkbenchCommand.PLAY_PAUSE, WorkbenchCommand.SEEK_BACKWARD, WorkbenchCommand.SEEK_FORWARD,
            WorkbenchCommand.SEEK_CLIP_START, WorkbenchCommand.SEEK_CLIP_END,
            WorkbenchCommand.AUDITION_BEFORE_SUBTITLE, WorkbenchCommand.AUDITION_AFTER_SUBTITLE,
            WorkbenchCommand.AUDITION_SUBTITLE_BEGIN, WorkbenchCommand.AUDITION_SUBTITLE
        ]);
        AddSection("Settings.ShortcutSectionPanelViews",
        [
            WorkbenchCommand.VIEW_SUBTITLES, WorkbenchCommand.VIEW_TIMELINE, WorkbenchCommand.VIEW_PREVIEW,
            WorkbenchCommand.VIEW_STYLES, WorkbenchCommand.VIEW_EFFECTS, WorkbenchCommand.VIEW_MASKS,
            WorkbenchCommand.VIEW_EXPORT, WorkbenchCommand.VIEW_LOG
        ]);
        AddSection("Settings.ShortcutSectionLayouts",
        [
            WorkbenchCommand.LAYOUT_SAVE, WorkbenchCommand.LAYOUT_SAVE_AS,
            WorkbenchCommand.LAYOUT_MANAGE, WorkbenchCommand.LAYOUT_RESTORE_DEFAULT
        ]);
        AddSection("Settings.ShortcutSectionApplication",
        [
            WorkbenchCommand.OPEN_SETTINGS, WorkbenchCommand.CHECK_UPDATES, WorkbenchCommand.OPEN_ABOUT, WorkbenchCommand.EXIT
        ]);
        if (remaining.Count != 0)
        {
            throw new InvalidOperationException("快捷键设置存在未分区的命令。");
        }

        return items.ToArray();

        void AddSection(string titleKey, WorkbenchCommand[] commands)
        {
            items.Add(new(titleKey));
            foreach (var command in commands)
            {
                if (!remaining.Remove(command, out var row))
                {
                    throw new InvalidOperationException($"快捷键分区中的命令缺失或重复：{command}。");
                }

                items.Add(new(row));
            }
        }
    }
}
