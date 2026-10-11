using System.Collections.Concurrent;
using AegiNext.Media.Decoding;
using AegiNext.Media.Preview;

namespace AegiNext.Desktop.Tests.Controllers;

internal sealed class InteractivePreviewQualityConverter(Func<int> getQuality,
    PreviewTestConverter original, PreviewTestConverter replacement) : IVideoPreviewConverter
{
    private readonly ConcurrentDictionary<SdrVideoFrame, int> identities = new();

    internal int GetQuality(SdrVideoFrame frame)
    {
        return identities[frame];
    }

    /// <inheritdoc />
    public SdrVideoFrame Convert(IVideoFrame frame, CancellationToken cancellationToken = default)
    {
        var quality = getQuality();
        var result = (quality == 0 ? original : replacement).Convert(frame, cancellationToken);
        identities.TryAdd(result, quality);
        return result;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        original.Dispose();
        replacement.Dispose();
        identities.Clear();
    }
}
