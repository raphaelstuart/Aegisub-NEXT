using System.Collections.Concurrent;
using AegiNext.Core.Timing;
using AegiNext.Media.Decoding;
using AegiNext.Media.Tests.Decoding;

namespace AegiNext.Media.Tests.Playback;

internal sealed class FakeVideoFrameSource(params long[] timestamps) : IVideoFrameSource
{
    private readonly ManualResetEventSlim seekGate = new(true);
    private int position;
    private int blockNextSeek;
    private int cancelCount;
    private int disposeCount;
    private int supersedeCount;

    internal ConcurrentQueue<FakeVideoFrame> IssuedFrames { get; } = new();

    internal TaskCompletionSource SeekEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Exception? ReadFailure { get; init; }

    internal int CancelCount => Volatile.Read(ref cancelCount);

    internal int DisposeCount => Volatile.Read(ref disposeCount);

    internal int SupersedeCount => Volatile.Read(ref supersedeCount);

    internal void BlockNextSeek()
    {
        seekGate.Reset();
        Volatile.Write(ref blockNextSeek, 1);
    }

    internal void ReleaseSeek()
    {
        seekGate.Set();
    }

    public PositionedVideoFrame? ReadFrame(CancellationToken cancellationToken = default)
    {
        CheckState(cancellationToken);
        if (ReadFailure is { } failure)
        {
            throw failure;
        }

        return position >= timestamps.Length ? null : CreatePositioned(position++, false);
    }

    public PositionedVideoFrame? SeekFrame(MediaTime target, CancellationToken cancellationToken = default)
    {
        CheckState(cancellationToken);
        if (timestamps.Length == 0)
        {
            return null;
        }

        var index = 0;
        while (index + 1 < timestamps.Length && new MediaTime(timestamps[index + 1], 1000) <= target)
        {
            index++;
        }

        position = index + 1;
        var result = CreatePositioned(index, target < new MediaTime(timestamps[0], 1000));
        try
        {
            if (Interlocked.Exchange(ref blockNextSeek, 0) != 0)
            {
                SeekEntered.TrySetResult();
                seekGate.Wait(cancellationToken);
            }

            CheckState(cancellationToken);
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    public void SupersedeSeek()
    {
        Interlocked.Increment(ref supersedeCount);
    }

    public void Cancel()
    {
        Interlocked.Increment(ref cancelCount);
        seekGate.Set();
    }

    public void Dispose()
    {
        Interlocked.Increment(ref disposeCount);
        seekGate.Dispose();
    }

    private PositionedVideoFrame CreatePositioned(int index, bool beforeFirst)
    {
        var frame = new FakeVideoFrame(timestamps[index], index);
        IssuedFrames.Enqueue(frame);
        return new(frame, new(timestamps[index], 1000),
            index + 1 < timestamps.Length ? new MediaTime(timestamps[index + 1], 1000) : null,
            beforeFirst, index + 1 == timestamps.Length);
    }

    private void CheckState(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(DisposeCount > 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (CancelCount != 0)
        {
            throw new OperationCanceledException("Fake source cancellation is terminal.");
        }
    }
}
