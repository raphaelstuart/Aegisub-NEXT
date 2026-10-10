using System.Collections.Immutable;
using AegiNext.Core.Editing;
using AegiNext.Core.Effects;
using AegiNext.Core.Projects;
using AegiNext.Core.Timing;

namespace AegiNext.Core.Tests.Effects;

public sealed class EffectScriptV2CompilerTests
{
    [Fact]
    public void LetterPulseUsesExactDelayStaggerAndLocalNeutralScale()
    {
        var line = new SubtitleLine { Text = "甲乙", End = new(2) };
        var layer = Layer(line) with { AnimationOffset = new(-1, 7), Transform = new() { Scale = new(3, 4) } };
        var script = Pulse("grapheme", "delay 100ms\nstagger 60ms");

        var result = EffectScriptCompiler.CompileTarget(script, layer, subtitle: line);

        Assert.Equal(2, result.Subtitle!.AnimationRanges.Length);
        for (var index = 0; index < 2; index++)
        {
            var range = result.Subtitle.AnimationRanges[index];
            var track = Assert.Single(result.Tracks.Where(track => track.Target.TextRangeId == range.Id));
            var start = new MediaTime(-1, 7) + new MediaTime(1, 10) + new MediaTime(index * 3, 50);
            Assert.Equal(new ScenePoint(1, 1), range.Scale);
            Assert.Equal(new ScenePoint(1.25, 1.25), SceneEvaluator.EvaluateVectorTrack(track, start + new MediaTime(3, 20)));
            Assert.Equal(new ScenePoint(1, 1), SceneEvaluator.EvaluateVectorTrack(track, start + new MediaTime(3, 10)));
            Assert.Equal(new MediaTime(13, 7), track.Keyframes[^1].Time);
        }
        Validate(result);
    }

    [Fact]
    public void CycleOnlyShortClipReservesOneFullPeriodForEveryLetter()
    {
        var line = new SubtitleLine { Text = "甲乙", End = new(1, 10) };
        var script = EffectScriptParser.Parse("""
            effect "cycle" version 2
            short-clip compress
            scope letters current
                unit grapheme
                stagger 200ms
                segment wave flex 1 cycle 300ms pingpong
                    at 0 scale base
                    at 1 scale factor(1.25,1.25)
                end
            end
            """);

        var result = EffectScriptCompiler.CompileTarget(script, Layer(line), subtitle: line);

        var first = result.Tracks.Single(track => track.Target.TextRangeId == result.Subtitle!.AnimationRanges[0].Id);
        var second = result.Tracks.Single(track => track.Target.TextRangeId == result.Subtitle!.AnimationRanges[1].Id);
        Assert.Equal(new ScenePoint(1.25, 1.25), SceneEvaluator.EvaluateVectorTrack(first, new(3, 100)));
        Assert.Equal(new ScenePoint(1.25, 1.25), SceneEvaluator.EvaluateVectorTrack(second, new(7, 100)));
        Assert.Equal(new ScenePoint(1, 1), SceneEvaluator.EvaluateVectorTrack(second, new(1, 10)));
        Validate(result);
    }

    [Fact]
    public void FlexCycleHoldsTheCompletedPeriodDuringRemainder()
    {
        var line = new SubtitleLine { Text = "甲", End = new(1) };
        var script = EffectScriptParser.Parse("""
            effect "cycles" version 2
            short-clip compress
            scope letters current
                unit grapheme
                segment wave flex 1 cycle 300ms pingpong
                    at 0 scale base
                    at 1 scale factor(2,2)
                end
            end
            """);

        var result = EffectScriptCompiler.CompileTarget(script, Layer(line), subtitle: line);
        var track = Assert.Single(result.Tracks);

        Assert.Equal(new ScenePoint(2, 2), SceneEvaluator.EvaluateVectorTrack(track, new(3, 4)));
        Assert.Equal(new ScenePoint(1, 1), SceneEvaluator.EvaluateVectorTrack(track, new(19, 20)));
        Assert.Contains(track.Keyframes, frame => frame.Time == new MediaTime(9, 10) && frame.Interpolation == KeyframeInterpolation.HOLD);
        Validate(result);
    }

    [Fact]
    public void PingPongPowerUsesThePreviousForwardKeysCurveInReverse()
    {
        var line = new SubtitleLine { Text = "甲", End = new(3) };
        var script = EffectScriptParser.Parse("""
            effect "power" version 2
            short-clip compress
            scope whole current
                segment wave fixed 1s pingpong
                    at 0 rotation 0 power(2)
                    at 1 rotation 100
                end
                segment rest flex 1
                end
            end
            """);

        var result = EffectScriptCompiler.CompileTarget(script, Layer(line), subtitle: line);
        var track = Assert.Single(result.Tracks);

        Assert.Equal(25, SceneEvaluator.EvaluateScalarTrack(track, new(1, 2)));
        Assert.Equal(25, SceneEvaluator.EvaluateScalarTrack(track, new(3, 2)));
        Assert.True(track.Keyframes.Single(frame => frame.Time == new MediaTime(1)).Reverse);
        Assert.Equal(2, track.Keyframes.Single(frame => frame.Time == new MediaTime(1)).Exponent);
        Validate(result);
    }

    [Theory]
    [InlineData("fixed 0.000001ms repeat 2147483647")]
    [InlineData("flex 1 cycle 0.000001ms")]
    public void HugeExpansionIsRejectedBeforeGeneratingKeys(string timing)
    {
        var line = new SubtitleLine { Text = "甲", End = new(1000) };
        var script = EffectScriptParser.Parse($"""
            effect "budget" version 2
            short-clip compress
            scope whole current
                segment wave {timing}
                    at 0 rotation 0
                    at 1 rotation 0
                end
                segment rest flex 1
                end
            end
            """);

        var error = Assert.Throws<EffectScriptException>(() => EffectScriptCompiler.CompileTarget(script, Layer(line), subtitle: line));

        Assert.Contains("预算", error.Message, StringComparison.Ordinal);
        Assert.True(error.Line > 0);
    }

    [Fact]
    public void ReapplicationReusesIdsAndResetsEveryOwnedPropertyAndStaticTransform()
    {
        var line = new SubtitleLine { Text = "甲乙", End = new(2) };
        var first = EffectScriptComposer.ComposeTarget(Pulse("grapheme"), Layer(line), subtitle: line);
        var oldRanges = first.Subtitle!.AnimationRanges;
        var changed = oldRanges[0] with { Scale = new(4, 5), Offset = new(20, 30), Rotation = 15, Pivot = SubtitleAnimationPivot.SUBTITLE_ANCHOR };
        var manual = new SubtitleAnimationRange(Guid.NewGuid(), 0, 2);
        var other = new SubtitleAnimationRange(Guid.NewGuid(), 0, 1) { GeneratedOrigin = new("other", "letters", null, "other-unit") };
        var extra = new AnimationTrack(new AnimationTrackTarget(AnimationProperty.FILL, TextRangeId: changed.Id, State: SubtitleAnimationState.ACTIVE), [new(new(0), SceneColor.Black)]);
        var unrelated = new AnimationTrack(new AnimationTrackTarget(AnimationProperty.ROTATION, TextRangeId: manual.Id), [new(new(0), 10)]);
        var inputLine = first.Subtitle with { AnimationRanges = [changed, manual, oldRanges[1], other] };
        var inputLayer = first.PreparedLayer! with { Tracks = first.Tracks.Add(extra).Add(unrelated) };

        var result = EffectScriptComposer.ComposeTarget(Pulse("grapheme"), inputLayer, subtitle: inputLine);

        Assert.Equal(inputLine.AnimationRanges.Select(range => range.Id), result.Subtitle!.AnimationRanges.Select(range => range.Id));
        var reset = result.Subtitle.AnimationRanges[0];
        Assert.Equal(new ScenePoint(1, 1), reset.Scale);
        Assert.Equal(default, reset.Offset);
        Assert.Equal(0, reset.Rotation);
        Assert.Equal(SubtitleAnimationPivot.RANGE_CENTER, reset.Pivot);
        Assert.DoesNotContain(result.Tracks, track => track.Target == extra.Target);
        Assert.Same(unrelated, result.Tracks.Single(track => track.Target == unrelated.Target));
        Assert.Same(manual, result.Subtitle.AnimationRanges[1]);
        Assert.Same(other, result.Subtitle.AnimationRanges[3]);
        Validate(result);
    }

    [Fact]
    public void ChangedGroupingRemovesStaleOwnedRangesAndTheirDescendants()
    {
        var line = new SubtitleLine { Text = "甲乙丙", End = new(2) };
        var first = EffectScriptComposer.ComposeTarget(Pulse("chunk(2)"), Layer(line), subtitle: line);
        var parent = first.Subtitle!.AnimationRanges[0];
        var child = new SubtitleAnimationRange(Guid.NewGuid(), 0, 1) { GeneratedOrigin = new("child", "letters", parent.Id, "child-unit") };
        var childTrack = new AnimationTrack(new AnimationTrackTarget(AnimationProperty.ROTATION, TextRangeId: child.Id), [new(new(0), 20)]);
        var oldLine = first.Subtitle with { AnimationRanges = first.Subtitle.AnimationRanges.Add(child) };
        var oldLayer = first.PreparedLayer! with { Tracks = first.Tracks.Add(childTrack) };

        var result = EffectScriptComposer.ComposeTarget(Pulse("chunk(3)"), oldLayer, subtitle: oldLine);

        var range = Assert.Single(result.Subtitle!.AnimationRanges);
        Assert.Equal(3, range.Utf16Length);
        Assert.NotEqual(parent.Id, range.Id);
        Assert.DoesNotContain(result.Tracks, track => track.Target.TextRangeId == child.Id);
        Validate(result);
    }

    [Fact]
    public void ReusedParentsKeepValidOtherBlockDescendants()
    {
        var line = new SubtitleLine { Text = "甲乙", End = new(2) };
        var first = EffectScriptComposer.ComposeTarget(Pulse("grapheme"), Layer(line), subtitle: line);
        var parent = first.Subtitle!.AnimationRanges[0];
        var child = new SubtitleAnimationRange(Guid.NewGuid(), 0, 1) { GeneratedOrigin = new("child", "letters", parent.Id, "child-unit") };
        var oldLine = first.Subtitle with { AnimationRanges = first.Subtitle.AnimationRanges.Add(child) };

        var result = EffectScriptComposer.ComposeTarget(Pulse("grapheme"), first.PreparedLayer! with { Tracks = first.Tracks }, subtitle: oldLine);

        Assert.Same(child, result.Subtitle!.AnimationRanges[^1]);
        Validate(result);
    }

    [Fact]
    public void OverlappingScopesWithTheSameFullTargetAreRejected()
    {
        var line = new SubtitleLine { Text = "甲", End = new(2) };
        var script = EffectScriptParser.Parse("""
            effect "conflict" version 2
            short-clip compress
            scope first current
                segment stay flex 1
                    at 0 scale base
                    at 1 scale base
                end
            end
            scope second subtitle
                segment stay flex 1
                    at 0 scale base
                    at 1 scale base
                end
            end
            """);

        var error = Assert.Throws<EffectScriptException>(() => EffectScriptCompiler.CompileTarget(script, Layer(line), subtitle: line));

        Assert.Contains("重叠", error.Message, StringComparison.Ordinal);
        Assert.True(error.Line > 0);
    }

    [Fact]
    public void WholeLayerGroupAnimatesEmptyTextAndCompileReturnsOnlyNewTargets()
    {
        var line = new SubtitleLine { End = new(2) };
        var old = new AnimationTrack(AnimationProperty.OPACITY, [new(new(0), 0.5)]);
        var layer = Layer(line) with { Tracks = [old] };

        var compiled = EffectScriptCompiler.CompileTarget(Pulse("group"), layer, subtitle: line);
        var composed = EffectScriptComposer.ComposeTarget(Pulse("group"), layer, subtitle: line);

        Assert.Empty(compiled.Subtitle!.AnimationRanges);
        Assert.Equal(AnimationProperty.SCALE, Assert.Single(compiled.Tracks).Property);
        Assert.Same(old, Assert.Single(compiled.PreparedLayer!.Tracks));
        Assert.Same(old, composed.Tracks.Single(track => track.Property == AnimationProperty.OPACITY));
        Validate(composed);
    }

    [Fact]
    public void GeneratedRangeBudgetIncludesExistingManualRanges()
    {
        var line = new SubtitleLine
        {
            Text = "甲乙",
            End = new(2),
            AnimationRanges = Enumerable.Range(0, 255).Select(_ => new SubtitleAnimationRange(Guid.NewGuid(), 0, 1)).ToImmutableArray()
        };

        var error = Assert.Throws<EffectScriptException>(() => EffectScriptCompiler.CompileTarget(Pulse("grapheme"), Layer(line), subtitle: line));

        Assert.Contains("预算", error.Message, StringComparison.Ordinal);
        Assert.Equal(255, line.AnimationRanges.Length);
    }

    [Fact]
    public void GroupedStyleBaseRejectsMixedValuesWhileLocalScaleRemainsNeutral()
    {
        var line = new SubtitleLine { Text = "甲乙", End = new(2), InlineSpans = [new(0, 1, new() { FontSize = 40 })] };
        var script = EffectScriptParser.Parse("""
            effect "mixed" version 2
            short-clip compress
            scope words current
                unit chunk(2)
                segment stay flex 1
                    at 0 font-size base
                    at 1 font-size base
                end
            end
            """);

        Assert.Throws<EffectScriptException>(() => EffectScriptCompiler.CompileTarget(script, Layer(line), subtitle: line));
        var local = EffectScriptCompiler.CompileTarget(Pulse("chunk(2)"), Layer(line), subtitle: line);
        Assert.Equal(new ScenePoint(1, 1), Assert.Single(local.Tracks).Keyframes[0].Value.Vector);
    }

    [Fact]
    public void ZeroDurationStillRejectsWholeLayerScope()
    {
        var line = new SubtitleLine { Text = "", End = MediaTime.Zero };

        Assert.Throws<EffectScriptException>(() => EffectScriptCompiler.CompileTarget(Pulse("group"), Layer(line), subtitle: line));
    }

    [Theory]
    [InlineData("rotation", "0", AnimationProperty.ROTATION, 9998)]
    [InlineData("scale", "(1,1)", AnimationProperty.SCALE, 19998)]
    public void RepeatBudgetUsesTheActualPropertyDimension(string property, string value, AnimationProperty animationProperty, int repeats)
    {
        var line = new SubtitleLine { Text = "甲", End = new(1) };
        var script = EffectScriptParser.Parse($"""
            effect "dimension-budget" version 2
            short-clip compress
            scope whole current
                segment wave fixed 0.000001ms repeat {repeats}
                    at 0 {property} {value}
                    at 1 {property} {value}
                end
                segment rest flex 1
                end
            end
            """);

        var result = EffectScriptCompiler.CompileTarget(script, Layer(line), subtitle: line);
        var track = Assert.Single(result.Tracks);

        Assert.Equal(AnimationPropertyMetadata.GetMaximumTrackEntries(animationProperty), track.Keyframes.Length);
        var tooMany = script with
        {
            Scopes = [script.Scopes[0] with
            {
                Segments = script.Scopes[0].Segments.SetItem(0, script.Scopes[0].Segments[0] with { RepeatCount = repeats + 1 })
            }]
        };
        Assert.Throws<EffectScriptException>(() => EffectScriptCompiler.CompileTarget(tooMany, Layer(line), subtitle: line));
    }

    [Fact]
    public void DisjointScopesPreserveTheExistingReversedCurveBetweenTheirDeclarations()
    {
        var line = new SubtitleLine { Text = "甲", End = new(5) };
        var original = new AnimationTrack(AnimationProperty.ROTATION,
        [
            new(new(0), 100, KeyframeInterpolation.POWER) { Exponent = 2.5, CurveStart = 0.1, CurveEnd = 0.9, Reverse = true },
            new(new(5), 0)
        ]);
        var layer = Layer(line) with { Tracks = [original] };
        var firstStart = SceneEvaluator.EvaluateScalarTrack(original, new(1));
        var firstEnd = SceneEvaluator.EvaluateScalarTrack(original, new(2));
        var secondStart = SceneEvaluator.EvaluateScalarTrack(original, new(3));
        var secondEnd = SceneEvaluator.EvaluateScalarTrack(original, new(4));
        var first = new EffectScriptScope("first", new(EffectScriptTargetKind.CURRENT),
        [
            new("before", new(1), 0, []),
            new("pulse", new(1), 0,
            [
                new(0, EffectScriptProperty.ROTATION, new(EffectScriptValueKind.ABSOLUTE, firstStart)),
                new(0.5m, EffectScriptProperty.ROTATION, new(EffectScriptValueKind.ABSOLUTE, 500)),
                new(1, EffectScriptProperty.ROTATION, new(EffectScriptValueKind.ABSOLUTE, firstEnd))
            ]),
            new("rest", null, 1, [])
        ]);
        var second = new EffectScriptScope("second", new(EffectScriptTargetKind.CURRENT),
        [
            new("before", new(3), 0, []),
            new("pulse", new(1), 0,
            [
                new(0, EffectScriptProperty.ROTATION, new(EffectScriptValueKind.ABSOLUTE, secondStart)),
                new(0.5m, EffectScriptProperty.ROTATION, new(EffectScriptValueKind.ABSOLUTE, -500)),
                new(1, EffectScriptProperty.ROTATION, new(EffectScriptValueKind.ABSOLUTE, secondEnd))
            ]),
            new("rest", null, 1, [])
        ]);
        var script = new EffectScript("two-overlays", EffectScriptShortClipPolicy.COMPRESS, []) { Version = 2, Scopes = [first, second] };

        var result = EffectScriptComposer.ComposeTarget(script, layer, subtitle: line);
        var actual = Assert.Single(result.Tracks);

        Assert.Equal(500, SceneEvaluator.EvaluateScalarTrack(actual, new(3, 2)));
        Assert.Equal(-500, SceneEvaluator.EvaluateScalarTrack(actual, new(7, 2)));
        for (var sample = 0; sample <= 100; sample++)
        {
            var time = new MediaTime(sample, 20);
            if (time <= new MediaTime(1) || time >= new MediaTime(2) && time <= new MediaTime(3) || time >= new MediaTime(4))
            {
                Assert.Equal(SceneEvaluator.EvaluateScalarTrack(original, time), SceneEvaluator.EvaluateScalarTrack(actual, time), 10);
            }
        }
        Assert.True(actual.Keyframes.Single(frame => frame.Time == new MediaTime(2)).Reverse);
        Validate(result);
    }

    [Fact]
    public void EmptyGroupedScopeStillRejectsAnUnsupportedRangeProperty()
    {
        var line = new SubtitleLine { Text = "", End = new(1) };
        var script = EffectScriptParser.Parse("""
            effect "invalid-empty-range" version 2
            short-clip compress
            scope letters current
                unit grapheme
                segment stay flex 1
                    at 0 opacity base
                    at 1 opacity base
                end
            end
            """);

        Assert.Throws<EffectScriptException>(() => EffectScriptCompiler.CompileTarget(script, Layer(line), subtitle: line));
    }

    [Fact]
    public void CurrentGroupPreservesTheGenericLayerContract()
    {
        var layer = new ProjectLayer { Kind = LayerKind.SHAPE, Shape = new(ShapeKind.RECTANGLE, 10, 10), End = new(2), Transform = new() { Scale = new(3, 4) } };

        var result = EffectScriptCompiler.CompileTarget(Pulse("group"), layer);

        Assert.Null(result.Subtitle);
        Assert.Equal(new ScenePoint(3.75, 5), SceneEvaluator.EvaluateVectorTrack(Assert.Single(result.Tracks), new(3, 20)));
        ProjectValidator.Validate(new() { Layers = [layer with { Tracks = result.Tracks }] });
    }

    [Fact]
    public void ForwardCycleRequiresClosedEndpointsEvenWhenOnlyOneCycleFits()
    {
        var line = new SubtitleLine { Text = "甲", End = new(1, 10) };
        var script = EffectScriptParser.Parse("""
            effect "unclosed-cycle" version 2
            short-clip compress
            scope whole current
                segment wave flex 1 cycle 300ms
                    at 0 rotation 0
                    at 1 rotation 100
                end
            end
            """);

        var error = Assert.Throws<EffectScriptException>(() => EffectScriptCompiler.CompileTarget(script, Layer(line), subtitle: line));

        Assert.Contains("闭合", error.Message, StringComparison.Ordinal);
        Assert.Equal(6, error.Line);
    }

    [Fact]
    public void ForwardClosureComparesResolvedValuesAndAllowsAnOrdinarySingleFixedEntrance()
    {
        var line = new SubtitleLine { Text = "甲", End = new(1) };
        var closed = EffectScriptParser.Parse("""
            effect "closed-cycle" version 2
            short-clip compress
            scope whole current
                segment wave flex 1 cycle 300ms
                    at 0 rotation base
                    at 1 rotation offset(0)
                end
            end
            """);
        var layer = Layer(line) with { Transform = new() { Rotation = 25 } };

        var result = EffectScriptCompiler.CompileTarget(closed, layer, subtitle: line);

        Assert.All(Assert.Single(result.Tracks).Keyframes, frame => Assert.Equal(25, frame.Value.Scalar));
        var entrance = EffectScriptParser.Parse("""
            effect "entrance" version 2
            short-clip compress
            scope whole current
                segment enter fixed 300ms repeat 1
                    at 0 rotation 0
                    at 1 rotation 100
                end
                segment rest flex 1
                end
            end
            """);
        var single = Assert.Single(EffectScriptCompiler.CompileTarget(entrance, layer, subtitle: line).Tracks);
        Assert.Equal(100, single.Keyframes[^1].Value.Scalar);
    }

    private static EffectScript Pulse(string unit, string fields = "") => EffectScriptParser.Parse($"""
        effect "pulse" version 2
        short-clip compress
        scope letters current
            unit {unit}
            {fields}
            segment pulse fixed 150ms pingpong
                at 0 scale base ease-in-out
                at 1 scale factor(1.25,1.25)
            end
            segment rest flex 1
            end
        end
        """);

    private static ProjectLayer Layer(SubtitleLine line) => new() { SubtitleId = line.Id, End = line.End };

    private static void Validate(EffectScriptCompilation result)
    {
        ProjectValidator.Validate(new() { Subtitles = [result.Subtitle!], Layers = [result.PreparedLayer! with { Tracks = result.Tracks }] });
    }
}
