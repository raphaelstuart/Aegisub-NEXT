using AegiNext.Application.SubtitleFormats;
using AegiNext.Core.Editing;
using AegiNext.Core.Projects;
using AegiNext.Core.Timing;

namespace AegiNext.Application.Tests;

public sealed class AssTransformImportTests
{
    [Fact]
    public void FileStyleGeometryAndMoveAreImportedIntoTheSameNativeClip()
    {
        var source = """
            [Script Info]
            ScriptType: v4.00+
            PlayResX: 640
            PlayResY: 360
            [V4+ Styles]
            Format: Name, Fontname, Fontsize, PrimaryColour, ScaleX, ScaleY, Angle, Outline, Shadow, Alignment
            Style: Default,Noto Sans,20,&H00FFFFFF,150,75,30,0,0,7
            [Events]
            Format: Layer, Start, End, Style, Text
            Dialogue: 0,0:00:10.00,0:00:12.00,Default,{\move(10,20,110,220,500,1500)}字幕
            """;
        var parsed = AssSubtitleFormat.Parse(source, 640, 360);
        var document = ProjectEditingOperations.ImportSubtitleLines(new() { Width = 640, Height = 360 }, parsed, "ASS");
        var clip = Assert.Single(document.Layers);

        Assert.Equal(new ScenePoint(1.5, 0.75), clip.Transform.Scale);
        Assert.Equal(-30, clip.Transform.Rotation);
        Assert.Equal(new ScenePoint(50, 100), Assert.Single(SceneEvaluator.Evaluate(document, new(11, 1))).Transform.Position);
        Assert.Equal(new ScenePoint(10, 20), Assert.Single(document.Subtitles).Style.Position!.Offset);
        Assert.DoesNotContain(parsed.Diagnostics, item => item.Code is "Ass.StyleGeometry" or "Ass.UnsupportedTag");
    }

    [Fact]
    public void DeclaredScaleAndAngleBecomeEditableNativeTransform()
    {
        var result = Parse("字幕", new(1.5, 0.75), 30);

        Assert.Equal(new ScenePoint(1.5, 0.75), result.Transform.Scale);
        Assert.Equal(-30, result.Transform.Rotation);
        Assert.Contains(result.Diagnostics, item => item.Code == "Ass.TransformLayout");
        Assert.DoesNotContain(result.Diagnostics, item => item.Code == "Ass.UnsupportedTag");
    }

    [Fact]
    public void UniformOverridesSurviveResetAndTrailingGeometryDoesNotAffectVisibleText()
    {
        var result = Parse("{\\fscx150\\fscy75\\frz30}a{\\rOther}b{\\r\\fscx999\\frz50}",
            new(1, 1), 0, new("Other", Style(), SceneColor.White) { Scale = new(1.5, 0.75), Rotation = 30 });

        Assert.Equal(new ScenePoint(1.5, 0.75), result.Transform.Scale);
        Assert.Equal(-30, result.Transform.Rotation);
        Assert.DoesNotContain(result.Diagnostics, item => item.Code == "Ass.InlineTransform");
    }

    [Fact]
    public void MixedInlineScaleKeepsTheVaryingAxisInTextRanges()
    {
        var result = Parse("{\\fscx150\\fscy75\\fr30}a{\\fscx200}b");

        Assert.Equal(new ScenePoint(1, 0.75), result.Transform.Scale);
        Assert.Equal(-30, result.Transform.Rotation);
        Assert.Equal(2, result.Line.AnimationRanges.Length);
        Assert.Equal(new ScenePoint(1.5, 1), result.Line.AnimationRanges[0].Scale);
        Assert.Equal(new ScenePoint(2, 1), result.Line.AnimationRanges[1].Scale);
        Assert.All(result.Line.AnimationRanges, range => Assert.Equal(0, range.Rotation));
        Assert.DoesNotContain(result.Diagnostics, item => item.Code == "Ass.InlineTransform");
    }

    [Theory]
    [InlineData("-10")]
    [InlineData("1000001")]
    public void UnsupportedScaleDropsOnlyItsOwnAxis(string scale)
    {
        var result = Parse("{\\fscx" + scale + "\\fscy125\\frz45}a");

        Assert.Equal(new ScenePoint(scale == "-10" ? 0 : 1, 1.25), result.Transform.Scale);
        Assert.Equal(-45, result.Transform.Rotation);
        if (scale == "-10")
        {
            Assert.DoesNotContain(result.Diagnostics, item => item.Code == "Ass.UnsupportedTransform");
        }
        else
        {
            Assert.Contains(result.Diagnostics, item => item.Code == "Ass.UnsupportedTransform");
        }
    }

    [Fact]
    public void EmptyOverridesUseTheCurrentlyResetStylesGeometry()
    {
        var result = Parse("{\\rOther\\fscx200\\fscx\\frz90\\frz}a", new(1, 1), 0,
            new("Other", Style(), SceneColor.White) { Scale = new(1.25, 0.5), Rotation = 20 });

        Assert.Equal(new ScenePoint(1.25, 0.5), result.Transform.Scale);
        Assert.Equal(-20, result.Transform.Rotation);
    }

    [Fact]
    public void MoveUsesLineRelativeTimingAndStationaryEnds()
    {
        var result = Parse("{\\move(10,20,110,220,500,1500)\\an7}a");
        var position = Assert.IsType<SubtitlePosition>(result.Line.Style.Position);
        var track = Assert.Single(result.PlacementTracks);

        Assert.Equal(new ScenePoint(0, 0), position.Anchor);
        Assert.Equal(new ScenePoint(0, 0), position.Pivot);
        Assert.Equal(new ScenePoint(10, 20), position.Offset);
        Assert.Equal(AnimationProperty.POSITION, track.Property);
        Assert.Equal(new ScenePoint(0, 0), SceneEvaluator.EvaluateVectorTrack(track, MediaTime.Zero));
        Assert.Equal(new ScenePoint(50, 100), SceneEvaluator.EvaluateVectorTrack(track, new(1, 1)));
        Assert.Equal(new ScenePoint(100, 200), SceneEvaluator.EvaluateVectorTrack(track, new(2, 1)));
    }

    [Theory]
    [InlineData("\\move(10,20,110,220)")]
    [InlineData("\\move(10,20,110,220,0,0)")]
    [InlineData("\\move(10,20,110,220,-3000,-1000)")]
    [InlineData("\\move(10,20,110,220,-1000,0)")]
    [InlineData("\\move(10,20,110,220,-0.9,0.9)")]
    public void ImplicitMoveTimesUseTheWholeDialogue(string tag)
    {
        var track = Assert.Single(Parse("{" + tag + "}a").PlacementTracks);

        Assert.Equal(MediaTime.Zero, track.Keyframes[0].Time);
        Assert.Equal(new MediaTime(2, 1), track.Keyframes[1].Time);
        Assert.Equal(new ScenePoint(50, 100), SceneEvaluator.EvaluateVectorTrack(track, new(1, 1)));
    }

    [Theory]
    [InlineData("\\pos(5,6)\\move(10,20,110,220)", 5, 6, false)]
    [InlineData("\\move(10,20,110,220)\\pos(5,6)", 10, 20, true)]
    [InlineData("\\move(10,20,110,220)\\move(5,6,15,16)", 10, 20, true)]
    public void PositionAndMoveShareFirstPlacementWins(string tags, double x, double y, bool hasMove)
    {
        var result = Parse("{" + tags + "}a");

        Assert.Equal(new ScenePoint(x, y), result.Line.Style.Position!.Offset);
        Assert.Equal(hasMove, !result.PlacementTracks.IsEmpty);
        Assert.Single(result.Diagnostics.Where(item => item.Code == "Ass.DuplicatePlacement"));
    }

    [Fact]
    public void NegativeAndBeyondEndMoveTimesRetainTheTrueVisiblePhase()
    {
        var result = Parse("{\\move(10,20,110,220,-1000,3000)}a");
        var track = Assert.Single(result.PlacementTracks);

        Assert.Equal(MediaTime.Zero, track.Keyframes[0].Time);
        Assert.Equal(new MediaTime(2, 1), track.Keyframes[1].Time);
        Assert.Equal(new ScenePoint(25, 50), SceneEvaluator.EvaluateVectorTrack(track, MediaTime.Zero));
        Assert.Equal(new ScenePoint(75, 150), SceneEvaluator.EvaluateVectorTrack(track, new(2, 1)));
    }

    [Theory]
    [InlineData(-1000, 3000, 25, 50, 75, false)]
    [InlineData(-3000, -1000, 0, 50, 100, false)]
    [InlineData(3000, 5000, 0, 0, 0, false)]
    [InlineData(-1000, 1000, 50, 100, 100, false)]
    [InlineData(1000, 3000, 0, 0, 50, false)]
    [InlineData(-1000, 3000, 25, 50, 75, true)]
    public void ImportedMoveClipsToNativeRangeWithoutChangingVisiblePosition(int start, int end,
        double atStart, double atMiddle, double atEnd, bool negativeKaraoke)
    {
        var karaoke = negativeKaraoke ? "\\kt-50\\k100" : string.Empty;
        var parsed = AssSubtitleFormat.Parse(File($"{{\\move(10,20,110,220,{start},{end}){karaoke}}}字幕"), 640, 360);
        var document = ProjectEditingOperations.ImportSubtitleLines(new() { Width = 640, Height = 360 }, parsed, "ASS");
        ProjectValidator.Validate(document);
        var clip = Assert.Single(document.Layers);
        var track = Assert.Single(clip.Tracks);
        var (minimum, maximum) = LayerAnimationTiming.GetRange(clip);

        Assert.Equal(negativeKaraoke ? new MediaTime(1, 2) : MediaTime.Zero, clip.AnimationOffset);
        Assert.All(track.Keyframes, key => Assert.True(key.Time >= minimum && key.Time <= maximum));
        Assert.Equal(new ScenePoint(10, 20), document.Subtitles[0].Style.Position!.Offset);
        AssertPoint(new(atStart, atStart * 2), Assert.Single(SceneEvaluator.Evaluate(document, clip.Start)).Transform.Position);
        AssertPoint(new(atMiddle, atMiddle * 2), Assert.Single(SceneEvaluator.Evaluate(document, clip.Start + new MediaTime(1, 1))).Transform.Position);
        AssertPoint(new(atEnd, atEnd * 2), SceneEvaluator.EvaluateVectorTrack(track, maximum));
    }

    [Fact]
    public void NonuniformCanvasResamplingWithRotationReportsItsGeometryApproximation()
    {
        var parsed = AssSubtitleFormat.Parse(File("{\\frz30}字幕"), 1280, 360);

        Assert.Equal(-30, Assert.Single(parsed.Clips).Transform.Rotation);
        Assert.Contains(parsed.Diagnostics, item => item.Code == "Ass.TransformLayout");
    }

    [Theory]
    [InlineData("500,500")]
    [InlineData("1500,500")]
    public void UnrepresentableMoveTimesAreDiagnosedWithoutLosingTheText(string times)
    {
        var result = Parse("{\\move(10,20,110,220," + times + ")}字幕");

        Assert.Equal("字幕", result.Line.Text);
        Assert.Empty(result.PlacementTracks);
        Assert.Contains(result.Diagnostics, item => item.Code == "Ass.MoveTiming");
    }

    [Fact]
    public void NegativeKaraokeRebasesMoveAndMaskIntoTheSameContentClock()
    {
        var result = Parse("{\\move(10,20,110,220,0,1000)\\clip(0,0,100,100)\\t(0,1000,\\clip(100,0,200,100))\\kt-50\\k100}a");

        Assert.Equal(new MediaTime(1, 2), result.ContentOffset);
        Assert.Equal(new MediaTime(1, 2), Assert.Single(result.PlacementTracks).Keyframes[0].Time);
        Assert.All(result.MaskTracks, track => Assert.Equal(new MediaTime(1, 2), track.Keyframes[0].Time));
        Assert.Equal(new ScenePoint(50, 100), SceneEvaluator.EvaluateVectorTrack(result.PlacementTracks[0], new(1, 1)));
        Assert.Equal(MediaTime.Zero, result.Line.Karaoke[0].Start);
    }

    [Fact]
    public void ScaleAndRotationCompensateNormalInlineAndKaraokeAppearance()
    {
        var result = Parse("{\\fscx200\\fscy200\\frz90}a{\\bord6\\xshad8\\yshad4\\kt100\\k100\\t(1000,1000,\\bord10\\xshad12\\yshad6)}b");

        Assert.Equal(1, result.Line.Style.StrokeWidth);
        AssertPoint(new(-1, 2), result.Line.Style.ShadowOffset);
        var local = result.Line.InlineSpans[^1].Style;
        Assert.Equal(3, local.StrokeWidth);
        AssertPoint(new(-2, 4), local.ShadowOffset!.Value);
        var active = result.Line.KaraokeStyleSpans[0].ActiveStyle!;
        Assert.Equal(5, active.StrokeWidth);
        AssertPoint(new(-3, 6), active.ShadowOffset!.Value);
    }

    [Fact]
    public void NonuniformScaleReportsApproximateStrokeConversion()
    {
        var result = Parse("{\\fscx400\\fscy100}a");

        Assert.Equal(1, result.Line.Style.StrokeWidth);
        Assert.Contains(result.Diagnostics, item => item.Code == "Ass.TransformAppearance");
    }

    [Fact]
    public void TinyScaleCannotMakeAppearanceCompensationInvalidateTheDocument()
    {
        var result = Parse("{\\fscx0.000001\\fscy0.000001}a");

        Assert.Equal(2, result.Line.Style.StrokeWidth);
        Assert.Contains(result.Diagnostics, item => item.Code == "Ass.TransformAppearance");
        ProjectValidator.ValidateSubtitleStyle(result.Line.Style);
    }

    [Fact]
    public void ProjectSourceKeepsGeometryOutsideTheAssTextEditor()
    {
        var original = new SubtitleLine { Text = "a", Style = Style() };
        var result = new AssTextParser(original, new Dictionary<string, AssStyleDefinition>(), SceneColor.White,
            projectSource: true).Parse("{\\pos(1,2)\\move(0,0,10,20)\\fscx200\\frz45}a");

        Assert.Equal(new LayerTransform(), result.Transform);
        Assert.Empty(result.PlacementTracks);
        Assert.Null(result.Line.Style.Position);
        Assert.Contains(result.Diagnostics, item => item.Code == "Ass.ProjectPositionUnsupported");
        Assert.Contains(result.Diagnostics, item => item.Code == "Ass.UnsupportedTag");
    }

    private static AssTextEditResult Parse(string text, ScenePoint? scale = null, double rotation = 0,
        AssStyleDefinition? additional = null)
    {
        var original = new SubtitleLine { Start = new(10, 1), End = new(12, 1), StyleName = "Default", Style = Style() };
        var styles = new Dictionary<string, AssStyleDefinition>
        {
            ["Default"] = new("Default", original.Style, SceneColor.White) { Scale = scale ?? new(1, 1), Rotation = rotation }
        };
        if (additional is not null)
        {
            styles.Add(additional.Name, additional);
        }
        return new AssTextParser(original, styles, SceneColor.White).Parse(text);
    }

    private static SubtitleStyle Style() => new() { ShadowOffset = new(4, 2), StrokeWidth = 2, ShadowBlur = 0 };

    private static string File(string body) => """
        [Script Info]
        ScriptType: v4.00+
        PlayResX: 640
        PlayResY: 360
        [V4+ Styles]
        Format: Name, Fontname, Fontsize, PrimaryColour, Outline, Shadow, Alignment
        Style: Default,Noto Sans,20,&H00FFFFFF,0,0,7
        [Events]
        Format: Layer, Start, End, Style, Text
        Dialogue: 0,0:00:10.00,0:00:12.00,Default,
        """ + body;

    private static void AssertPoint(ScenePoint expected, ScenePoint actual)
    {
        Assert.Equal(expected.X, actual.X, 8);
        Assert.Equal(expected.Y, actual.Y, 8);
    }
}
