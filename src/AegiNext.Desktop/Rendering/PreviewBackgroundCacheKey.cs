using System.Runtime.CompilerServices;
using AegiNext.Media.Decoding;
using AegiNext.Media.Preview;

namespace AegiNext.Desktop.Rendering;

internal readonly struct PreviewBackgroundCacheKey(VideoFrameInfo frameInfo, SdrPreviewOptions options)
    : IEquatable<PreviewBackgroundCacheKey>
{
    internal VideoFrameInfo FrameInfo { get; } = frameInfo;
    internal SdrPreviewOptions Options { get; } = options;

    /// <inheritdoc />
    public bool Equals(PreviewBackgroundCacheKey other)
    {
        return ReferenceEquals(FrameInfo, other.FrameInfo) && Options == other.Options;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return obj is PreviewBackgroundCacheKey other && Equals(other);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        return HashCode.Combine(RuntimeHelpers.GetHashCode(FrameInfo), Options);
    }
}
