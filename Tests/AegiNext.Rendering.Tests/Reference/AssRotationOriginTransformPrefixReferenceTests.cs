using AegiNext.Application;
using AegiNext.Application.SubtitleFormats;
using AegiNext.Core.Projects;

namespace AegiNext.Rendering.Tests.Reference;

/// <summary>用独立 libass 验证变换标签前导文本的原点识别与真实公开出口。</summary>
public sealed class AssRotationOriginTransformPrefixReferenceTests
{
    /// <summary>前导文本及嵌套纯原点与手写直接原点保持相同覆盖，并保留导出画面。</summary>
    [LibassReferenceTheory]
    [InlineData(@"\t(comment\org(205,155))")]
    [InlineData(@"\t(0,1000,comment\org(205,155))")]
    [InlineData(@"\t(comment\t(inner\org(205,155))\org(300,250))")]
    [InlineData(@"\t(0,1000,\t(inner\org(205,155))\org(300,250))")]
    public void LeadingTextOriginMatchesIndependentSourceAndPublicExport(string tags)
    {
        var source = ReferenceSubtitleProject.Script(@"{\q2\4a&HFF&\pos(240,180)\frz37" + tags + "}Fj");
        var canonical = ReferenceSubtitleProject.Script(@"{\q2\4a&HFF&\pos(240,180)\frz37\org(205,155)}Fj");
        var parsed = AssSubtitleFormat.Parse(source, 640, 360);
        var document = ProjectEditingOperations.ImportSubtitleLines(new() { Width = 640, Height = 360 }, parsed, "ASS");
        var written = AssSubtitleFormat.Write(document);

        Assert.DoesNotContain(parsed.Diagnostics, diagnostic => diagnostic.Code is "Ass.RotationOrigin" or "Ass.UnsupportedTag" or "Ass.TransformTiming");
        Assert.Contains(@"\org(205,155)", written.Text, StringComparison.Ordinal);
        using var reference = new LibassReferenceRenderer();
        foreach (var time in new long[] { 0, 500, 1000, 3999 })
        {
            var expected = reference.Render(canonical, time);
            var original = reference.Render(source, time);
            var actual = reference.Render(written.Text, time);
            Assert.Equal(expected.Pixels, original.Pixels);
            var energy = expected.Energy(3);
            Assert.True(energy > 100 && actual.Energy(3) > 100, $"Empty reference ink at {time} ms.");
            var difference = 0d;
            for (var index = 3; index < expected.Pixels.Length; index += 4)
            {
                difference += Math.Abs(actual.Pixels[index] - expected.Pixels[index]);
            }
            Assert.InRange(difference / energy, 0, 0.02);
            var expectedBounds = expected.InkBounds();
            var actualBounds = actual.InkBounds();
            Assert.InRange(Math.Abs(actualBounds.Left - expectedBounds.Left), 0, 1);
            Assert.InRange(Math.Abs(actualBounds.Top - expectedBounds.Top), 0, 1);
            Assert.InRange(Math.Abs(actualBounds.Right - expectedBounds.Right), 0, 1);
            Assert.InRange(Math.Abs(actualBounds.Bottom - expectedBounds.Bottom), 0, 1);
        }
    }
}
