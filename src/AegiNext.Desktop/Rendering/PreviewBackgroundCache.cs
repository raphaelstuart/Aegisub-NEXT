using System.Diagnostics.CodeAnalysis;
using AegiNext.Core.Timing;
using AegiNext.Media.Decoding;
using AegiNext.Media.Preview;

namespace AegiNext.Desktop.Rendering;

internal sealed class PreviewBackgroundCache
{
    internal const int MAXIMUM_FRAMES = 64;
    internal const long MAXIMUM_BYTES = 64L * 1024 * 1024;
    private static readonly MediaTime maximumApproximation = new(1, 10);
    private readonly Lock gate = new();
    private readonly Dictionary<PreviewBackgroundCacheKey, LinkedListNode<PreviewBackgroundCacheEntry>> entries = [];
    private readonly LinkedList<PreviewBackgroundCacheEntry> lru = new();
    private readonly int maximumFrames;
    private readonly long maximumBytes;
    private long bytes;
    private long peakBytes;
    private int peakCount;
    private long hits;
    private long misses;
    private long approximateHits;

    internal PreviewBackgroundCache(int maximumFrames = MAXIMUM_FRAMES, long maximumBytes = MAXIMUM_BYTES)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumFrames);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        this.maximumFrames = maximumFrames;
        this.maximumBytes = maximumBytes;
    }

    internal PreviewBackgroundCacheStatistics Statistics
    {
        get
        {
            lock (gate)
            {
                return new(hits, misses, approximateHits, entries.Count, peakCount, bytes, peakBytes);
            }
        }
    }

    internal bool TryGet(VideoFrameInfo source, SdrPreviewOptions options,
        [NotNullWhen(true)] out SdrVideoFrame? background)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(new(source, options), out var node))
            {
                misses++;
                background = null;
                return false;
            }

            hits++;
            Touch(node);
            background = node.Value.Background;
            return true;
        }
    }

    internal void Add(VideoFrameInfo source, SdrPreviewOptions options, SdrVideoFrame background,
        MediaTime time, MediaTime? nextFrameTime, bool reachedEnd)
    {
        if (nextFrameTime is { } next && next <= time || reachedEnd && nextFrameTime is not null)
        {
            throw new ArgumentException("缓存视频帧的显示区间无效。", nameof(nextFrameTime));
        }

        lock (gate)
        {
            var key = new PreviewBackgroundCacheKey(source, options);
            if (entries.TryGetValue(key, out var previous))
            {
                if (ReferenceEquals(previous.Value.Background, background) && previous.Value.Time == time &&
                    previous.Value.NextFrameTime == nextFrameTime && previous.Value.ReachedEnd == reachedEnd)
                {
                    Touch(previous);
                    return;
                }

                entries.Remove(key);
                bytes -= previous.Value.Background.Pixels.Length;
                lru.Remove(previous);
            }

            if (maximumFrames == 0 || background.Pixels.Length > maximumBytes)
            {
                return;
            }

            while (lru.Last is { } oldest &&
                (entries.Count >= maximumFrames || bytes + background.Pixels.Length > maximumBytes))
            {
                entries.Remove(oldest.Value.Key);
                bytes -= oldest.Value.Background.Pixels.Length;
                lru.RemoveLast();
            }

            entries.Add(key, lru.AddFirst(new PreviewBackgroundCacheEntry(key, background, time, nextFrameTime, reachedEnd)));
            bytes += background.Pixels.Length;
            peakBytes = Math.Max(peakBytes, bytes);
            peakCount = Math.Max(peakCount, entries.Count);
        }
    }

    internal bool Contains(MediaTime target, MediaTime maximumDistance, SdrPreviewOptions options)
    {
        lock (gate)
        {
            return Find(target, maximumDistance, options, out _) is not null;
        }
    }

    internal bool TryFind(MediaTime target, MediaTime maximumDistance, SdrPreviewOptions options,
        [NotNullWhen(true)] out PreviewBackgroundCacheEntry? entry, out bool approximate)
    {
        lock (gate)
        {
            var node = Find(target, maximumDistance, options, out approximate);
            if (node is null)
            {
                misses++;
                entry = null;
                return false;
            }

            hits++;
            if (approximate)
            {
                approximateHits++;
            }
            Touch(node);
            entry = node.Value;
            return true;
        }
    }

    internal void Clear()
    {
        lock (gate)
        {
            entries.Clear();
            lru.Clear();
            bytes = 0;
        }
    }

    private LinkedListNode<PreviewBackgroundCacheEntry>? Find(MediaTime target, MediaTime maximumDistance,
        SdrPreviewOptions options, out bool approximate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumDistance, MediaTime.Zero);

        var limit = maximumDistance < maximumApproximation ? maximumDistance : maximumApproximation;
        LinkedListNode<PreviewBackgroundCacheEntry>? nearest = null;
        var nearestDistance = limit;
        approximate = false;
        for (var node = lru.First; node is not null; node = node.Next)
        {
            var entry = node.Value;
            if (entry.Key.Options != options)
            {
                continue;
            }

            if (target == entry.Time || target > entry.Time && entry.NextFrameTime is { } end && target < end)
            {
                return node;
            }

            var distance = target < entry.Time ? entry.Time - target : target - (entry.NextFrameTime ?? entry.Time);
            if (limit > MediaTime.Zero && distance <= limit && (nearest is null || distance < nearestDistance))
            {
                nearest = node;
                nearestDistance = distance;
            }
        }

        approximate = nearest is not null;
        return nearest;
    }

    private void Touch(LinkedListNode<PreviewBackgroundCacheEntry> node)
    {
        lru.Remove(node);
        lru.AddFirst(node);
    }
}
