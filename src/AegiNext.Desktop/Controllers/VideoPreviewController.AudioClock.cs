using AegiNext.Core.Timing;
using AegiNext.Media.Audio;
using AegiNext.Media.Playback;

namespace AegiNext.Desktop.Controllers;

public sealed partial class VideoPreviewController
{
    private Func<AudioOutputClockSnapshot, MediaTime>? audioCalibration;

    public AudioOutputClockSnapshot? AudioClock
    {
        get
        {
            lock (gate)
            {
                return opening ? null : current?.Audio?.ClockSnapshot;
            }
        }
    }

    /// <summary>配置新音频会话使用的设备校准提供方；回调必须线程安全且不能修改工程。</summary>
    public void ConfigureAudioCalibration(Func<AudioOutputClockSnapshot, MediaTime>? provider)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            audioCalibration = provider;
            if (current?.Audio is { } audio)
            {
                audio.ConfigureCalibration(provider);
            }
        }
    }

    /// <summary>暂停输出并更新校准，在原始位置恢复同一播放范围、循环和试听拥有者。</summary>
    public Task ApplyAudioCalibrationAsync(Action updateCalibration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(updateCalibration);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            if (current?.Audio is null && (!opening || current is null))
            {
                updateCalibration();
                return Task.CompletedTask;
            }
            return ChangeAudioOutputAsync(updateCalibration, false, cancellationToken);
        }
    }

    /// <summary>重新打开当前设备，保留原始位置、播放范围、循环及独立音频试听的暂停画面。</summary>
    public Task ReopenAudioOutputAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ChangeAudioOutputAsync(null, true, cancellationToken);
    }

    private Task ChangeAudioOutputAsync(Action? updateCalibration, bool reopen, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            var run = current ?? throw new InvalidOperationException("视频尚未打开。");
            if (opening || run.Error is not null || run.Session is not { } session || run.Token.IsCancellationRequested)
            {
                throw new InvalidOperationException("视频尚未就绪。");
            }
            var scope = CaptureAudioOutputScopeUnderLock(run, session);
            var previousRange = rangeWorker;
            CancelPlaybackRangeUnderLock();
            var previousStop = rangeStop;
            return ExecuteAsync((ready, playback, operationRevision) => ChangeAudioOutputCoreAsync(ready, playback,
                operationRevision, scope, previousRange, previousStop, updateCalibration, reopen, cancellationToken), !scope.AudioOnly);
        }
    }

    private AudioOutputPlaybackScope CaptureAudioOutputScopeUnderLock(VideoPreviewRun run, VideoPlaybackSession session)
    {
        var snapshot = session.Snapshot;
        var range = mediaRangeInstalled ? playbackScopeRange : null;
        var audioOnly = range is not null && audioOnlyRangeInstalled;
        var rangeActive = rangeCancellation is { IsCancellationRequested: false } || rangeInterruptedByAudioFailure;
        var resume = snapshot.State == VideoPlaybackState.PLAYING ||
            rangeActive && (audioOnly || rangeLoop && snapshot.State == VideoPlaybackState.ENDED);
        return new(snapshot.Position, audioOnly ? run.Audio?.Position ?? snapshot.Position : snapshot.Position,
            resume, range, rangeLoop, audioOnly, rangeOwnerToken);
    }

    private async Task ChangeAudioOutputCoreAsync(VideoPreviewRun run, VideoPlaybackSession session, long operationRevision,
        AudioOutputPlaybackScope scope, Task previousRange, Task previousStop, Action? updateCalibration, bool reopen,
        CancellationToken cancellationToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(run.Token, cancellationToken,
            scope.Range is not null && scope.WasPlaying ? scope.OwnerToken : default);
        var token = cancellation.Token;
        await Task.WhenAll(previousRange, previousStop).WaitAsync(token).ConfigureAwait(false);
        Task pause;
        lock (gate)
        {
            RequireAudioOutputChangeUnderLock(run, operationRevision, token);
            pause = Task.WhenAll(run.Audio is { } audio ? TryAudioAsync(run, audio, audio.PauseAsync) : Task.CompletedTask,
                scope.AudioOnly ? Task.CompletedTask : session.PauseAsync(token));
        }
        await pause.WaitAsync(token).ConfigureAwait(false);
        lock (gate)
        {
            RequireAudioOutputChangeUnderLock(run, operationRevision, token);
            updateCalibration?.Invoke();
            reopen |= run.Audio is null || run.AudioError is not null || run.Audio.Error is not null ||
                run.Audio.ClockSnapshot.Quality == AudioClockQuality.UNAVAILABLE;
        }
        if (reopen)
        {
            await ReplaceAudioOutputAsync(run, operationRevision, scope.AudioPosition, token).ConfigureAwait(false);
        }

        Task install;
        lock (gate)
        {
            RequireAudioOutputChangeUnderLock(run, operationRevision, token);
            mediaRangeInstalled = scope.Range is not null;
            audioOnlyRangeInstalled = scope.AudioOnly;
            playbackScopeRange = scope.Range;
            rangeLoop = scope.Loop;
            rangeOwnerToken = scope.Range is not null ? scope.OwnerToken : default;
            session.SetPlaybackRange(scope.AudioOnly ? null : scope.Range);
            install = SubmitAudioCommandAsync(run, operationRevision, audio => audio.SetPlaybackRangeAsync(scope.Range));
        }
        await install.WaitAsync(token).ConfigureAwait(false);
        if (!scope.AudioOnly)
        {
            Task seek;
            lock (gate)
            {
                RequireAudioOutputChangeUnderLock(run, operationRevision, token);
                seek = session.SeekAsync(scope.VideoPosition, token);
            }
            await seek.ConfigureAwait(false);
        }
        Task audioSeek;
        lock (gate)
        {
            RequireAudioOutputChangeUnderLock(run, operationRevision, token);
            audioSeek = SubmitAudioCommandAsync(run, operationRevision, audio => audio.SeekAsync(scope.AudioPosition));
        }
        await audioSeek.WaitAsync(token).ConfigureAwait(false);
        if (!scope.WasPlaying)
        {
            return;
        }
        Task audioPlay;
        lock (gate)
        {
            RequireAudioOutputChangeUnderLock(run, operationRevision, token);
            audioPlay = SubmitAudioCommandAsync(run, operationRevision, audio => audio.PlayAsync());
        }
        await audioPlay.WaitAsync(token).ConfigureAwait(false);
        if (!scope.AudioOnly)
        {
            Task play;
            lock (gate)
            {
                RequireAudioOutputChangeUnderLock(run, operationRevision, token);
                run.SeekPreviewState = null;
                play = session.PlayAsync(token);
            }
            await play.ConfigureAwait(false);
        }
        lock (gate)
        {
            RequireAudioOutputChangeUnderLock(run, operationRevision, token);
            if (scope.Range is { } range)
            {
                var ownerCancellation = CancellationTokenSource.CreateLinkedTokenSource(scope.OwnerToken, run.Token);
                rangeCancellation = ownerCancellation;
                var owner = ++rangeRevision;
                rangeWorker = RunPlaybackRangeAsync(run, range, owner, ownerCancellation,
                    new(TaskCreationOptions.RunContinuationsAsynchronously), scope.AudioOnly, true);
            }
        }
    }

    private async Task ReplaceAudioOutputAsync(VideoPreviewRun run, long operationRevision, MediaTime target,
        CancellationToken cancellationToken)
    {
        if (run.Audio is { } previous)
        {
            await previous.DisposeAsync().ConfigureAwait(false);
        }
        int index;
        lock (gate)
        {
            RequireAudioOutputChangeUnderLock(run, operationRevision, cancellationToken);
            if (audioFactory is null || run.Media?.AudioStreamIndex is not { } audioIndex)
            {
                throw new InvalidOperationException("当前媒体没有可重建的音频输出。");
            }
            index = audioIndex;
        }
        var replacement = await audioFactory(run.Path, index, target, cancellationToken).ConfigureAwait(false);
        var attached = false;
        try
        {
            lock (gate)
            {
                RequireAudioOutputChangeUnderLock(run, operationRevision, cancellationToken);
                replacement.ConfigureCalibration(audioCalibration);
                replacement.SetGain(muted ? 0 : volume);
                run.AttachAudio(replacement);
                run.AudioError = null;
                attached = true;
            }
        }
        finally
        {
            if (!attached)
            {
                await replacement.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private void RequireAudioOutputChangeUnderLock(VideoPreviewRun run, long operationRevision, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfCommandObsoleteUnderLock(run, operationRevision);
    }
}
