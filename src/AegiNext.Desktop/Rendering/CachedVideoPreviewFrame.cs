using AegiNext.Core.Timing;
using AegiNext.Media.Preview;

namespace AegiNext.Desktop.Rendering;

internal sealed record CachedVideoPreviewFrame(SdrVideoFrame Frame, MediaTime Time, MediaTime? NextFrameTime,
    bool ReachedEnd, bool IsApproximate);
