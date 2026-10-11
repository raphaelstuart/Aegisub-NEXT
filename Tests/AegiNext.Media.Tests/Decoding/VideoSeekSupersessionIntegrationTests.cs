using AegiNext.Core.Timing;
using AegiNext.Media.Decoding;

namespace AegiNext.Media.Tests.Decoding;

[Collection(nameof(NativeDecoderTestGroup))]
public sealed class VideoSeekSupersessionIntegrationTests
{
    [DecoderFact]
    [Trait("Category", "DecoderIntegration")]
    public async Task EpochInteropKeepsTheSessionUsableAndCannotResetTerminalCancellation()
    {
        using var fixture = await DecoderFixture.CreateAsync(variableFrameRate: true);
        var times = fixture.ExpectedFrames.EnumerateArray()
            .Select(frame => new MediaTimestamp(frame.GetProperty("pts").GetInt64(), fixture.TimeBase).ToMediaTime()).ToArray();
        var initialDecoders = FfmpegVideoDecoder.GetLiveDecoderCount();
        var initialFrames = FfmpegVideoDecoder.GetLiveFrameCount();
        using (var decoder = FfmpegVideoDecoder.Open(fixture.MediaPath, fixture.VideoStreamIndex,
            new VideoDecoderOptions { Mode = VideoDecodeMode.Software }))
        {
            using var first = Assert.IsType<DecodedVideoFrame>(decoder.ReadFrame());
            var retainedPixels = first.CopyPlane(0);
            var generation = decoder.SessionInfo.Generation;
            decoder.SetSeekEpoch(2);
            var superseded = Assert.Throws<VideoSeekSupersededException>(() => decoder.ReadFrameForSeek(times[8], 1, default));
            Assert.True(superseded.RequiresSeek);
            Assert.Equal(generation, decoder.SessionInfo.Generation);
            Assert.Equal(1ul, decoder.SessionInfo.DeliveredFrames);
            Assert.Throws<InvalidOperationException>(() => decoder.ReadFrame());
            decoder.SetSeekEpoch(1);
            decoder.SeekToKeyFrame(times[4]);
            using var selected = Assert.IsType<DecodedVideoFrame>(decoder.ReadFrameForSeek(times[4], 2, default));
            Assert.Equal(times[4], selected.Info.DisplayTiming!.Timestamp.ToMediaTime());
            using var next = Assert.IsType<DecodedVideoFrame>(decoder.ReadFrame());
            Assert.Equal(times[5], next.Info.DisplayTiming!.Timestamp.ToMediaTime());
            Assert.Equal(retainedPixels, first.CopyPlane(0));
            Assert.Equal(initialDecoders + 1, FfmpegVideoDecoder.GetLiveDecoderCount());
            decoder.Cancel();
            decoder.SetSeekEpoch(3);
            Assert.ThrowsAny<OperationCanceledException>(() => decoder.SeekToKeyFrame(times[0]));
        }
        Assert.Equal(initialDecoders, FfmpegVideoDecoder.GetLiveDecoderCount());
        Assert.Equal(initialFrames, FfmpegVideoDecoder.GetLiveFrameCount());
    }
}
