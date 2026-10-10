using AegiNext.Core.Editing;
using AegiNext.Core.Effects;
using AegiNext.Core.Projects;
using AegiNext.Rendering.Projects;
using SkiaSharp;

namespace AegiNext.Rendering.Tests;

/// <summary>验证分组脚本的编译结果进入真实塑形、绘制和布局缓存链路。</summary>
public sealed class GroupedEffectRenderingTests
{
    /// <summary>逐字错峰只改变当前组的可见位置或缩放，保留整句及连字的原始布局。</summary>
    [Theory]
    [InlineData("ABCD", false)]
    [InlineData("ABCD", true)]
    [InlineData("ffi", false)]
    [InlineData("ffi", true)]
    public void StaggeredPulseAndBounceMoveOnlyTheActiveGraphemeAndReuseShaping(string text, bool bounce)
    {
        var document = Compile(Document(text), Source("grapheme", bounce));
        using var renderer = new ProjectSceneRenderer(new DirectoryProjectAssetResolver(AppContext.BaseDirectory));
        var plain = renderer.MeasureSubtitleTextLayout(document, document.Subtitles[0]);
        var shapers = renderer.CachedTextShaperCount;
        var before = new float[document.Width * document.Height * 4];
        var after = new float[before.Length];
        Assert.True(renderer.CopyCachedFramePixels(document, new(0), before));

        var active = renderer.MeasureSubtitleTextLayout(document, Assert.Single(SceneEvaluator.Evaluate(document, new(1, 8))));

        Assert.Equal(text.Length, document.Subtitles[0].AnimationRanges.Length);
        Assert.Equal(plain.Runs, active.Runs);
        Assert.Equal(plain.BasePosition, active.BasePosition);
        Assert.NotEqual(plain.Graphemes[0].Bounds, active.Graphemes[0].Bounds);
        for (var index = 1; index < plain.Graphemes.Length; index++)
        {
            Assert.Equal(plain.Graphemes[index].Bounds, active.Graphemes[index].Bounds);
        }
        Assert.True(renderer.CopyCachedFramePixels(document, new(1, 8), after));
        Assert.NotEqual(before, after);
        Assert.False(renderer.CopyCachedFramePixels(document, new(1, 8), after));
        for (var frame = 0; frame <= 4; frame++)
        {
            var layout = renderer.MeasureSubtitleTextLayout(document, Assert.Single(SceneEvaluator.Evaluate(document, new(frame, 8))));
            Assert.Equal(plain.Runs, layout.Runs);
            Assert.Equal(1, renderer.CachedSubtitleLayoutCount);
            Assert.Equal(shapers, renderer.CachedTextShaperCount);
        }
        var returned = renderer.MeasureSubtitleTextLayout(document, Assert.Single(SceneEvaluator.Evaluate(document, new(1, 2))));
        Assert.Equal(plain.Graphemes[0].Bounds, returned.Graphemes[0].Bounds);
    }

    /// <summary>跨字体的两字组和硬行组共用组中心，局部缩放不重排其他行。</summary>
    [Theory]
    [InlineData("chunk(2)")]
    [InlineData("line")]
    public void GroupScaleUsesOneSharedCenterAndKeepsMixedFontLinesInTheirOriginalLayout(string unit)
    {
        var document = Document("AB\nبب");
        var arabic = new ProjectAsset(Guid.NewGuid(), ProjectAssetKind.FONT, "Fixtures/NotoSansArabic.ttf");
        document = document with
        {
            Assets = [.. document.Assets, arabic],
            Subtitles = [document.Subtitles[0] with
            {
                InlineSpans = [new(3, 2, new() { FontAssetId = arabic.Id, FontSize = 36 })]
            }]
        };
        document = Compile(document, Source(unit, false));
        using var renderer = new ProjectSceneRenderer(new DirectoryProjectAssetResolver(AppContext.BaseDirectory));
        var plain = renderer.MeasureSubtitleTextLayout(document, document.Subtitles[0]);
        var evaluated = Assert.Single(SceneEvaluator.Evaluate(document, new(1, 8)));
        var active = renderer.MeasureSubtitleTextLayout(document, evaluated);
        var range = evaluated.AnimationRanges[0];
        var bounds = plain.GetSelectionRects(range.Utf16Start, range.Utf16Length).Aggregate(SKRect.Union);
        var matrix = SKMatrix.CreateTranslation(bounds.MidX, bounds.MidY);
        matrix = SKMatrix.Concat(matrix, SKMatrix.CreateScale((float)range.Scale.X, (float)range.Scale.Y));
        matrix = SKMatrix.Concat(matrix, SKMatrix.CreateTranslation(-bounds.MidX, -bounds.MidY));

        Assert.Equal(2, document.Subtitles[0].AnimationRanges.Length);
        Assert.Equal(2, range.Utf16Length);
        Assert.Equal(plain.Runs, active.Runs);
        Assert.Equal(plain.BasePosition, active.BasePosition);
        Assert.Equal(plain.Bounds, renderer.MeasureSubtitlePlacement(document, evaluated).Bounds);
        Assert.Contains(active.Runs, run => run.Style.FontAssetId == arabic.Id && run.Style.FontSize == 36);
        foreach (var glyph in plain.Graphemes)
        {
            var actual = active.Graphemes.Single(item => item.Utf16Start == glyph.Utf16Start);
            Assert.Equal(glyph.Utf16Start < 2 ? matrix.MapRect(glyph.Bounds) : glyph.Bounds, actual.Bounds);
        }
        Assert.Equal(1, renderer.CachedSubtitleLayoutCount);
    }

    private static ProjectDocument Compile(ProjectDocument document, string source)
    {
        var result = EffectScriptCompiler.CompileTarget(EffectScriptParser.Parse(source), document.Layers[0], subtitle: document.Subtitles[0]);
        return document with { Subtitles = [result.Subtitle!], Layers = [result.PreparedLayer! with { Tracks = result.Tracks }] };
    }

    private static string Source(string unit, bool bounce)
    {
        var property = bounce ? "position" : "scale";
        var peak = bounce ? "offset(0, -12)" : "factor(1.25, 1.25)";
        return $$"""
            effect "grouped-render" version 2
            short-clip compress
            scope letters current
                unit {{unit}}
                stagger 1s
                segment pulse fixed 250ms pingpong
                    at 0 {{property}} base power(2)
                    at 1 {{property}} {{peak}}
                end
                segment rest flex 1
                end
            end
            """;
    }

    private static ProjectDocument Document(string text)
    {
        var font = new ProjectAsset(Guid.NewGuid(), ProjectAssetKind.FONT, "Fixtures/NotoSans.ttf");
        var line = new SubtitleLine
        {
            Text = text, End = new(5), Style = new()
            {
                FontAssetId = font.Id, FontSize = 24, Alignment = TextAlignment.TOP_LEFT, Margins = new(8, 24, 8),
                WrapMode = SubtitleWrapMode.NO_WRAP, Fill = SceneColor.White, StrokeWidth = 0, ShadowColor = SceneColor.Transparent
            }
        };
        return new() { Width = 384, Height = 160, Assets = [font], Subtitles = [line], Layers = [new() { SubtitleId = line.Id, End = line.End }] };
    }
}
