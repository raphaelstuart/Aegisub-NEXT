using System.Globalization;
using System.Text.RegularExpressions;
using AegiNext.Application.SubtitleFormats;
using AegiNext.Core.Projects;

namespace AegiNext.Application.Tests;

public sealed class AssTransformExportTests
{
    [Fact]
    public void MeasuredPivotCompensationKeepsStaticGeometryAfterEveryInlineReset()
    {
        var line = Line() with
        {
            Text = "ab",
            Style = Line().Style with
            {
                Position = new() { Anchor = new(0.5, 0.5), Pivot = new(0.2, 0.3), Offset = new(0, 0) }
            },
            InlineSpans = [new(1, 1, new() { Fill = SceneColor.Black })]
        };
        var layer = Layer(line) with
        {
            Transform = new() { Position = new(20, 30), Pivot = new(5, 10), Scale = new(2, 2), Rotation = 90 }
        };
        var document = Document(line, layer);
        var measurer = new FixedSubtitlePlacementMeasurer(new(new(600, 400), new(50, 44), new(10, 20), new(200, 80)));
        var written = AssSubtitleFormat.Write(document, placementMeasurer: measurer);
        var body = Assert.Single(Bodies(written.Text));

        Assert.Contains("\\org(620,430)", body, StringComparison.Ordinal);
        Assert.Contains("\\pos(730,522)", body, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(body, @"\\org\("));
        Assert.Single(Regex.Matches(body, @"\\pos\("));
        var resets = Regex.Matches(body, @"\{\\r[^}]*\}");
        Assert.Equal(2, resets.Count);
        foreach (Match reset in resets)
        {
            Assert.Contains("\\fscx200", reset.Value, StringComparison.Ordinal);
            Assert.Contains("\\fscy200", reset.Value, StringComparison.Ordinal);
            Assert.Contains("\\frz-90", reset.Value, StringComparison.Ordinal);
            Assert.Contains("\\bord4", reset.Value, StringComparison.Ordinal);
            Assert.Contains("\\xshad-6", reset.Value, StringComparison.Ordinal);
            Assert.Contains("\\yshad4", reset.Value, StringComparison.Ordinal);
        }
        Assert.DoesNotContain(written.Diagnostics, diagnostic => diagnostic.Code is "Subtitle.Composition" or "Ass.Pivot" or "Ass.PlacementMeasurement");
        Assert.DoesNotContain(written.Diagnostics, diagnostic => diagnostic.Code == "Ass.NumberPrecision");
        Assert.Same(layer, document.Layers[0]);
        Assert.Equal(new ScenePoint(2, 3), line.Style.ShadowOffset);
    }

    [Fact]
    public void MatchingExplicitPivotAllowsStaticGeometryWithoutFontMeasurement()
    {
        var line = Line();
        var layer = Layer(line) with { Transform = new() { Position = new(10, 20), Scale = new(2, 2), Rotation = 30 } };
        var written = AssSubtitleFormat.Write(Document(line, layer));

        Assert.Contains("\\pos(110,220)", written.Text, StringComparison.Ordinal);
        Assert.Contains("\\fscx200", written.Text, StringComparison.Ordinal);
        Assert.Contains("\\frz-30", written.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(written.Diagnostics, diagnostic => diagnostic.Code is "Ass.PlacementMeasurement" or "Subtitle.Composition");
    }

    [Fact]
    public void AlignmentOnlyGeometryWithoutMeasurementReportsTheMissingPlacementInformation()
    {
        var line = Line() with { Style = Line().Style with { Position = null } };
        var layer = Layer(line) with { Transform = new() { Rotation = 30 } };
        var written = AssSubtitleFormat.Write(Document(line, layer));

        Assert.Contains(written.Diagnostics, diagnostic => diagnostic.SubtitleId == line.Id && diagnostic.Code == "Ass.PlacementMeasurement");
        Assert.Equal("ab", Assert.Single(AssSubtitleFormat.Parse(written.Text).Lines).Text);
    }

    [Fact]
    public void ExplicitMatchingPivotPreservesCoordinatePrecisionWithoutRemeasuringTheFont()
    {
        var line = Line();
        line = line with { Style = line.Style with { Position = line.Style.Position! with { Offset = new(100.123456789, 200.123456789) } } };
        var measurer = new FixedSubtitlePlacementMeasurer(new(new(100, 200), new(1, 1), new(0, 0), new(2, 1)));
        var written = AssSubtitleFormat.Write(Document(line, Layer(line)), placementMeasurer: measurer);

        Assert.Contains("\\pos(100.123456789,200.123456789)", written.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(written.Diagnostics, diagnostic => diagnostic.Code == "Ass.NumberPrecision");
    }

    [Theory]
    [InlineData(-2)]
    public void NegativeScaleIsSkippedWhileSupportedPositionAndNativeStateArePreserved(double scale)
    {
        var line = Line();
        var layer = Layer(line) with { Transform = new() { Position = new(10, 20), Scale = new(scale, 1) } };
        var document = Document(line, layer);
        var written = AssSubtitleFormat.Write(document);

        Assert.Contains("\\pos(110,220)", written.Text, StringComparison.Ordinal);
        Assert.Contains(written.Diagnostics, diagnostic => diagnostic.SubtitleId == line.Id && diagnostic.Code == "Ass.TransformScale");
        Assert.DoesNotContain("\\fscx-", written.Text, StringComparison.Ordinal);
        Assert.Same(layer, document.Layers[0]);
        Assert.Equal(scale, layer.Transform.Scale.X);
    }

    [Fact]
    public void ZeroScaleIsExportedAsACollapsedAxisWithoutChangingTheProject()
    {
        var line = Line();
        var layer = Layer(line) with { Transform = new() { Scale = new(0, 1) } };
        var written = AssSubtitleFormat.Write(Document(line, layer));
        Assert.Contains("\\fscx0\\fscy100", written.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(written.Diagnostics, diagnostic => diagnostic.Code == "Ass.TransformScale");
        Assert.Equal(0, layer.Transform.Scale.X);
    }

    [Fact]
    public void AnimatedRotationKeepsStaticPositionAndReportsOnlyItsAppearanceCoupling()
    {
        var line = Line();
        var layer = Layer(line) with
        {
            Transform = new() { Position = new(10, 20) },
            Tracks = [new(AnimationProperty.ROTATION, [new(new(0), 0), new(new(2), 90)])]
        };
        var written = AssSubtitleFormat.Write(Document(line, layer));

        Assert.Contains("\\pos(110,220)", written.Text, StringComparison.Ordinal);
        Assert.Contains("\\frz0\\t(0,2000,1,\\frz-90)", written.Text, StringComparison.Ordinal);
        Assert.Contains(written.Diagnostics, diagnostic => diagnostic.Code == "Ass.TransformAppearanceAnimation");
        Assert.DoesNotContain(written.Diagnostics, diagnostic => diagnostic.Code == "Subtitle.Composition");
    }

    [Fact]
    public void PositionTrackOverridesStaticPositionAndKeepsContentAndExternalClocksSeparate()
    {
        var line = Line() with { Start = new(10), End = new(14) };
        var layer = Layer(line) with
        {
            Transform = new() { Position = new(999, 999) },
            AnimationOffset = new(1, 2),
            Tracks = [new(AnimationProperty.POSITION, [new(new(1), new ScenePoint(10, 20)), new(new(3), new ScenePoint(50, 60))])]
        };
        var written = AssSubtitleFormat.Write(Document(line, layer), timeOffset: new(2));
        var body = Assert.Single(Bodies(written.Text));

        Assert.Contains("Dialogue: 0,0:00:12.00,0:00:16.00", written.Text, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(body, @"\\move\("));
        Assert.DoesNotContain("\\pos(", body, StringComparison.Ordinal);
        AssertPoint(new(110, 220), PositionAt(body, 0, 4000));
        AssertPoint(new(110, 220), PositionAt(body, 500, 4000));
        AssertPoint(new(130, 240), PositionAt(body, 1500, 4000));
        AssertPoint(new(150, 260), PositionAt(body, 3000, 4000));
        Assert.DoesNotContain(written.Diagnostics, diagnostic => diagnostic.Code is "Subtitle.Composition" or "Ass.MoveApproximation");
        Assert.Equal(new ScenePoint(999, 999), layer.Transform.Position);
    }

    [Fact]
    public void ConstantPositionTrackUsesOnePositionWithoutAMoveOrCompositionWarning()
    {
        var line = Line();
        var layer = Layer(line) with
        {
            Transform = new() { Position = new(999, 999) },
            Tracks = [new(AnimationProperty.POSITION, [new(new(0), new ScenePoint(10, 20)), new(new(2), new ScenePoint(10, 20))])]
        };
        var written = AssSubtitleFormat.Write(Document(line, layer));

        Assert.Contains("\\pos(110,220)", written.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("\\move(", written.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(written.Diagnostics, diagnostic => diagnostic.Code is "Subtitle.Composition" or "Ass.MoveApproximation");
    }

    [Fact]
    public void SharedMonotoneCurveCanBecomeALinearMoveWithOneApproximationWarning()
    {
        var line = Line();
        var layer = Layer(line) with
        {
            Tracks = [new(AnimationProperty.POSITION,
                [new(new(0), new ScenePoint(0, 0), KeyframeInterpolation.EASE_IN_OUT), new(new(2), new ScenePoint(80, 40))])]
        };
        var written = AssSubtitleFormat.Write(Document(line, layer));
        var body = Assert.Single(Bodies(written.Text));

        Assert.Contains("\\move(", body, StringComparison.Ordinal);
        AssertPoint(new(100, 200), PositionAt(body, 0, 2000));
        AssertPoint(new(140, 220), PositionAt(body, 1000, 2000));
        AssertPoint(new(180, 240), PositionAt(body, 2000, 2000));
        Assert.Single(written.Diagnostics.Where(diagnostic => diagnostic.SubtitleId == line.Id && diagnostic.Code == "Ass.MoveApproximation"));
        Assert.DoesNotContain(written.Diagnostics, diagnostic => diagnostic.Code == "Subtitle.Composition");
    }

    [Fact]
    public void DifferentComponentCurvesDoNotTurnACurvedMotionIntoADiagonalMove()
    {
        var line = Line();
        var layer = Layer(line) with
        {
            Tracks = [new(AnimationProperty.POSITION,
                [new(new(0), new ScenePoint(0, 0), KeyframeInterpolation.EASE_IN)
                {
                    ComponentCurves = [new(KeyframeInterpolation.EASE_OUT)]
                }, new(new(2), new ScenePoint(80, 40))])]
        };
        var written = AssSubtitleFormat.Write(Document(line, layer));

        Assert.DoesNotContain("\\move(", written.Text, StringComparison.Ordinal);
        Assert.Contains(written.Diagnostics, diagnostic => diagnostic.Code == "Subtitle.Composition");
        Assert.DoesNotContain(written.Diagnostics, diagnostic => diagnostic.Code == "Ass.MoveApproximation");
    }

    [Theory]
    [InlineData(80, 0, 80, 40)]
    [InlineData(80, 40, 40, 20)]
    public void TurningOrReturningTracksAreNotCollapsedToOneMove(double middleX, double middleY, double endX, double endY)
    {
        var line = Line();
        var layer = Layer(line) with
        {
            Tracks = [new(AnimationProperty.POSITION,
                [new(new(0), new ScenePoint(0, 0)), new(new(1), new ScenePoint(middleX, middleY)), new(new(2), new ScenePoint(endX, endY))])]
        };
        var written = AssSubtitleFormat.Write(Document(line, layer));

        Assert.DoesNotContain("\\move(", written.Text, StringComparison.Ordinal);
        Assert.Contains(written.Diagnostics, diagnostic => diagnostic.Code == "Subtitle.Composition");
    }

    [Fact]
    public void MaskSamplingRebasesEveryMoveToItsOwnEventWithoutRestartingTheMotion()
    {
        var line = Line() with { Start = new(10), End = new(12) };
        var mask = new VectorClipMask
        {
            Contours = [new() { Nodes = [new() { Position = new(0, 0) }, new() { Position = new(100, 0) }, new() { Position = new(100, 100) }] }]
        };
        var layer = Layer(line) with
        {
            Mask = mask,
            AnimationOffset = new(1, 2),
            Tracks =
            [
                new(AnimationProperty.POSITION, [new(new(1, 2), new ScenePoint(0, 0)), new(new(5, 2), new ScenePoint(200, 100))]),
                new(AnimationProperty.MASK_POSITION, [new(new(1, 2), new ScenePoint(0, 0)), new(new(5, 2), new ScenePoint(30, 0))])
            ]
        };
        var document = Document(line, layer) with { FrameRate = new(4, 1) };
        var written = AssSubtitleFormat.Write(document, timeOffset: new(2));
        var bodies = Bodies(written.Text);

        Assert.Equal(8, bodies.Length);
        for (var index = 0; index < bodies.Length; index++)
        {
            var body = bodies[index];
            Assert.Single(Regex.Matches(body, @"\\move\("));
            var elapsed = index * 0.25 + 0.125;
            AssertPoint(new(100 + elapsed * 100, 200 + elapsed * 50), PositionAt(body, 125, 250));
        }
        Assert.Contains(written.Diagnostics, diagnostic => diagnostic.Code == "Ass.MaskAnimationExpanded");
        Assert.DoesNotContain(written.Diagnostics, diagnostic => diagnostic.Code == "Subtitle.Composition");
    }

    [Fact]
    public void MovementTimeAndCoordinatePrecisionLossesAreReported()
    {
        var line = Line();
        var layer = Layer(line) with
        {
            Tracks = [new(AnimationProperty.POSITION,
                [new(new(1, 3000), new ScenePoint(1.0 / 3, 0)), new(new(5001, 3000), new ScenePoint(80, 40))])]
        };
        var written = AssSubtitleFormat.Write(Document(line, layer));

        Assert.Contains("\\move(", written.Text, StringComparison.Ordinal);
        Assert.Single(written.Diagnostics.Where(diagnostic => diagnostic.SubtitleId == line.Id && diagnostic.Code == "Ass.MoveTimeQuantization"));
        Assert.Single(written.Diagnostics.Where(diagnostic => diagnostic.SubtitleId == line.Id && diagnostic.Code == "Ass.NumberPrecision"));
    }

    [Fact]
    public void ALinearUnorientedMotionPathAddsItsDisplacementToTheNativePosition()
    {
        var line = Line();
        var layer = Layer(line) with
        {
            Transform = new() { Position = new(10, 20) },
            MotionPath = new(new(new(0, 0), [new(new(20, 10), new(40, 20), new(60, 30))]), new(2))
        };
        var written = AssSubtitleFormat.Write(Document(line, layer));
        var body = Assert.Single(Bodies(written.Text));

        AssertPoint(new(110, 220), PositionAt(body, 0, 2000));
        AssertPoint(new(140, 235), PositionAt(body, 1000, 2000));
        AssertPoint(new(170, 250), PositionAt(body, 2000, 2000));
        Assert.DoesNotContain(written.Diagnostics, diagnostic => diagnostic.Code is "Subtitle.Composition" or "Ass.MoveApproximation");
    }

    private static SubtitleLine Line()
    {
        return new()
        {
            Text = "ab",
            End = new(2),
            Style = new()
            {
                ShadowBlur = 0,
                ShadowColor = SceneColor.Black,
                ShadowOffset = new(2, 3),
                StrokeWidth = 2,
                Alignment = TextAlignment.BOTTOM_CENTER,
                Position = new() { Anchor = new(0, 0), Pivot = new(0.5, 1), Offset = new(100, 200) }
            }
        };
    }

    private static ProjectLayer Layer(SubtitleLine line)
    {
        return new() { Kind = LayerKind.SUBTITLE, SubtitleId = line.Id, Start = line.Start, End = line.End };
    }

    private static ProjectDocument Document(SubtitleLine line, ProjectLayer layer) => new() { Subtitles = [line], Layers = [layer] };

    private static string[] Bodies(string source)
    {
        return source.Split('\n').Where(row => row.StartsWith("Dialogue: ", StringComparison.Ordinal))
            .Select(row => row.Split(',', 10)[9]).ToArray();
    }

    private static ScenePoint PositionAt(string body, double milliseconds, double durationMilliseconds)
    {
        var move = Regex.Match(body, @"\\move\(([^)]+)\)");
        if (!move.Success)
        {
            var position = Regex.Match(body, @"\\pos\(([^)]+)\)");
            Assert.True(position.Success, "导出事件应携带可评价的位置或移动标签。");
            var point = Numbers(position.Groups[1].Value);
            return new(point[0], point[1]);
        }

        var values = Numbers(move.Groups[1].Value);
        Assert.True(values.Length is 4 or 6);
        var start = values.Length == 6 ? values[4] : 0;
        var end = values.Length == 6 ? values[5] : durationMilliseconds;
        var fraction = end.Equals(start) ? milliseconds >= end ? 1 : 0 : Math.Clamp((milliseconds - start) / (end - start), 0, 1);
        return new(values[0] + (values[2] - values[0]) * fraction, values[1] + (values[3] - values[1]) * fraction);
    }

    private static double[] Numbers(string text) => text.Split(',').Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();

    private static void AssertPoint(ScenePoint expected, ScenePoint actual)
    {
        Assert.Equal(expected.X, actual.X, 7);
        Assert.Equal(expected.Y, actual.Y, 7);
    }
}
