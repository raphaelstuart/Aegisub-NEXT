using System.Globalization;
using System.Text.RegularExpressions;
using AegiNext.Application.SubtitleFormats;
using AegiNext.Core.Editing;
using AegiNext.Core.Projects;
using AegiNext.Core.Timing;

namespace AegiNext.Application.Tests;

/// <summary>验证源事件半开可见窗口内的恒定几何与旋转原点交换。</summary>
public sealed class AssRotationOriginWindowTests
{
    /// <summary>窗口前已完成、窗口后才开始及零加速度边界的源变换保留有效几何。</summary>
    [Theory]
    [InlineData(@"\frz23\t(-1000,-500,\frz90)", 2, true, 1, 1, 90, false)]
    [InlineData(@"\frz23\t(2500,3500,\frz90)", 2, true, 1, 1, 23, false)]
    [InlineData(@"\frz23\t(-1000,-500,\fscx150\fscy150)", 4, false, 1.5, 1.5, 23, false)]
    [InlineData(@"\frz23\t(-2000,-1500,\fscx200)\t(-1000,-500,\fscx150)\t(-1000,-500,\fscy150)", 4, false, 1.5, 1.5, 23, false)]
    [InlineData(@"\frz23\fscy75\t(-1000,-500,\fscx150)", 4, false, 1.5, 0.75, 23, false)]
    [InlineData(@"\frz23\t(-1000,-500,\frz90)", 4, true, 1, 1, 90, true)]
    [InlineData(@"\frz23\t(0,1000,0,\frz90)", 2, true, 1, 1, 90, false)]
    [InlineData(@"\frz23\t(2000,3000,0,\frz90)", 2, true, 1, 1, 23, false)]
    [InlineData(@"\frz23\t(0,1000,0,\fscx150\fscy150)", 2, false, 1.5, 1.5, 23, false)]
    [InlineData(@"\frz23\t(2000,3000,0,\fscx150\fscy150)", 2, false, 1, 1, 23, false)]
    [InlineData(@"\frz23\t(-1000,-1,\frz90)", 2, true, 1, 1, 90, false)]
    [InlineData(@"\frz23\t(-1000,-500,\fscx150\fscy150)", 4, true, 1.5, 1.5, 23, false)]
    public void VisibleWindowConstantsPreserveOriginAndIndependentGeometryThroughPublicExchange(
        string transforms, int duration, bool move, double scaleX, double scaleY, double angle, bool negativeKaraoke)
    {
        var parsed = Parse(File(transforms, duration, move, negativeKaraoke));
        Assert.DoesNotContain(parsed.Diagnostics, diagnostic => diagnostic.Code is "Ass.RotationOrigin" or
            "Ass.UnsupportedTag" or "Ass.AnimationSampling" or "Ass.AnimationSamplingLimit");
        var document = Import(parsed);
        var layer = Assert.Single(document.Layers);
        Assert.Equal(negativeKaraoke ? new MediaTime(1, 2) : MediaTime.Zero, layer.AnimationOffset);
        AssertPoint(new(-35 / scaleX, -25 / scaleY), layer.Transform.Pivot);
        AssertIndependentGeometry(document, duration, move, scaleX, scaleY, angle);

        var restored = ProjectStore.Deserialize(ProjectStore.Serialize(document));
        var nativeBytes = ProjectStore.Serialize(restored);
        var written = AssSubtitleFormat.Write(restored);
        Assert.Equal(nativeBytes, ProjectStore.Serialize(restored));
        Assert.DoesNotContain(written.Diagnostics, diagnostic => diagnostic.Code is "Ass.RotationOrigin" or
            "Ass.TransformPivotAnimation" or "Subtitle.Composition" or "Ass.AnimationSampling" or "Ass.AnimationSamplingLimit");
        var origin = Assert.Single(Regex.Matches(written.Text, @"\\org\(([^)]*)\)"));
        AssertPoint(new(205, 155), Point(origin.Groups[1].Value));
        var roundTripped = Parse(written.Text);
        Assert.DoesNotContain(roundTripped.Diagnostics, diagnostic => diagnostic.Code is "Ass.RotationOrigin" or "Ass.UnsupportedTag");
        var line = Assert.Single(roundTripped.Lines);
        Assert.Equal(MediaTime.Zero, line.Start);
        Assert.Equal(new MediaTime(duration), line.End);
        Assert.Equal("Fj", line.Text);
        AssertIndependentGeometry(Import(roundTripped), duration, move, scaleX, scaleY, angle);
    }

    /// <summary>首尾相同仍有窗内变化，或源约束需要采样时，不允许用端点误证为恒值。</summary>
    [Theory]
    [InlineData(@"\frz23\t(0,500,\fscx200)\t(1000,1500,\fscx100)", false, false)]
    [InlineData(@"\frz23\t(0,500,\frz90)\t(1000,1500,\frz23)", true, false)]
    [InlineData(@"\frz23\t(-2000,-1500,\fscx-50)\t(-1000,-500,\fscx150)", false, true)]
    [InlineData(@"\frz23\t(1500,500,\frz97)", false, false)]
    [InlineData(@"\frz23\t(0,2000,\t(0,2000,\frz90))", false, false)]
    public void UnprovedSourceGeometryKeepsTheExistingGeometryWithoutOriginSideEffects(
        string transforms, bool move, bool sampled)
    {
        var source = File(transforms, 2, move, false);
        var parsed = Parse(source);
        var baseline = Parse(source.Replace(@"\org(205,155)", string.Empty, StringComparison.Ordinal));
        Assert.Contains(parsed.Diagnostics, diagnostic => diagnostic.Code == "Ass.RotationOrigin");
        if (sampled)
        {
            Assert.Contains(parsed.Diagnostics, diagnostic => diagnostic.Code is "Ass.AnimationSampling" or "Ass.AnimationSamplingLimit");
        }
        var actual = Assert.Single(parsed.Clips);
        var expected = Assert.Single(baseline.Clips);
        Assert.Equal(expected.Transform, actual.Transform);
        Assert.Equal(default(ScenePoint), actual.Transform.Pivot);
        Assert.Equal(expected.Line.Style.Position, actual.Line.Style.Position);
        Assert.Equal(expected.ContentOffset, actual.ContentOffset);
        Assert.Equal(expected.Tracks.Length, actual.Tracks.Length);
        foreach (var pair in expected.Tracks.Zip(actual.Tracks))
        {
            Assert.Equal(pair.First.Property, pair.Second.Property);
            Assert.Equal(pair.First.Target.State, pair.Second.Target.State);
            foreach (var time in new[] { MediaTime.Zero, new MediaTime(1, 2), new MediaTime(733, 1000), new MediaTime(1999, 1000) })
            {
                Assert.Equal(SceneEvaluator.EvaluateTrack(pair.First, time), SceneEvaluator.EvaluateTrack(pair.Second, time));
            }
        }
        _ = ProjectStore.Deserialize(ProjectStore.Serialize(Import(parsed)));
    }

    /// <summary>舍弃逆序颜色变换只诊断该变换，不影响完整静态几何的原点证明。</summary>
    [Fact]
    public void DiscardedColorTimingDoesNotInvalidateTheOriginGeometry()
    {
        var parsed = Parse(File(@"\frz23\t(1500,500,\1c&H0000FF&)", 2, false, false));
        Assert.Contains(parsed.Diagnostics, diagnostic => diagnostic.Code == "Ass.TransformTiming");
        Assert.DoesNotContain(parsed.Diagnostics, diagnostic => diagnostic.Code == "Ass.RotationOrigin");
        var document = Import(parsed);
        AssertIndependentGeometry(document, 2, false, 1, 1, 23);
        var written = AssSubtitleFormat.Write(ProjectStore.Deserialize(ProjectStore.Serialize(document)));
        AssertPoint(new(205, 155), Point(Assert.Single(Regex.Matches(written.Text, @"\\org\(([^)]*)\)")).Groups[1].Value));
        AssertIndependentGeometry(Import(Parse(written.Text)), 2, false, 1, 1, 23);
    }

    /// <summary>外部文件的源几何完整性检查不改变高级 ASS 编辑的嵌套标签降级行为。</summary>
    [Fact]
    public void ProjectProjectionDoesNotScanUnsupportedNestedTransformsForOriginGeometry()
    {
        var original = new SubtitleLine { Text = "Fj", End = new(2) };
        var edited = AssTextProjection.Apply(original, @"{\t(0,1000,\t\frz47)}Fj");

        Assert.Equal(original.Id, edited.Line.Id);
        Assert.Equal("Fj", edited.Line.Text);
        Assert.Contains(edited.Diagnostics, diagnostic => diagnostic.Code == "Ass.UnsupportedTag");
        Assert.DoesNotContain(edited.Diagnostics, diagnostic => diagnostic.Code == "Ass.RotationOrigin");
        var rotation = Assert.Single(edited.TextAnimationTracks!.Value.Where(track => track.Property == AnimationProperty.ROTATION));
        Assert.Equal(-23.5, SceneEvaluator.EvaluateTrack(rotation, new(1, 2)).Scalar);
    }

    private static void AssertIndependentGeometry(ProjectDocument document, int duration, bool move,
        double scaleX, double scaleY, double angle)
    {
        var layer = Assert.Single(document.Layers);
        var line = Assert.Single(document.Subtitles);
        foreach (var milliseconds in new[] { 0, 1, 499, 500, 733, 1000, duration * 1000 - 1 })
        {
            var time = layer.AnimationOffset + new MediaTime(milliseconds, 1000);
            var effective = SceneEvaluator.EvaluateLayer(layer, line, time).Transform;
            var fraction = move ? milliseconds / (duration * 1000d) : 0;
            foreach (var local in new[] { default(ScenePoint), new(25, 11), new(-7, 31) })
            {
                var expected = Add(new(205, 155), Rotate(new(35 + 60 * fraction + scaleX * local.X,
                    25 + 30 * fraction + scaleY * local.Y), -angle));
                AssertPoint(expected, NativePoint(new(240, 180), effective, local));
            }
        }
    }

    private static ProjectDocument Import(AssImportResult parsed)
    {
        return ProjectEditingOperations.ImportSubtitleLines(new() { Width = 640, Height = 360 }, parsed, "ASS visible window");
    }

    private static AssImportResult Parse(string source) => AssSubtitleFormat.Parse(source, 640, 360);

    private static string File(string transforms, int duration, bool move, bool negativeKaraoke)
    {
        var placement = move ? $@"\move(240,180,300,210,0,{duration * 1000})" : @"\pos(240,180)";
        var karaoke = negativeKaraoke ? $@"\kt-50\k{duration * 100}" : string.Empty;
        var body = "{" + placement + @"\org(205,155)" + transforms + karaoke + "}Fj";
        return $$"""
            [Script Info]
            ScriptType: v4.00+
            PlayResX: 640
            PlayResY: 360
            WrapStyle: 2
            ScaledBorderAndShadow: yes
            YCbCr Matrix: None
            [V4+ Styles]
            Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
            Style: Default,Noto Sans,48,&H0000FF00,&H000000FF,&H00FF0000,&HFF000000,0,0,0,0,100,100,0,0,1,0,0,7,32,32,32,1
            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            Dialogue: 0,0:00:00.00,0:00:0{{duration}}.00,Default,,0,0,0,,{{body}}
            """;
    }

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

    private static ScenePoint Point(string value)
    {
        var numbers = value.Split(',').Select(component => double.Parse(component, CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(2, numbers.Length);
        return new(numbers[0], numbers[1]);
    }

    private static void AssertPoint(ScenePoint expected, ScenePoint actual)
    {
        Assert.Equal(expected.X, actual.X, 8);
        Assert.Equal(expected.Y, actual.Y, 8);
    }
}
