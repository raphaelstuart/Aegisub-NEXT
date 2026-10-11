using AegiNext.Core.Projects;

namespace AegiNext.Desktop.Workspace;

internal sealed partial class WorkbenchSession
{
    private TimelineViewState timelineViewState = new();
    private TimelineViewState savedTimelineViewState = new();
    private readonly HashSet<TimelineAnimationRowId> knownTimelineAnimationRows = [];

    internal TimelineViewState TimelineViewState => timelineViewState;
    internal bool HasUnsavedChanges => HasProjectDrafts || editor.HasUnsavedChanges ||
        !TimelineViewStatesEqual(timelineViewState, savedTimelineViewState);

    internal void SetTimelineAnimationRowCollapsed(TimelineAnimationRowId id, bool isCollapsed)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (closing || IsProjectBusy)
        {
            return;
        }

        knownTimelineAnimationRows.Add(id);
        var rows = timelineViewState.CollapsedAnimationRows;
        if (rows.Contains(id) == isCollapsed)
        {
            return;
        }

        ApplyTimelineViewState(timelineViewState with
        {
            CollapsedAnimationRows = isCollapsed ? rows.Add(id) : rows.Remove(id)
        });
    }

    internal void SetTimelineTrackCollapsed(Guid id, bool isCollapsed)
    {
        if (closing || IsProjectBusy || id == Guid.Empty || !GetTimelineTrackIds().Contains(id))
        {
            return;
        }

        var ids = timelineViewState.CollapsedTrackIds;
        if (ids.Contains(id) == isCollapsed)
        {
            return;
        }

        ApplyTimelineViewState(timelineViewState with
        {
            CollapsedTrackIds = isCollapsed ? ids.Add(id) : ids.Remove(id)
        });
    }

    internal void SetAllTimelineTracksCollapsed(bool isCollapsed)
    {
        if (closing || IsProjectBusy)
        {
            return;
        }

        ApplyTimelineViewState(timelineViewState with
        {
            CollapsedTrackIds = isCollapsed
                ? [.. timelineViewState.CollapsedTrackIds.Union(GetTimelineTrackIds())] : []
        });
    }

    internal IEnumerable<Guid> GetTimelineTrackIds() => DocumentSnapshot.Tracks.Select(track => track.Id);

    private void ApplyTimelineViewState(TimelineViewState state)
    {
        var next = NormalizeTimelineViewState(state);
        if (TimelineViewStatesEqual(timelineViewState, next))
        {
            return;
        }

        timelineViewState = next;
        ViewModel.Timeline.TimelineViewState = next;
        RefreshTitle();
        ViewModel.RefreshCommands();
    }

    internal void ResetTimelineViewState(ProjectDocument document)
    {
        var state = NormalizeTimelineViewState(document.TimelineViewState);
        knownTimelineAnimationRows.Clear();
        knownTimelineAnimationRows.UnionWith(GetTimelineAnimationRows(document));
        knownTimelineAnimationRows.UnionWith(state.CollapsedAnimationRows);
        timelineViewState = state;
        savedTimelineViewState = state;
        ViewModel.Timeline.TimelineViewState = state;
    }

    private void InitializeNewTimelineAnimationRows(ProjectDocument document)
    {
        var added = GetTimelineAnimationRows(document).Where(knownTimelineAnimationRows.Add).ToArray();
        if (added.Length == 0)
        {
            return;
        }

        ApplyTimelineViewState(timelineViewState with
        {
            CollapsedAnimationRows = [.. timelineViewState.CollapsedAnimationRows.Union(added)]
        });
    }

    private static IEnumerable<TimelineAnimationRowId> GetTimelineAnimationRows(ProjectDocument document) =>
        document.Layers.SelectMany(layer => layer.Tracks
            .Where(track => !track.Keyframes.IsEmpty || !track.Transforms.IsEmpty)
            .Select(track => new TimelineAnimationRowId(TimelineRowScope.TRACK, layer.TrackId, track.Property,
                track.Target.TextRangeId, track.Target.State)));

    internal ProjectDocument CreatePersistenceSnapshot(ProjectDocument contentSnapshot)
    {
        return TimelineViewStatesEqual(contentSnapshot.TimelineViewState, timelineViewState)
            ? contentSnapshot : contentSnapshot with { TimelineViewState = timelineViewState };
    }

    internal void AcceptProjectSave(ProjectDocument contentSnapshot, ProjectDocument persistedSnapshot, bool resetContent = false)
    {
        var savedState = NormalizeTimelineViewState(persistedSnapshot.TimelineViewState);
        var persistedContent = persistedSnapshot with { TimelineViewState = contentSnapshot.TimelineViewState };
        if (persistedContent == contentSnapshot)
        {
            persistedContent = contentSnapshot;
        }

        if (resetContent)
        {
            editor.Reset(persistedContent);
        }
        else
        {
            editor.MarkSaved(contentSnapshot, persistedContent);
        }

        savedTimelineViewState = savedState;
        RefreshTitle();
        ViewModel.RefreshCommands();
    }

    internal void AcceptRelocatedProjectSave(ProjectDocument expectedCurrent, ProjectDocument relocatedCurrent,
        ProjectDocument originalSaveSnapshot, ProjectDocument relocatedSaveSnapshot)
    {
        var savedState = NormalizeTimelineViewState(relocatedSaveSnapshot.TimelineViewState);
        var persistedContent = relocatedSaveSnapshot with { TimelineViewState = originalSaveSnapshot.TimelineViewState };
        var currentContent = relocatedCurrent with { TimelineViewState = expectedCurrent.TimelineViewState };
        if (ReferenceEquals(expectedCurrent, originalSaveSnapshot))
        {
            currentContent = persistedContent;
        }
        editor.AcceptRelocatedSave(expectedCurrent, currentContent, originalSaveSnapshot, persistedContent);
        savedTimelineViewState = savedState;
        RefreshTitle();
        ViewModel.RefreshCommands();
    }

    private void RefreshRelocatedDocument()
    {
        MaskEditing.RebindRelocatedSource(editor.Snapshot);
        ViewModel.Effects.RebindRelocatedSource(editor.Snapshot);
        RefreshDocument(preserveDrafts: true);
    }

    private static TimelineViewState NormalizeTimelineViewState(TimelineViewState state)
    {
        state.Validate();
        var sorted = state.CollapsedAnimationRows.OrderBy(row => row.Scope).ThenBy(row => row.OwnerId)
            .ThenBy(row => row.Property).ToArray();
        var sortedTracks = state.CollapsedTrackIds.Order().ToArray();
        return state.CollapsedAnimationRows.SequenceEqual(sorted) && state.CollapsedTrackIds.SequenceEqual(sortedTracks)
            ? state : state with { CollapsedAnimationRows = [.. sorted], CollapsedTrackIds = [.. sortedTracks] };
    }

    private static bool TimelineViewStatesEqual(TimelineViewState first, TimelineViewState second) =>
        first.CollapsedAnimationRows.SequenceEqual(second.CollapsedAnimationRows) &&
        first.CollapsedTrackIds.SequenceEqual(second.CollapsedTrackIds);
}
