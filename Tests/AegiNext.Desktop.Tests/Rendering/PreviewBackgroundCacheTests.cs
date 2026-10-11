using AegiNext.Core.Timing;
using AegiNext.Desktop.Rendering;
using AegiNext.Media.Preview;

namespace AegiNext.Desktop.Tests.Rendering;

public sealed class PreviewBackgroundCacheTests
{
    [Fact]
    public void DuplicatePtsFramesHaveIndependentPixelIdentityAndEquivalentOptionsReuseTheSameEntry()
    {
        var cache = new PreviewBackgroundCache();
        using var first = new PreviewTestFrame(40, 1);
        using var duplicate = new PreviewTestFrame(40, 2);
        var options = new SdrPreviewOptions(960, 540);
        var firstBackground = Pixel(1);
        var duplicateBackground = Pixel(2);
        cache.Add(first.Info, options, firstBackground, new(40, 1000), new(80, 1000), false);
        cache.Add(duplicate.Info, options, duplicateBackground, new(40, 1000), new(80, 1000), false);

        Assert.True(cache.TryGet(first.Info, new(960, 540), out var selectedFirst));
        Assert.Same(firstBackground, selectedFirst);
        Assert.True(cache.TryGet(duplicate.Info, options, out var selectedDuplicate));
        Assert.Same(duplicateBackground, selectedDuplicate);
        Assert.Equal(2, cache.Statistics.Count);
        Assert.True(cache.TryFind(new(50, 1000), MediaTime.Zero, options, out var selectedInterval, out var approximate));
        Assert.Same(duplicateBackground, selectedInterval.Background);
        Assert.False(approximate);
    }

    [Theory]
    [InlineData(2, 64)]
    [InlineData(64, 8)]
    public void FrameAndPixelBudgetsEvictTheLeastRecentlyUsedEntry(int maximumFrames, long maximumBytes)
    {
        var cache = new PreviewBackgroundCache(maximumFrames, maximumBytes);
        var options = new SdrPreviewOptions();
        using var first = new PreviewTestFrame(0, 1);
        using var second = new PreviewTestFrame(40, 2);
        using var third = new PreviewTestFrame(80, 3);
        cache.Add(first.Info, options, Pixel(1), MediaTime.Zero, new(40, 1000), false);
        cache.Add(second.Info, options, Pixel(2), new(40, 1000), new(80, 1000), false);
        Assert.True(cache.TryGet(first.Info, options, out _));
        cache.Add(third.Info, options, Pixel(3), new(80, 1000), new(120, 1000), false);

        Assert.True(cache.TryGet(first.Info, options, out _));
        Assert.False(cache.TryGet(second.Info, options, out _));
        Assert.True(cache.TryGet(third.Info, options, out _));
        Assert.Equal(2, cache.Statistics.Count);
        Assert.Equal(8, cache.Statistics.Bytes);
        Assert.Equal(2, cache.Statistics.PeakCount);
        Assert.Equal(8, cache.Statistics.PeakBytes);
        cache.Clear();
        Assert.Equal(0, cache.Statistics.Count);
        Assert.Equal(0, cache.Statistics.Bytes);
    }

    [Fact]
    public void AnOversizedBackgroundIsNotRetainedAndDoesNotEvictUsefulEntries()
    {
        var cache = new PreviewBackgroundCache(maximumBytes: 4);
        var options = new SdrPreviewOptions();
        using var first = new PreviewTestFrame(0, 1);
        using var second = new PreviewTestFrame(40, 2);
        cache.Add(first.Info, options, Pixel(1), MediaTime.Zero, new(40, 1000), false);
        cache.Add(second.Info, options, new(2, 1, [2, 0, 0, 255, 2, 0, 0, 255]), new(40, 1000), null, false);

        Assert.True(cache.TryGet(first.Info, options, out _));
        Assert.False(cache.TryGet(second.Info, options, out _));
        Assert.Equal(4, cache.Statistics.Bytes);
    }

    [Fact]
    public void ApproximationUsesRealIntervalsCapsTheGapAtOneHundredMillisecondsAndDoesNotExtendEof()
    {
        var cache = new PreviewBackgroundCache();
        var options = new SdrPreviewOptions();
        using var frame = new PreviewTestFrame(1000, 1);
        cache.Add(frame.Info, options, Pixel(1), new(1), new(2), false);

        Assert.True(cache.TryFind(new(19, 10), MediaTime.Zero, options, out _, out var exact));
        Assert.False(exact);
        Assert.False(cache.TryFind(new(2), MediaTime.Zero, options, out _, out _));
        Assert.True(cache.TryFind(new(21, 10), new(1), options, out _, out var near));
        Assert.True(near);
        Assert.False(cache.TryFind(new(2101, 1000), new(1), options, out _, out _));
        Assert.True(cache.TryFind(new(9, 10), new(1, 10), options, out _, out _));
        Assert.False(cache.TryFind(new(899, 1000), new(1), options, out _, out _));

        cache.Add(frame.Info, options, Pixel(1), new(1), null, true);
        Assert.True(cache.TryFind(new(11, 10), new(1), options, out var tail, out var approximate));
        Assert.True(tail.ReachedEnd);
        Assert.Null(tail.NextFrameTime);
        Assert.True(approximate);
        Assert.False(cache.TryFind(new(2), new(1), options, out _, out _));
    }

    [Fact]
    public void CachedBackgroundsRequireMatchingConversionOptionsAndAvailabilityQueriesDoNotChangeStatistics()
    {
        var cache = new PreviewBackgroundCache();
        using var frame = new PreviewTestFrame(40, 1);
        var options = new SdrPreviewOptions(960, 540);
        cache.Add(frame.Info, options, Pixel(1), new(40, 1000), new(80, 1000), false);
        var before = cache.Statistics;

        Assert.True(cache.Contains(new(50, 1000), MediaTime.Zero, options));
        Assert.False(cache.Contains(new(50, 1000), new(1, 10), new(568, 320)));
        Assert.Equal(before, cache.Statistics);
        Assert.False(cache.TryGet(frame.Info, new(1920, 1080), out _));
        Assert.True(cache.TryFind(new(90, 1000), new(1, 10), options, out _, out var approximate));
        Assert.True(approximate);
        Assert.Equal(1, cache.Statistics.Hits);
        Assert.Equal(1, cache.Statistics.Misses);
        Assert.Equal(1, cache.Statistics.ApproximateHits);
    }

    [Fact]
    public void ApproximateLookupPrefersAnActualCoveringIntervalToAMoreRecentlyUsedNeighbour()
    {
        var cache = new PreviewBackgroundCache();
        var options = new SdrPreviewOptions();
        using var covering = new PreviewTestFrame(0, 1);
        using var neighbour = new PreviewTestFrame(100, 2);
        var background = Pixel(1);
        cache.Add(covering.Info, options, background, MediaTime.Zero, new(1, 10), false);
        cache.Add(neighbour.Info, options, Pixel(2), new(1, 10), new(1, 5), false);

        Assert.True(cache.TryFind(new(9, 100), new(1, 10), options, out var selected, out var approximate));
        Assert.Same(background, selected.Background);
        Assert.False(approximate);
    }

    private static SdrVideoFrame Pixel(byte marker)
    {
        return new(1, 1, [marker, 0, 0, 255]);
    }
}
