using AegiNext.Core.Timing;
using AegiNext.Desktop.Rendering;
using AegiNext.Media.Playback;

namespace AegiNext.Desktop.Controllers;

public sealed partial class VideoPreviewController
{
    private static readonly MediaTime MAXIMUM_CACHED_PREVIEW_DISTANCE = new(1, 10);

    private void CaptureSeekPreviewStateUnderLock(VideoPreviewRun run, VideoPlaybackSession session, MediaTime target)
    {
        var state = run.CachedConverter?.CaptureState();
        run.SeekPreviewState = state is null ? null : state with
        {
            TargetTime = target - (state.Document.Media?.MediaOrigin ?? MediaTime.Zero),
            EvaluateAtTarget = true
        };
        run.SeekPreviewGeneration = session.Snapshot.Generation;
        run.SeekPreviewRevision = revision;
    }

    private Task StartCachedPreviewUnderLock(VideoPreviewRun run, VideoPlaybackSession session, MediaTime target,
        long presentationRevision)
    {
        if (!run.CachedPreviewOperation.IsCompleted || run.SeekPreviewState is not { IsInteractive: true } state ||
            run.CachedConverter is not { } converter || run.ConversionWorker is not { } worker ||
            !converter.HasCachedFrame(target, MAXIMUM_CACHED_PREVIEW_DISTANCE, state))
        {
            return Task.CompletedTask;
        }

        var generation = session.Snapshot.Generation;
        var token = run.PreparationCancellation.Token;
        run.CachedPreviewOperation = PresentCachedPreviewAsync(run, session, converter, worker, state, target,
            generation, presentationRevision, token);
        return run.CachedPreviewOperation;
    }

    private async Task PresentCachedPreviewAsync(VideoPreviewRun run, VideoPlaybackSession session,
        ICachedVideoPreviewConverter converter, SynchronousMediaWorker worker, ProjectPreviewState state,
        MediaTime target, long generation, long presentationRevision, CancellationToken token)
    {
        try
        {
            var cached = await worker.ExecuteAsync(() =>
                converter.TryConvertCached(target, MAXIMUM_CACHED_PREVIEW_DISTANCE, state, token, out var result)
                    ? result : null, token).ConfigureAwait(false);
            if (cached is null)
            {
                return;
            }

            await dispatchGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await dispatch(() =>
                {
                    VideoPreviewSnapshot snapshot;
                    lock (gate)
                    {
                        if (!IsCurrentUnderLock(run) || run.Error is not null || token.IsCancellationRequested ||
                            revision != presentationRevision || session.Snapshot.Generation != generation ||
                            run.PresentedGeneration == generation && run.PresentedRevision == presentationRevision)
                        {
                            return;
                        }
                        snapshot = GetSnapshotUnderLock();
                    }

                    present(new(snapshot, cached.Frame, false)
                    {
                        IsTransientPreview = true,
                        SourceFrameTime = cached.Time,
                        SourceFrameEnd = cached.NextFrameTime,
                        RequestedPosition = target
                    });
                }, token).ConfigureAwait(false);
            }
            finally
            {
                dispatchGate.Release();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }
}
