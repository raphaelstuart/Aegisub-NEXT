using AegiNext.Core.Timing;
using AegiNext.Media.Preview;

namespace AegiNext.Desktop.Rendering;

internal sealed record PreviewBackgroundCacheEntry(PreviewBackgroundCacheKey Key, SdrVideoFrame Background,
    MediaTime Time, MediaTime? NextFrameTime, bool ReachedEnd);
