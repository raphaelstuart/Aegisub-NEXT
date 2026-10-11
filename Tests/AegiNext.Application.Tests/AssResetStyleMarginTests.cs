using AegiNext.Application.SubtitleFormats;
using AegiNext.Core.Projects;

namespace AegiNext.Application.Tests;

/// <summary>通过公开文件交换验证最终重置样式的整行边距。</summary>
public sealed class AssResetStyleMarginTests
{
    /// <summary>重置所在正文位置不改变最终样式控制整行边距的规则。</summary>
    [Theory]
    [InlineData(@"{\rAlternate}Fj Fj Fj Fj")]
    [InlineData(@"Fj Fj {\rAlternate}Fj Fj")]
    [InlineData(@"Fj Fj Fj Fj{\rAlternate}")]
    [InlineData(@"{\rThird}Fj Fj{\rAlternate} Fj Fj")]
    public void FinalResetStyleSuppliesTheWholeLineMargins(string text)
    {
        var parsed = AssSubtitleFormat.Parse(File(text), 640, 360);
        var line = Assert.Single(parsed.Lines);
        Assert.Equal(new SubtitleMargins(280, 280, 70), line.Style.Margins);
        Assert.Equal(TextAlignment.TOP_LEFT, line.Style.Alignment);
        Assert.Null(line.Style.Position);
        AssertFileRoundTrip(parsed, 640, 360);
    }

    /// <summary>非零 Dialogue 边距逐轴覆盖最终样式，零值继承该样式。</summary>
    [Theory]
    [InlineData(0, 0, 0, 280, 280, 70)]
    [InlineData(7, 0, 0, 7, 280, 70)]
    [InlineData(0, 9, 0, 280, 9, 70)]
    [InlineData(0, 0, 11, 280, 280, 11)]
    [InlineData(7, 9, 11, 7, 9, 11)]
    public void DialogueOverridesEachFinalResetMarginIndependently(int left, int right, int vertical,
        int expectedLeft, int expectedRight, int expectedVertical)
    {
        var parsed = AssSubtitleFormat.Parse(File(@"Fj Fj Fj Fj{\rAlternate}", left, right, vertical), 640, 360);
        Assert.Equal(new SubtitleMargins(expectedLeft, expectedRight, expectedVertical), Assert.Single(parsed.Lines).Style.Margins);
        AssertFileRoundTrip(parsed, 640, 360);
    }

    /// <summary>样式与 Dialogue 两种来源都只按各轴画布比例缩放一次。</summary>
    [Fact]
    public void FinalStyleAndDialogueMarginsScaleOnceOnTheirOwnAxes()
    {
        var parsed = AssSubtitleFormat.Parse(File(@"{\rAlternate}Fj", 7, 0, 11), 1280, 1080);
        Assert.Equal(new SubtitleMargins(14, 560, 33), Assert.Single(parsed.Lines).Style.Margins);
        AssertFileRoundTrip(parsed, 1280, 1080);
    }

    /// <summary>裸重置及未知样式回到 Dialogue 原样式，保留逐轴 Dialogue 覆盖。</summary>
    [Theory]
    [InlineData(@"{\rAlternate}Fj Fj{\r} Fj Fj", false)]
    [InlineData(@"{\rAlternate}Fj Fj Fj Fj{\r}", false)]
    [InlineData(@"{\rAlternate}Fj Fj{\rMissing} Fj Fj", true)]
    [InlineData(@"{\rAlternate}Fj Fj Fj Fj{\rMissing}", true)]
    public void BareAndUnknownResetRestoreOriginalStyleMargins(string text, bool unknownStyle)
    {
        var parsed = AssSubtitleFormat.Parse(File(text, 7, 0, 11), 640, 360);
        Assert.Equal(new SubtitleMargins(7, 32, 11), Assert.Single(parsed.Lines).Style.Margins);
        Assert.Equal(unknownStyle ? 1 : 0, parsed.Diagnostics.Count(diagnostic => diagnostic.Code == "Ass.UnknownStyle"));
        AssertFileRoundTrip(parsed, 640, 360);
    }

    /// <summary>边距重置不覆盖全局首个对齐标签或已经解析的显式位置。</summary>
    [Theory]
    [InlineData(@"\an5\pos(230,120)\rAlternate")]
    [InlineData(@"\pos(230,120)\rAlternate\an5")]
    [InlineData(@"\rAlternate\an5\pos(230,120)")]
    [InlineData(@"\an5\rAlternate\pos(230,120)")]
    public void ResetMarginsPreserveAlignmentAndExplicitPosition(string tags)
    {
        var parsed = AssSubtitleFormat.Parse(File("{" + tags + "}Fj Fj Fj Fj"), 640, 360);
        var line = Assert.Single(parsed.Lines);
        Assert.Equal(new SubtitleMargins(280, 280, 70), line.Style.Margins);
        Assert.Equal(TextAlignment.MIDDLE_CENTER, line.Style.Alignment);
        Assert.Equal(new ScenePoint(0, 0), line.Style.Position!.Anchor);
        Assert.Equal(new ScenePoint(0.5, 0.5), line.Style.Position.Pivot);
        Assert.Equal(new ScenePoint(230, 120), line.Style.Position.Offset);
        AssertFileRoundTrip(parsed, 640, 360);
    }

    /// <summary>空对齐标签仍从当前重置样式取值，边距修复不改变其规则。</summary>
    [Theory]
    [InlineData("an")]
    [InlineData("a")]
    public void EmptyAlignmentReadsTheFinalResetStyleSeparatelyFromMargins(string tag)
    {
        var parsed = AssSubtitleFormat.Parse(File("{\\pos(230,120)\\rAlternate\\" + tag + "}Fj"), 640, 360);
        var line = Assert.Single(parsed.Lines);
        Assert.Equal(new SubtitleMargins(280, 280, 70), line.Style.Margins);
        Assert.Equal(TextAlignment.BOTTOM_RIGHT, line.Style.Alignment);
        Assert.Equal(new ScenePoint(1, 1), line.Style.Position!.Pivot);
        Assert.Equal(new ScenePoint(230, 120), line.Style.Position.Offset);
        AssertFileRoundTrip(parsed, 640, 360);
    }

    /// <summary>工程来源编辑继续保留原生整行边距和位置。</summary>
    [Theory]
    [InlineData(@"\r")]
    [InlineData(@"\rMissing")]
    public void ProjectSourceResetDoesNotReplaceNativeMargins(string reset)
    {
        var original = new SubtitleLine
        {
            Text = "Fj", End = new(4),
            Style = new() { Margins = new(13, 41, 27), Position = new() { Offset = new(17, -23) } }
        };
        var source = AssTextProjection.Create(original).Source + "{" + reset + "}";
        var edited = AssTextProjection.Apply(original, source).Line;
        Assert.Equal(original.Style.Margins, edited.Style.Margins);
        Assert.Equal(original.Style.Position, edited.Style.Position);
        Assert.Equal(original.Style.Alignment, edited.Style.Alignment);
        Assert.Equal(original.Text, edited.Text);
    }

    private static void AssertFileRoundTrip(AssImportResult parsed, int width, int height)
    {
        var line = Assert.Single(parsed.Lines);
        var document = ProjectEditingOperations.ImportSubtitleLines(new() { Width = width, Height = height }, parsed, "Reset style margins");
        var restored = ProjectStore.Deserialize(ProjectStore.Serialize(document));
        var written = AssSubtitleFormat.Write(restored);
        var roundTripped = Assert.Single(AssSubtitleFormat.Parse(written.Text, width, height).Lines);
        Assert.Equal(line.Style.Margins, roundTripped.Style.Margins);
        Assert.Equal(line.Style.Alignment, roundTripped.Style.Alignment);
        Assert.Equal(line.Style.Position, roundTripped.Style.Position);
        Assert.Equal(line.Text, roundTripped.Text);
        Assert.Equal(line.Start, roundTripped.Start);
        Assert.Equal(line.End, roundTripped.End);
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
