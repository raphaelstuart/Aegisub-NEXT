namespace AegiNext.Desktop.Rendering;

internal sealed record PreviewBackgroundCacheStatistics(long Hits, long Misses, long ApproximateHits,
    int Count, int PeakCount, long Bytes, long PeakBytes);
