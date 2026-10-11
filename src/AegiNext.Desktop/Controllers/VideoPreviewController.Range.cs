using AegiNext.Core.Timing;
using AegiNext.Media.Audio;
using AegiNext.Media.Playback;

namespace AegiNext.Desktop.Controllers;

public sealed partial class VideoPreviewController
{
    private CancellationTokenSource? rangeCancellation;
    private Task rangeWorker = Task.CompletedTask;
    private Task rangeStop = Task.CompletedTask;
    private long rangeRevision;
    private bool mediaRangeInstalled;
    private bool rangeLoop;
    private bool audioOnlyRangeInstalled;
    private CancellationToken rangeOwnerToken;
    private MediaTimeRange? playbackScopeRange;
    private bool rangeInterruptedByAudioFailure;

    /// <summary>当前媒体是否仍由字幕范围播放任务拥有；主窗口定位和暂停会立即撤销。</summary>
    public bool IsRangePlaybackActive
    {
        get
        {
            lock (gate)
            {
                return rangeCancellation is { IsCancellationRequested: false };
            }
        }
    }

    /// <summary>在原始媒体时间范围内手动开始播放；首次开始后返回，循环由后台拥有者协调。</summary>
    public async Task PlayRangeAsync(MediaTime start, MediaTime end, bool loop, CancellationToken cancellationToken = default)
    {
        await PlayRangeCoreAsync(start, end, loop, false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>在原始媒体范围内独立试听音频，视频保持暂停位置与当前画面。</summary>
    public async Task PlayAudioRangeAsync(MediaTime start, MediaTime end, CancellationToken cancellationToken = default)
    {
        await PlayRangeCoreAsync(start, end, false, true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>判断当前正在播放的范围是否由指定取消令牌持有。</summary>
    public bool IsPlaybackRangeOwnedBy(CancellationToken ownerToken)
    {
        lock (gate)
        {
            return rangeCancellation is { IsCancellationRequested: false } && rangeOwnerToken == ownerToken;
        }
    }

    private async Task PlayRangeCoreAsync(MediaTime start, MediaTime end, bool loop, bool audioOnly,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var requested = new MediaTimeRange(start, end);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            var run = current ?? throw new InvalidOperationException("视频尚未打开。");
            if (opening || run.Error is not null || run.Session is null || run.Media is null)
            {
                throw new InvalidOperationException("视频尚未就绪。");
            }
            if (audioOnly && (run.AudioError is not null || run.Audio is not { Error: null }))
            {
                throw new InvalidOperationException("音频尚未就绪。", run.AudioError ?? run.Audio?.Error);
            }
            var lower = run.Media.Start ?? MediaTime.Zero;
            var upper = run.Media.Duration is { } duration ? lower + duration : requested.End;
            start = requested.Start > lower ? requested.Start : lower;
            end = requested.End < upper ? requested.End : upper;
            var range = new MediaTimeRange(start, end);
            CancelPlaybackRangeUnderLock();
            revision++;
            commandSequence++;
            pendingSeek = null;
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, run.Token);
            rangeCancellation = cancellation;
            rangeOwnerToken = cancellationToken;
            playbackScopeRange = range;
            rangeInterruptedByAudioFailure = false;
            rangeLoop = loop;
            var owner = ++rangeRevision;
            rangeWorker = RunPlaybackRangeAsync(run, range, owner, cancellation, started, audioOnly);
        }
        await started.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>调整当前段内播放的循环开关；暂停后调用不会开始播放或重新定位。</summary>
    public void SetPlaybackRangeLoop(bool loop)
    {
        lock (gate)
        {
            if (rangeCancellation is { IsCancellationRequested: false })
            {
                rangeLoop = loop;
            }
        }
    }

    /// <summary>仅在指定取消令牌仍拥有当前范围时调整循环开关。</summary>
    public void SetPlaybackRangeLoop(bool loop, CancellationToken ownerToken)
    {
        lock (gate)
        {
            if (rangeOwnerToken == ownerToken && rangeCancellation is { IsCancellationRequested: false })
            {
                rangeLoop = loop;
            }
        }
    }

    /// <summary>立即撤销循环拥有者并暂停，等待旧任务排空后解除媒体范围。</summary>
    public async Task ClearPlaybackRangeAsync()
    {
        await ClearPlaybackRangeCoreAsync(null).ConfigureAwait(false);
    }

    /// <summary>仅撤销指定取消令牌持有的范围，旧拥有者无法停止较新的试听。</summary>
    public async Task ClearPlaybackRangeAsync(CancellationToken ownerToken)
    {
        await ClearPlaybackRangeCoreAsync(ownerToken).ConfigureAwait(false);
    }

    private async Task ClearPlaybackRangeCoreAsync(CancellationToken? ownerToken)
    {
        Task worker;
        Task stop;
        long owner;
        bool preservePausedFrame;
        VideoPreviewRun? run;
        lock (gate)
        {
            if (rangeCancellation is null && !mediaRangeInstalled)
            {
                return;
            }
            if (ownerToken is { } expectedOwner && rangeOwnerToken != expectedOwner)
            {
                return;
            }
            preservePausedFrame = audioOnlyRangeInstalled && current?.Session?.Snapshot.State is
                VideoPlaybackState.PAUSED or VideoPlaybackState.ENDED;
            CancelPlaybackRangeUnderLock();
            worker = rangeWorker;
            stop = rangeStop;
            owner = rangeRevision;
            run = current;
        }
        await Task.WhenAll(worker, stop).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        Task clear;
        lock (gate)
        {
            if (closed || opening || owner != rangeRevision || run is null || !IsCurrentUnderLock(run) || run.Session is null)
            {
                return;
            }
            clear = ExecuteAsync(async (ready, session, operationRevision) =>
            {
                lock (gate)
                {
                    if (owner != rangeRevision)
                    {
                        return;
                    }
                    ThrowIfCommandObsoleteUnderLock(ready, operationRevision);
                }
                await ClearMediaRangeAsync(ready, session, operationRevision).ConfigureAwait(false);
            }, !preservePausedFrame);
        }
        try
        {
            await clear.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (IsRangeClearObsolete(run, owner))
        {
        }
    }

    private bool IsRangeClearObsolete(VideoPreviewRun run, long owner)
    {
        lock (gate)
        {
            return closed || owner != rangeRevision || !IsCurrentUnderLock(run);
        }
    }

    private async Task RunPlaybackRangeAsync(VideoPreviewRun run, MediaTimeRange range, long owner,
        CancellationTokenSource cancellation, TaskCompletionSource started, bool audioOnly, bool alreadyStarted = false)
    {
        var token = cancellation.Token;
        try
        {
            while (true)
            {
                if (alreadyStarted)
                {
                    alreadyStarted = false;
                }
                else if (audioOnly)
                {
                    await StartAudioRangeAsync(run, range, owner, token).ConfigureAwait(false);
                }
                else
                {
                    await ExecuteAsync(async (ready, session, operationRevision) =>
                    {
                        Task installAudio;
                        lock (gate)
                        {
                            RequireRangeOwnerUnderLock(run, owner, token);
                            ThrowIfCommandObsoleteUnderLock(ready, operationRevision);
                            mediaRangeInstalled = true;
                            audioOnlyRangeInstalled = false;
                            session.SetPlaybackRange(range);
                            installAudio = SubmitAudioCommandAsync(ready, operationRevision, audio => audio.SetPlaybackRangeAsync(range));
                        }
                        await installAudio.ConfigureAwait(false);
                        Task seek;
                        lock (gate)
                        {
                            RequireRangeOwnerUnderLock(run, owner, token);
                            ThrowIfCommandObsoleteUnderLock(ready, operationRevision);
                            seek = session.SeekAsync(range.Start, token);
                        }
                        await seek.ConfigureAwait(false);
                        Task audioPlay;
                        lock (gate)
                        {
                            RequireRangeOwnerUnderLock(run, owner, token);
                            audioPlay = SubmitAudioCommandAsync(ready, operationRevision, audio => audio.PlayAsync());
                        }
                        await audioPlay.ConfigureAwait(false);
                        Task play;
                        lock (gate)
                        {
                            RequireRangeOwnerUnderLock(run, owner, token);
                            ThrowIfCommandObsoleteUnderLock(ready, operationRevision);
                            ready.SeekPreviewState = null;
                            play = session.PlayAsync(token);
                        }
                        await play.ConfigureAwait(false);
                    }, true).ConfigureAwait(false);
                }
                started.TrySetResult();
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    lock (gate)
                    {
                        RequireRangeOwnerUnderLock(run, owner, token);
                    }
                    if (audioOnly && (run.AudioError is not null || run.Audio?.Error is not null))
                    {
                        throw new InvalidOperationException("音频试听失败。", run.AudioError ?? run.Audio?.Error);
                    }
                    if (audioOnly ? run.Audio is { ReachedPlaybackRangeEnd: true }
                        : run.Session!.Snapshot.State == VideoPlaybackState.ENDED)
                    {
                        break;
                    }
                    await Task.Delay(5, token).ConfigureAwait(false);
                }
                lock (gate)
                {
                    RequireRangeOwnerUnderLock(run, owner, token);
                    if (!rangeLoop)
                    {
                        break;
                    }
                }
            }
            var finish = Task.CompletedTask;
            lock (gate)
            {
                RequireRangeOwnerUnderLock(run, owner, token);
                if (run.Audio is { } finishedAudio)
                {
                    finish = TryAudioAsync(run, finishedAudio, finishedAudio.PauseAsync);
                }
            }
            await finish.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            started.TrySetCanceled(token.IsCancellationRequested ? token : new(true));
        }
        catch (ObjectDisposedException)
        {
            started.TrySetCanceled(new CancellationToken(true));
        }
        catch (Exception error)
        {
            started.TrySetException(error);
            lock (gate)
            {
                if (IsCurrentUnderLock(run) && owner == rangeRevision)
                {
                    if (audioOnly)
                    {
                        run.AudioError = error;
                        rangeInterruptedByAudioFailure = true;
                    }
                    else
                    {
                        run.Error = error;
                    }
                }
            }
        }
        finally
        {
            var stop = Task.CompletedTask;
            long? completionRevision = null;
            lock (gate)
            {
                if (!closed && owner == rangeRevision && IsCurrentUnderLock(run) && !run.Token.IsCancellationRequested && run.Session is { } session)
                {
                    stop = Task.WhenAll(run.Audio is { } audio ? TryAudioAsync(run, audio, audio.PauseAsync) : Task.CompletedTask,
                        session.Snapshot.State == VideoPlaybackState.PLAYING ? PauseRangeSessionAsync(session) : Task.CompletedTask);
                }
                if (ReferenceEquals(rangeCancellation, cancellation))
                {
                    rangeCancellation = null;
                    completionRevision = revision;
                }
            }
            await stop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            cancellation.Dispose();
            if (completionRevision is { } completed)
            {
                try
                {
                    await DispatchAsync(run, null, null, false, run.Token, completed).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (run.Token.IsCancellationRequested)
                {
                }
            }
        }
    }

    private async Task StartAudioRangeAsync(VideoPreviewRun run, MediaTimeRange range, long owner,
        CancellationToken token)
    {
        await ExecuteAsync(async (ready, session, operationRevision) =>
        {
            Task pause;
            lock (gate)
            {
                RequireRangeOwnerUnderLock(run, owner, token);
                ThrowIfCommandObsoleteUnderLock(ready, operationRevision);
                pause = session.Snapshot.State == VideoPlaybackState.PLAYING
                    ? session.PauseAsync(token) : Task.CompletedTask;
            }
            await pause.ConfigureAwait(false);
            Task installAudio;
            lock (gate)
            {
                RequireRangeOwnerUnderLock(run, owner, token);
                ThrowIfCommandObsoleteUnderLock(ready, operationRevision);
                session.SetPlaybackRange(null);
                mediaRangeInstalled = true;
                audioOnlyRangeInstalled = true;
                installAudio = SubmitAudioCommandAsync(ready, operationRevision, audio => audio.SetPlaybackRangeAsync(range));
            }
            await installAudio.ConfigureAwait(false);
            Task audioPlay;
            lock (gate)
            {
                RequireRangeOwnerUnderLock(run, owner, token);
                ThrowIfCommandObsoleteUnderLock(ready, operationRevision);
                audioPlay = SubmitAudioCommandAsync(ready, operationRevision, audio => audio.PlayAsync());
            }
            await audioPlay.ConfigureAwait(false);
            lock (gate)
            {
                RequireRangeOwnerUnderLock(run, owner, token);
            }
        }, false).ConfigureAwait(false);
    }

    private void RequireRangeOwnerUnderLock(VideoPreviewRun run, long owner, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (closed || owner != rangeRevision || !IsCurrentUnderLock(run))
        {
            throw new OperationCanceledException("字幕范围播放已被新的操作替换。", token);
        }
    }

    private void CancelPlaybackRangeUnderLock()
    {
        if (rangeCancellation is null && !mediaRangeInstalled)
        {
            return;
        }
        rangeInterruptedByAudioFailure = false;
        rangeCancellation?.Cancel();
        rangeCancellation = null;
        rangeRevision++;
        revision++;
        commandSequence++;
        pendingSeek = null;
        if (current is { Token.IsCancellationRequested: false, Session: { } session } run &&
            session.Snapshot.State is VideoPlaybackState.PAUSED or VideoPlaybackState.PLAYING or VideoPlaybackState.ENDED)
        {
            rangeStop = Task.WhenAll(run.Audio is { } audio ? TryAudioAsync(run, audio, audio.PauseAsync) : Task.CompletedTask,
                session.Snapshot.State == VideoPlaybackState.PAUSED ? Task.CompletedTask : PauseRangeSessionAsync(session));
        }
    }

    private async Task ClearMediaRangeAsync(VideoPreviewRun run, VideoPlaybackSession session, long operationRevision)
    {
        Task clearAudio;
        bool restoreAudioPosition;
        lock (gate)
        {
            ThrowIfCommandObsoleteUnderLock(run, operationRevision);
            if (!mediaRangeInstalled)
            {
                return;
            }
            mediaRangeInstalled = false;
            restoreAudioPosition = audioOnlyRangeInstalled;
            audioOnlyRangeInstalled = false;
            rangeOwnerToken = default;
            playbackScopeRange = null;
            rangeInterruptedByAudioFailure = false;
            session.SetPlaybackRange(null);
            clearAudio = SubmitAudioCommandAsync(run, operationRevision, audio => audio.SetPlaybackRangeAsync(null));
        }
        await clearAudio.ConfigureAwait(false);
        if (restoreAudioPosition)
        {
            await SubmitAudioCommandAsync(run, operationRevision, audio => audio.SeekAsync(session.Snapshot.Position))
                .ConfigureAwait(false);
        }
    }

    private Task SubmitAudioCommandAsync(VideoPreviewRun run, long operationRevision, Func<AudioPlaybackSession, Task> command)
    {
        lock (gate)
        {
            ThrowIfCommandObsoleteUnderLock(run, operationRevision);
            return run.AudioError is null && run.Audio is { Error: null } audio
                ? TryAudioAsync(run, audio, () => command(audio)) : Task.CompletedTask;
        }
    }

    private static async Task PauseRangeSessionAsync(VideoPlaybackSession session)
    {
        await session.PauseAsync().ConfigureAwait(false);
    }
}
