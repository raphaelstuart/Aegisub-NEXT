using AegiNext.Core.Projects;
using AegiNext.Core.Timing;
using AegiNext.Desktop.Rendering;
using AegiNext.Desktop.Settings;
using AegiNext.Media.Decoding;
using AegiNext.Media.Preview;

namespace AegiNext.Desktop.Tests.Rendering;

public sealed class ProjectPreviewBackgroundReuseTests
{
    [Fact]
    public void CapturedPreciseSeekStateUsesItsExactTargetAndQualityWithoutReadingTheLaterRequest()
    {
        var document = new ProjectDocument
        {
            Width = 1, Height = 1,
            Layers = [new()
            {
                Kind = LayerKind.SHAPE, Shape = new(ShapeKind.RECTANGLE, 1, 1), Fill = new(1, 0, 0), End = new(11, 10)
            }]
        };
        var state = new ProjectPreviewState(document, Path.GetTempPath(), new(5, 4),
            Quality: PreviewQuality.HIGH, QualityRevision: 3, EvaluateAtTarget: true);
        var captured = state;
        var reads = 0;
        var options = new List<SdrPreviewOptions>();
        var catalog = new PreviewFrameCatalog();
        Exception? error = null;
        using var converter = new ProjectPreviewConverter(() =>
        {
            reads++;
            return state;
        }, value => error = value, catalog, createConverter: value =>
        {
            options.Add(value);
            return new PreviewTestConverter();
        });
        state = state with { Document = document with { Name = "Later request" }, TargetTime = new(9), IsInteractive = true };
        using var source = new PositionedVideoFrame(new PreviewTestFrame(1000, 1), new(1), new(2));

        var frame = converter.Convert(source, captured);

        Assert.Null(error);
        Assert.Equal(0, reads);
        Assert.Equal(new SdrPreviewOptions(1920, 1080), Assert.Single(options));
        var identity = Assert.IsType<PreviewFrameRecord>(catalog.FindIdentity(frame));
        Assert.Same(document, identity.Document);
        Assert.Equal(new MediaTime(5, 4), identity.Time);
        Assert.False(identity.Interactive);
        Assert.Equal(3, identity.QualityRevision);
        Assert.Equal(0, frame.Pixels.Span[2]);

        var playback = converter.Convert(source, captured with { EvaluateAtTarget = false });
        Assert.Null(error);
        Assert.Equal(new MediaTime(1), catalog.FindIdentity(playback)!.Time);
        Assert.Equal(255, playback.Pixels.Span[2]);
        Assert.Equal(new MediaTime(5, 4), catalog.FindIdentity(frame)!.Time);
        Assert.Equal(1, converter.BackgroundCacheStatistics.Hits);
    }

    [Fact]
    public void CachedBackgroundRecomposesCurrentLayersAtEachRequestedTime()
    {
        var document = new ProjectDocument
        {
            Width = 1, Height = 1,
            Layers = [new()
            {
                Kind = LayerKind.SHAPE, Shape = new(ShapeKind.RECTANGLE, 1, 1), Fill = new(1, 0, 0), End = new(1)
            }]
        };
        var state = new ProjectPreviewState(document, Path.GetTempPath(), new(1, 2), true);
        var sourceConverter = new PreviewTestConverter();
        Exception? error = null;
        using var converter = new ProjectPreviewConverter(() => state, value => error = value,
            createConverter: _ => sourceConverter);
        using var source = new PositionedVideoFrame(new PreviewTestFrame(0, 1), MediaTime.Zero, new(2));
        var composed = converter.Convert(source);
        Assert.Null(error);
        Assert.Equal(255, composed.Pixels.Span[2]);

        var afterLayer = state with { TargetTime = new(3, 2) };
        Assert.True(converter.TryConvertCached(new(3, 2), new(1, 10), afterLayer, default, out var background));
        Assert.Null(error);
        Assert.Equal(1, background.Frame.Pixels.Span[0]);
        Assert.Equal(0, background.Frame.Pixels.Span[2]);
        Assert.False(background.IsApproximate);
        Assert.Equal(1, sourceConverter.ConversionCount);
        Assert.Equal(255, composed.Pixels.Span[2]);
    }

    [Fact]
    public void RepeatedConversionsSharePixelsButPreserveIndependentCompositionIdentities()
    {
        var state = new ProjectPreviewState(new() { Width = 1, Height = 1 }, Path.GetTempPath(), new(1), true);
        var catalog = new PreviewFrameCatalog();
        var sourceConverter = new PreviewTestConverter();
        using var converter = new ProjectPreviewConverter(() => state, previewFrames: catalog,
            createConverter: _ => sourceConverter);
        using var source = new PreviewTestFrame(0, 1);
        var first = converter.Convert(source);
        state = state with { TargetTime = new(2), QualityRevision = 7, Document = state.Document with { Name = "Changed" } };
        var second = converter.Convert(source);

        Assert.Equal(1, sourceConverter.ConversionCount);
        Assert.NotSame(first, second);
        Assert.True(first.Pixels.Equals(second.Pixels));
        Assert.Equal(new MediaTime(1), catalog.FindIdentity(first)!.Time);
        Assert.Equal(new MediaTime(2), catalog.FindIdentity(second)!.Time);
        Assert.Equal(0, catalog.FindIdentity(first)!.QualityRevision);
        Assert.Equal(7, catalog.FindIdentity(second)!.QualityRevision);
        Assert.NotSame(catalog.FindIdentity(first)!.Document, catalog.FindIdentity(second)!.Document);
        Assert.Same(second, catalog.FindBackground(second));
        Assert.Equal(4, converter.GetRetainedBytes(second));
        Assert.Equal(1, converter.BackgroundCacheStatistics.Hits);
        Assert.Equal(1, converter.BackgroundCacheStatistics.Misses);
    }

    [Fact]
    public void DifferentLeasesOfTheSameSourceReuseConversionButDuplicatePtsSourcesDoNot()
    {
        var state = new ProjectPreviewState(new() { Width = 1, Height = 1 }, Path.GetTempPath());
        var sourceConverter = new PreviewTestConverter();
        using var converter = new ProjectPreviewConverter(() => state, createConverter: _ => sourceConverter);
        var source = new PreviewTestFrame(40, 1);
        var entry = new VideoFrameCacheEntry(source, new(40, 1000), new(80, 1000), false, 1);
        try
        {
            using var first = entry.Acquire();
            using var second = entry.Acquire();
            Assert.NotSame(first.Frame, second.Frame);
            Assert.Same(first.Frame.Info, second.Frame.Info);
            var firstOutput = converter.Convert(first);
            var secondOutput = converter.Convert(second);
            Assert.True(firstOutput.Pixels.Equals(secondOutput.Pixels));
            Assert.Equal(1, sourceConverter.ConversionCount);
            using var duplicate = new PreviewTestFrame(40, 2);
            var duplicateOutput = converter.Convert(duplicate);
            Assert.Equal(2, sourceConverter.ConversionCount);
            Assert.Equal(2, duplicateOutput.Pixels.Span[0]);
        }
        finally
        {
            entry.Release();
        }

        Assert.Equal(1, source.DisposeCount);
    }

    [Fact]
    public void ActualInteractiveQualityControlsReuseAndPreciseQualityRetainsItsOwnBackground()
    {
        var state = new ProjectPreviewState(new() { Width = 1, Height = 1 }, Path.GetTempPath(),
            IsInteractive: true, Quality: PreviewQuality.HIGH);
        var conversions = new List<SdrPreviewOptions>();
        using var converter = new ProjectPreviewConverter(() => state, createConverter: options =>
        {
            conversions.Add(options);
            return new PreviewTestConverter();
        });
        using var source = new PreviewTestFrame(0, 1);
        converter.Convert(source);
        state = state with { Quality = PreviewQuality.STANDARD, QualityRevision = 1 };
        converter.Convert(source);
        Assert.Single(conversions);
        Assert.Equal(new(960, 540), conversions[0]);
        state = state with { IsInteractive = false, Quality = PreviewQuality.HIGH };
        converter.Convert(source);
        Assert.Equal(2, conversions.Count);
        Assert.Equal(new(1920, 1080), conversions[1]);
        state = state with { IsInteractive = true };
        converter.Convert(source);
        Assert.Equal(2, converter.BackgroundCacheStatistics.Count);
        Assert.Equal(2, converter.BackgroundCacheStatistics.Hits);
    }

    [Fact]
    public void CachedDeliveryUsesCapturedDocumentAndTargetAndNeverExtendsItsSourceInterval()
    {
        var asset = new ProjectAsset(Guid.NewGuid(), ProjectAssetKind.MEDIA, "test.mp4");
        var document = new ProjectDocument
        {
            Width = 1, Height = 1, Assets = [asset], Media = new(asset.Id, 0, null, new(5))
        };
        var state = new ProjectPreviewState(document, Path.GetTempPath(), new(1), true, QualityRevision: 3);
        var catalog = new PreviewFrameCatalog();
        var sourceConverter = new PreviewTestConverter();
        using var converter = new ProjectPreviewConverter(() => state, previewFrames: catalog,
            createConverter: _ => sourceConverter);
        using var source = new PositionedVideoFrame(new PreviewTestFrame(6000, 1), new(6), new(6040, 1000));
        converter.Convert(source);
        var captured = converter.CaptureState() with { TargetTime = new(11, 10) };
        state = state with { TargetTime = new(9), Document = document with { Name = "Later state" } };

        Assert.True(converter.HasCachedFrame(new(61, 10), new(1, 10), captured));
        Assert.True(converter.TryConvertCached(new(61, 10), new(1, 10), captured, default, out var result));
        Assert.True(result.IsApproximate);
        Assert.Equal(new MediaTime(6), result.Time);
        Assert.Equal(new MediaTime(6040, 1000), result.NextFrameTime);
        Assert.Same(document, catalog.FindIdentity(result.Frame)!.Document);
        Assert.Equal(new MediaTime(11, 10), catalog.FindIdentity(result.Frame)!.Time);
        Assert.Equal(3, catalog.FindIdentity(result.Frame)!.QualityRevision);
        Assert.Equal(1, sourceConverter.ConversionCount);
        Assert.False(converter.HasCachedFrame(new(7), new(1, 10), captured));
        Assert.False(converter.TryConvertCached(new(7), new(1, 10), captured, default, out _));
    }

    [Fact]
    public void CancelledCacheDeliveriesDoNotPublishAndDisposalDropsOnlyTheOwnedCacheReferences()
    {
        var state = new ProjectPreviewState(new() { Width = 1, Height = 1 }, Path.GetTempPath(), MediaTime.Zero, true);
        var sourceConverter = new PreviewTestConverter();
        var converter = new ProjectPreviewConverter(() => state, createConverter: _ => sourceConverter);
        using var source = new PositionedVideoFrame(new PreviewTestFrame(0, 1), MediaTime.Zero, new(40, 1000));
        var delivered = converter.Convert(source);
        try
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() =>
                converter.TryConvertCached(MediaTime.Zero, new(1, 10), state, cancellation.Token, out _));
            Assert.Equal(1, sourceConverter.ConversionCount);
        }
        finally
        {
            converter.Dispose();
        }

        Assert.Equal(0, converter.BackgroundCacheStatistics.Bytes);
        Assert.Equal(0, converter.BackgroundCacheStatistics.Count);
        Assert.Equal(1, sourceConverter.DisposeCount);
        Assert.Equal(1, delivered.Pixels.Span[0]);
        Assert.False(converter.HasCachedFrame(MediaTime.Zero, new(1, 10), state));
    }

    [Fact]
    public void PixelViewsHaveIndependentObjectIdentityAndDoNotDuplicateRetainedPixelAccounting()
    {
        var background = new SdrVideoFrame(1, 1, [1, 2, 3, 255]);
        var view = background.CreateView();
        Assert.NotSame(background, view);
        Assert.True(background.Pixels.Equals(view.Pixels));
        var catalog = new PreviewFrameCatalog();
        catalog.Register(view, background);
        using var converter = new ProjectPreviewConverter(() => new(new(), Path.GetTempPath()), previewFrames: catalog);
        Assert.Equal(4, converter.GetRetainedBytes(view));
    }
}
