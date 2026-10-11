using AegiNext.Core.Timing;
using AegiNext.Media.Audio;
using AegiNext.Media.Playback;

namespace AegiNext.Desktop.Controllers;

internal sealed class VideoPreviewRun : IDisposable
{
    private readonly Lock gate = new();
    private readonly CancellationTokenSource cancellation = new();
    private VideoPlaybackSession? session;
    private AudioPlaybackSession? audio;
    private Task? stopTask;
    private bool disposed;

    internal VideoPreviewRun(long epoch, string path)
    {
        Epoch = epoch;
        Path = path;
        Token = cancellation.Token;
        PreparationCancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
    }

    internal long Epoch { get; }
    internal string Path { get; }
    internal TaskCompletionSource<bool> FirstPresentation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource PresentationChanged { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal CancellationToken Token { get; }
    internal VideoPreviewMedia? Media { get; set; }
    internal Exception? Error { get; set; }
    internal Exception? AudioError { get; set; }
    internal MediaTime? PresentedFrameTime { get; set; }
    internal MediaTime? PresentedAtPosition { get; set; }
    internal long? PresentedGeneration { get; set; }
    internal long? PresentedRevision { get; set; }
    internal CancellationTokenSource? ConversionCancellation { get; set; }
    internal CancellationTokenSource PreparationCancellation { get; set; }
    internal SemaphoreSlim PreparedSlots { get; } = new(2, 2);
    internal Queue<PreparedVideoPreview> PreparedFrames { get; } = new();
    internal TaskCompletionSource PreparedChanged { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Queue<ObservedVideoPreparationCost> ConversionCosts { get; } = new();
    internal Queue<ObservedVideoPreparationCost> DispatchCosts { get; } = new();
    internal Queue<ObservedVideoPreparationCost> DispatchWaitCosts { get; } = new();
    internal MediaTime ConversionLead { get; set; }
    internal MediaTime DispatchLead { get; set; }
    internal VideoPreviewDelivery? ActivePreparedDispatch { get; set; }
    internal long ActivePreparedDispatchStarted { get; set; }
    internal VideoPreviewPipelineDiagnostics Diagnostics { get; } = new();
    internal int PreparedFrameCount { get; set; }
    internal long PreparedBytes { get; set; }
    internal MediaTime? PresentedFrameEnd { get; set; }
    internal Task Pump { get; set; } = Task.CompletedTask;
    internal TaskCompletionSource<bool> Resume { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal VideoPlaybackSession? Session
    {
        get
        {
            lock (gate)
            {
                return session;
            }
        }
    }

    internal AudioPlaybackSession? Audio
    {
        get
        {
            lock (gate)
            {
                return audio;
            }
        }
    }

    internal void AttachAudio(AudioPlaybackSession value)
    {
        lock (gate)
        {
            if (stopTask is not null)
            {
                throw new OperationCanceledException("预览运行已开始关闭。", Token);
            }
            Token.ThrowIfCancellationRequested();
            audio = value;
        }
    }

    internal void Attach(VideoPlaybackSession value)
    {
        lock (gate)
        {
            if (stopTask is not null)
            {
                throw new OperationCanceledException("预览运行已开始关闭。", Token);
            }
            Token.ThrowIfCancellationRequested();
            session = value;
        }
    }

    internal Task Stop()
    {
        TaskCompletionSource completion;
        VideoPlaybackSession? stoppingSession;
        AudioPlaybackSession? stoppingAudio;
        lock (gate)
        {
            if (stopTask is not null)
            {
                return stopTask;
            }

            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            stopTask = completion.Task;
            stoppingSession = session;
            stoppingAudio = audio;
        }
        _ = CompleteStopAsync(stoppingSession, stoppingAudio, completion);
        return completion.Task;
    }

    private async Task CompleteStopAsync(VideoPlaybackSession? stoppingSession, AudioPlaybackSession? stoppingAudio,
        TaskCompletionSource completion)
    {
        var failures = new List<Exception>();
        try
        {
            cancellation.Cancel();
        }
        catch (Exception error)
        {
            failures.Add(error);
        }
        FirstPresentation.TrySetCanceled(Token);
        try
        {
            await Task.WhenAll(stoppingSession?.CloseAsync() ?? Task.CompletedTask,
                stoppingAudio?.CloseAsync() ?? Task.CompletedTask).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            failures.Add(error);
        }
        if (failures.Count == 0)
        {
            completion.TrySetResult();
        }
        else
        {
            completion.TrySetException(failures);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (gate)
        {
            if (!disposed)
            {
                disposed = true;
                PreparationCancellation.Dispose();
                cancellation.Dispose();
                PreparedSlots.Dispose();
            }
        }
    }
}
