using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using AegiNext.Core.Timing;
using AegiNext.Desktop.Rendering;
using AegiNext.Desktop.Settings;
using AegiNext.Media.Decoding;
using AegiNext.Media.Preview;

namespace AegiNext.Desktop.Tests.Controllers;

internal sealed class CachedPreviewTestConverter : IVideoPreviewConverter, ICachedVideoPreviewConverter
{
    private readonly TaskCompletionSource cachedRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ProjectPreviewState state = new(new(), Path.GetTempPath(), IsInteractive: true);
    private readonly ConcurrentQueue<int> activeCounts = new();
    private int activeCount;
    private int blockNextCached;
    private int cachedCount;
    private int disposeCount;
    private bool interactive = true;

    internal TaskCompletionSource CachedEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal ConcurrentQueue<int> WorkerThreadIds { get; } = new();
    internal ConcurrentQueue<ProjectPreviewState> ExactStates { get; } = new();
    internal ConcurrentQueue<ProjectPreviewState> CachedStates { get; } = new();
    internal int CachedCount => Volatile.Read(ref cachedCount);
    internal int DisposeCount => Volatile.Read(ref disposeCount);
    internal int MaximumActiveCount => activeCounts.DefaultIfEmpty().Max();

    internal bool Interactive
    {
        get => Volatile.Read(ref interactive);
        set => Volatile.Write(ref interactive, value);
    }

    internal void BlockNextCachedConversion()
    {
        Volatile.Write(ref blockNextCached, 1);
    }

    internal void ReleaseCachedConversion()
    {
        cachedRelease.TrySetResult();
    }

    internal void UpdateQuality(PreviewQuality quality, long qualityRevision)
    {
        Volatile.Write(ref state, Volatile.Read(ref state) with { Quality = quality, QualityRevision = qualityRevision });
    }

    public ProjectPreviewState CaptureState() => Volatile.Read(ref state) with { IsInteractive = Interactive };

    public bool HasCachedFrame(MediaTime mediaTarget, MediaTime maximumDistance, ProjectPreviewState previewState) => true;

    public SdrVideoFrame Convert(IVideoFrame frame, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Enter();
        try
        {
            return new(1, 1, [frame.CopyPlane(0)[0], 0, 0, 255]);
        }
        finally
        {
            Interlocked.Decrement(ref activeCount);
        }
    }

    public SdrVideoFrame Convert(PositionedVideoFrame frame, ProjectPreviewState previewState,
        CancellationToken cancellationToken)
    {
        ExactStates.Enqueue(previewState);
        return Convert(frame.Frame, cancellationToken);
    }

    public bool TryConvertCached(MediaTime mediaTarget, MediaTime maximumDistance, ProjectPreviewState previewState,
        CancellationToken cancellationToken, [NotNullWhen(true)] out CachedVideoPreviewFrame? result)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Enter();
        try
        {
            Interlocked.Increment(ref cachedCount);
            CachedStates.Enqueue(previewState);
            if (Interlocked.Exchange(ref blockNextCached, 0) != 0)
            {
                CachedEntered.TrySetResult();
                cachedRelease.Task.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).GetAwaiter().GetResult();
            }
            result = new(new(1, 1, [200, 0, 0, 255]), MediaTime.Zero, new(40, 1000), false, true);
            return true;
        }
        finally
        {
            Interlocked.Decrement(ref activeCount);
        }
    }

    public void Dispose()
    {
        Enter();
        try
        {
            Interlocked.Increment(ref disposeCount);
        }
        finally
        {
            Interlocked.Decrement(ref activeCount);
        }
    }

    private void Enter()
    {
        ObjectDisposedException.ThrowIf(DisposeCount != 0, this);
        WorkerThreadIds.Enqueue(Environment.CurrentManagedThreadId);
        activeCounts.Enqueue(Interlocked.Increment(ref activeCount));
    }
}
