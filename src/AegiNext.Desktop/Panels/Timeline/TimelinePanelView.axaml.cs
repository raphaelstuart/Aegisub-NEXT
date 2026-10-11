using System.ComponentModel;
using AegiNext.Desktop.Controls;
using AegiNext.Desktop.Controls.Common;
using System.Windows.Input;
using Avalonia;
using AegiNext.Desktop.Workspace;
using AegiNext.Desktop.Editing;
using AegiNext.Desktop.I18n;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using AegiNext.Desktop.Styling;
using Material.Icons.Avalonia;
using AegiNext.Desktop.Shortcuts;
using AegiNext.Desktop.Windowing;
using AegiNext.Core.Timing;
using AegiNext.Media.Analysis;

namespace AegiNext.Desktop.Panels.Timeline;

internal sealed partial class TimelinePanelView : UserControl, IWorkbenchPanelView, IWorkbenchFocusCommandTarget
{
    private readonly WorkbenchSession session;
    private readonly TimelinePanelViewModel viewModel;
    private readonly SubtitleTimelineControl timeline;
    private readonly TimelineOverviewControl overview;
    private readonly MenuItem collapseTrackItem;
    private readonly MenuItem trackStyleItem;
    private readonly MenuItem autoTrackStyleItem;
    private readonly MenuItem animationClearItem;
    private bool animationContextIsClip;
    private readonly ToolbarToggleButton snapButton;
    private readonly ToolbarToggleButton stepButton;
    private readonly ToolbarToggleButton spectrumButton;
    private readonly ToolbarToggleButton waveformButton;
    private AegiNext.Media.Analysis.SpectrogramData? spectrum;
    private SpectrogramData? spectrumOverview;
    private WaveformData? waveform;
    private WaveformData? waveformOverview;
    private MediaTime? audioDuration;
    private TopLevel? scalingHost;
    private IPointer? animationRowCollapsePointer;
    private bool disposed;
    private bool applying;
    private bool releasingTransportSeek;
    internal TimelinePanelView(TimelinePanelViewModel viewModel, WorkbenchSession session)
    {
        this.session = session;
        this.viewModel = viewModel;
        AvaloniaXamlLoader.Load(this);
        DataContext = viewModel;
        timeline = this.FindControl<SubtitleTimelineControl>("Timeline")!;
        timeline.SetAudioGraphPalette(session.Preferences.AudioGraph);
        timeline.SetAudioAnalysisDisplay(session.Preferences.AudioAnalysis.Display);
        timeline.SetClipPalette(session.Preferences.TimelineClips);
        overview = this.FindControl<TimelineOverviewControl>("TimelineMinimap")!;
        snapButton = this.FindControl<ToolbarToggleButton>("TimelineSnapButton")!;
        stepButton = this.FindControl<ToolbarToggleButton>("TimelineStepButton")!;
        spectrumButton = this.FindControl<ToolbarToggleButton>("TimelineSpectrumButton")!;
        waveformButton = this.FindControl<ToolbarToggleButton>("TimelineWaveformButton")!;
        this.FindControl<MaterialIcon>("TimelineSnapIcon")!.Kind = WorkbenchIcon.ResolveKind("Magnet");
        this.FindControl<MaterialIcon>("TimelineSpectrumIcon")!.Kind = WorkbenchIcon.ResolveKind("Spectrum");
        this.FindControl<MaterialIcon>("TimelineWaveformIcon")!.Kind = WorkbenchIcon.ResolveKind("Waveform");
        TrackMenu = new();
        TrackMenu.Items.Add(CreateMenuItem("AddSubtitleTrackMenuItem", "AddTrack", viewModel.AddTrackCommand));
        TrackMenu.Items.Add(CreateMenuItem("RenameSubtitleTrackMenuItem", "RenameTrack", viewModel.RenameTrackCommand));
        TrackMenu.Items.Add(CreateMenuItem("DeleteSubtitleTrackMenuItem", "DeleteTrack", viewModel.DeleteTrackCommand));
        TrackMenu.Items.Add(new Separator());
        TrackMenu.Items.Add(CreateMenuItem("MoveSubtitleTrackUpMenuItem", "MoveTrackUp", viewModel.MoveTrackUpCommand));
        TrackMenu.Items.Add(CreateMenuItem("MoveSubtitleTrackDownMenuItem", "MoveTrackDown", viewModel.MoveTrackDownCommand));
        collapseTrackItem = new() { Name = "CollapseSubtitleTrackMenuItem" };
        collapseTrackItem.Click += (_, _) =>
        {
            if (viewModel.SelectedTrackId is { } id)
            {
                timeline.ToggleTrackCollapse(id);
            }
        };
        TrackMenu.Items.Add(collapseTrackItem);
        TrackMenu.Items.Add(new Separator());
        trackStyleItem = CreateMenuItem("SubtitleTrackStyleMenuItem", "TrackSubtitleStyle");
        autoTrackStyleItem = new()
        {
            Name = "AutoApplySubtitleTrackStyleMenuItem", ToggleType = MenuItemToggleType.CheckBox,
            Command = viewModel.ToggleTrackAutoStyleCommand
        };
        autoTrackStyleItem.Bind(MenuItem.HeaderProperty, Localization.Observe("Workbench.TrackStyleAutoApply").ToBinding());
        TrackMenu.Items.Add(trackStyleItem);
        TrackMenu.Items.Add(autoTrackStyleItem);
        ClipMenu = new();
        ClipMenu.Items.Add(CreateMenuItem("CreateTimelineSubtitleMenuItem", "CreateTimelineSubtitle", viewModel.CreateSubtitleCommand));
        ClipMenu.Items.Add(new Separator());
        ClipMenu.Items.Add(CreateMenuItem("CopyTimelineClipsMenuItem", "CopyTimelineClips", viewModel.CopyClipsCommand));
        ClipMenu.Items.Add(CreateMenuItem("PasteTimelineClipsMenuItem", "PasteTimelineClips", viewModel.PasteClipsCommand));
        ClipMenu.Items.Add(CreateMenuItem("MoveTimelineClipsMenuItem", "Move", viewModel.MoveClipsCommand));
        ClipMenu.Items.Add(CreateMenuItem("ClearClipAnimationTracksMenuItem", "ClearClipAnimationTracks", viewModel.ClearClipAnimationTracksCommand));
        ClipMenu.Items.Add(CreateMenuItem("DeleteTimelineClipsMenuItem", "DeleteTimelineClips", viewModel.DeleteClipsCommand));
        ClipMenu.Items.Add(colorTagMenu.Item);
        AnimationMenu = new();
        animationClearItem = new() { Name = "ClearAnimationPropertyTracksMenuItem", Command = viewModel.ClearAnimationPropertyTracksCommand };
        AnimationMenu.Items.Add(animationClearItem);
        RefreshAnimationMenu();
        timeline.TrackContextRequested += OnTrackContextRequested;
        timeline.ClipContextRequested += OnClipContextRequested;
        timeline.AnimationRowContextRequested += OnAnimationRowContextRequested;
        timeline.TrackSoloRequested += OnTrackSoloRequested;
        timeline.TrackCollapseRequested += OnTrackCollapseRequested;
        timeline.SeekRequested += async (_, e) =>
        {
            if (timeline.IsSeeking && !viewModel.IsSeeking)
            {
                viewModel.IsSeeking = true;
            }
            await viewModel.SeekAsync(e.Time);
        };
        timeline.ClipSelectionChanged += (_, e) => e.SelectionAccepted = viewModel.SelectLayers(e);
        timeline.TrackSelected += (_, e) => e.SelectionAccepted = viewModel.SelectTrack(e.Id);
        timeline.TrackReorderCompleted += async (_, e) => await viewModel.CommitTrackReorderAsync(e);
        timeline.ViewportChanged += OnViewportChanged;
        overview.ViewportChanged += OnViewportChanged;
        timeline.TimingChanged += async (_, e) => await viewModel.CommitTimingAsync(e);
        timeline.ClassicTimingRequested += async (_, e) => await viewModel.CommitClassicTimingAsync(e);
        timeline.ClipsMoveCompleted += async (_, e) => await viewModel.CommitClipsMoveAsync(e);
        timeline.KeyframeSelected += (_, e) => e.SelectionAccepted = viewModel.SelectKeyframe(e);
        timeline.KeyframeMoved += async (_, e) => await viewModel.MoveKeyframeAsync(e);
        timeline.AnimationRowCollapseRequested += OnAnimationRowCollapseRequested;
        AddHandler(PointerPressedEvent, OnPreviewPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnPreviewPointerReleased, RoutingStrategies.Tunnel);
        timeline.AddHandler(PointerPressedEvent, (_, _) => viewModel.IsSeeking = timeline.IsSeeking, RoutingStrategies.Bubble, true);
        timeline.AddHandler(PointerReleasedEvent, (_, _) => releasingTransportSeek = timeline.IsSeeking,
            RoutingStrategies.Tunnel, true);
        timeline.AddHandler(PointerReleasedEvent, (_, _) =>
        {
            viewModel.IsSeeking = false;
            releasingTransportSeek = false;
        }, RoutingStrategies.Bubble, true);
        timeline.AddHandler(PointerCaptureLostEvent, (_, _) =>
        {
            if (!releasingTransportSeek && viewModel.IsSeeking)
            {
                session.CancelInteractiveSeeking();
                viewModel.IsSeeking = false;
            }
        }, RoutingStrategies.Bubble, true);
        timeline.SizeChanged += (_, _) =>
        {
            viewModel.RefreshViewport();
        };
        viewModel.PropertyChanged += OnViewModelChanged;
        session.PreferencesChanged += OnPreferencesChanged;
        Localization.LanguageChanged += OnLanguageChanged;
        session.StyleLibraryChanged += OnStyleLibraryChanged;
        session.ViewModel.GesturesCancelled += OnGesturesCancelled;
        ApplyState();
        RefreshTrackMenu();
        var nameInput = this.FindControl<TextBox>("TimelineTrackNameInput")!;
        nameInput.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                viewModel.CancelTrackRenameCommand.Execute(null);
                timeline.Focus();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && viewModel.ConfirmTrackRenameCommand.CanExecute(null))
            {
                viewModel.ConfirmTrackRenameCommand.Execute(null);
                e.Handled = true;
            }
        };
    }

    public string PanelId => "timeline";
    internal ContextMenu TrackMenu { get; }
    internal ContextMenu ClipMenu { get; }
    internal ContextMenu AnimationMenu { get; }
    public bool CanExecuteFocusCommand(WorkbenchCommand command, IInputElement focusedElement)
    {
        if (!ReferenceEquals(focusedElement, timeline) || TrackMenu.IsOpen || ClipMenu.IsOpen || AnimationMenu.IsOpen)
        {
            return false;
        }
        return command switch
        {
            WorkbenchCommand.AUDITION_BEFORE_SUBTITLE or WorkbenchCommand.AUDITION_AFTER_SUBTITLE or
                WorkbenchCommand.AUDITION_SUBTITLE_BEGIN or WorkbenchCommand.AUDITION_SUBTITLE => session.CanAuditionSubtitle && !timeline.HasActiveDrag,
            WorkbenchCommand.END_TEXT_INPUT => timeline.HasActiveDrag,
            WorkbenchCommand.COPY_CLIPS or WorkbenchCommand.DELETE_SUBTITLE => viewModel.CanCopyClips,
            WorkbenchCommand.PASTE_CLIPS => viewModel.CanPasteClips && timeline.GetClipPasteTarget() is not null,
            _ => false
        };
    }

    public bool TryExecuteFocusCommand(WorkbenchCommand command, IInputElement focusedElement)
    {
        if (!CanExecuteFocusCommand(command, focusedElement))
        {
            return false;
        }
        switch (command)
        {
            case WorkbenchCommand.AUDITION_BEFORE_SUBTITLE:
            case WorkbenchCommand.AUDITION_AFTER_SUBTITLE:
            case WorkbenchCommand.AUDITION_SUBTITLE_BEGIN:
            case WorkbenchCommand.AUDITION_SUBTITLE:
                _ = session.ExecuteCommandAsync(command);
                break;
            case WorkbenchCommand.END_TEXT_INPUT:
                CancelGestures();
                break;
            case WorkbenchCommand.COPY_CLIPS:
                _ = viewModel.CopySelectedClipsAsync();
                break;
            case WorkbenchCommand.PASTE_CLIPS:
                if (timeline.GetClipPasteTarget() is not { } target)
                {
                    return false;
                }
                _ = viewModel.PasteSelectedClipsAsync(target);
                break;
            case WorkbenchCommand.DELETE_SUBTITLE:
                _ = viewModel.DeleteSelectedClipsAsync();
                break;
        }
        return true;
    }
    public void CancelGestures()
    {
        session.CancelInteractiveSeeking();
        releasingTransportSeek = false;
        timeline.CancelGesture();
        overview.CancelGesture();
        viewModel.IsSeeking = false;
    }
    public void FocusInvalidField(string? fieldKey) => timeline.Focus();
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (disposed || applying)
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(viewModel.Position):
                timeline.Position = viewModel.Position;
                overview.Position = viewModel.Position;
                break;
            case nameof(viewModel.TimingPreview):
                timeline.SetTimingPreview(viewModel.TimingPreview);
                overview.SetScene(viewModel.Document, viewModel.Viewport, viewModel.FullDuration, viewModel.Position, viewModel.TimingPreview);
                break;
            case null:
            case "":
            case nameof(viewModel.Document):
            case nameof(viewModel.TimelineViewState):
            case nameof(viewModel.SoloTrackId):
            case nameof(viewModel.SelectedCueId):
            case nameof(viewModel.SelectedLayer):
            case nameof(viewModel.SelectedLayerIds):
            case nameof(viewModel.SelectedTrackId):
            case nameof(viewModel.SelectedMaskNodeId):
            case nameof(viewModel.EffectTarget):
            case nameof(viewModel.Viewport):
            case nameof(viewModel.FullDuration):
            case nameof(viewModel.MediaDuration):
            case nameof(viewModel.IsSnapEnabled):
            case nameof(viewModel.IsStepEnabled):
            case nameof(viewModel.IsSpectrumVisible):
            case nameof(viewModel.IsWaveformVisible):
            case nameof(viewModel.IsClassicTimingEnabled):
            case nameof(viewModel.Spectrogram):
            case nameof(viewModel.SpectrogramOverview):
            case nameof(viewModel.Waveform):
            case nameof(viewModel.WaveformOverview):
            case nameof(viewModel.AudioDuration):
                ApplyState();
                break;
        }
        if (e.PropertyName is nameof(viewModel.Document) or nameof(viewModel.SelectedTrackId))
        {
            RefreshTrackMenu();
        }
        if (e.PropertyName == nameof(viewModel.IsRenamingTrack) && viewModel.IsRenamingTrack)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!disposed && viewModel.IsRenamingTrack)
                {
                    var input = this.FindControl<TextBox>("TimelineTrackNameInput")!;
                    input.Focus();
                    input.SelectAll();
                }
            });
        }
    }
    private void OnAnimationRowCollapseRequested(object? sender, TimelineAnimationRowCollapseEventArgs e) =>
        viewModel.SetAnimationRowCollapsed(e);

    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ReferenceEquals(animationRowCollapsePointer, e.Pointer))
        {
            animationRowCollapsePointer = null;
        }

        if (ReferenceEquals(e.Source, timeline) && e.GetCurrentPoint(timeline).Properties.IsLeftButtonPressed &&
            (timeline.TryRequestTrackSolo(e.GetPosition(timeline)) || timeline.TryRequestTrackCollapse(e.GetPosition(timeline)) ||
                timeline.TryRequestAnimationRowCollapse(e.GetPosition(timeline))))
        {
            animationRowCollapsePointer = e.Pointer;
            e.Handled = true;
            return;
        }

        if (ReferenceEquals(e.Source, timeline) &&
            TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox { DataContext: SubtitleRow })
        {
            timeline.TryRequestClassicTiming(e, preserveFocus: true);
        }
    }

    private void OnPreviewPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (ReferenceEquals(animationRowCollapsePointer, e.Pointer))
        {
            animationRowCollapsePointer = null;
            e.Handled = true;
        }
    }

    private void ApplyState()
    {
        if (applying || disposed)
        {
            return;
        }

        applying = true;
        try
        {
            timeline.Position = viewModel.Position;
            timeline.EffectTarget = viewModel.EffectTarget;
            timeline.SelectedMaskNodeId = viewModel.SelectedMaskNodeId;
            timeline.IsSnapEnabled = viewModel.IsSnapEnabled;
            timeline.IsStepEnabled = viewModel.IsStepEnabled;
            timeline.IsSpectrumVisible = viewModel.IsSpectrumVisible;
            timeline.IsWaveformVisible = viewModel.IsWaveformVisible;
            timeline.IsClassicTimingEnabled = viewModel.IsClassicTimingEnabled;
            timeline.SetMediaDuration(viewModel.MediaDuration);
            timeline.SetTimingPreview(viewModel.TimingPreview);
            timeline.SetDocument(viewModel.Document, viewModel.SelectedCueId, viewModel.SelectedLayer,
                viewModel.SelectedLayerIds.Count == 0 && viewModel.SelectedLayer is { } selected ? [selected.Id] : viewModel.SelectedLayerIds,
                viewModel.SelectedTrackId);
            timeline.SoloTrackId = viewModel.SoloTrackId;
            timeline.TimelineViewState = viewModel.TimelineViewState;
            timeline.SetViewport(viewModel.Viewport, viewModel.FullDuration);
            if (!ReferenceEquals(spectrum, viewModel.Spectrogram) || !ReferenceEquals(spectrumOverview, viewModel.SpectrogramOverview))
            {
                spectrum = viewModel.Spectrogram;
                spectrumOverview = viewModel.SpectrogramOverview;
                timeline.SetSpectrogram(spectrum, spectrumOverview);
            }

            if (!ReferenceEquals(waveform, viewModel.Waveform) ||
                !ReferenceEquals(waveformOverview, viewModel.WaveformOverview) || audioDuration != viewModel.AudioDuration)
            {
                waveform = viewModel.Waveform;
                waveformOverview = viewModel.WaveformOverview;
                audioDuration = viewModel.AudioDuration;
                timeline.SetWaveform(waveform, waveformOverview, viewModel.AudioDuration);
            }

            viewModel.Viewport = timeline.Viewport;
            if (viewModel.ApplyPendingSubtitleCenter())
            {
                timeline.SetViewport(viewModel.Viewport, viewModel.FullDuration);
                viewModel.Viewport = timeline.Viewport;
            }
            overview.SetScene(viewModel.Document, viewModel.Viewport, viewModel.FullDuration, viewModel.Position, viewModel.TimingPreview);
        }
        finally
        {
            applying = false;
        }
    }
    private void OnViewportChanged(object? sender, TimelineViewportEventArgs e)
    {
        if (!applying && e.IsUserInitiated)
        {
            viewModel.SuspendPlaybackFollow();
        }
        timeline.SetViewport(e.Viewport, viewModel.FullDuration, !applying && e.IsUserInitiated);
        viewModel.Viewport = e.Viewport;
    }
    private void OnTrackContextRequested(object? sender, TimelineTrackContextEventArgs e)
    {
        AnimationMenu.Close();
        ClipMenu.Close();
        TrackMenu.Close();
        if (e.TrackId is { } id && !viewModel.SelectTrack(id))
        {
            return;
        }

        RefreshTrackMenu();
        TrackMenu.Open(timeline);
    }

    private void OnTrackSoloRequested(object? sender, TimelineTrackSoloEventArgs e)
    {
        viewModel.ToggleTrackSolo(e.TrackId);
    }

    private void OnTrackCollapseRequested(object? sender, TimelineTrackCollapseEventArgs e) => viewModel.SetTrackCollapsed(e);
    private void OnClipContextRequested(object? sender, TimelineClipContextEventArgs e)
    {
        AnimationMenu.Close();
        TrackMenu.Close();
        ClipMenu.Close();
        viewModel.SetClipContext(e);
        RefreshColorTagMenu();
        ClipMenu.Open(timeline);
    }
    private void OnAnimationRowContextRequested(object? sender, TimelineAnimationRowContextEventArgs e)
    {
        TrackMenu.Close();
        ClipMenu.Close();
        AnimationMenu.Close();
        viewModel.SetAnimationRowContext(e);
        animationContextIsClip = e.ClipId.HasValue;
        RefreshAnimationMenu();
        AnimationMenu.Open(timeline);
    }
    private void RefreshAnimationMenu() => animationClearItem.Header = Localization.Get("Workbench." +
        (animationContextIsClip ? "ClearClipAnimationPropertyTracks" : "ClearAnimationPropertyTracks"));
    private void RefreshTrackMenu()
    {
        collapseTrackItem.IsEnabled = viewModel.SelectedTrackId.HasValue;
        collapseTrackItem.Header = Localization.Get("Workbench." + (viewModel.SelectedTrackId is { } id && timeline.IsTrackCollapsed(id)
            ? "ExpandTrack" : "CollapseTrack"));
        RefreshStylePresets();
    }
    private void RefreshStylePresets()
    {
        trackStyleItem.Items.Clear();
        var presets = viewModel.StylePresets;
        trackStyleItem.IsEnabled = viewModel.SelectedTrackId.HasValue && presets.Length > 0;
        var currentTrack = viewModel.Document.Tracks.FirstOrDefault(track => track.Id == viewModel.SelectedTrackId);
        autoTrackStyleItem.IsEnabled = currentTrack is not null;
        autoTrackStyleItem.IsChecked = currentTrack?.AutoApplyStyle == true;
        autoTrackStyleItem.CommandParameter = currentTrack?.Id;
        foreach (var preset in presets)
        {
            if (currentTrack is null)
            {
                break;
            }
            trackStyleItem.Items.Add(new MenuItem
            {
                Header = preset.Name,
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = currentTrack.StylePresetId == preset.Id,
                Command = viewModel.ApplyTrackStyleCommand,
                CommandParameter = new TrackStylePresetRequest(currentTrack.Id, preset.Id)
            });
        }

        ToolTip.SetTip(trackStyleItem, presets.Length == 0 ? Localization.Get("Workbench.NoStylePresets") : currentTrack?.StylePresetName);
    }
    private void OnStyleLibraryChanged(object? sender, EventArgs e)
    {
        if (!disposed)
        {
            RefreshTrackMenu();
        }
    }
    private void OnPreferencesChanged(object? sender, EventArgs e)
    {
        timeline.SetAudioGraphPalette(session.Preferences.AudioGraph);
        timeline.SetAudioAnalysisDisplay(session.Preferences.AudioAnalysis.Display);
        timeline.SetClipPalette(session.Preferences.TimelineClips);
        timeline.InvalidateVisual();
    }
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RefreshTrackMenu();
        RefreshAnimationMenu();
    }
    private static MenuItem CreateMenuItem(string name, string key, ICommand? command = null)
    {
        var item = new MenuItem { Name = name, Command = command };
        item.Bind(MenuItem.HeaderProperty, Localization.Observe("Workbench." + key).ToBinding());
        return item;
    }
    private void OnGesturesCancelled(object? sender, EventArgs e) => CancelGestures();

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (!disposed)
        {
            scalingHost = TopLevel.GetTopLevel(this);
            if (scalingHost is not null)
            {
                scalingHost.ScalingChanged += OnRenderScalingChanged;
                OnRenderScalingChanged(scalingHost, EventArgs.Empty);
            }
        }
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        DetachScalingHost();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnRenderScalingChanged(object? sender, EventArgs e)
    {
        if (scalingHost is { } host && !disposed)
        {
            viewModel.RenderScaling = host.RenderScaling;
            timeline.InvalidateVisual();
        }
    }

    private void DetachScalingHost()
    {
        if (scalingHost is { } host)
        {
            host.ScalingChanged -= OnRenderScalingChanged;
            scalingHost = null;
        }
    }

    public void Dispose()
    {
        if (!disposed)
        {
            disposed = true;
            DetachScalingHost();
            viewModel.PropertyChanged -= OnViewModelChanged;
            session.PreferencesChanged -= OnPreferencesChanged;
            Localization.LanguageChanged -= OnLanguageChanged;
            session.StyleLibraryChanged -= OnStyleLibraryChanged;
            session.ViewModel.GesturesCancelled -= OnGesturesCancelled;
            timeline.ViewportChanged -= OnViewportChanged;
            overview.ViewportChanged -= OnViewportChanged;
            timeline.TrackContextRequested -= OnTrackContextRequested;
            timeline.ClipContextRequested -= OnClipContextRequested;
            timeline.AnimationRowContextRequested -= OnAnimationRowContextRequested;
            timeline.TrackSoloRequested -= OnTrackSoloRequested;
            timeline.TrackCollapseRequested -= OnTrackCollapseRequested;
            timeline.AnimationRowCollapseRequested -= OnAnimationRowCollapseRequested;
            RemoveHandler(PointerPressedEvent, OnPreviewPointerPressed);
            RemoveHandler(PointerReleasedEvent, OnPreviewPointerReleased);
            animationRowCollapsePointer = null;
            TrackMenu.Close();
            ClipMenu.Close();
            colorTagMenu.Dispose();
            AnimationMenu.Close();
            overview.CancelGesture();
            overview.Dispose();
            timeline.Dispose();
        }
    }
}
