using AegiNext.Application;
using AegiNext.Application.SubtitleFormats;
using AegiNext.Core.Projects;

namespace AegiNext.Rendering.Tests.Reference;

/// <summary>用手写 ASS 和真实文件出口对照 libass 的最终重置样式边距。</summary>
public sealed class AssResetStyleMarginReferenceTests
{
    /// <summary>最终命名重置控制整行换行和自动边距，保持事件原有对齐。</summary>
    [LibassReferenceTheory]
    [InlineData(@"{\rAlternate}Fj Fj Fj Fj")]
    [InlineData(@"Fj Fj Fj Fj{\rAlternate}")]
    [InlineData(@"{\rThird}Fj Fj{\rAlternate} Fj Fj")]
    public void FinalResetStyleControlsWholeLineWrappingAndAutomaticPlacement(string text)
    {
        AssertRoundTripCoverage(File(text));
    }

    /// <summary>Dialogue 非零覆盖逐轴优先，其余轴仍采用最终重置样式。</summary>
    [LibassReferenceTheory]
    [InlineData(100, 0, 55)]
    [InlineData(0, 100, 0)]
    public void DialogueOverridesFinalResetMarginsIndependently(int left, int right, int vertical)
    {
        AssertRoundTripCoverage(File(@"Fj Fj Fj Fj{\rAlternate}", left, right, vertical));
    }

    /// <summary>显式位置和原点旋转下仍由有效边距决定换行宽度。</summary>
    [LibassReferenceFact]
    public void ResetStyleWrappingRemainsEffectiveWithPositionAndRotationOrigin()
    {
        AssertRoundTripCoverage(File(@"{\rAlternate\pos(320,100)\org(300,80)\frz37}Fj Fj Fj Fj"));
    }

    /// <summary>首个显式对齐不被命名重置覆盖，垂直边距使用最终样式。</summary>
    [LibassReferenceFact]
    public void FirstExplicitAlignmentAndFinalResetMarginsRemainIndependent()
    {
        AssertRoundTripCoverage(File(@"{\an3\rAlternate}Fj Fj Fj Fj"));
    }

    /// <summary>尾部裸重置或未知样式恢复原样式边距，不保留先前命名样式的窄行宽。</summary>
    [LibassReferenceTheory]
    [InlineData(@"\r")]
    [InlineData(@"\rMissing")]
    public void TrailingBareAndUnknownResetRestoreOriginalStyleMargins(string reset)
    {
        AssertRoundTripCoverage(File(@"{\rAlternate}Fj Fj Fj Fj{" + reset + "}", 0, 0, 55));
    }

    private static void AssertRoundTripCoverage(string source)
    {
        var parsed = AssSubtitleFormat.Parse(source, 640, 360);
        var document = ProjectEditingOperations.ImportSubtitleLines(new() { Width = 640, Height = 360 }, parsed, "Reset style reference");
        var restored = ProjectStore.Deserialize(ProjectStore.Serialize(document));
        var written = AssSubtitleFormat.Write(restored);
        using var reference = new LibassReferenceRenderer();
        foreach (var time in new long[] { 0, 733, 3999 })
        {
            var expected = reference.Render(source, time);
            var actual = reference.Render(written.Text, time);
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
    }

    private static string File(string text, int left = 0, int right = 0, int vertical = 0)
    {
        return $$"""
            [Script Info]
            ScriptType: v4.00+
            PlayResX: 640
            PlayResY: 360
            WrapStyle: 1
            ScaledBorderAndShadow: yes
            YCbCr Matrix: None
            [V4+ Styles]
            Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
            Style: Default,Noto Sans,48,&H0000FF00,&H000000FF,&H00FF0000,&HFF000000,0,0,0,0,100,100,0,0,1,0,0,7,32,32,32,1
            Style: Alternate,Noto Sans,48,&H0000FF00,&H000000FF,&H00FF0000,&HFF000000,0,0,0,0,100,100,0,0,1,0,0,3,280,280,70,1
            Style: Third,Noto Sans,48,&H0000FF00,&H000000FF,&H00FF0000,&HFF000000,0,0,0,0,100,100,0,0,1,0,0,5,110,150,50,1
            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            Dialogue: 0,0:00:00.00,0:00:04.00,Default,,{{left}},{{right}},{{vertical}},,{{text}}
            """;
    }
}
