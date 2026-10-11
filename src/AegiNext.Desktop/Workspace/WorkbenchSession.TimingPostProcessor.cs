using System.Collections.Immutable;
using AegiNext.Application.Tasks;
using AegiNext.Core.Media;
using AegiNext.Core.Presets;
using AegiNext.Core.Projects;
using AegiNext.Core.Timing;
using AegiNext.Desktop.I18n;
using AegiNext.Media.Probing;

namespace AegiNext.Desktop.Workspace;

internal sealed partial class WorkbenchSession
{
    private readonly Func<string, int, MediaTime, CancellationToken, Task<VideoTimingIndex>> videoTimingProbe;
    private Task timingProcessingCompletion = Task.CompletedTask;
    private VideoTimingCacheEntry? videoTimingCache;
    private SubtitleTimingAssociationCacheEntry? timingAssociationCache;

    private void OnTimingLibrariesBusyChanged(object? sender, EventArgs e)
    {
        if (!closing)
        {
            ViewModel.RefreshCommands();
        }
    }

    internal bool HasApplicableSelectedTimingPostProcessor
    {
        get
        {
            if (closing || IsProjectBusy || styles.IsBusy)
            {
                return false;
            }
            var clips = ClipIndex;
            var associations = TimingAssociations;
            if (SelectedLayerId is { } primary && HasApplicableTimingAssociation(clips, associations, primary))
            {
                return true;
            }
            foreach (var id in ViewModel.Effects.SelectedIds)
            {
                if (id != SelectedLayerId && HasApplicableTimingAssociation(clips, associations, id))
                {
                    return true;
                }
            }
            return false;
        }
    }

    private IReadOnlyDictionary<Guid, SubtitleStylePreset> TimingAssociations
    {
        get
        {
            var clips = ClipIndex;
            var presets = styleLibrary.Snapshot;
            if (timingAssociationCache is null || !ReferenceEquals(timingAssociationCache.Document, clips.Document) ||
                !ReferenceEquals(timingAssociationCache.Presets, presets))
            {
                timingAssociationCache = new(clips.Document, presets,
                    SubtitleTimingAssociationResolver.Resolve(clips, presets.Presets));
            }
            return timingAssociationCache.Associations;
        }
    }

    private static bool HasApplicableTimingAssociation(ProjectClipIndex clips,
        IReadOnlyDictionary<Guid, SubtitleStylePreset> associations, Guid clipId)
    {
        return clips.TryGetClip(clipId, out var layer) && layer.Kind == LayerKind.SUBTITLE &&
            layer.SubtitleId is { } subtitleId && associations.TryGetValue(subtitleId, out var preset) &&
            preset.TimingPostProcessor is { } options && HasTimingStages(options);
    }

    internal Task<int> ApplySelectedTimingPostProcessorAsync(CancellationToken cancellationToken = default)
    {
        return StartTimingProcessing(null, null, true, cancellationToken);
    }

    internal Task<int> ApplyTimingPostProcessorAsync(TimingPostProcessorOptions options,
        IReadOnlySet<string> styleNames, bool onlySelected, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(styleNames);
        options.Validate();
        return StartTimingProcessing(options, styleNames, onlySelected, cancellationToken);
    }

    private Task<int> StartTimingProcessing(TimingPostProcessorOptions? options,
        IReadOnlySet<string>? styleNames, bool onlySelected, CancellationToken cancellationToken)
    {
        if (!timingProcessingCompletion.IsCompleted || IsProjectBusy || closing || IsUpdating || styles.IsBusy)
        {
            return Task.FromResult(0);
        }

        try
        {
            PrepareTimingProcessing(cancellationToken);
            var source = editor.Snapshot;
            var selected = options is null ? SelectedTimelineSubtitleIds(source).ToImmutableHashSet() :
                onlySelected ? SelectedSubtitleIds.ToImmutableHashSet() : null;
            var task = new TimingPostProcessingTask(this, source, TaskInputRevision, options,
                styleNames?.ToImmutableHashSet(StringComparer.Ordinal),
                selected,
                options is null ? TimingAssociations.Where(pair => selected!.Contains(pair.Key) &&
                    HasTimingStages(pair.Value.TimingPostProcessor!))
                    .ToDictionary() : null);
            var handle = applicationContext.Tasks.Submit(task);
            timingProcessingCompletion = handle.Completion;
            return handle.WaitAsync(cancellationToken);
        }
        catch (Exception error)
        {
            return Task.FromException<int>(error);
        }
    }

    internal async Task<int> ExecuteTimingProcessingAsync(ProjectDocument source, long inputRevision,
        TimingPostProcessorOptions? options, IReadOnlySet<string>? styleNames, IReadOnlySet<Guid>? selected,
        IReadOnlyDictionary<Guid, SubtitleStylePreset>? associations, AegiTaskExecutionContext context)
    {
        if (associations is { Count: 0 })
        {
            return 0;
        }
        var requestKeyframes = options?.KeyframeSnapEnabled ?? associations!.Values.Any(value => value.TimingPostProcessor!.KeyframeSnapEnabled);
        var skippedKeyframes = 0;
        var count = await ProcessTimingSnapshotAsync(source, inputRevision, requestKeyframes, (available, index) =>
        {
            if (options is not null)
            {
                return AegiNext.Application.Timing.TimingPostProcessor.Process(source,
                    options with { KeyframeSnapEnabled = available }, styleNames!, selected, index);
            }
            available = available && index is { Keyframes.IsEmpty: false };
            skippedKeyframes = available ? 0 : associations!.Values.Count(value => value.TimingPostProcessor!.KeyframeSnapEnabled);
            var result = AegiNext.Application.Timing.TimingPostProcessor.Process(source,
                associations!.ToDictionary(pair => pair.Key, pair => pair.Value.TimingPostProcessor!), index,
                skipUnavailableKeyframes: !available);
            var presets = associations!;
            if (!result.Subtitles.Any(line => presets.TryGetValue(line.Id, out var preset) && line.StylePresetId != preset.Id))
            {
                return result;
            }
            return result with
            {
                Subtitles = result.Subtitles.Select(line => presets.TryGetValue(line.Id, out var preset) && line.StylePresetId != preset.Id
                    ? line with { StylePresetId = preset.Id } : line).ToImmutableArray()
            };
        }, context);
        if (associations is not null)
        {
            LogInfo("Timing", Localization.Format("WorkflowLog.TimingPostProcessorCompleted", count,
                associations.Count, selected!.Count - associations.Count));
            if (skippedKeyframes > 0)
            {
                LogInfo("Timing", Localization.Format("WorkflowLog.TimingPostProcessorKeyframesSkipped", skippedKeyframes));
            }
        }
        return count;
    }

    private void PrepareTimingProcessing(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryCommitDrafts())
        {
            throw new InvalidOperationException(Localization.Get("Settings.TimingDraftInvalid"));
        }

        InvalidateTimingSession();
        ViewModel.CancelGestures();
    }

    private HashSet<Guid> SelectedTimelineSubtitleIds(ProjectDocument source)
    {
        var clips = TimelineClipIds().ToHashSet();
        return source.Layers.Where(layer => layer.Kind == LayerKind.SUBTITLE && clips.Contains(layer.Id) &&
            layer.SubtitleId.HasValue).Select(layer => layer.SubtitleId!.Value).ToHashSet();
    }

    private static bool HasTimingStages(TimingPostProcessorOptions options)
    {
        return options.LeadInEnabled || options.LeadOutEnabled || options.AdjacencyEnabled || options.KeyframeSnapEnabled;
    }

    private async Task<int> ProcessTimingSnapshotAsync(ProjectDocument source, long inputRevision, bool requestKeyframes,
        Func<bool, VideoTimingIndex?, ProjectDocument> process, AegiTaskExecutionContext context)
    {
        var generation = projectGeneration;
        var mediaSnapshot = controller.Snapshot;
        var media = controller.MediaInfo;
        var keyframesAvailable = requestKeyframes && mediaSnapshot.FilePath is not null &&
            !mediaSnapshot.IsOpening && mediaSnapshot.Error is null && media is not null;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, ProjectOperationsToken);
        context.ReportProgress(new("Tasks.TimingIndex"));
        try
        {
            VideoTimingIndex? index = null;
            VideoTimingCacheEntry? indexIdentity = null;
            if (keyframesAvailable)
            {
                var path = mediaSnapshot.FilePath!;
                var streamIndex = media!.VideoStreamIndex;
                var origin = media.Start ?? MediaTime.Zero;
                var file = new FileInfo(path);
                var fileLength = file.Exists ? (long?)file.Length : null;
                var lastWriteTime = file.Exists ? (DateTime?)file.LastWriteTimeUtc : null;
                var creationTime = file.Exists ? (DateTime?)file.CreationTimeUtc : null;
                if (videoTimingCache is { } cached && cached.Path == path && cached.StreamIndex == streamIndex &&
                    cached.Origin == origin && cached.Epoch == mediaSnapshot.Epoch && cached.FileLength == fileLength &&
                    cached.LastWriteTime == lastWriteTime && cached.CreationTime == creationTime)
                {
                    index = cached.Index;
                    indexIdentity = cached;
                }
                else
                {
                    videoTimingCache = null;
                    index = await videoTimingProbe(path, streamIndex, origin, lifetime.Token);
                    lifetime.Token.ThrowIfCancellationRequested();
                    file.Refresh();
                    if ((file.Exists ? (long?)file.Length : null) != fileLength ||
                        (file.Exists ? (DateTime?)file.LastWriteTimeUtc : null) != lastWriteTime ||
                        (file.Exists ? (DateTime?)file.CreationTimeUtc : null) != creationTime)
                    {
                        throw new OperationCanceledException(Localization.Get("Settings.TimingContextChanged"), lifetime.Token);
                    }

                    indexIdentity = new(path, streamIndex, origin, mediaSnapshot.Epoch, fileLength, lastWriteTime, creationTime, index);
                }
            }

            context.ReportProgress(new("Tasks.TimingCalculation"));
            var result = await Task.Run(() => process(keyframesAvailable, index), lifetime.Token);
            lifetime.Token.ThrowIfCancellationRequested();
            if (indexIdentity is { } identity)
            {
                var file = new FileInfo(identity.Path);
                if ((file.Exists ? (long?)file.Length : null) != identity.FileLength ||
                    (file.Exists ? (DateTime?)file.LastWriteTimeUtc : null) != identity.LastWriteTime ||
                    (file.Exists ? (DateTime?)file.CreationTimeUtc : null) != identity.CreationTime)
                {
                    videoTimingCache = null;
                    throw new OperationCanceledException(Localization.Get("Settings.TimingContextChanged"), lifetime.Token);
                }

                videoTimingCache = identity;
            }

            if (closing || generation != projectGeneration || inputRevision != TaskInputRevision ||
                HasProjectDrafts || SceneEditing.GestureTarget is not null || !ReferenceEquals(source, editor.Snapshot) ||
                keyframesAvailable && (controller.Snapshot.Epoch != mediaSnapshot.Epoch ||
                    controller.Snapshot.FilePath != mediaSnapshot.FilePath))
            {
                throw new OperationCanceledException(Localization.Get("Settings.TimingContextChanged"), lifetime.Token);
            }

            var count = source.Subtitles.Zip(result.Subtitles).Count(pair =>
                pair.First.Start != pair.Second.Start || pair.First.End != pair.Second.End);
            using var editLease = context.AcquireEditLease();
            context.EnterCommit(() => !closing && generation == projectGeneration && inputRevision == TaskInputRevision &&
                !HasProjectDrafts && SceneEditing.GestureTarget is null && ReferenceEquals(source, editor.Snapshot));
            editor.Apply("Timing post-processor", _ => result);
            return count;
        }
        finally
        {
            ViewModel.RefreshCommands();
        }
    }

    private static Task<VideoTimingIndex> ProbeVideoTimingAsync(string path, int streamIndex, MediaTime origin,
        CancellationToken cancellationToken)
    {
        var probe = new FfprobeVideoTimingProbe(new(MediaToolchain.ResolveFfprobe(), TimeSpan.FromMinutes(10),
            maximumOutputCharacters: 128 * 1024 * 1024));
        return probe.ProbeAsync(path, streamIndex, origin, cancellationToken);
    }
}
