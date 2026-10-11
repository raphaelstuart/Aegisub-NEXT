using AegiNext.Core.Timing;
using AegiNext.Media.Decoding;

namespace AegiNext.Media.Tests.Decoding;

[Collection(nameof(NativeDecoderTestGroup))]
public sealed class VideoFrameNavigatorIntegrationTests
{
    private static readonly int[] seekOrder = [23, 4, 47, 10, 0, 35, 2, 46, 12];

    [DecoderFact]
    [Trait("Category", "DecoderIntegration")]
    public async Task NativeSupersessionReseeksWithoutReopeningAndKeepsExistingFrameLeases()
    {
        using var fixture = await DecoderFixture.CreateAsync(variableFrameRate: true, frameCount: 48,
            keyFrameInterval: 48, startTimeMilliseconds: 2000);
        var times = fixture.ExpectedFrames.EnumerateArray()
            .Select(frame => new MediaTimestamp(frame.GetProperty("pts").GetInt64(), fixture.TimeBase).ToMediaTime()).ToArray();
        var initialDecoders = FfmpegVideoDecoder.GetLiveDecoderCount();
        var initialFrames = FfmpegVideoDecoder.GetLiveFrameCount();
        var opened = 0;
        FfmpegVideoDecoder? decoder = null;
        using (var navigator = new VideoFrameNavigator(token =>
        {
            opened++;
            return decoder = FfmpegVideoDecoder.Open(fixture.MediaPath, fixture.VideoStreamIndex,
                new VideoDecoderOptions { Mode = VideoDecodeMode.Software }, token);
        }))
        {
            using var first = Assert.IsType<PositionedVideoFrame>(navigator.ReadFrame());
            var generation = decoder!.SessionInfo.Generation;
            var notified = false;
            var error = Assert.Throws<VideoSeekSupersededException>(() => navigator.SeekFrame(times[23], () =>
            {
                if (!notified && decoder.SessionInfo.Generation > generation)
                {
                    notified = true;
                    navigator.SupersedeSeek();
                }
                return false;
            }));
            Assert.True(error.RequiresSeek);
            Assert.Equal(1, opened);
            using var recovered = Assert.IsType<PositionedVideoFrame>(navigator.SeekFrame(times[24]));
            Assert.Equal(times[24], recovered.Time);
            AssertFramePixels(fixture, recovered.Frame, 24);
            AssertFramePixels(fixture, first.Frame, 0);
            using var next = Assert.IsType<PositionedVideoFrame>(navigator.ReadFrame());
            Assert.Equal(times[25], next.Time);
            AssertFramePixels(fixture, next.Frame, 25);
            Assert.Equal(1, opened);
            Assert.Equal(initialDecoders + 1, FfmpegVideoDecoder.GetLiveDecoderCount());
        }
        Assert.Equal(initialDecoders, FfmpegVideoDecoder.GetLiveDecoderCount());
        Assert.Equal(initialFrames, FfmpegVideoDecoder.GetLiveFrameCount());
    }

    [DecoderFact]
    [Trait("Category", "DecoderIntegration")]
    public async Task AbandonedPrerollLeavesTheNativeDecoderUsableWithoutReopening()
    {
        using var fixture = await DecoderFixture.CreateAsync(frameCount: 48, keyFrameInterval: 12,
            startTimeMilliseconds: 2000);
        var times = fixture.ExpectedFrames.EnumerateArray()
            .Select(frame => new MediaTimestamp(frame.GetProperty("pts").GetInt64(), fixture.TimeBase).ToMediaTime()).ToArray();
        var initialDecoders = FfmpegVideoDecoder.GetLiveDecoderCount();
        var initialFrames = FfmpegVideoDecoder.GetLiveFrameCount();
        var opened = 0;
        using (var navigator = new VideoFrameNavigator(token =>
        {
            opened++;
            return FfmpegVideoDecoder.Open(fixture.MediaPath, fixture.VideoStreamIndex, token);
        }))
        {
            using var first = navigator.ReadFrame();
            var checks = 0;
            Assert.ThrowsAny<OperationCanceledException>(() => navigator.SeekFrame(times[23], () => ++checks >= 5));
            using var recovered = Assert.IsType<PositionedVideoFrame>(navigator.SeekFrame(times[24]));
            Assert.Equal(times[24], recovered.Time);
            AssertFramePixels(fixture, recovered.Frame, 24);
            Assert.Equal(1, opened);
            for (var index = 25; index < 30; index++)
            {
                using var forward = Assert.IsType<PositionedVideoFrame>(navigator.SeekFrame(times[index]));
                Assert.Equal(times[index], forward.Time);
                AssertFramePixels(fixture, forward.Frame, index);
            }
        }

        Assert.Equal(initialDecoders, FfmpegVideoDecoder.GetLiveDecoderCount());
        Assert.Equal(initialFrames, FfmpegVideoDecoder.GetLiveFrameCount());
    }

    [DecoderTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "DecoderIntegration")]
    public async Task SeeksAcrossMultipleGopsAndEofWithExactRawPixels(bool variableFrameRate)
    {
        using var fixture = await DecoderFixture.CreateAsync(variableFrameRate: variableFrameRate,
            frameCount: 48, keyFrameInterval: 12, startTimeMilliseconds: 2000);
        var times = fixture.ExpectedFrames.EnumerateArray()
            .Select(frame => new MediaTimestamp(frame.GetProperty("pts").GetInt64(), fixture.TimeBase).ToMediaTime()).ToArray();
        Assert.True(times[0] > MediaTime.Zero);
        Assert.True(fixture.ExpectedFrames.EnumerateArray().Count(frame => frame.GetProperty("key_frame").GetInt32() != 0) >= 3);
        var initialDecoders = FfmpegVideoDecoder.GetLiveDecoderCount();
        var initialFrames = FfmpegVideoDecoder.GetLiveFrameCount();
        using (var navigator = VideoFrameNavigator.Open(fixture.MediaPath, fixture.VideoStreamIndex, new VideoDecoderOptions { Mode = VideoDecodeMode.Software }))
        {
            using (var before = Assert.IsType<PositionedVideoFrame>(navigator.SeekFrame(times[0] - new MediaTime(1))))
            {
                Assert.True(before.IsBeforeFirst);
                Assert.Equal(times[0], before.Time);
                AssertFramePixels(fixture, before.Frame, 0);
            }

            using (var tail = Assert.IsType<PositionedVideoFrame>(navigator.SeekFrame(times[^1] + new MediaTime(10))))
            {
                Assert.True(tail.ReachedEnd);
                Assert.Null(tail.NextFrameTime);
                Assert.Equal(times[^1], tail.Time);
                AssertFramePixels(fixture, tail.Frame, times.Length - 1);
            }

            Assert.Null(navigator.ReadFrame());
            Assert.Null(navigator.ReadFrame());
            foreach (var index in seekOrder)
            {
                var target = index == times.Length - 1 ? times[index] : (times[index] + times[index + 1]) / 2;
                using (var interval = Assert.IsType<PositionedVideoFrame>(navigator.SeekFrame(target)))
                {
                    Assert.Equal(times[index], interval.Time);
                    Assert.Equal(index + 1 < times.Length ? times[index + 1] : null, interval.NextFrameTime);
                    AssertFramePixels(fixture, interval.Frame, index);
                }

                using var exact = Assert.IsType<PositionedVideoFrame>(navigator.SeekFrame(times[index]));
                Assert.Equal(times[index], exact.Time);
                AssertFramePixels(fixture, exact.Frame, index);
            }
        }

        Assert.Equal(initialDecoders, FfmpegVideoDecoder.GetLiveDecoderCount());
        Assert.Equal(initialFrames, FfmpegVideoDecoder.GetLiveFrameCount());
    }

    private static void AssertFramePixels(DecoderFixture fixture, IVideoFrame frame, int frameIndex)
    {
        var offset = frameIndex * DecoderFixture.WIDTH * DecoderFixture.HEIGHT * 3;
        Assert.Equal("yuv420p10le", frame.Info.PixelFormat);
        for (var plane = 0; plane < 3; plane++)
        {
            var pixels = frame.CopyPlane(plane);
            Assert.Equal(fixture.RawFrames.AsSpan(offset, pixels.Length).ToArray(), pixels);
            offset += pixels.Length;
        }
    }
}
