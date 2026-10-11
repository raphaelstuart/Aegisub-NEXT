using AegiNext.Application.SubtitleFormats;
using AegiNext.Core.Editing;
using AegiNext.Core.Projects;
using AegiNext.Core.Timing;

namespace AegiNext.Application.Tests;

public sealed class AssRotationOriginImportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FixedPlacementUsesExistingLocalPivotAndPreservesIndependentPointGeometry(bool animated)
    {
        var animation = animated ? "\\t(500,1500,\\frz97)" : string.Empty;
        var parsed = Parse("{\\pos(120,80)\\org(200,140)\\fscx150\\fscy75\\frz23" + animation + "}Axis");
        var document = Import(parsed);
        var layer = Assert.Single(document.Layers);

        AssertPoint(new(80, 60), layer.Transform.Position);
        AssertPoint(new(80 / 1.5, 80), layer.Transform.Pivot);
        Assert.DoesNotContain(parsed.Diagnostics, item => item.Code is "Ass.RotationOrigin" or "Ass.UnsupportedTag");
        foreach (var milliseconds in new[] { 0, 499, 500, 723, 1000, 1500, 1999 })
        {
            var angle = animated ? 23 + 74 * Math.Clamp((milliseconds - 500) / 1000d, 0, 1) : 23;
            var effective = SceneEvaluator.EvaluateLayer(layer, document.Subtitles[0], new(milliseconds, 1000)).Transform;
            foreach (var local in new[] { new ScenePoint(), new(25, 11), new(-7, 31) })
            {
                var expected = Add(new(200, 140), Rotate(new(-80 + 1.5 * local.X, -60 + 0.75 * local.Y), -angle));
                AssertPoint(expected, NativePoint(new(120, 80), effective, local));
            }
        }
    }

    [Theory]
    [InlineData(500, 1500, false)]
    [InlineData(-1000, 3000, false)]
    [InlineData(-1000, 3000, true)]
    [InlineData(-3000, -1000, false)]
    [InlineData(3000, 5000, false)]
    public void StaticRotationMapsTheVisibleMoveWithoutChangingItsClock(int start, int end, bool negativeKaraoke)
    {
        var karaoke = negativeKaraoke ? "\\kt-50\\k100" : string.Empty;
        var parsed = Parse($"{{\\move(120,80,260,180,{start},{end})\\org(200,140)\\fscx150\\fscy75\\frz23{karaoke}}}Axis");
        var document = Import(parsed);
        var layer = Assert.Single(document.Layers);

        AssertPoint(new(80 / 1.5, 80), layer.Transform.Pivot);
        Assert.Equal(negativeKaraoke ? new MediaTime(1, 2) : MediaTime.Zero, layer.AnimationOffset);
        Assert.DoesNotContain(parsed.Diagnostics, item => item.Code is "Ass.RotationOrigin" or "Ass.UnsupportedTag");
        foreach (var milliseconds in new[] { 0, 500, 723, 1000, 1500, 1999 })
        {
            var fraction = Math.Clamp((milliseconds - start) / (double)(end - start), 0, 1);
            var effective = SceneEvaluator.EvaluateLayer(layer, document.Subtitles[0],
                layer.AnimationOffset + new MediaTime(milliseconds, 1000)).Transform;
            var expected = Add(new(200, 140), Rotate(new(-80 + 140 * fraction + 1.5 * 25,
                -60 + 100 * fraction + 0.75 * 11), -23));
            AssertPoint(expected, NativePoint(new(120, 80), effective, new(25, 11)));
        }
    }

    [Theory]
    [InlineData("{\\pos(120,80)\\org(200,140)\\org(300,250)}Axis")]
    [InlineData("{\\pos(120,80)\\org(200,140)\\r\\org(300,250)}Axis")]
    [InlineData("{\\pos(120,80)}Axis{\\org(200,140)}")]
    [InlineData("{\\pos(120,80)\\t(500,1500,\\org(200,140))}Axis")]
    [InlineData("{\\pos(120,80)\\t(500,1500,\\org(200,140)\\frz30)}Axis")]
    [InlineData("{\\pos(120,80)\\t(\\t(\\org(200,140))\\org(300,250))}Axis")]
    public void FirstValidOriginIsAnEventPropertyIndependentOfVisibleRuns(string body)
    {
        var parsed = Parse(body);
        var clip = Assert.Single(parsed.Clips);

        AssertPoint(new(80, 60), clip.Transform.Position);
        AssertPoint(new(80, 60), clip.Transform.Pivot);
        Assert.DoesNotContain(parsed.Diagnostics, item => item.Code is "Ass.RotationOrigin" or "Ass.UnsupportedTag");
    }

    [Theory]
    [InlineData("\\org(no,140)")]
    [InlineData("\\org(200)")]
    [InlineData("\\org(NaN,140)")]
    public void MalformedOriginIsDiagnosedLocallyAndDoesNotConsumeTheFirstValidValue(string malformed)
    {
        var parsed = Parse("{\\pos(120,80)" + malformed + "\\org(200,140)}Axis");

        Assert.Equal("Axis", Assert.Single(parsed.Lines).Text);
        AssertPoint(new(80, 60), Assert.Single(parsed.Clips).Transform.Pivot);
        Assert.Contains(parsed.Diagnostics, item => item.Code == "Ass.RotationOrigin");
    }

    [Fact]
    public void SourceValidButUnstorableFirstOriginCannotBeReplacedByALaterOne()
    {
        var parsed = Parse("{\\pos(120,80)\\org(2000000000,140)\\org(200,140)\\frz23}Axis");
        var clip = Assert.Single(parsed.Clips);

        Assert.Equal(new ScenePoint(), clip.Transform.Pivot);
        Assert.Equal(new ScenePoint(), clip.Transform.Position);
        Assert.Equal(-23, clip.Transform.Rotation);
        Assert.Contains(parsed.Diagnostics, item => item.Code == "Ass.RotationOrigin");
    }

    [Theory]
    [InlineData("{\\pos(120,80)\\org(200,140)\\fscx0\\frz23}Axis")]
    [InlineData("{\\pos(120,80)\\org(200,140)\\fscx0.000001\\frz23}Axis")]
    [InlineData("{\\pos(-1000000000,80)\\org(1000000000,140)\\frz23}Axis")]
    [InlineData("{\\move(0,80,1000000000,180)\\org(1000000000,140)}Axis")]
    [InlineData("{\\move(120,80,260,180)\\org(200,140)\\t(0,2000,\\frz90)}Axis")]
    [InlineData("{\\pos(120,80)\\org(200,140)\\t(0,2000,\\fscx200)}Axis")]
    [InlineData("{\\pos(120,80)\\org(200,140)\\t(0,1500,\\fscx200)\\t(500,2000,\\fscx300)\\t(0,2000,\\fscy400)}Axis")]
    [InlineData("{\\pos(120,80)\\org(200,140)\\frz23}A{\\frz47}xis")]
    [InlineData("{\\pos(120,80)\\org(200,140)}A{\\fscx150}xis")]
    [InlineData("{\\org(200,140)\\frz23}Axis")]
    public void UnsupportedOriginKeepsAllPreviouslyRepresentableGeometry(string body)
    {
        var parsed = Parse(body);
        var withoutOrigin = System.Text.RegularExpressions.Regex.Replace(body, @"\\org\([^)]*\)", string.Empty);
        var baseline = Parse(withoutOrigin);
        var actual = Assert.Single(parsed.Clips);
        var expected = Assert.Single(baseline.Clips);

        Assert.Equal(expected.Transform, actual.Transform);
        Assert.Equal(expected.Line.Text, actual.Line.Text);
        Assert.Equal(expected.Line.Style.Position, actual.Line.Style.Position);
        Assert.Equal(expected.Tracks.Length, actual.Tracks.Length);
        foreach (var pair in expected.Tracks.Zip(actual.Tracks))
        {
            Assert.Equal(pair.First.Property, pair.Second.Property);
            foreach (var time in new[] { MediaTime.Zero, new MediaTime(723, 1000), new MediaTime(2) })
            {
                Assert.Equal(SceneEvaluator.EvaluateTrack(pair.First, time), SceneEvaluator.EvaluateTrack(pair.Second, time));
            }
        }
        Assert.Contains(parsed.Diagnostics, item => item.Code == "Ass.RotationOrigin");
        _ = Import(parsed);
    }

    [Fact]
    public void ColorOnlyRangesDoNotPreventOriginMapping()
    {
        var parsed = Parse("{\\pos(120,80)\\org(200,140)\\frz23}A{\\t(0,2000,\\1c&H0000FF&)}xis");

        AssertPoint(new(80, 60), Assert.Single(parsed.Clips).Transform.Pivot);
        Assert.NotEmpty(Assert.Single(parsed.Lines).AnimationRanges);
        Assert.DoesNotContain(parsed.Diagnostics, item => item.Code == "Ass.RotationOrigin");
    }

    [Fact]
    public void NonuniformCanvasResamplingRejectsEvenAnInitiallyZeroRotationAnimation()
    {
        var parsed = AssSubtitleFormat.Parse(File("{\\pos(120,80)\\org(200,140)\\t(0,2000,\\frz90)}Axis"), 1280, 360);

        Assert.Equal(new ScenePoint(), Assert.Single(parsed.Clips).Transform.Pivot);
        Assert.Contains(parsed.Diagnostics, item => item.Code == "Ass.RotationOrigin");
        Assert.Contains(Assert.Single(parsed.Clips).Tracks, track => track.Property == AnimationProperty.ROTATION);
    }

    [Fact]
    public void ProjectTextProjectionDoesNotImportExternalRotationOrigins()
    {
        var original = new SubtitleLine { Text = "Axis", End = new(2) };
        var result = new AssTextParser(original, new Dictionary<string, AssStyleDefinition>(), SceneColor.White,
            projectSource: true).Parse("{\\org(200,140)}Axis");

        Assert.Equal(new LayerTransform(), result.Transform);
        Assert.Empty(result.PlacementTracks);
        Assert.Contains(result.Diagnostics, item => item.Code == "Ass.UnsupportedTag");
    }

    private static ProjectDocument Import(AssImportResult parsed)
    {
        var document = ProjectEditingOperations.ImportSubtitleLines(new() { Width = 640, Height = 360 }, parsed, "ASS");
        ProjectValidator.Validate(document);
        return document;
    }

    private static AssImportResult Parse(string body) => AssSubtitleFormat.Parse(File(body), 640, 360);

    private static string File(string body) => """
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
        """ + body;

    private static ScenePoint NativePoint(ScenePoint basis, LayerTransform transform, ScenePoint local)
    {
        var scaled = new ScenePoint((local.X - transform.Pivot.X) * transform.Scale.X,
            (local.Y - transform.Pivot.Y) * transform.Scale.Y);
        return Add(Add(basis, transform.Position), Rotate(scaled, transform.Rotation));
    }

    private static ScenePoint Rotate(ScenePoint point, double angle)
    {
        var radians = angle * Math.PI / 180;
        return new(point.X * Math.Cos(radians) - point.Y * Math.Sin(radians),
            point.X * Math.Sin(radians) + point.Y * Math.Cos(radians));
    }

    private static ScenePoint Add(ScenePoint first, ScenePoint second) => new(first.X + second.X, first.Y + second.Y);

    private static void AssertPoint(ScenePoint expected, ScenePoint actual)
    {
        Assert.Equal(expected.X, actual.X, 8);
        Assert.Equal(expected.Y, actual.Y, 8);
    }
}
