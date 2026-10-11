using System.Globalization;
using AegiNext.Application;
using AegiNext.Application.SubtitleFormats;
using AegiNext.Core.Projects;
using AegiNext.Core.Timing;

namespace AegiNext.Rendering.Tests.Reference;

/// <summary>独立 libass 对照源事件窗口内的恒量原点几何与实际持久化出口。</summary>
public sealed class AssRotationOriginWindowReferenceTests
{
    /// <summary>手写动态来源、独立常量基线和公开出口在半开事件窗口内保持相同覆盖。</summary>
    [LibassReferenceTheory]
    [InlineData(@"\frz23\t(-1000,-500,\frz90)", 2, true, 1, 1, 90, false)]
    [InlineData(@"\frz23\t(2500,3500,\frz90)", 2, true, 1, 1, 23, false)]
    [InlineData(@"\frz23\t(-1000,-500,\fscx150\fscy150)", 4, false, 1.5, 1.5, 23, false)]
    [InlineData(@"\frz23\t(-2000,-1500,\fscx200)\t(-1000,-500,\fscx150)\t(-1000,-500,\fscy150)", 4, false, 1.5, 1.5, 23, false)]
    [InlineData(@"\frz23\fscy75\t(-1000,-500,\fscx150)", 4, false, 1.5, 0.75, 23, false)]
    [InlineData(@"\frz23\t(-1000,-500,\frz90)", 4, true, 1, 1, 90, true)]
    [InlineData(@"\frz23\t(0,1000,0,\frz90)", 2, true, 1, 1, 90, false)]
    [InlineData(@"\frz23\t(2000,3000,0,\frz90)", 2, true, 1, 1, 23, false)]
    [InlineData(@"\frz23\t(0,1000,0,\fscx150\fscy150)", 2, false, 1.5, 1.5, 23, false)]
    [InlineData(@"\frz23\t(2000,3000,0,\fscx150\fscy150)", 2, false, 1, 1, 23, false)]
    [InlineData(@"\frz23\t(-1000,-1,\frz90)", 2, true, 1, 1, 90, false)]
    [InlineData(@"\frz23\t(-1000,-500,\fscx150\fscy150)", 4, true, 1.5, 1.5, 23, false)]
    public void VisibleWindowConstantsMatchIndependentLibassAndActualPublicExport(
        string transforms, int duration, bool move, double scaleX, double scaleY, double angle, bool negativeKaraoke)
    {
        var source = File(transforms, duration, move, negativeKaraoke);
        var fixedTransforms = string.Create(CultureInfo.InvariantCulture, $@"\fscx{scaleX * 100}\fscy{scaleY * 100}\frz{angle}");
        var canonical = File(fixedTransforms, duration, move, negativeKaraoke);
        var parsed = AssSubtitleFormat.Parse(source, 640, 360);
        Assert.DoesNotContain(parsed.Diagnostics, diagnostic => diagnostic.Code is "Ass.RotationOrigin" or
            "Ass.UnsupportedTag" or "Ass.AnimationSampling" or "Ass.AnimationSamplingLimit");
        var document = ProjectEditingOperations.ImportSubtitleLines(new() { Width = 640, Height = 360 }, parsed, "ASS visible window reference");
        var restored = ProjectStore.Deserialize(ProjectStore.Serialize(document));
        var written = AssSubtitleFormat.Write(restored);
        Assert.DoesNotContain(written.Diagnostics, diagnostic => diagnostic.Code is "Ass.RotationOrigin" or
            "Ass.TransformPivotAnimation" or "Subtitle.Composition" or "Ass.AnimationSampling" or "Ass.AnimationSamplingLimit");
        var roundTripped = AssSubtitleFormat.Parse(written.Text, 640, 360);
        Assert.DoesNotContain(roundTripped.Diagnostics, diagnostic => diagnostic.Code is "Ass.RotationOrigin" or "Ass.UnsupportedTag");
        Assert.Equal(MediaTime.Zero, Assert.Single(roundTripped.Lines).Start);
        Assert.Equal(new MediaTime(duration), Assert.Single(roundTripped.Lines).End);

        using var reference = new LibassReferenceRenderer();
        foreach (var time in new long[] { 0, 1, 499, 500, 733, 1000, duration * 1000 - 1 })
        {
            var expected = reference.Render(canonical, time);
            var original = reference.Render(source, time);
            Assert.Equal(expected.Pixels, original.Pixels);
            AssertCoverage(original, reference.Render(written.Text, time), time);
        }
    }

    private static void AssertCoverage(SubtitleReferenceFrame expected, SubtitleReferenceFrame actual, long time)
    {
        var expectedEnergy = expected.Energy(3);
        var actualEnergy = actual.Energy(3);
        Assert.True(expectedEnergy > 100 && actualEnergy > 100, $"Empty reference ink at {time} ms.");
        Assert.InRange(Math.Abs(actualEnergy / expectedEnergy - 1), 0, 0.02);
        Assert.InRange(Math.Abs(actual.MaximumAlpha - expected.MaximumAlpha), 0, 0.005);
        for (var channel = 0; channel < 4; channel++)
        {
            var difference = 0d;
            for (var index = channel; index < expected.Pixels.Length; index += 4)
            {
                difference += Math.Abs(actual.Pixels[index] - expected.Pixels[index]);
            }
            Assert.InRange(difference / expectedEnergy, 0, 0.02);
        }
        var expectedBounds = expected.InkBounds();
        var actualBounds = actual.InkBounds();
        Assert.InRange(Math.Abs(actualBounds.Left - expectedBounds.Left), 0, 1);
        Assert.InRange(Math.Abs(actualBounds.Top - expectedBounds.Top), 0, 1);
        Assert.InRange(Math.Abs(actualBounds.Right - expectedBounds.Right), 0, 1);
        Assert.InRange(Math.Abs(actualBounds.Bottom - expectedBounds.Bottom), 0, 1);
    }

    private static string File(string transforms, int duration, bool move, bool negativeKaraoke)
    {
        var placement = move ? $@"\move(240,180,300,210,0,{duration * 1000})" : @"\pos(240,180)";
        var karaoke = negativeKaraoke ? $@"\kt-50\k{duration * 100}" : string.Empty;
        var body = "{" + placement + @"\org(205,155)" + transforms + karaoke + "}Fj";
        return $$"""
            [Script Info]
            ScriptType: v4.00+
            PlayResX: 640
            PlayResY: 360
            WrapStyle: 2
            ScaledBorderAndShadow: yes
            YCbCr Matrix: None
            [V4+ Styles]
            Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
            Style: Default,Noto Sans,48,&H0000FF00,&H000000FF,&H00FF0000,&HFF000000,0,0,0,0,100,100,0,0,1,0,0,7,32,32,32,1
            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            Dialogue: 0,0:00:00.00,0:00:0{{duration}}.00,Default,,0,0,0,,{{body}}
            """;
    }
}
