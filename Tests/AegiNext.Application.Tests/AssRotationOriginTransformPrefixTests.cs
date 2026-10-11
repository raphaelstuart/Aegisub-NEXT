using AegiNext.Application.SubtitleFormats;
using AegiNext.Core.Editing;
using AegiNext.Core.Projects;
using AegiNext.Core.Timing;

namespace AegiNext.Application.Tests;

/// <summary>验证前导普通文本中的事件旋转原点；混合嵌套用例仅保护既有轨道解析，不证明 libass 嵌套动画语义等价。</summary>
public sealed class AssRotationOriginTransformPrefixTests
{
    /// <summary>各变换参数形式及嵌套位置采用首个有效原点，并保持公开导出往返。</summary>
    [Theory]
    [InlineData(@"\t(comment\org(200,140))")]
    [InlineData(@"\t(2,comment\org(200,140))")]
    [InlineData(@"\t(0,1000,comment\org(200,140))")]
    [InlineData(@"\t(0,1000,2,comment\org(200,140))")]
    [InlineData(@"\t(comment\t(inner\org(200,140))\org(300,250))")]
    [InlineData(@"\t(0,1000,\t(inner\org(200,140))\org(300,250))")]
    [InlineData(@"\t(comment\org(200,140))\r\org(300,250)")]
    [InlineData(@"\org(200,140)\t(comment\org(300,250))")]
    public void LeadingTextOriginSurvivesPublicImportAndExport(string tags)
    {
        var parsed = Parse(@"{\pos(120,80)\frz23" + tags + "}Axis");
        var clip = Assert.Single(parsed.Clips);

        Assert.Equal(new ScenePoint(80, 60), clip.Transform.Position);
        Assert.Equal(new ScenePoint(80, 60), clip.Transform.Pivot);
        Assert.Equal("Axis", clip.Line.Text);
        AssertNoOriginLoss(parsed);

        var document = ProjectEditingOperations.ImportSubtitleLines(new() { Width = 640, Height = 360 }, parsed, "ASS");
        var written = AssSubtitleFormat.Write(document);
        Assert.Contains(@"\org(200,140)", written.Text, StringComparison.Ordinal);
        Assert.Contains(@"\pos(120,80)", written.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(written.Diagnostics, diagnostic => diagnostic.Code is "Ass.RotationOrigin" or "Ass.TransformPivotAnimation");

        var roundTrip = AssSubtitleFormat.Parse(written.Text, 640, 360);
        Assert.Equal(clip.Transform, Assert.Single(roundTrip.Clips).Transform);
        AssertNoOriginLoss(roundTrip);
    }

    /// <summary>过滤嵌套纯原点时保留既有外层旋转轨道及其求值，不验证该混合嵌套写法的 libass 语义。</summary>
    [Fact]
    public void NestedOriginKeepsTheExistingOuterRotationTrack()
    {
        var parsed = Parse(@"{\pos(120,80)\frz23\t(0,1000,\t(comment\org(200,140))\frz47)}Axis");
        var clip = Assert.Single(parsed.Clips);
        var rotation = Assert.Single(clip.Tracks.Where(track => track.Property == AnimationProperty.ROTATION));

        Assert.Equal(new ScenePoint(80, 60), clip.Transform.Pivot);
        Assert.Equal(-23, SceneEvaluator.EvaluateTrack(rotation, MediaTime.Zero).Scalar);
        Assert.Equal(-35, SceneEvaluator.EvaluateTrack(rotation, new(1, 2)).Scalar);
        Assert.Equal(-47, SceneEvaluator.EvaluateTrack(rotation, new(3, 2)).Scalar);
        AssertNoOriginLoss(parsed);
    }

    /// <summary>原点识别保留既有外层颜色轨道，不验证该混合嵌套写法的 libass 语义。</summary>
    [Fact]
    public void NestedOriginKeepsTheExistingOuterColorTrack()
    {
        var parsed = Parse(@"{\pos(120,80)\frz23\t(0,1000,\t(comment\org(200,140))\1c&H0000FF&)}Axis");
        var clip = Assert.Single(parsed.Clips);
        var fill = Assert.Single(clip.Tracks.Where(track => track.Property == AnimationProperty.FILL));

        Assert.Equal(new ScenePoint(80, 60), clip.Transform.Pivot);
        Assert.NotEqual(SceneEvaluator.EvaluateTrack(fill, MediaTime.Zero), SceneEvaluator.EvaluateTrack(fill, new(1)));
        AssertNoOriginLoss(parsed);
    }

    /// <summary>嵌套几何被舍弃时保留外层轨道，原点转换报告损失而不推断完整源几何。</summary>
    [Fact]
    public void MixedNestedTransformKeepsItsUnsupportedDiagnosticAndTheOuterAnimation()
    {
        var parsed = Parse(@"{\pos(120,80)\frz23\t(0,1000,\t(comment\org(200,140)\frz97)\frz47)}Axis");
        var clip = Assert.Single(parsed.Clips);
        var rotation = Assert.Single(clip.Tracks.Where(track => track.Property == AnimationProperty.ROTATION));

        Assert.Equal(default, clip.Transform.Pivot);
        Assert.Equal(-35, SceneEvaluator.EvaluateTrack(rotation, new(1, 2)).Scalar);
        Assert.Contains(parsed.Diagnostics, diagnostic => diagnostic.Code == "Ass.UnsupportedTag");
        Assert.Contains(parsed.Diagnostics, diagnostic => diagnostic.Code == "Ass.RotationOrigin");
    }

    /// <summary>前导文本的处理不会扩大既有严格数字规则，非法原点仍局部诊断。</summary>
    [Fact]
    public void MalformedNumberRemainsDiagnosedAndDoesNotConsumeTheValidOrigin()
    {
        var parsed = Parse(@"{\pos(120,80)\frz23\t(comment\org(no,140)\org(200,140))}Axis");

        Assert.Equal(new ScenePoint(80, 60), Assert.Single(parsed.Clips).Transform.Pivot);
        Assert.Single(parsed.Diagnostics.Where(diagnostic => diagnostic.Code == "Ass.RotationOrigin"));
        Assert.DoesNotContain(parsed.Diagnostics, diagnostic => diagnostic.Code is "Ass.UnsupportedTag" or "Ass.TransformTiming");
    }

    /// <summary>语法和数字有效但超出原生范围的首原点仍消费事件原点，后值不能替代。</summary>
    [Fact]
    public void UnstorableFirstOriginIsNotReplacedByTheFollowingOrigin()
    {
        var parsed = Parse(@"{\pos(120,80)\frz23\t(comment\org(2000000000,140)\org(200,140))}Axis");
        var clip = Assert.Single(parsed.Clips);

        Assert.Equal(new ScenePoint(), clip.Transform.Position);
        Assert.Equal(new ScenePoint(), clip.Transform.Pivot);
        Assert.Equal(-23, clip.Transform.Rotation);
        Assert.Contains(parsed.Diagnostics, diagnostic => diagnostic.Code == "Ass.RotationOrigin");
    }

    private static AssImportResult Parse(string body) => AssSubtitleFormat.Parse("""
        [Script Info]
        ScriptType: v4.00+
        PlayResX: 640
        PlayResY: 360
        WrapStyle: 2
        [V4+ Styles]
        Format: Name, Fontname, Fontsize, PrimaryColour, Outline, Shadow, Alignment
        Style: Default,Noto Sans,20,&H00FFFFFF,0,0,7
        [Events]
        Format: Layer, Start, End, Style, Text
        Dialogue: 0,0:00:10.00,0:00:12.00,Default,
        """ + body, 640, 360);

    private static void AssertNoOriginLoss(AssImportResult parsed)
    {
        Assert.DoesNotContain(parsed.Diagnostics, diagnostic => diagnostic.Code is "Ass.RotationOrigin" or "Ass.UnsupportedTag" or "Ass.TransformTiming");
    }
}
