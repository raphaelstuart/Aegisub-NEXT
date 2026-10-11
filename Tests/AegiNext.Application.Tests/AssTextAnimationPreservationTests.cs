using System.Collections.Immutable;
using AegiNext.Application.SubtitleFormats;
using AegiNext.Core.Editing;
using AegiNext.Core.Projects;
using AegiNext.Core.Timing;

namespace AegiNext.Application.Tests;

public sealed class AssTextAnimationPreservationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FontEditPreservesUnchangedNativeStrokeKeyframesAndValues(bool submillisecond)
    {
        var font = Linear(AnimationProperty.FONT_SIZE, 20, 40);
        var stroke = Precise(AnimationProperty.STROKE_WIDTH, 2, 8, submillisecond);
        var document = Document([font, stroke]);

        var changed = Edit(document, source => source.Replace(@"\fs40", @"\fs50", StringComparison.Ordinal));

        Assert.Same(stroke, Track(changed, stroke.Target));
        Assert.Equal(35, Value(changed, AnimationProperty.FONT_SIZE, 0, new(1)), 9);
        Assert.Equal(SceneEvaluator.EvaluateScalarTrack(stroke, new(1, 3000)),
            Value(changed, AnimationProperty.STROKE_WIDTH, 0, new(1, 3000)));
        Assert.Equal(SceneEvaluator.EvaluateScalarTrack(stroke, new(1, 3)),
            Value(changed, AnimationProperty.STROKE_WIDTH, 0, new(1, 3)));
    }

    [Fact]
    public void StrokeEditPreservesUnchangedNativeFontCurve()
    {
        var font = Precise(AnimationProperty.FONT_SIZE, 20, 40);
        var stroke = Linear(AnimationProperty.STROKE_WIDTH, 2, 8);
        var document = Document([font, stroke]);

        var changed = Edit(document, source => source.Replace(@"\bord8", @"\bord10", StringComparison.Ordinal));

        Assert.Same(font, Track(changed, font.Target));
        Assert.Equal(6, Value(changed, AnimationProperty.STROKE_WIDTH, 0, new(1)), 9);
        Assert.Equal(SceneEvaluator.EvaluateScalarTrack(font, new(1, 3)),
            Value(changed, AnimationProperty.FONT_SIZE, 0, new(1, 3)));
    }

    [Fact]
    public void ScopedFontEditAndTextInsertionKeepTheOtherScopesIdentityAndNativeCurve()
    {
        var first = new SubtitleAnimationRange(Guid.NewGuid(), 0, 1);
        var second = new SubtitleAnimationRange(Guid.NewGuid(), 1, 1);
        var editedFont = Linear(AnimationProperty.FONT_SIZE, 20, 40, first.Id);
        var untouchedFont = Precise(AnimationProperty.FONT_SIZE, 20, 60) with
        {
            Target = new(AnimationProperty.FONT_SIZE, TextRangeId: second.Id)
        };
        var document = Document([editedFont, untouchedFont], [first, second]);

        var changed = Edit(document, source => source.Replace(@"\t(0,2000,1,\fs40)",
            @"\t(0,2000,1,\fs50)", StringComparison.Ordinal) + "c");

        Assert.Equal("abc", changed.Subtitles[0].Text);
        Assert.Same(untouchedFont, Track(changed, untouchedFont.Target));
        Assert.Equal(second with { Utf16Length = 2 },
            Assert.Single(changed.Subtitles[0].AnimationRanges, range => range.Id == second.Id));
        Assert.Equal(35, Value(changed, AnimationProperty.FONT_SIZE, 0, new(1)), 9);
        var expected = SceneEvaluator.EvaluateScalarTrack(untouchedFont, new(1, 3));
        Assert.Equal(expected, Value(changed, AnimationProperty.FONT_SIZE, 1, new(1, 3)));
        Assert.Equal(expected, Value(changed, AnimationProperty.FONT_SIZE, 2, new(1, 3)));
    }

    [Fact]
    public void SplittingFontAnimationIntoNewScopesKeepsWholeLineStrokeExact()
    {
        var font = Linear(AnimationProperty.FONT_SIZE, 20, 40);
        var stroke = Precise(AnimationProperty.STROKE_WIDTH, 2, 8);
        var document = Document([font, stroke]);

        var changed = Edit(document, source => source.Replace("}ab", @"}a{\t(0,2000,\fs60)}b", StringComparison.Ordinal));

        Assert.Same(stroke, Track(changed, stroke.Target));
        Assert.Single(changed.Layers[0].Tracks, track => track.Property == AnimationProperty.STROKE_WIDTH);
        Assert.Equal(30, Value(changed, AnimationProperty.FONT_SIZE, 0, new(1)), 9);
        Assert.Equal(45, Value(changed, AnimationProperty.FONT_SIZE, 1, new(1)), 9);
        for (var offset = 0; offset < 2; offset++)
        {
            Assert.Equal(SceneEvaluator.EvaluateScalarTrack(stroke, new(1, 3)),
                Value(changed, AnimationProperty.STROKE_WIDTH, offset, new(1, 3)));
        }
    }

    [Fact]
    public void EquivalentNativeStrokeScopesSurviveTheirWholeLineProjection()
    {
        var first = new SubtitleAnimationRange(Guid.NewGuid(), 0, 1);
        var second = new SubtitleAnimationRange(Guid.NewGuid(), 1, 1);
        var firstStroke = Precise(AnimationProperty.STROKE_WIDTH, 2, 8) with
        {
            Target = new(AnimationProperty.STROKE_WIDTH, TextRangeId: first.Id)
        };
        var secondStroke = firstStroke with { Target = firstStroke.Target with { TextRangeId = second.Id } };
        var font = Linear(AnimationProperty.FONT_SIZE, 20, 40);
        var document = Document([font, firstStroke, secondStroke], [first, second]);

        var changed = Edit(document, source => source.Replace(@"\fs40", @"\fs50", StringComparison.Ordinal));

        Assert.Same(firstStroke, Track(changed, firstStroke.Target));
        Assert.Same(secondStroke, Track(changed, secondStroke.Target));
        Assert.Equal(2, changed.Layers[0].Tracks.Count(track => track.Property == AnimationProperty.STROKE_WIDTH));
        Assert.Equal(new[] { first.Id, second.Id }, changed.Subtitles[0].AnimationRanges.Select(range => range.Id));
        for (var offset = 0; offset < 2; offset++)
        {
            Assert.Equal(SceneEvaluator.EvaluateScalarTrack(firstStroke, new(1, 3)),
                Value(changed, AnimationProperty.STROKE_WIDTH, offset, new(1, 3)));
        }
    }

    [Fact]
    public void MergingEditedFontScopesKeepsTheUneditedNativeTarget()
    {
        var first = new SubtitleAnimationRange(Guid.NewGuid(), 0, 1);
        var second = new SubtitleAnimationRange(Guid.NewGuid(), 1, 1);
        var firstFont = Linear(AnimationProperty.FONT_SIZE, 20, 40, first.Id);
        var secondFont = Linear(AnimationProperty.FONT_SIZE, 20, 60, second.Id);
        var document = Document([firstFont, secondFont], [first, second]);

        var changed = Edit(document, source => source.Replace(@"\fs40", @"\fs60", StringComparison.Ordinal));

        Assert.Same(secondFont, Track(changed, secondFont.Target));
        Assert.DoesNotContain(changed.Layers[0].Tracks, track => ReferenceEquals(track, firstFont));
        Assert.Equal(second, Assert.Single(changed.Subtitles[0].AnimationRanges, range => range.Id == second.Id));
        Assert.Equal(40, Value(changed, AnimationProperty.FONT_SIZE, 0, new(1)), 9);
        Assert.Equal(40, Value(changed, AnimationProperty.FONT_SIZE, 1, new(1)), 9);
    }

    [Fact]
    public void NormalStrokeEditKeepsTheOtherVisualStatesAndFontTrack()
    {
        var font = Precise(AnimationProperty.FONT_SIZE, 20, 40);
        var stroke = Linear(AnimationProperty.STROKE_WIDTH, 2, 8);
        var active = Precise(AnimationProperty.STROKE_WIDTH, 3, 9) with
        {
            Target = new(AnimationProperty.STROKE_WIDTH, State: SubtitleAnimationState.ACTIVE)
        };
        var inactive = Precise(AnimationProperty.STROKE_WIDTH, 4, 10) with
        {
            Target = new(AnimationProperty.STROKE_WIDTH, State: SubtitleAnimationState.INACTIVE)
        };
        var document = Document([font, stroke, active, inactive]);
        var line = document.Subtitles[0] with { Karaoke = [new(0, 2, new(1), new(2), SceneColor.White)] };
        document = document with { Subtitles = [line] };

        var changed = Edit(document, source => source.Replace(@"\bord8", @"\bord12", StringComparison.Ordinal));

        Assert.Same(font, Track(changed, font.Target));
        Assert.Same(active, Track(changed, active.Target));
        Assert.Same(inactive, Track(changed, inactive.Target));
        Assert.Equal(7, Value(changed, AnimationProperty.STROKE_WIDTH, 0, new(1)), 9);
    }

    [Fact]
    public void ExplicitEditsToBothPropertiesReplaceBothAnimations()
    {
        var font = Linear(AnimationProperty.FONT_SIZE, 20, 40);
        var stroke = Linear(AnimationProperty.STROKE_WIDTH, 2, 8);
        var document = Document([font, stroke]);

        var changed = Edit(document, source => source.Replace(@"\fs40", @"\fs50", StringComparison.Ordinal)
            .Replace(@"\bord8", @"\bord10", StringComparison.Ordinal));

        Assert.NotSame(font, Track(changed, font.Target));
        Assert.NotSame(stroke, Track(changed, stroke.Target));
        Assert.Equal(35, Value(changed, AnimationProperty.FONT_SIZE, 0, new(1)), 9);
        Assert.Equal(6, Value(changed, AnimationProperty.STROKE_WIDTH, 0, new(1)), 9);
    }

    [Fact]
    public void RemovingFontAnimationKeepsUnchangedStrokeWithoutResurrectingTheDeletedTrack()
    {
        var font = Linear(AnimationProperty.FONT_SIZE, 20, 40);
        var stroke = Precise(AnimationProperty.STROKE_WIDTH, 2, 8);
        var document = Document([font, stroke]);

        var changed = Edit(document, source => source.Replace(@"\t(0,2000,1,\fs40)", string.Empty, StringComparison.Ordinal));

        Assert.DoesNotContain(changed.Layers[0].Tracks, track => track.Property == AnimationProperty.FONT_SIZE);
        Assert.Same(stroke, Track(changed, stroke.Target));
        Assert.Equal(20, Value(changed, AnimationProperty.FONT_SIZE, 0, new(1)), 9);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EditingOrRemovingTheFullTextScopeDoesNotRestoreItsHiddenWholeLineTrack(bool remove)
    {
        var range = new SubtitleAnimationRange(Guid.NewGuid(), 0, 2);
        var hidden = Linear(AnimationProperty.FONT_SIZE, 20, 40);
        var visible = Linear(AnimationProperty.FONT_SIZE, 20, 60, range.Id);
        var document = Document([hidden, visible], [range]);

        var changed = Edit(document, source => source.Replace(@"\t(0,2000,1,\fs60)",
            remove ? string.Empty : @"\t(0,2000,1,\fs70)", StringComparison.Ordinal));

        Assert.DoesNotContain(changed.Layers[0].Tracks, track => ReferenceEquals(track, hidden));
        for (var offset = 0; offset < 2; offset++)
        {
            Assert.Equal(remove ? 20 : 45, Value(changed, AnimationProperty.FONT_SIZE, offset, new(1)), 9);
        }
        if (remove)
        {
            Assert.DoesNotContain(changed.Layers[0].Tracks, track => track.Property == AnimationProperty.FONT_SIZE);
        }
    }

    [Fact]
    public void UnrelatedStrokeEditPreservesBothVisibleAndHiddenNativeFontTracks()
    {
        var range = new SubtitleAnimationRange(Guid.NewGuid(), 0, 2);
        var hidden = Precise(AnimationProperty.FONT_SIZE, 20, 40);
        var visible = Precise(AnimationProperty.FONT_SIZE, 30, 60) with
        {
            Target = new(AnimationProperty.FONT_SIZE, TextRangeId: range.Id)
        };
        var document = Document([hidden, visible, Linear(AnimationProperty.STROKE_WIDTH, 2, 8)], [range]);

        var changed = Edit(document, source => source.Replace(@"\bord8", @"\bord10", StringComparison.Ordinal));

        Assert.Same(hidden, Track(changed, hidden.Target));
        Assert.Same(visible, Track(changed, visible.Target));
        Assert.Equal(SceneEvaluator.EvaluateScalarTrack(visible, new(1, 3)),
            Value(changed, AnimationProperty.FONT_SIZE, 0, new(1, 3)));
        Assert.Equal(6, Value(changed, AnimationProperty.STROKE_WIDTH, 0, new(1)), 9);
    }

    [Fact]
    public void ClearingProjectedTextDoesNotRestoreRemovedTextAnimations()
    {
        var font = Linear(AnimationProperty.FONT_SIZE, 20, 40);
        var stroke = Precise(AnimationProperty.STROKE_WIDTH, 2, 8);
        var opacity = Linear(AnimationProperty.OPACITY, 1, 0.5);
        var document = Document([font, stroke, opacity]);

        var changed = Edit(document, _ => string.Empty);

        Assert.Empty(changed.Subtitles[0].Text);
        Assert.Same(opacity, Assert.Single(changed.Layers[0].Tracks));
    }

    private static AnimationTrack Linear(AnimationProperty property, double initial, double final, Guid? rangeId = null)
    {
        return new(new AnimationTrackTarget(property, TextRangeId: rangeId),
            [new(MediaTime.Zero, initial), new(new(2), final)]);
    }

    private static AnimationTrack Precise(AnimationProperty property, double initial, double final, bool submillisecond = false)
    {
        return new(property,
        [
            new(new(1, 3000), initial, submillisecond ? KeyframeInterpolation.LINEAR : KeyframeInterpolation.EASE_OUT),
            new(submillisecond ? new(2, 3000) : new(4, 3), final)
        ]);
    }

    private static ProjectDocument Document(ImmutableArray<AnimationTrack> tracks,
        ImmutableArray<SubtitleAnimationRange> ranges = default)
    {
        var line = new SubtitleLine
        {
            Text = "ab", End = new(2), AnimationRanges = ranges.IsDefault ? [] : ranges,
            Style = new() { FontSize = 20, StrokeWidth = 2, ShadowBlur = 0 }
        };
        return new()
        {
            Subtitles = [line],
            Layers = [new()
            {
                Id = line.Id, Kind = LayerKind.SUBTITLE, SubtitleId = line.Id, End = line.End, Tracks = tracks
            }]
        };
    }

    private static ProjectDocument Edit(ProjectDocument document, Func<string, string> edit)
    {
        ProjectValidator.Validate(document);
        var line = document.Subtitles[0];
        var layer = document.Layers[0];
        var source = AssTextProjection.Create(line, layer: layer).Source;
        var editedSource = edit(source);
        Assert.NotEqual(source, editedSource);
        var result = AssTextProjection.Apply(line, editedSource, layer: layer);
        return ProjectEditingOperations.ApplyAssTextEdit(document, line.Id, result);
    }

    private static AnimationTrack Track(ProjectDocument document, AnimationTrackTarget target)
    {
        return Assert.Single(document.Layers[0].Tracks, track => track.Target == target);
    }

    private static double Value(ProjectDocument document, AnimationProperty property, int offset, MediaTime time)
    {
        var line = document.Subtitles[0];
        var evaluated = SceneEvaluator.EvaluateLayer(document.Layers[0], line, time);
        var baseStyle = line.InlineSpans.FirstOrDefault(span => span.Utf16Start <= offset && offset < span.Utf16Start + span.Utf16Length)
            ?.Style.ApplyTo(line.Style) ?? line.Style;
        var style = SubtitleAnimationEvaluation.ApplyStyleAnimations(evaluated, baseStyle, offset, SubtitleAnimationState.NORMAL);
        return property == AnimationProperty.FONT_SIZE ? style.FontSize : style.StrokeWidth;
    }
}
