using System.Diagnostics.CodeAnalysis;
using AegiNext.Core.Timing;
using AegiNext.Media.Decoding;
using AegiNext.Media.Preview;

namespace AegiNext.Desktop.Rendering;

internal interface ICachedVideoPreviewConverter
{
    ProjectPreviewState CaptureState();

    SdrVideoFrame Convert(PositionedVideoFrame frame, ProjectPreviewState state, CancellationToken cancellationToken);

    bool HasCachedFrame(MediaTime mediaTarget, MediaTime maximumDistance, ProjectPreviewState state);

    bool TryConvertCached(MediaTime mediaTarget, MediaTime maximumDistance, ProjectPreviewState state,
        CancellationToken cancellationToken, [NotNullWhen(true)] out CachedVideoPreviewFrame? result);
}
