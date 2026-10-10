using AegiNext.Core.Editing;
using AegiNext.Core.Effects;
using AegiNext.Core.Projects;
using AegiNext.Core.Timing;

namespace AegiNext.Core.Tests.Effects;

public sealed class EffectScriptV2SampleTests
{
    private static readonly MediaTime[] durations = [new(1, 1000), new(2, 5), new(2), new(10), new(1001, 30000)];

    [Theory]
    [InlineData("letter-bounce", AnimationProperty.POSITION)]
    [InlineData("letter-pulse", AnimationProperty.SCALE)]
    public void EmbeddedLetterEffectsGrowOrBounceOnceAndReturnToTheLocalBase(string id, AnimationProperty property)
    {
        var template = BuiltinEffectScripts.Get(id);
        Assert.Equal(2, template.Script.Version);
        Assert.Equal(id, EffectScriptParser.Parse(template.Source).Id);
        foreach (var duration in durations)
        {
            var line = new SubtitleLine { Text = "甲 A👩‍💻é", End = duration };
            var layer = new ProjectLayer { SubtitleId = line.Id, End = duration, AnimationOffset = new(-1, 7), Transform = new() { Scale = new(3, 4) } };
            var result = EffectScriptCompiler.CompileTarget(template.Script, layer, subtitle: line);
            Assert.Equal(4, result.Subtitle!.AnimationRanges.Length);
            Assert.Equal(4, result.Tracks.Length);
            foreach (var track in result.Tracks)
            {
                Assert.Equal(property, track.Property);
                Assert.Equal(new MediaTime(-1, 7) + duration, track.Keyframes[^1].Time);
                Assert.Equal(property == AnimationProperty.SCALE ? new ScenePoint(1, 1) : new(0, 0), track.Keyframes[^1].Value.Vector);
            }
            ProjectValidator.Validate(new() { Subtitles = [result.Subtitle], Layers = [result.PreparedLayer! with { Tracks = result.Tracks }] });
        }
        var normalLine = new SubtitleLine { Text = "AB", End = new(2) };
        var normal = EffectScriptCompiler.CompileTarget(template.Script, new() { SubtitleId = normalLine.Id, End = normalLine.End }, subtitle: normalLine);
        var first = normal.Tracks.Single(track => track.Target.TextRangeId == normal.Subtitle!.AnimationRanges[0].Id);
        Assert.Equal(property == AnimationProperty.SCALE ? new ScenePoint(1.25, 1.25) : new(0, -20),
            SceneEvaluator.EvaluateVectorTrack(first, new(3, 20)));
        Assert.Equal(property == AnimationProperty.SCALE ? new ScenePoint(1, 1) : new(0, 0),
            SceneEvaluator.EvaluateVectorTrack(first, new(3, 10)));
    }

    [Theory]
    [InlineData("grouped-words", "one two  three", 3)]
    [InlineData("grouped-pairs", "ABCDE", 3)]
    [InlineData("grouped-split", "甲、乙,丙", 3)]
    [InlineData("grouped-lines", "AB\r\nCD\nEF", 3)]
    [InlineData("scoped-fade-pulse", "ABC", 3)]
    public void DocumentedGroupingSamplesCompileAtShortAndLongDurations(string id, string text, int count)
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", id + ".aegifx"));
        var script = EffectScriptParser.Parse(source);
        Assert.Equal(id, script.Id);
        foreach (var duration in durations)
        {
            var line = new SubtitleLine { Text = text, End = duration };
            var result = EffectScriptCompiler.CompileTarget(script, new() { SubtitleId = line.Id, End = duration }, subtitle: line);
            Assert.Equal(count, result.Subtitle!.AnimationRanges.Length);
            Assert.Equal(id == "scoped-fade-pulse" ? count + 1 : count, result.Tracks.Length);
            ProjectValidator.Validate(new() { Subtitles = [result.Subtitle], Layers = [result.PreparedLayer! with { Tracks = result.Tracks }] });
        }
    }
}
