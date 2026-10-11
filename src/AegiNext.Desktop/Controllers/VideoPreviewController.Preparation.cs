using AegiNext.Core.Timing;
using AegiNext.Media.Playback;
using AegiNext.Media.Preview;
using AegiNext.Desktop.Rendering;

namespace AegiNext.Desktop.Controllers;

public sealed partial class VideoPreviewController
{
    private const long MAXIMUM_PREPARED_BYTES = 128L * 1024 * 1024;
    private const int PREPARATION_OBSERVATION_COUNT = 16;

    internal string PipelineDiagnostics
    {
        get
        {
            lock (gate)
            {
                return current is { Session: { } session } run
                    ? run.Diagnostics.Describe(session.PreparationLead, run.ConversionLead, run.DispatchLead)
                    : "No active preview run.";
            }
        }
    }

    private async Task PrepareFramesAsync(VideoPreviewRun run, VideoPlaybackSession session,
        IVideoPreviewConverter converter, SynchronousMediaWorker conversionWorker, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await AcquirePreparedSlotAsync(run, session, cancellationToken).ConfigureAwait(false);
            var queued = false;
            try
            {
                Task resume;
                lock (gate)
                {
                    RefreshPreparationLeadUnderLock(run, session);
                    resume = run.Resume.Task;
                }
                using var presentation = await session.ReadPreparationAsync(cancellationToken).ConfigureAwait(false);
                if (presentation is null)
                {
                    run.PreparedSlots.Release();
                    queued = true;
                    var pause = Task.CompletedTask;
                    lock (gate)
                    {
                        if (rangeCancellation is null && IsCurrentUnderLock(run) && session.Snapshot.State == VideoPlaybackState.ENDED &&
                            run.AudioError is null && run.Audio is { Error: null } audio)
                        {
                            pause = TryAudioAsync(run, audio, audio.PauseAsync);
                        }
                    }
                    await pause.ConfigureAwait(false);
                    await resume.WaitAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                VideoPreviewDelivery identity;
                ProjectPreviewState? seekState;
                lock (gate)
                {
                    seekState = run.SeekPreviewGeneration == presentation.Generation && session.Snapshot.State != VideoPlaybackState.PLAYING
                        ? run.SeekPreviewState : null;
                    if (seekState is not null && run.SeekPreviewRevision != revision)
                    {
                        continue;
                    }
                    identity = new(session, presentation.Generation, revision,
                        presentation.PositionedFrame.Time, presentation.PositionedFrame.NextFrameTime,
                        run.PreparationCancellation.Token);
                    var playback = session.Snapshot;
                    run.Diagnostics.Candidates++;
                    RefreshPreparationLeadUnderLock(run, session);
                    var conversionLead = GetExpectedPreparationCostUnderLock(run.ConversionCosts, session.MaximumPreparationAhead);
                    if (!IsPresentationCurrentUnderLock(run, identity) || playback.State == VideoPlaybackState.PLAYING &&
                        identity.NextTime is { } next && next <= playback.Position + conversionLead)
                    {
                        run.Diagnostics.CandidateSkips++;
                        run.Diagnostics.Record(new("candidate-skipped", identity.Time, identity.NextTime, playback.Position, conversionLead));
                        continue;
                    }
                }

                using var conversion = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, identity.PreparationToken);
                lock (gate)
                {
                    if (!IsPresentationCurrentUnderLock(run, identity))
                    {
                        continue;
                    }
                    run.ConversionCancellation = conversion;
                    if (seekState is not null && ReferenceEquals(run.SeekPreviewState, seekState))
                    {
                        run.SeekPreviewState = null;
                    }
                }
                try
                {
                    var started = session.TimeProvider.GetTimestamp();
                    var frame = await conversionWorker.ExecuteAsync(
                        () => seekState is not null && converter is ICachedVideoPreviewConverter cached
                            ? cached.Convert(presentation.PositionedFrame, seekState, conversion.Token)
                            : converter.Convert(presentation.PositionedFrame, conversion.Token), conversion.Token).ConfigureAwait(false);
                    var elapsed = MediaTime.FromTimeSpan(session.TimeProvider.GetElapsedTime(started));
                    PreparedVideoPreview prepared;
                    bool catchup;
                    lock (gate)
                    {
                        run.Diagnostics.Conversions++;
                        run.Diagnostics.LastConversionCost = elapsed;
                        run.Diagnostics.Record(new("converted", identity.Time, identity.NextTime, session.Snapshot.Position, elapsed));
                        ObservePreparationCostUnderLock(run, session, elapsed, false);
                        if (!IsPresentationCurrentUnderLock(run, identity) || conversion.IsCancellationRequested)
                        {
                            continue;
                        }
                        prepared = new(identity, frame, converter.GetRetainedBytes(frame));
                        catchup = identity.NextTime is { } next && next - identity.Time <= GetDownstreamPreparationLeadUnderLock(run, session);
                        if (prepared.Bytes < frame.Pixels.Length || prepared.Bytes > MAXIMUM_PREPARED_BYTES / 2 ||
                            run.PreparedBytes + prepared.Bytes > MAXIMUM_PREPARED_BYTES)
                        {
                            throw new NotSupportedException("预览图像超过提前准备的内存预算。");
                        }
                        run.PreparedFrames.Enqueue(prepared);
                        run.PreparedFrameCount++;
                        catchup &= run.PreparedFrameCount == 1;
                        run.PreparedBytes += prepared.Bytes;
                        queued = true;
                        PulsePreparedUnderLock(run);
                    }
                    presentation.Dispose();
                    if (catchup)
                    {
                        await prepared.Submitted.Task.WaitAsync(conversion.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        await Task.Yield();
                    }
                }
                catch (OperationCanceledException) when (conversion.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                }
                finally
                {
                    lock (gate)
                    {
                        if (ReferenceEquals(run.ConversionCancellation, conversion))
                        {
                            run.ConversionCancellation = null;
                        }
                    }
                }
            }
            finally
            {
                if (!queued)
                {
                    run.PreparedSlots.Release();
                }
            }
        }
    }

    private async Task AcquirePreparedSlotAsync(VideoPreviewRun run, VideoPlaybackSession session, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task changed;
            MediaTime? expiry;
            long generation;
            lock (gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var playback = session.Snapshot;
                while (run.PreparedFrames.TryPeek(out var prepared) &&
                    (!IsPresentationCurrentUnderLock(run, prepared.Identity) || playback.State == VideoPlaybackState.PLAYING &&
                        prepared.Identity.NextTime is { } end && end <= playback.Position))
                {
                    run.PreparedFrames.Dequeue();
                    run.Diagnostics.QueuedReclaims++;
                    run.Diagnostics.Record(new("queued-expired-reclaimed", prepared.Identity.Time, prepared.Identity.NextTime, playback.Position, MediaTime.Zero));
                    ReleasePreparedFrameUnderLock(run, prepared);
                }
                if (run.PreparedSlots.Wait(0, cancellationToken))
                {
                    return;
                }
                changed = run.PreparedChanged.Task;
                expiry = playback.State == VideoPlaybackState.PLAYING && run.PreparedFrames.TryPeek(out var queued)
                    ? queued.Identity.NextTime : null;
                generation = playback.Generation;
            }
            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var permit = run.PreparedSlots.WaitAsync(waiting.Token);
            var deadline = expiry is { } endTime
                ? session.WaitForPositionAsync(endTime, generation, waiting.Token)
                : changed.WaitAsync(waiting.Token);
            await Task.WhenAny(permit, deadline, changed).ConfigureAwait(false);
            await waiting.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(permit, deadline).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (permit.IsCompletedSuccessfully)
            {
                return;
            }
        }
    }

    private async Task PresentPreparedFramesAsync(VideoPreviewRun run, VideoPlaybackSession session,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            PreparedVideoPreview? prepared;
            Task changed;
            lock (gate)
            {
                run.PreparedFrames.TryDequeue(out prepared);
                changed = run.PreparedChanged.Task;
            }
            if (prepared is null)
            {
                await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }
            using var deliveryCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, prepared.Identity.PreparationToken);
            var delivery = new PreparedVideoPreviewDelivery(prepared);
            try
            {
                lock (gate)
                {
                    if (!IsPresentationCurrentUnderLock(run, delivery.Prepared.Identity))
                    {
                        continue;
                    }
                }
                while (true)
                {
                    MediaTime target;
                    var wasWaiting = false;
                    lock (gate)
                    {
                        RefreshPreparationLeadUnderLock(run, session);
                        var playback = session.Snapshot;
                        target = GetDispatchTargetUnderLock(run, session, delivery.Prepared.Identity, playback.State == VideoPlaybackState.PLAYING);
                        wasWaiting = playback.State == VideoPlaybackState.PLAYING && target > playback.Position;
                    }
                    await session.WaitForPositionAsync(target, delivery.Prepared.Identity.Generation,
                        deliveryCancellation.Token).ConfigureAwait(false);
                    if (wasWaiting)
                    {
                        lock (gate)
                        {
                            var playback = session.Snapshot;
                            if (IsPresentationCurrentUnderLock(run, delivery.Prepared.Identity) && playback.State == VideoPlaybackState.PLAYING)
                            {
                                var waitCost = playback.Position > target ? playback.Position - target : MediaTime.Zero;
                                RecordPreparationCostUnderLock(run.DispatchWaitCosts, session, waitCost);
                                run.Diagnostics.Record(new("dispatch-wait", delivery.Prepared.Identity.Time, delivery.Prepared.Identity.NextTime, playback.Position, waitCost));
                            }
                        }
                    }
                    var result = await DispatchAsync(run, delivery.Prepared.Identity, delivery.Prepared.Frame, false,
                        deliveryCancellation.Token, preparedDelivery: delivery).ConfigureAwait(false);
                    if (result != VideoPreviewDispatchStatus.EARLY)
                    {
                        break;
                    }
                    lock (gate)
                    {
                        if (delivery.Prepared.Identity.NextTime is { } end && session.Snapshot.Position + GetExpectedDispatchCostUnderLock(run, session) >= end)
                        {
                            run.Diagnostics.SkippedEarlyRetries++;
                            run.Diagnostics.Record(new("early-retry-skipped", delivery.Prepared.Identity.Time, end, session.Snapshot.Position, run.DispatchLead));
                            break;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (deliveryCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
            }
            finally
            {
                lock (gate)
                {
                    ReleasePreparedFrameUnderLock(run, delivery.Prepared);
                }
            }
        }
    }

    private static async Task CancelCompanionOnExitAsync(Func<Task> action, CancellationTokenSource cancellation)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        finally
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
        }
    }

    private static void PulsePreparedUnderLock(VideoPreviewRun run)
    {
        var previous = run.PreparedChanged;
        run.PreparedChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        previous.TrySetResult();
    }

    private static void ClearPreparedFramesUnderLock(VideoPreviewRun? run)
    {
        if (run is null)
        {
            return;
        }
        run.PreparationCancellation.Cancel();
        run.PreparationCancellation.Dispose();
        run.PreparationCancellation = CancellationTokenSource.CreateLinkedTokenSource(run.Token);
        while (run.PreparedFrames.TryDequeue(out var prepared))
        {
            ReleasePreparedFrameUnderLock(run, prepared);
        }
        PulsePreparedUnderLock(run);
    }

    private static void ReleasePreparedFrameUnderLock(VideoPreviewRun run, PreparedVideoPreview prepared)
    {
        run.PreparedFrameCount--;
        run.PreparedBytes -= prepared.Bytes;
        prepared.Submitted.TrySetResult();
        run.PreparedSlots.Release();
    }

    private void SelectPreparedFrameUnderLock(VideoPreviewRun run, PreparedVideoPreviewDelivery delivery, MediaTime position)
    {
        if (!IsDeliveryIdentityCurrentUnderLock(run, delivery.Prepared.Identity))
        {
            return;
        }
        while (run.PreparedFrames.TryPeek(out var next))
        {
            if (!IsPresentationCurrentUnderLock(run, next.Identity))
            {
                run.PreparedFrames.Dequeue();
                ReleasePreparedFrameUnderLock(run, next);
                continue;
            }
            var current = delivery.Prepared.Identity;
            var expired = current.NextTime is { } end && end <= position;
            if (!expired && next.Identity.Time > position)
            {
                return;
            }
            run.PreparedFrames.Dequeue();
            ReleasePreparedFrameUnderLock(run, delivery.Prepared);
            delivery.Prepared = next;
            run.Diagnostics.Record(new("selected-successor", next.Identity.Time, next.Identity.NextTime, position, MediaTime.Zero));
        }
    }

    private static void ObservePreparationCostUnderLock(VideoPreviewRun run, VideoPlaybackSession session,
        MediaTime elapsed, bool dispatchCost)
    {
        var samples = dispatchCost ? run.DispatchCosts : run.ConversionCosts;
        RecordPreparationCostUnderLock(samples, session, elapsed);
        RefreshPreparationLeadUnderLock(run, session);
    }

    private static void RecordPreparationCostUnderLock(Queue<ObservedVideoPreparationCost> samples,
        VideoPlaybackSession session, MediaTime elapsed)
    {
        samples.Enqueue(new(elapsed, session.TimeProvider.GetTimestamp()));
        while (samples.Count > PREPARATION_OBSERVATION_COUNT)
        {
            samples.Dequeue();
        }
    }

    private static void RefreshPreparationLeadUnderLock(VideoPreviewRun run, VideoPlaybackSession session)
    {
        var timestamp = session.TimeProvider.GetTimestamp();
        var maximumAge = session.MaximumPreparationAhead.ToTimeSpan(MediaTimeRounding.CEILING);
        foreach (var samples in new[] { run.ConversionCosts, run.DispatchCosts, run.DispatchWaitCosts })
        {
            while (samples.TryPeek(out var sample) && session.TimeProvider.GetElapsedTime(sample.Timestamp, timestamp) >= maximumAge)
            {
                samples.Dequeue();
            }
        }
        run.ConversionLead = run.ConversionCosts.Count == 0 ? MediaTime.Zero : run.ConversionCosts.Max(sample => sample.Cost);
        run.DispatchLead = run.DispatchCosts.Count == 0 ? MediaTime.Zero : run.DispatchCosts.Max(sample => sample.Cost);
        var conversionLead = GetExpectedPreparationCostUnderLock(run.ConversionCosts, session.MaximumPreparationAhead);
        var dispatchLead = GetExpectedDispatchCostUnderLock(run, session);
        var minimumLead = conversionLead + dispatchLead;
        if (run.ActivePreparedDispatch is { } active && ReferenceEquals(active.Session, session) &&
            active.Generation == session.Snapshot.Generation && !active.PreparationToken.IsCancellationRequested)
        {
            var elapsed = MediaTime.FromTimeSpan(session.TimeProvider.GetElapsedTime(run.ActivePreparedDispatchStarted, timestamp));
            var remaining = dispatchLead > elapsed ? dispatchLead - elapsed : MediaTime.Zero;
            minimumLead = conversionLead > remaining ? conversionLead : remaining;
        }
        session.SetPreparationLead(conversionLead + dispatchLead, minimumLead);
    }

    private static MediaTime GetDownstreamPreparationLeadUnderLock(VideoPreviewRun run, VideoPlaybackSession session)
    {
        var lead = run.ConversionLead + run.DispatchLead;
        return lead < session.MaximumPreparationAhead ? lead : session.MaximumPreparationAhead;
    }

    private static MediaTime GetExpectedDispatchCostUnderLock(VideoPreviewRun run, VideoPlaybackSession session)
    {
        return GetExpectedPreparationCostUnderLock(run.DispatchCosts, session.MaximumPreparationAhead);
    }

    private static MediaTime GetExpectedPreparationCostUnderLock(Queue<ObservedVideoPreparationCost> costs, MediaTime maximum)
    {
        if (costs.Count == 0)
        {
            return MediaTime.Zero;
        }
        var samples = costs.Select(sample => sample.Cost).Order().ToArray();
        var middle = samples.Length / 2;
        var cost = samples.Length % 2 == 0
            ? ObservedVideoPreparationCost.Quantize((samples[middle - 1] + samples[middle]) / 2)
            : samples[middle];
        return cost < maximum ? cost : maximum;
    }

    private static MediaTime GetDispatchTargetUnderLock(VideoPreviewRun run, VideoPlaybackSession session,
        VideoPreviewDelivery delivery, bool playing)
    {
        var expectedCost = GetExpectedDispatchCostUnderLock(run, session);
        if (!playing || expectedCost <= MediaTime.Zero || delivery.NextTime is not { } next)
        {
            return delivery.Time - expectedCost;
        }
        var wakeCost = run.DispatchWaitCosts.Count == 0 ? MediaTime.Zero :
            ObservedVideoPreparationCost.Quantize(
                (run.DispatchWaitCosts.Min(sample => sample.Cost) + run.DispatchWaitCosts.Max(sample => sample.Cost)) / 2);
        var lead = expectedCost + wakeCost;
        lead = lead < session.MaximumPreparationAhead ? lead : session.MaximumPreparationAhead;
        var bias = (next - delivery.Time) / 2;
        bias = bias < expectedCost ? bias : expectedCost;
        return delivery.Time + bias - lead;
    }
}
