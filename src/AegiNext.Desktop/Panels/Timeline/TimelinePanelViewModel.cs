using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AegiNext.Desktop.Workspace;
using AegiNext.Desktop.Editing;
using AegiNext.Desktop.Controls;
using AegiNext.Core.Projects;
using AegiNext.Core.Timing;
using AegiNext.Media.Analysis;
using Avalonia.Media;

namespace AegiNext.Desktop.Panels.Timeline;

internal sealed partial class TimelinePanelViewModel : ObservableObject
{
    private readonly WorkbenchSession session;
    private ProjectDocument document = new();
    private TimelineViewState timelineViewState = new();
    private Guid? selectedCueId;
    private Guid? pendingCenterCueId;
    private Guid? selectedTrackId;
    private Guid? renamingTrackId;
    private string trackNameDraft = string.Empty;
    private ProjectLayer? selectedLayer;
    private TimelineTimingPreview? timingPreview;
    private bool isClassicTimingEnabled;
    private Guid? selectedMaskNodeId;
    private MediaTime position = MediaTime.Zero;
    private TimelineViewport viewport = new();
    private double mediaDuration;
    private double documentDuration = 10;
    private IReadOnlyList<Guid> selectedLayerIds = [];
    private double scrollMaximum = 1;
    private double viewportSize = 10;
    private bool isSeeking;
    private bool isSnapEnabled = true;
    private bool isStepEnabled;
    private bool isSpectrumVisible = true;
    private bool isWaveformVisible = true;
    private AnimationTrackTarget effectTarget = new(AnimationProperty.OPACITY);
    private SpectrogramData? spectrogram;
    private SpectrogramData? spectrogramOverview;
    private WaveformData? waveform;
    private WaveformData? waveformOverview;
    private MediaTime audioDuration;
    private double renderScaling = 1;
    private string analysisStatus = string.Empty;
    private TimelineClipContextEventArgs? clipContext;
    private ProjectDocument? clipContextDocument;
    private Guid[] clipContextIds = [];
    private Guid? clipContextPrimary;
    private TimelineAnimationRowId? animationContext;
    private Guid? animationContextClipId;
    private ProjectDocument? animationContextDocument;
    private Guid[] animationContextIds = [];

    internal TimelinePanelViewModel(WorkbenchSession session)
    {
        this.session = session;
        ExpandAllTracksCommand = new(() => session.SetAllTimelineTracksCollapsed(false), () => CanExpandAllTracks);
        CollapseAllTracksCommand = new(() => session.SetAllTimelineTracksCollapsed(true), () => CanCollapseAllTracks);
        AddTrackCommand = new(() => session.RunCommandAsync(() => session.EditAsync(session.AddSubtitleTrack)));
        RenameTrackCommand = new(BeginRenameTrack, () => SelectedTrackId.HasValue);
        DeleteTrackCommand = new(session.RemoveSubtitleTrackAsync, () => CanDeleteTrack);
        MoveTrackUpCommand = new(() => session.MoveCurrentSubtitleTrackAsync(-1), () => CanMoveTrackUp);
        MoveTrackDownCommand = new(() => session.MoveCurrentSubtitleTrackAsync(1), () => CanMoveTrackDown);
        ConfirmTrackRenameCommand = new(ConfirmTrackRenameAsync, () => CanConfirmTrackRename);
        CancelTrackRenameCommand = new(CancelTrackRename);
        ApplyTrackStyleCommand = new(request => request is null
            ? Task.CompletedTask : session.ApplySubtitleTrackStyleAsync(request.TrackId, request.PresetId));
        ToggleTrackAutoStyleCommand = new(session.ToggleTrackAutoApplyStyleAsync);
        CopyClipsCommand = new(CopyContextClipsAsync, () => IsClipContextCurrent && clipContextIds.Length > 0);
        PasteClipsCommand = new(PasteContextClipsAsync, () => IsClipContextCurrent && session.CanPasteTimelineClips);
        DeleteClipsCommand = new(DeleteContextClipsAsync, () => IsClipContextCurrent && clipContextIds.Length > 0);
        ClearClipAnimationTracksCommand = new(ClearContextClipAnimationTracksAsync,
            () => IsClipContextCurrent && HasAnimationTracks(clipContextIds));
        ClearAnimationPropertyTracksCommand = new(ClearContextAnimationPropertyTracksAsync,
            () => IsAnimationContextCurrent && HasAnimationTracks(animationContextIds, animationContext!.Property));
        MoveClipsCommand = new(MoveContextClipsAsync, () => IsClipContextCurrent && clipContextIds.Length > 0);
        CreateSubtitleCommand = new(CreateContextSubtitleAsync, () => IsClipContextCurrent && clipContext?.TrackId is not null);
    }

    public ProjectDocument Document
    {
        get => document;
        set
        {
            if (SetProperty(ref document, value))
            {
                ValidateTrackSoloDocument(value);
                documentDuration = Math.Max(1, value.Layers.Select(layer => Seconds(layer.End))
                    .Concat(value.Subtitles.Select(cue => Seconds(cue.End))).DefaultIfEmpty(1).Max());
                OnPropertyChanged(nameof(FullDuration));
                RefreshTrackCommands();
                RefreshClipCommands();
            }
        }
    }

    public TimelineViewState TimelineViewState
    {
        get => timelineViewState;
        internal set
        {
            if (SetProperty(ref timelineViewState, value))
            {
                RefreshTrackCollapseCommands();
            }
        }
    }

    public Guid? SelectedMaskNodeId
    {
        get => selectedMaskNodeId;
        set => SetProperty(ref selectedMaskNodeId, value);
    }

    public Guid? SelectedTrackId
    {
        get => selectedTrackId;
        set
        {
            if (SetProperty(ref selectedTrackId, value))
            {
                CancelTrackRename();
                RefreshTrackCommands();
            }
        }
    }

    public AsyncRelayCommand AddTrackCommand { get; }
    public RelayCommand RenameTrackCommand { get; }
    public AsyncRelayCommand DeleteTrackCommand { get; }
    public AsyncRelayCommand MoveTrackUpCommand { get; }
    public AsyncRelayCommand MoveTrackDownCommand { get; }
    public AsyncRelayCommand ConfirmTrackRenameCommand { get; }
    public RelayCommand CancelTrackRenameCommand { get; }
    public AsyncRelayCommand<TrackStylePresetRequest> ApplyTrackStyleCommand { get; }
    public AsyncRelayCommand<Guid> ToggleTrackAutoStyleCommand { get; }
    public AsyncRelayCommand CopyClipsCommand { get; }
    public AsyncRelayCommand PasteClipsCommand { get; }
    public AsyncRelayCommand DeleteClipsCommand { get; }
    public AsyncRelayCommand ClearClipAnimationTracksCommand { get; }
    public AsyncRelayCommand ClearAnimationPropertyTracksCommand { get; }
    public AsyncRelayCommand MoveClipsCommand { get; }
    public AsyncRelayCommand CreateSubtitleCommand { get; }
    /// <summary>使用共享命令对实际选中的字幕片段执行关联的时间后续处理。</summary>
    public ICommand ApplyTimingPostProcessorCommand => session.ViewModel.GetCommand(
        AegiNext.Desktop.Shortcuts.WorkbenchCommand.APPLY_TIMING_POST_PROCESSOR);
    public bool CanCopyClips => session.CanCopyTimelineClips;
    public bool CanPasteClips => session.CanPasteTimelineClips;
    private bool IsClipContextCurrent => clipContext is not null && ReferenceEquals(Document, clipContextDocument) &&
        !session.IsClosing && !session.ViewModel.IsBusy;
    private bool IsAnimationContextCurrent => animationContext is not null && ReferenceEquals(Document, animationContextDocument) &&
        !session.IsClosing && !session.ViewModel.IsBusy;
    public StylePresetListItem[] StylePresets => session.StyleLibrary.Snapshot.Presets
        .Select(preset => new StylePresetListItem(preset.Id, preset.Name)).ToArray();
    public bool CanDeleteTrack => !session.IsClosing && !session.IsProjectBusy && SelectedTrackId is { } id &&
        Document.Tracks.Any(track => track.Id == id);
    public bool CanMoveTrackUp => SelectedTrackIndex > 0;
    public bool CanMoveTrackDown => SelectedTrackIndex >= 0 && SelectedTrackIndex < Document.Tracks.Length - 1;
    public bool IsRenamingTrack => renamingTrackId.HasValue;
    public bool CanConfirmTrackRename => IsRenamingTrack && !string.IsNullOrWhiteSpace(TrackNameDraft) &&
        TrackNameDraft.Length <= 128 && !TrackNameDraft.Any(char.IsControl);
    private int SelectedTrackIndex => SelectedTrackId is { } id
        ? Document.Tracks.IndexOf(Document.Tracks.FirstOrDefault(track => track.Id == id)!) : -1;

    public string TrackNameDraft
    {
        get => trackNameDraft;
        set
        {
            if (SetProperty(ref trackNameDraft, value))
            {
                OnPropertyChanged(nameof(CanConfirmTrackRename));
                ConfirmTrackRenameCommand.NotifyCanExecuteChanged();
            }
        }
    }

    private void BeginRenameTrack()
    {
        if (SelectedTrackId is { } id && session.SelectTrack(id))
        {
            renamingTrackId = id;
            TrackNameDraft = Document.Tracks.Single(track => track.Id == id).Name;
            OnPropertyChanged(nameof(IsRenamingTrack));
            ConfirmTrackRenameCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task ConfirmTrackRenameAsync()
    {
        if (!CanConfirmTrackRename || renamingTrackId is not { } id)
        {
            return;
        }

        var name = TrackNameDraft;
        await session.RunCommandAsync(() => session.EditAsync(() => session.RenameSubtitleTrack(id, name)));
        if (session.DocumentSnapshot.Tracks.FirstOrDefault(track => track.Id == id)?.Name == name)
        {
            CancelTrackRename();
        }
    }

    private void CancelTrackRename()
    {
        renamingTrackId = null;
        OnPropertyChanged(nameof(IsRenamingTrack));
        ConfirmTrackRenameCommand.NotifyCanExecuteChanged();
    }

    private void RefreshTrackCommands()
    {
        RefreshTrackCollapseCommands();
        RenameTrackCommand.NotifyCanExecuteChanged();
        DeleteTrackCommand.NotifyCanExecuteChanged();
        MoveTrackUpCommand.NotifyCanExecuteChanged();
        MoveTrackDownCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanDeleteTrack));
        OnPropertyChanged(nameof(CanMoveTrackUp));
        OnPropertyChanged(nameof(CanMoveTrackDown));
        if (renamingTrackId is { } id && !Document.Tracks.Any(track => track.Id == id))
        {
            CancelTrackRename();
        }
    }

    public Guid? SelectedCueId
    {
        get => selectedCueId;
        set
        {
            if (selectedCueId != value)
            {
                pendingCenterCueId = null;
            }
            if (value is null)
            {
                ResumePlaybackFollow();
            }
            SetProperty(ref selectedCueId, value);
        }
    }

    public ProjectLayer? SelectedLayer
    {
        get => selectedLayer;
        set => SetProperty(ref selectedLayer, value);
    }

    public TimelineTimingPreview? TimingPreview
    {
        get => timingPreview;
        internal set
        {
            if (SetProperty(ref timingPreview, value))
            {
                OnPropertyChanged(nameof(FullDuration));
            }
        }
    }

    public bool IsClassicTimingEnabled
    {
        get => isClassicTimingEnabled;
        set => SetProperty(ref isClassicTimingEnabled, value);
    }

    public MediaTime Position
    {
        get => position;
        set => SetProperty(ref position, value);
    }

    public double PixelsPerSecond
    {
        get => viewport.PixelsPerSecond;
        set => Viewport = viewport with { PixelsPerSecond = value };
    }

    public double ViewStart
    {
        get => viewport.StartSeconds;
        set => Viewport = viewport with { StartSeconds = value };
    }

    public double VisibleDuration
    {
        get => viewport.VisibleDuration;
        set => Viewport = viewport with { Width = Math.Max(0, value) * viewport.PixelsPerSecond };
    }

    public TimelineViewport Viewport
    {
        get => viewport;
        set
        {
            if (SetProperty(ref viewport, value))
            {
                OnPropertyChanged(nameof(PixelsPerSecond));
                OnPropertyChanged(nameof(ViewStart));
                OnPropertyChanged(nameof(VisibleDuration));
            }
        }
    }

    internal bool IsPlaybackFollowEnabled { get; private set; } = true;

    internal void CenterSubtitle(Guid cueId)
    {
        if (SelectedCueId != cueId || !Document.Subtitles.Any(cue => cue.Id == cueId))
        {
            return;
        }

        SuspendPlaybackFollow();
        pendingCenterCueId = cueId;
        ApplyPendingSubtitleCenter();
    }

    internal bool ApplyPendingSubtitleCenter()
    {
        if (pendingCenterCueId is not { } cueId || Viewport.Width <= 0)
        {
            return false;
        }

        var cue = Document.Subtitles.FirstOrDefault(value => value.Id == cueId);
        if (SelectedCueId != cueId || cue is null)
        {
            ResumePlaybackFollow();
            return false;
        }

        pendingCenterCueId = null;
        var midpoint = (Seconds(cue.Start) + Seconds(cue.End)) / 2;
        ViewStart = Math.Max(0, midpoint - VisibleDuration / 2);
        return true;
    }

    internal void ResumePlaybackFollow()
    {
        IsPlaybackFollowEnabled = true;
        pendingCenterCueId = null;
    }

    internal void AdaptViewportToMedia()
    {
        var duration = FullDuration;
        var current = Viewport;
        Viewport = (current with
        {
            PixelsPerSecond = Math.Max(current.PixelsPerSecond, current.Width / duration)
        }).Normalize(duration, double.MaxValue);
        ResumePlaybackFollow();
    }

    internal void SuspendPlaybackFollow()
    {
        IsPlaybackFollowEnabled = false;
        pendingCenterCueId = null;
    }

    public IReadOnlyList<Guid> SelectedLayerIds
    {
        get => selectedLayerIds;
        set => SetProperty(ref selectedLayerIds, value);
    }

    public double MediaDuration
    {
        get => mediaDuration;
        set
        {
            if (SetProperty(ref mediaDuration, value))
            {
                OnPropertyChanged(nameof(FullDuration));
            }
        }
    }

    public double FullDuration => Math.Max(Math.Max(Math.Max(1, mediaDuration), documentDuration),
        timingPreview is { } preview ? Seconds(preview.End) : 0);

    public double ScrollMaximum
    {
        get => scrollMaximum;
        set => SetProperty(ref scrollMaximum, value);
    }

    public double ViewportSize
    {
        get => viewportSize;
        set => SetProperty(ref viewportSize, value);
    }

    public bool IsSeeking
    {
        get => isSeeking;
        set
        {
            if (SetProperty(ref isSeeking, value))
            {
                session.SetInteractiveSeeking(value);
            }
        }
    }

    public bool IsSnapEnabled
    {
        get => isSnapEnabled;
        set => SetProperty(ref isSnapEnabled, value);
    }

    public bool IsStepEnabled
    {
        get => isStepEnabled;
        set => SetProperty(ref isStepEnabled, value);
    }

    public bool IsSpectrumVisible
    {
        get => isSpectrumVisible;
        set => SetProperty(ref isSpectrumVisible, value);
    }

    public bool IsWaveformVisible
    {
        get => isWaveformVisible;
        set => SetProperty(ref isWaveformVisible, value);
    }

    public AnimationProperty EffectProperty
    {
        get => EffectTarget.Property;
        set => EffectTarget = new(value);
    }

    public AnimationTrackTarget EffectTarget
    {
        get => effectTarget;
        set
        {
            if (SetProperty(ref effectTarget, value))
            {
                OnPropertyChanged(nameof(EffectProperty));
            }
        }
    }

    public SpectrogramData? Spectrogram
    {
        get => spectrogram;
        set => SetProperty(ref spectrogram, value);
    }

    public WaveformData? Waveform
    {
        get => waveform;
        internal set => SetProperty(ref waveform, value);
    }

    public SpectrogramData? SpectrogramOverview
    {
        get => spectrogramOverview;
        internal set => SetProperty(ref spectrogramOverview, value);
    }

    public WaveformData? WaveformOverview
    {
        get => waveformOverview;
        internal set => SetProperty(ref waveformOverview, value);
    }

    public MediaTime AudioDuration
    {
        get => audioDuration;
        internal set => SetProperty(ref audioDuration, value);
    }

    public double RenderScaling
    {
        get => renderScaling;
        set => SetProperty(ref renderScaling, value);
    }

    public string AnalysisStatus
    {
        get => analysisStatus;
        set => SetProperty(ref analysisStatus, value);
    }

    /// <summary>提交时间线上的播放定位请求。</summary>
    public Task SeekAsync(MediaTime time) => session.SeekProjectTimeAsync(time);
    /// <summary>同步字幕选择。</summary>
    public void SelectCue(Guid id) => session.SelectCue(id);
    /// <summary>同步非字幕片段选择。</summary>
    public void SelectLayer(Guid id) => session.SelectLayer(id, [id]);
    /// <summary>将时间线的主层和多选集合同步到同一会话选择。</summary>
    public bool SelectLayers(TimelineSelectionEventArgs value) => session.SelectTimelineLayers(value);
    /// <summary>切换当前轨道，保留工程合成顺序。</summary>
    public bool SelectTrack(Guid id) => session.SelectTrack(id);
    /// <summary>一次完成的时间线手势对应一次工程事务。</summary>
    public Task CommitTimingAsync(TimelineTimingEventArgs value) => value.TrackId is { } trackId
        ? session.CommitClipMoveAsync(value.Id, trackId, value.Start, value.End, value.Mode, value.IsMove, value.ExpectedDocument)
        : Task.CompletedTask;

    /// <summary>按手势冻结的工程快照提交一次轨道重排。</summary>
    public Task CommitTrackReorderAsync(TimelineTrackReorderEventArgs value) =>
        session.MoveTrackAsync(value.TrackId, value.Index, value.ExpectedDocument);

    /// <summary>按经典鼠标输入修改当前主选字幕的单个时间边界。</summary>
    public Task CommitClassicTimingAsync(TimelineClassicTimingEventArgs value) => session.CommitClassicTimingAsync(value);
    /// <summary>选择关键帧并同步属性检查器。</summary>
    public bool SelectKeyframe(TimelineKeyframeEventArgs value) => session.SelectKeyframe(value);
    /// <summary>更新工程的独立属性行视图状态，不提交内容草稿。</summary>
    public void SetAnimationRowCollapsed(TimelineAnimationRowCollapseEventArgs value) =>
        session.SetTimelineAnimationRowCollapsed(value.Id, value.IsCollapsed);
    /// <summary>提交完成的关键帧手势。</summary>
    public Task MoveKeyframeAsync(TimelineKeyframeEventArgs value) => session.RunCommandAsync(() => session.EditAsync(() => session.MoveKeyframe(value)));
    /// <summary>提交冻结目标集合的一次整体平移。</summary>
    public Task CommitClipsMoveAsync(TimelineClipsMoveEventArgs value) => session.CommitTimelineClipsMoveAsync(value);
    /// <summary>捕获主体菜单打开时的工程、选择及时间。</summary>
    public void SetClipContext(TimelineClipContextEventArgs value)
    {
        clipContext = value;
        clipContextDocument = Document;
        clipContextIds = session.TimelineClipIds().ToArray();
        clipContextPrimary = SelectedLayer is { } primary && clipContextIds.Contains(primary.Id)
            ? primary.Id : clipContextIds.Cast<Guid?>().FirstOrDefault();
        RefreshClipCommands();
    }

    /// <summary>固定属性菜单打开时的工程、属性及命中片段或整行集合，不改变片段选择。</summary>
    public void SetAnimationRowContext(TimelineAnimationRowContextEventArgs value)
    {
        animationContext = value.Id;
        animationContextClipId = value.ClipId;
        animationContextDocument = Document;
        animationContextIds = value.ClipId is { } id ? [id] : WorkbenchSession.TimelineAnimationRowLayerIds(Document, value.Id).ToArray();
        RefreshClipCommands();
    }

    /// <summary>复制当前项目的时间线选择。</summary>
    public async Task CopySelectedClipsAsync()
    {
        if (SelectedLayer is { } primary)
        {
            await session.CopyTimelineClipsAsync(primary.Id, session.TimelineClipIds());
            RefreshClipCommands();
        }
    }
    /// <summary>将已复制片段组粘贴到鼠标命中的时间和轨道。</summary>
    public Task PasteSelectedClipsAsync(TimelineClipContextEventArgs target) => session.PasteTimelineClipsAtTargetAsync(target);
    /// <summary>一次删除时间线选择。</summary>
    public Task DeleteSelectedClipsAsync() => session.DeleteTimelineClipsAsync(session.TimelineClipIds());

    private async Task CopyContextClipsAsync()
    {
        if (clipContextPrimary is { } id)
        {
            await session.CopyTimelineClipsAsync(id, clipContextIds, clipContextDocument);
            RefreshClipCommands();
        }
    }
    private Task PasteContextClipsAsync() => clipContext is { } context
        ? session.PasteTimelineClipsAtTargetAsync(context, clipContextDocument) : Task.CompletedTask;
    private Task DeleteContextClipsAsync() => session.DeleteTimelineClipsAsync(clipContextIds, clipContextDocument);
    private Task ClearContextClipAnimationTracksAsync() =>
        session.ClearTimelineClipAnimationTracksAsync(clipContextIds, clipContextDocument);
    private Task ClearContextAnimationPropertyTracksAsync() => animationContext is { } row && animationContextDocument is { } source
        ? animationContextClipId is { } id
            ? session.ClearTimelineClipAnimationRowAsync(id, row, source)
            : session.ClearTimelineAnimationRowAsync(row, source, animationContextIds)
        : Task.CompletedTask;
    private Task MoveContextClipsAsync() => session.MoveTimelineClipsAsync(clipContextIds, clipContextDocument);
    private Task CreateContextSubtitleAsync() => clipContext?.TrackId is { } trackId && clipContextDocument is { } source
        ? session.CreateTimelineSubtitleAsync(trackId, clipContext.Time, source) : Task.CompletedTask;

    private void RefreshClipCommands()
    {
        CopyClipsCommand.NotifyCanExecuteChanged();
        PasteClipsCommand.NotifyCanExecuteChanged();
        DeleteClipsCommand.NotifyCanExecuteChanged();
        ClearClipAnimationTracksCommand.NotifyCanExecuteChanged();
        ClearAnimationPropertyTracksCommand.NotifyCanExecuteChanged();
        RefreshMoveCommand();
        CreateSubtitleCommand.NotifyCanExecuteChanged();
    }

    internal void RefreshMoveCommand()
    {
        MoveClipsCommand.NotifyCanExecuteChanged();
        RefreshTrackCollapseCommands();
    }

    private bool HasAnimationTracks(IReadOnlyCollection<Guid> ids, AnimationProperty? property = null)
    {
        var selected = ids.ToHashSet();
        return Document.Layers.Any(layer => selected.Contains(layer.Id) &&
            layer.Tracks.Any(track => property is null || track.Property == property));
    }
    /// <summary>视口尺寸改变后重新计算滚动范围。</summary>
    public void RefreshViewport() => session.Tick();

    private static double Seconds(MediaTime value) => (double)value.Numerator / value.Denominator;
}
