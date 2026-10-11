using AegiNext.Core.Timing;
using AegiNext.Desktop.Controllers;
using AegiNext.Media.Playback;
using Avalonia.Threading;

namespace AegiNext.Desktop.Workspace;

internal sealed class PlaybackSeekingCoordinator(WorkbenchSession session, VideoPreviewController controller)
{
    private long revision;
    private readonly DispatcherTimer interactiveTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private MediaTime? queuedTarget;
    private MediaTime? interactiveTarget;
    private bool interactiveResumePlayback;
    private bool? transportResumePlayback;
    private bool seeking;
    internal bool IsInteractive { get; private set; }
    internal bool IsPlaybackRequested => IsInteractive ? interactiveResumePlayback :
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
            interactiveResumePlayback = IsPlaybackRequested;
            interactiveTarget = null;
            IsInteractive = true;
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
            var finalTarget = interactiveTarget ?? queuedTarget ?? PendingPosition;
            var resumePlayback = interactiveResumePlayback;
            IsInteractive = false;
            interactiveTimer.Stop();
            interactiveTimer.Tick -= OnInteractiveTick;
            interactiveTarget = null;
            queuedTarget = null;
            if (finalTarget is { } target)
            {
                controller.InvalidatePreview();
                _ = session.RunCommandAsync(() => SeekCoreAsync(target, false, resumePlayback));
            }
        }
        session.Tick();
    }

    private async void OnInteractiveTick(object? sender, EventArgs e)
    {
        if (seeking || queuedTarget is not { } target)
        {
            return;
        }
        queuedTarget = null;
        seeking = true;
        try
        {
            await SeekCoreAsync(target, false, interactiveResumePlayback);
        }
        finally
        {
            seeking = false;
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
        interactiveTarget = null;
        transportResumePlayback = null;
        interactiveResumePlayback = false;
        interactiveTimer.Stop();
        interactiveTimer.Tick -= OnInteractiveTick;
        IsInteractive = false;
    }

    internal Task SeekForEditingAsync(MediaTime position)
    {
        Invalidate();
        return SeekCoreAsync(position, false, null);
    }

    internal Task SeekFromUserAsync(MediaTime position)
    {
        return SeekCoreAsync(position, true, IsPlaybackRequested);
    }

    private async Task SeekCoreAsync(MediaTime position, bool clearEditingTarget, bool? resumePlayback)
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
        if (IsInteractive && resumePlayback is not null)
        {
            interactiveTarget = target;
            queuedTarget = null;
        }
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
                    await controller.SeekForInteractivePlaybackAsync(target, resume);
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
                PendingPosition = queuedTarget;
                if (!session.IsClosing)
                {
                    session.Tick();
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
        interactiveTarget = queuedTarget = PendingPosition = target;
        session.InteractionDiagnostics.Input(target);
        session.Tick();
        return Task.CompletedTask;
    }
}
