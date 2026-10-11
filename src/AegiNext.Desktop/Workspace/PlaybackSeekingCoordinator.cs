using AegiNext.Core.Timing;
using AegiNext.Desktop.Controllers;
using AegiNext.Media.Playback;
using Avalonia.Threading;

namespace AegiNext.Desktop.Workspace;

internal sealed class PlaybackSeekingCoordinator(WorkbenchSession session, VideoPreviewController controller)
{
    private long revision;
    private readonly DispatcherTimer interactiveTimer = new() { Interval = InteractiveSeekingState.UPDATE_INTERVAL };
    private readonly InteractiveSeekingState interaction = new();
    private Task interactiveOperation = Task.CompletedTask;
    private bool refreshPending;
    private bool completingInteraction;
    private MediaTime? queuedTarget;
    private bool? transportResumePlayback;
    internal bool IsInteractive => interaction.IsActive;
    internal bool UsesInteractiveQuality => interaction.UsesInteractiveQuality;
    internal void RequestRefresh() => refreshPending = true;
    internal bool IsPlaybackRequested => IsInteractive ? interaction.ResumePlayback :
        transportResumePlayback ?? controller.Snapshot.State == VideoPlaybackState.PLAYING;

    internal void SetInteractive(bool value)
    {
        if (IsInteractive == value)
        {
            return;
        }
        if (value)
        {
            session.InteractionDiagnostics.Begin();
            interaction.Begin(IsPlaybackRequested);
            completingInteraction = false;
            var requestRevision = ++revision;
            interactiveOperation = ObservePauseAsync(requestRevision, true);
            session.ViewModel.Timeline.ResumePlaybackFollow();
            session.TryCommitDrafts(false);
            session.CancelSceneGesture();
            session.ClearKeyframeSelection();
            interactiveTimer.Tick += OnInteractiveTick;
            interactiveTimer.Start();
        }
        else
        {
            session.InteractionDiagnostics.Record("released");
            var finalTarget = interaction.Target ?? queuedTarget ?? PendingPosition ?? controller.Snapshot.Position;
            var resumePlayback = interaction.ResumePlayback;
            interaction.End();
            completingInteraction = true;
            interactiveTimer.Stop();
            interactiveTimer.Tick -= OnInteractiveTick;
            queuedTarget = null;
            refreshPending = false;
            controller.InvalidatePreview();
            _ = SeekCoreAsync(finalTarget, false, resumePlayback, true);
        }
        session.Tick();
    }

    private void OnInteractiveTick(object? sender, EventArgs e)
    {
        if (!IsInteractive)
        {
            return;
        }
        var settled = interaction.TrySettle();
        if (settled)
        {
            session.InteractionDiagnostics.Record("settled");
            queuedTarget = interaction.Target;
            controller.InvalidatePreview();
            refreshPending = true;
        }
        if ((!settled && !interactiveOperation.IsCompleted) || queuedTarget is not { } target)
        {
            if (refreshPending)
            {
                refreshPending = false;
                session.Tick();
            }
            return;
        }
        refreshPending = false;
        queuedTarget = null;
        interactiveOperation = SeekCoreAsync(target, false, false);
    }

    private async Task ObservePauseAsync(long requestRevision, bool beginning = false)
    {
        try
        {
            await (beginning ? controller.BeginInteractiveSeekAsync() : controller.PauseAsync());
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            if (!session.IsClosing && requestRevision == revision)
            {
                session.ShowError(error);
            }
        }
    }

    internal void Cancel()
    {
        var wasInteractive = IsInteractive || completingInteraction;
        Invalidate();
        if (wasInteractive && !session.IsClosing)
        {
            controller.InvalidatePreview();
            interactiveOperation = ObservePauseAsync(revision);
            session.Tick();
        }
    }

    internal MediaTime? PendingPosition { get; private set; }

    internal void Invalidate()
    {
        if (IsInteractive)
        {
            session.InteractionDiagnostics.Record("cancelled");
        }
        revision++;
        PendingPosition = null;
        queuedTarget = null;
        transportResumePlayback = null;
        completingInteraction = false;
        refreshPending = false;
        interactiveTimer.Stop();
        interactiveTimer.Tick -= OnInteractiveTick;
        interaction.End();
    }

    internal Task SeekForEditingAsync(MediaTime position)
    {
        Invalidate();
        return SeekCoreAsync(position, false, null);
    }

    internal Task SeekFromUserAsync(MediaTime position)
    {
        var resumePlayback = IsPlaybackRequested;
        if (IsInteractive || completingInteraction)
        {
            Invalidate();
            session.ViewModel.CancelGestures();
        }
        return SeekCoreAsync(position, true, resumePlayback);
    }

    private async Task SeekCoreAsync(MediaTime position, bool clearEditingTarget, bool? resumePlayback, bool completeInteraction = false)
    {
        session.InvalidateTimingSession();
        if (clearEditingTarget)
        {
            session.ViewModel.Timeline.ResumePlaybackFollow();
            session.TryCommitDrafts(false);
            session.CancelSceneGesture();
            session.ClearKeyframeSelection();
        }
        var snapshot = controller.Snapshot;
        var start = snapshot.Start ?? MediaTime.Zero;
        var target = position < start ? start : position;
        if (snapshot.Duration is { } duration && target > start + duration)
        {
            target = start + duration;
        }

        var requestRevision = ++revision;
        transportResumePlayback = resumePlayback;
        PendingPosition = target;
        session.InteractionDiagnostics.Accept(target);
        session.ViewModel.Error = null;
        session.Tick();
        try
        {
            if (resumePlayback is { } resume)
            {
                if (IsInteractive)
                {
                    await controller.SeekInteractiveAsync(target);
                }
                else if (completeInteraction)
                {
                    await controller.CompleteInteractiveSeekAsync(target, resume);
                }
                else
                {
                    await controller.SeekForPlaybackAsync(target, resume);
                }
            }
            else
            {
                await controller.SeekAsync(target);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            if (!session.IsClosing && requestRevision == revision)
            {
                session.ShowError(error);
            }
        }
        finally
        {
            if (requestRevision == revision)
            {
                transportResumePlayback = null;
                completingInteraction = false;
                PendingPosition = IsInteractive ? interaction.Target : queuedTarget;
                if (!session.IsClosing)
                {
                    if (IsInteractive)
                    {
                        refreshPending = true;
                    }
                    else
                    {
                        session.Tick();
                    }
                }
            }
        }
    }

    internal Task SeekRelativeAsync(long seconds)
    {
        var snapshot = controller.Snapshot;
        var start = snapshot.Start ?? MediaTime.Zero;
        var target = (PendingPosition ?? snapshot.Position) + new MediaTime(seconds);
        return SeekFromUserAsync(target < start ? start : target);
    }

    internal Task SeekProjectTimeAsync(MediaTime time)
    {
        session.ViewModel.Timeline.ResumePlaybackFollow();
        var target = (controller.Snapshot.Start ?? MediaTime.Zero) + time;
        if (!IsInteractive)
        {
            return SeekFromUserAsync(target);
        }
        var qualityChanged = !UsesInteractiveQuality;
        interaction.Move(target);
        queuedTarget = PendingPosition = target;
        session.InteractionDiagnostics.Input(target);
        refreshPending = true;
        if (qualityChanged)
        {
            controller.InvalidatePreview();
            interactiveOperation = Task.CompletedTask;
        }
        session.RefreshPendingPlaybackPosition();
        return Task.CompletedTask;
    }
}
