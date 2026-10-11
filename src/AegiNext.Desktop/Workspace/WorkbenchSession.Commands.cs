using AegiNext.Core.Timing;
using AegiNext.Desktop.Shortcuts;
using AegiNext.Media.Playback;
using AegiNext.Media.Audio;

namespace AegiNext.Desktop.Workspace;

internal sealed partial class WorkbenchSession
{
    internal bool CanExecuteCommand(WorkbenchCommand command)
    {
        if (command == WorkbenchCommand.CHECK_UPDATES)
        {
            return !closing;
        }
        if (closing || IsSwitchingAudioDevice || workflow.IsNewProjectDialogOpen || IsProjectBusy && command != WorkbenchCommand.VIEW_LOG &&
            !(command == WorkbenchCommand.TIMING_EXIT && pendingTimingEntry is not null && pendingTimingEnd is null))
        {
            return false;
        }

        return command switch
        {
            WorkbenchCommand.APPLY_TIMING_POST_PROCESSOR => HasApplicableSelectedTimingPostProcessor,
            WorkbenchCommand.COPY_CLIPS => CanCopyTimelineClips,
            WorkbenchCommand.PASTE_CLIPS => CanPasteTimelineClips,
            WorkbenchCommand.UNDO => editor.CanUndo,
            WorkbenchCommand.REDO => editor.CanRedo,
            WorkbenchCommand.AUDITION_BEFORE_SUBTITLE or WorkbenchCommand.AUDITION_AFTER_SUBTITLE or
                WorkbenchCommand.AUDITION_SUBTITLE_BEGIN or WorkbenchCommand.AUDITION_SUBTITLE => CanAuditionSubtitle,
            WorkbenchCommand.ADVANCE_SUBTITLE_ROW or WorkbenchCommand.INSERT_SUBTITLE_LINE_BREAK => false,
            WorkbenchCommand.SEEK_CLIP_START or WorkbenchCommand.SEEK_CLIP_END => SelectedLayer is not null &&
                controller.Snapshot.Error is null && controller.Snapshot.State is VideoPlaybackState.PAUSED or VideoPlaybackState.PLAYING or VideoPlaybackState.ENDED,
            WorkbenchCommand.PLAY_PAUSE or WorkbenchCommand.SEEK_BACKWARD or WorkbenchCommand.SEEK_FORWARD =>
                controller.Snapshot.Error is null && controller.Snapshot.State is VideoPlaybackState.PAUSED or VideoPlaybackState.PLAYING or VideoPlaybackState.ENDED,
            WorkbenchCommand.TIMING_ENTER => IsTimingClockAvailable && CurrentTrackId.HasValue && playback.PendingPosition is null && controller.Snapshot.Error is null && controller.Snapshot.State is VideoPlaybackState.PAUSED or VideoPlaybackState.PLAYING or VideoPlaybackState.ENDED,
            WorkbenchCommand.TIMING_EXIT => IsTimingClockAvailable && (timingSession.ActiveCueId is not null || pendingTimingEntry is not null),
            WorkbenchCommand.DELETE_SUBTITLE or WorkbenchCommand.SPLIT_SUBTITLE or WorkbenchCommand.OPEN_SUBTITLE_DETAILS => SelectedCue is not null,
            WorkbenchCommand.MERGE_SUBTITLE => CanMergeSubtitleSelection(),
            WorkbenchCommand.ADD_SUBTITLE => CurrentTrackId.HasValue,
            WorkbenchCommand.EXPORT_VIDEO => editor.Snapshot.Media is not null && export.CanStart,
            _ => true
        };
    }

    private bool IsTimingClockAvailable
    {
        get
        {
            var snapshot = controller.Snapshot;
            if (snapshot.AudioError is not null)
            {
                return false;
            }
            if (controller.MediaInfo?.AudioStreamIndex is null)
            {
                return true;
            }
            return snapshot.AudioAvailable && controller.AudioClock is
                { Quality: AudioClockQuality.ESTIMATED or AudioClockQuality.SYSTEM };
        }
    }

    internal async Task ExecuteCommandAsync(WorkbenchCommand command)
    {
        if (!CanExecuteCommand(command))
        {
            return;
        }

        if (command == WorkbenchCommand.CHECK_UPDATES)
        {
            await ViewModel.RequestHostCommandAsync(command);
            return;
        }

        var pausePlayback = command == WorkbenchCommand.PLAY_PAUSE && IsTransportPlaybackRequested;
        if (command == WorkbenchCommand.PLAY_PAUSE)
        {
            CancelInteractiveSeeking();
        }

        MediaTime? timingPosition = null;
        if (command is WorkbenchCommand.TIMING_ENTER or WorkbenchCommand.TIMING_EXIT)
        {
            if (!IsTimingClockAvailable)
            {
                return;
            }
            var timingSnapshot = controller.Snapshot;
            if (timingSnapshot.AudioError is not null)
            {
                return;
            }
            timingPosition = (playback.PendingPosition ?? timingSnapshot.Position) - (timingSnapshot.Start ?? MediaTime.Zero);
        }
        if (command == WorkbenchCommand.TIMING_EXIT && pendingTimingEntry is not null)
        {
            if (ViewModel.Timeline.TimingPreview is { } preview && timingPreviewTrackId is { } trackId)
            {
                pendingTimingEnd = ResolveTimingEnd(preview.CueId, trackId, preview.Start, timingPosition!.Value);
                ClearTimingPreview();
            }
            ViewModel.RefreshCommands();
            return;
        }

        if (command is not (WorkbenchCommand.TIMING_ENTER or WorkbenchCommand.TIMING_EXIT))
        {
            InvalidateTimingSession();
        }

        await RunCommandAsync(async () =>
        {
            LogInfo("Command", command.ToString());
            switch (command)
            {
                case WorkbenchCommand.APPLY_TIMING_POST_PROCESSOR:
                    await ApplySelectedTimingPostProcessorAsync();
                    break;
                case WorkbenchCommand.NEW_PROJECT: await workflow.NewProjectAsync(); break;
                case WorkbenchCommand.OPEN_PROJECT: await workflow.OpenProjectAsync(); break;
                case WorkbenchCommand.MERGE_PROJECT: await workflow.MergeProjectsAsync(); break;
                case WorkbenchCommand.SAVE_PROJECT: await workflow.SaveProjectAsync(false); break;
                case WorkbenchCommand.SAVE_PROJECT_AS: await workflow.SaveProjectAsync(true); break;
                case WorkbenchCommand.OPEN_MEDIA:
                    var path = await dialogs.OpenFileAsync("Open", "Videos", VideoFileTypes.Patterns);
                    if (path is not null && !closing)
                    {
                        await workflow.OpenMediaAsync(path, true);
                    }
                    break;
                case WorkbenchCommand.IMPORT_SUBTITLES: await workflow.ImportSubtitlesAsync(); break;
                case WorkbenchCommand.EXPORT_SUBTITLES: await workflow.ExportSubtitlesAsync(); break;
                case WorkbenchCommand.IMPORT_ASS: await workflow.ImportSubtitlesAsync(true); break;
                case WorkbenchCommand.EXPORT_ASS: await workflow.ExportSubtitlesAsync(true); break;
                case WorkbenchCommand.EXPORT_VIDEO: await export.EncodeAsync(); break;
                case WorkbenchCommand.PLAY_PAUSE:
                    ViewModel.Timeline.ResumePlaybackFollow();
                    TryCommitDrafts(false);
                    ViewModel.CancelGestures();
                    ClearKeyframeSelection();
                    var playbackSnapshot = controller.Snapshot;
                    if (playbackSnapshot.AudioAuditionActive)
                    {
                        await controller.ClearPlaybackRangeAsync();
                    }
                    else if (pausePlayback)
                    {
                        await controller.PauseAsync();
                    }
                    else
                    {
                        if (playbackSnapshot.State == VideoPlaybackState.ENDED)
                        {
                            var mediaStart = playbackSnapshot.Start ?? MediaTime.Zero;
                            var resumePosition = playbackSnapshot.PlaybackRangeInstalled &&
                                (playbackSnapshot.Duration is not { } mediaDuration || playbackSnapshot.Position < mediaStart + mediaDuration)
                                ? playbackSnapshot.Position : mediaStart;
                            await SeekFromUserAsync(resumePosition);
                        }
                        await controller.PlayAsync();
                    }
                    break;
                case WorkbenchCommand.SEEK_BACKWARD: await SeekRelativeAsync(-5); break;
                case WorkbenchCommand.SEEK_FORWARD: await SeekRelativeAsync(5); break;
                case WorkbenchCommand.SEEK_CLIP_START:
                    await SeekSelectedClipBoundaryAsync(false);
                    break;
                case WorkbenchCommand.SEEK_CLIP_END:
                    await SeekSelectedClipBoundaryAsync(true);
                    break;
                case WorkbenchCommand.AUDITION_BEFORE_SUBTITLE:
                case WorkbenchCommand.AUDITION_AFTER_SUBTITLE:
                case WorkbenchCommand.AUDITION_SUBTITLE_BEGIN:
                case WorkbenchCommand.AUDITION_SUBTITLE:
                    await PlaySubtitleAuditionAsync(command);
                    break;
                case WorkbenchCommand.UNDO:
                    if (TryCommitDrafts())
                    {
                        ViewModel.CancelGestures();
                        ResetTiming();
                        editor.Undo();
                    }
                    break;
                case WorkbenchCommand.REDO:
                    if (TryCommitDrafts())
                    {
                        ViewModel.CancelGestures();
                        ResetTiming();
                        editor.Redo();
                    }
                    break;
                case WorkbenchCommand.TIMING_ENTER:
                    if (TryCommitDrafts())
                    {
                        await SetCueStartAsync(timingPosition!.Value);
                    }
                    break;
                case WorkbenchCommand.TIMING_EXIT:
                    if (TryCommitDrafts())
                    {
                        SetCueEndAt(timingPosition!.Value);
                    }
                    break;
                case WorkbenchCommand.ADD_SUBTITLE:
                    if (TryCommitDrafts())
                    {
                        await AddCueAsync();
                    }
                    break;
                case WorkbenchCommand.DELETE_SUBTITLE:
                    if (TryCommitDrafts() && SelectedCue is not null)
                    {
                        var subtitleIds = SelectedSubtitleIds.ToHashSet();
                        var layerIds = editor.Snapshot.Layers
                            .Where(layer => layer.SubtitleId is { } subtitleId && subtitleIds.Contains(subtitleId))
                            .Select(layer => layer.Id).ToArray();
                        editor.RemoveClips(layerIds);
                        ResetTiming();
                    }
                    break;
                case WorkbenchCommand.SPLIT_SUBTITLE:
                    if (TryCommitDrafts())
                    {
                        SplitCue();
                        ResetTiming();
                    }
                    break;
                case WorkbenchCommand.MERGE_SUBTITLE:
                    if (TryCommitDrafts())
                    {
                        MergeCue();
                        ResetTiming();
                    }
                    break;
                default:
                    await ViewModel.RequestHostCommandAsync(command);
                    break;
            }
        });
    }

    internal Task CancelExportAsync()
    {
        export.Cancel();
        return Task.CompletedTask;
    }

    internal void ResetTiming()
    {
        FreezeTimingPreview();
        ViewModel.RefreshCommands();
    }

    internal void InvalidateTimingSession()
    {
        if (timingSession.ActiveCueId is not null || pendingTimingEntry is not null || timingPreviewFollowing)
        {
            ResetTiming();
        }
    }
}
