using System.Globalization;
using System.Text.RegularExpressions;
using AegiNext.Application.SubtitleFormats;
using AegiNext.Core.Editing;
using AegiNext.Core.Projects;
using AegiNext.Core.Timing;

namespace AegiNext.Application.Tests;

public sealed class AssRotationOriginExportTests
{
    [Theory]
    [InlineData("\\pos(120,80)\\frz23")]
    [InlineData("\\pos(120,80)\\frz23\\t(500,1500,\\frz97)")]
    [InlineData("\\move(120,80,260,180,500,1500)\\frz23")]
    [InlineData("\\move(120,80,260,180,-1000,3000)\\frz23")]
    [InlineData("\\move(120,80,260,180,-1000,3000)\\frz23\\kt-50\\k100")]
    [InlineData("\\move(120,80,260,180,-3000,-1000)\\frz23")]
    [InlineData("\\move(120,80,260,180,3000,5000)\\frz23")]
    public void UneditedSupportedImportRestoresOriginAndMotionAfterProjectPersistence(string geometry)
    {
        var imported = Import("{" + geometry + "\\org(200,140)\\fscx150\\fscy75}Axis");
        var restored = ProjectStore.Deserialize(ProjectStore.Serialize(imported));
        var nativeBytes = ProjectStore.Serialize(restored);
        var written = AssSubtitleFormat.Write(restored);
        var origins = Regex.Matches(written.Text, @"\\org\(([^)]*)\)");

        AssertPoint(new(200, 140), Point(Assert.Single(origins).Groups[1].Value));
        Assert.DoesNotContain(written.Diagnostics, item => item.Code is "Ass.RotationOrigin" or "Ass.TransformPivotAnimation" or "Subtitle.Composition");
        var roundTrip = ImportFile(written.Text);
        AssertGeometry(imported, roundTrip);
        Assert.Equal(nativeBytes, ProjectStore.Serialize(restored));
    }

    [Fact]
    public void DynamicRotationUsesEffectiveConstantPositionInsteadOfTheStaticFallback()
    {
        var document = Import("{\\pos(120,80)}Axis");
        var layer = document.Layers[0] with
        {
            Transform = new() { Position = new(999, 999), Pivot = new(20, 30), Scale = new(1.5, 0.75) },
            Tracks =
            [
                new(AnimationProperty.POSITION, [new(new(0), new ScenePoint(80, 60)), new(new(2), new ScenePoint(80, 60))]),
                new(AnimationProperty.ROTATION, [new(new(0), 0), new(new(2), -97)])
            ]
        };
        document = document with { Layers = [layer] };
        var written = AssSubtitleFormat.Write(document);

        Assert.Contains("\\org(200,140)", written.Text, StringComparison.Ordinal);
        Assert.Contains("\\pos(170,117.5)", written.Text, StringComparison.Ordinal);
        Assert.Contains("\\t(", written.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(written.Diagnostics, item => item.Code is "Ass.TransformPivotAnimation" or "Subtitle.Composition");
        AssertGeometry(document, ImportFile(written.Text));
        Assert.Same(layer, document.Layers[0]);
        Assert.Equal(new ScenePoint(999, 999), layer.Transform.Position);
    }

    [Theory]
    [InlineData(AnimationProperty.POSITION)]
    [InlineData(AnimationProperty.SCALE)]
    [InlineData(AnimationProperty.ROTATION)]
    public void ActualRangeGeometryDoesNotEnterTheWholeLineOriginConversion(AnimationProperty property)
    {
        var document = Import("{\\pos(120,80)\\org(200,140)\\frz23\\t(0,2000,\\frz97)}Axis");
        var range = new SubtitleAnimationRange(Guid.NewGuid(), 0, 1)
        {
            Offset = property == AnimationProperty.POSITION ? new(5, 2) : default,
            Scale = property == AnimationProperty.SCALE ? new(1.5, 1) : new(1, 1),
            Rotation = property == AnimationProperty.ROTATION ? 15 : 0
        };
        document = document with { Subtitles = [document.Subtitles[0] with { AnimationRanges = [range] }] };
        var written = AssSubtitleFormat.Write(document);

        Assert.DoesNotContain("\\org(", written.Text, StringComparison.Ordinal);
        Assert.Contains(written.Diagnostics, item => item.Code == "Ass.TransformPivotAnimation");
        Assert.Same(range, document.Subtitles[0].AnimationRanges[0]);
    }

    [Fact]
    public void GeneratedColorRangeDoesNotPreventWholeLineOriginConversion()
    {
        var document = Import("{\\pos(120,80)\\org(200,140)\\frz23\\t(0,2000,\\frz97)}Axis");
        var range = new SubtitleAnimationRange(Guid.NewGuid(), 0, 1)
        {
            GeneratedOrigin = new("test", "letters", null, "grapheme")
        };
        document = document with
        {
            Subtitles = [document.Subtitles[0] with { AnimationRanges = [range] }],
            Layers = [document.Layers[0] with
            {
                Tracks = document.Layers[0].Tracks.Add(new(new AnimationTrackTarget(AnimationProperty.FILL, TextRangeId: range.Id),
                    [new(new(0), SceneColor.White), new(new(2), SceneColor.Black)]))
            }]
        };
        var written = AssSubtitleFormat.Write(document);

        Assert.Contains("\\org(200,140)", written.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(written.Diagnostics, item => item.Code == "Ass.TransformPivotAnimation");
        var roundTrip = ImportFile(written.Text);
        AssertGeometry(document, roundTrip);
        Assert.DoesNotContain(roundTrip.Subtitles[0].AnimationRanges, item => item.Rotation != 0);
        Assert.DoesNotContain(roundTrip.Layers[0].Tracks, track => track.Target.TextRangeId is not null &&
            track.Property == AnimationProperty.ROTATION);
    }

    [Fact]
    public void ComponentScaleOperationsAreCheckedBeforeTheTextWriterConsumesThem()
    {
        var document = Import("{\\pos(120,80)\\org(200,140)\\frz23\\t(0,2000,\\frz97)}Axis");
        var scale = new AnimationTrack(AnimationProperty.SCALE, [])
        {
            InitialValue = new ScenePoint(1, 1),
            Transforms = [new(Guid.NewGuid(), new(0), new(2), new ScenePoint(2, 1)) { ComponentMask = 1 }]
        };
        document = document with { Layers = [document.Layers[0] with { Tracks = document.Layers[0].Tracks.Add(scale) }] };
        var written = AssSubtitleFormat.Write(document);

        Assert.DoesNotContain("\\org(", written.Text, StringComparison.Ordinal);
        Assert.Contains(written.Diagnostics, item => item.Code == "Ass.TransformPivotAnimation");
        Assert.Contains(document.Layers[0].Tracks, track => ReferenceEquals(track, scale));
    }

    [Theory]
    [InlineData(AnimationProperty.POSITION)]
    [InlineData(AnimationProperty.SCALE)]
    [InlineData(AnimationProperty.ROTATION)]
    public void RangeGeometryTracksAreCheckedBeforeTheyAreFiltered(AnimationProperty property)
    {
        var document = Import("{\\pos(120,80)\\org(200,140)\\frz23\\t(0,2000,\\frz97)}Axis");
        var range = new SubtitleAnimationRange(Guid.NewGuid(), 0, 1);
        var first = property == AnimationProperty.ROTATION ? AnimationValue.FromScalar(0) :
            AnimationValue.FromVector(property == AnimationProperty.SCALE ? new(1, 1) : new());
        var last = property == AnimationProperty.ROTATION ? AnimationValue.FromScalar(45) : AnimationValue.FromVector(new(2, 3));
        var track = new AnimationTrack(new AnimationTrackTarget(property, TextRangeId: range.Id), [new(new(0), first), new(new(2), last)]);
        document = document with
        {
            Subtitles = [document.Subtitles[0] with { AnimationRanges = [range] }],
            Layers = [document.Layers[0] with { Tracks = document.Layers[0].Tracks.Add(track) }]
        };
        var written = AssSubtitleFormat.Write(document);

        Assert.DoesNotContain("\\org(", written.Text, StringComparison.Ordinal);
        Assert.Contains(written.Diagnostics, item => item.Code == "Ass.TransformPivotAnimation");
    }

    [Theory]
    [InlineData(AnimationProperty.FONT_SIZE, false)]
    [InlineData(AnimationProperty.FONT_SIZE, true)]
    [InlineData(AnimationProperty.LETTER_SPACING, true)]
    public void StaticMeasurementDoesNotClaimAChangingCustomLayoutPivotIsExact(AnimationProperty property, bool dynamic)
    {
        var document = Import("{\\pos(120,80)\\org(200,140)\\frz23\\t(0,2000,\\frz97)}Axis");
        var line = document.Subtitles[0];
        var layoutTrack = new AnimationTrack(property, [new(new(0), 30), new(new(2), dynamic ? 60 : 30)]);
        document = document with
        {
            Subtitles = [line with { Style = line.Style with { Position = line.Style.Position! with { Pivot = new(0.2, 0.3) } } }],
            Layers = [document.Layers[0] with { Tracks = document.Layers[0].Tracks.Add(layoutTrack) }]
        };
        var measurer = new FixedSubtitlePlacementMeasurer(new(new(120, 80), new(10, 10), new(0, 0), new(80, 30)));
        var written = AssSubtitleFormat.Write(document, placementMeasurer: measurer);

        Assert.DoesNotContain("\\org(", written.Text, StringComparison.Ordinal);
        Assert.Contains(written.Diagnostics, item => item.Code == "Ass.TransformPivotAnimation");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void UnsupportedScaleUsesTheExistingFallbackAndKeepsTheNativePivot(double scale)
    {
        var document = Import("{\\pos(120,80)\\org(200,140)\\frz23}Axis");
        var layer = document.Layers[0] with { Transform = document.Layers[0].Transform with { Scale = new(scale, 1) } };
        document = document with { Layers = [layer] };

        var written = AssSubtitleFormat.Write(document);

        Assert.DoesNotContain("\\org(", written.Text, StringComparison.Ordinal);
        Assert.Equal(new ScenePoint(80, 60), layer.Transform.Pivot);
        Assert.Same(layer, document.Layers[0]);
    }

    [Theory]
    [InlineData(0.125, false)]
    [InlineData(0.3333333333333333, true)]
    public void OriginPrecisionIsReportedEvenWhenTheExportedPositionIsExact(double offset, bool quantized)
    {
        var document = Import("{\\pos(120,80)}Axis");
        document = document with
        {
            Layers = [document.Layers[0] with
            {
                Transform = new() { Position = new(offset, 0), Pivot = new(offset, 0), Rotation = -23 }
            }]
        };

        var written = AssSubtitleFormat.Write(document);

        Assert.Contains("\\pos(120,80)", written.Text, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(written.Text, @"\\org\("));
        Assert.Equal(quantized, written.Diagnostics.Any(item => item.Code == "Ass.NumberPrecision"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void OrderedPositionUsesOnlyTheComponentsEnabledByItsMask(bool animatedRotation, bool moving)
    {
        var rotation = animatedRotation ? "\\frz23\\t(0,2000,\\frz97)" : "\\frz23";
        var document = Import("{\\pos(120,80)\\org(200,140)" + rotation + "}Axis");
        var position = new AnimationTrack(AnimationProperty.POSITION, [])
        {
            InitialValue = new ScenePoint(80, 60),
            Transforms = [new(Guid.NewGuid(), new(0), new(2), new ScenePoint(moving ? 180 : 80, 999)) { ComponentMask = 1 }]
        };
        document = document with { Layers = [document.Layers[0] with { Tracks = document.Layers[0].Tracks.Add(position) }] };
        ProjectValidator.Validate(document);

        var written = AssSubtitleFormat.Write(document);

        Assert.Contains("\\org(200,140)", written.Text, StringComparison.Ordinal);
        Assert.Equal(moving, written.Text.Contains("\\move(", StringComparison.Ordinal));
        Assert.DoesNotContain(written.Diagnostics, item => item.Code is "Ass.RotationOrigin" or "Ass.TransformPivotAnimation" or "Subtitle.Composition");
        AssertGeometry(document, ImportFile(written.Text));
        Assert.Equal(new ScenePoint(moving ? 180 : 80, 999), position.Transforms[0].Value.Vector);
    }

    private static ProjectDocument Import(string body) => ImportFile("""
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
        """ + body);

    private static ProjectDocument ImportFile(string source)
    {
        var document = ProjectEditingOperations.ImportSubtitleLines(new() { Width = 640, Height = 360 },
            AssSubtitleFormat.Parse(source, 640, 360), "ASS");
        ProjectValidator.Validate(document);
        return document;
    }

    private static void AssertGeometry(ProjectDocument expected, ProjectDocument actual)
    {
        var beforeLayer = Assert.Single(expected.Layers);
        var afterLayer = Assert.Single(actual.Layers);
        foreach (var time in new[] { new MediaTime(0), new(499, 1000), new(723, 1000), new(1), new(1501, 1000), new(1999, 1000) })
        {
            var before = SceneEvaluator.EvaluateLayer(beforeLayer, expected.Subtitles[0], beforeLayer.AnimationOffset + time).Transform;
            var after = SceneEvaluator.EvaluateLayer(afterLayer, actual.Subtitles[0], afterLayer.AnimationOffset + time).Transform;
            foreach (var local in new[] { new ScenePoint(), new(25, 11) })
            {
                AssertPoint(NativePoint(expected.Subtitles[0].Style.Position!.Offset, before, local),
                    NativePoint(actual.Subtitles[0].Style.Position!.Offset, after, local));
            }
        }
    }

    private static ScenePoint NativePoint(ScenePoint basis, LayerTransform transform, ScenePoint local)
    {
        var x = (local.X - transform.Pivot.X) * transform.Scale.X;
        var y = (local.Y - transform.Pivot.Y) * transform.Scale.Y;
        var radians = transform.Rotation * Math.PI / 180;
        return new(basis.X + transform.Position.X + x * Math.Cos(radians) - y * Math.Sin(radians),
            basis.Y + transform.Position.Y + x * Math.Sin(radians) + y * Math.Cos(radians));
    }

    private static ScenePoint Point(string text)
    {
        var values = text.Split(',').Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        return new(values[0], values[1]);
    }

    private static void AssertPoint(ScenePoint expected, ScenePoint actual)
    {
        Assert.Equal(expected.X, actual.X, 7);
        Assert.Equal(expected.Y, actual.Y, 7);
    }
}
