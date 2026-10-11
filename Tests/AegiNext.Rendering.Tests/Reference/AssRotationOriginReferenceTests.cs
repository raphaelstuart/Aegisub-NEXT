using System.Globalization;
using System.Text.RegularExpressions;
using AegiNext.Application;
using AegiNext.Application.SubtitleFormats;
using AegiNext.Core.Projects;
using AegiNext.Core.Timing;

namespace AegiNext.Rendering.Tests.Reference;

/// <summary>用手写 ASS 与真实持久化出口验证条件旋转原点交换，不依赖生产逆换算生成基线。</summary>
public sealed class AssRotationOriginReferenceTests
{
    /// <summary>非直角旋转和正非等比缩放保留源定位与有效原点。</summary>
    [LibassReferenceFact]
    public void StaticRotationAndNonuniformScalePreserveIndependentLibassCoverage()
    {
        var source = ReferenceSubtitleProject.Script(@"{\q2\4a&HFF&\pos(240,180)\org(205,155)\fscx125\fscy80\frz37}Fj");
        var written = RoundTrip(Import(source));
        var dialogue = Assert.Single(Dialogues(written.Text));
        AssertOrigin(dialogue, new(205, 155));
        AssertEventTime(dialogue, 0, 4000);
        AssertPositionAt(dialogue, 733, new(240, 180));
        AssertCoverage(source, written.Text, [0, 733, 2500, 3999]);
    }

    /// <summary>有延迟和幂函数加速的整行旋转在内部时刻保持连续出口语义。</summary>
    [LibassReferenceFact]
    public void WholeLineRotationPreservesIndependentLibassAtInteriorTimes()
    {
        var source = ReferenceSubtitleProject.Script(@"{\q2\4a&HFF&\pos(240,180)\org(205,155)\fscx125\fscy80\frz-15\t(500,2500,2,\frz67)}Fj");
        var written = RoundTrip(Import(source));
        var dialogue = Assert.Single(Dialogues(written.Text));
        AssertOrigin(dialogue, new(205, 155));
        AssertEventTime(dialogue, 0, 4000);
        AssertPositionAt(dialogue, 733, new(240, 180));
        Assert.Contains(@"\t(", dialogue, StringComparison.Ordinal);
        AssertCoverage(source, written.Text, [0, 499, 500, 733, 1500, 2499, 2500, 3100, 3999]);
    }

    /// <summary>固定非直角旋转下的延迟直线移动保持原点、端点和移动时钟。</summary>
    [LibassReferenceFact]
    public void DelayedMoveWithStaticRotationPreservesIndependentLibassMotion()
    {
        var source = ReferenceSubtitleProject.Script(@"{\q2\4a&HFF&\move(240,180,300,210,500,2500)\org(205,155)\fscx125\fscy80\frz37}Fj");
        var written = RoundTrip(Import(source));
        var dialogue = Assert.Single(Dialogues(written.Text));
        AssertOrigin(dialogue, new(205, 155));
        AssertEventTime(dialogue, 0, 4000);
        var movement = Numbers(Assert.Single(Regex.Matches(dialogue, @"\\move\(([^)]*)\)")).Groups[1].Value);
        Assert.Equal(6, movement.Length);
        AssertPoint(new(240, 180), new(movement[0], movement[1]));
        AssertPoint(new(300, 210), new(movement[2], movement[3]));
        Assert.Equal(500, movement[4]);
        Assert.Equal(2500, movement[5]);
        foreach (var time in new long[] { 0, 499, 500, 733, 1500, 2499, 2500, 3100, 3999 })
        {
            AssertPositionAt(dialogue, time, SourceMoveAt(time, 500, 2500));
        }
        AssertCoverage(source, written.Text, [0, 499, 500, 733, 1500, 2499, 2500, 3100, 3999]);
    }

    /// <summary>负起始移动被可见窗口裁剪后，非零首位移不会改变有效原点。</summary>
    [LibassReferenceFact]
    public void NegativeMoveStartPreservesOriginAfterVisibleWindowClipping()
    {
        var source = ReferenceSubtitleProject.Script(@"{\q2\4a&HFF&\move(240,180,300,210,-500,2000)\org(205,155)\fscx125\fscy80\frz37}Fj");
        var written = RoundTrip(Import(source));
        var dialogue = Assert.Single(Dialogues(written.Text));
        AssertOrigin(dialogue, new(205, 155));
        AssertEventTime(dialogue, 0, 4000);
        foreach (var time in new long[] { 0, 1, 733, 1500, 1999, 2000, 3100, 3999 })
        {
            AssertPositionAt(dialogue, time, SourceMoveAt(time, -500, 2000));
        }
        AssertCoverage(source, written.Text, [0, 1, 733, 1500, 1999, 2000, 3100, 3999]);
    }

    /// <summary>两个非正移动时间按完整事件时长解释，不被误判为已结束的移动。</summary>
    [LibassReferenceFact]
    public void BothNegativeMoveTimesUseTheWholeEventDurationInIndependentLibass()
    {
        var source = ReferenceSubtitleProject.Script(@"{\q2\4a&HFF&\move(240,180,300,210,-1000,-500)\org(205,155)\fscx125\fscy80\frz37}Fj");
        var written = RoundTrip(Import(source));
        var dialogue = Assert.Single(Dialogues(written.Text));
        AssertOrigin(dialogue, new(205, 155));
        AssertEventTime(dialogue, 0, 4000);
        var movement = Numbers(Assert.Single(Regex.Matches(dialogue, @"\\move\(([^)]*)\)")).Groups[1].Value);
        Assert.Equal(6, movement.Length);
        AssertPoint(new(240, 180), new(movement[0], movement[1]));
        AssertPoint(new(300, 210), new(movement[2], movement[3]));
        Assert.Equal(0, movement[4]);
        Assert.Equal(4000, movement[5]);
        foreach (var time in new long[] { 0, 1, 733, 1500, 2000, 3100, 3999 })
        {
            AssertPositionAt(dialogue, time, SourceMoveAt(time, 0, 4000));
        }
        AssertCoverage(source, written.Text, [0, 1, 733, 1500, 2000, 3100, 3999]);
    }

    /// <summary>负卡拉 OK 起点产生内容原点重基时，旋转和定位仍与源事件同相。</summary>
    [LibassReferenceFact]
    public void NegativeKaraokeOffsetPreservesWholeLineRotationClock()
    {
        var source = ReferenceSubtitleProject.Script(@"{\q2\4a&HFF&\pos(240,180)\org(205,155)\fscx125\fscy80\frz-15\t(500,2500,2,\frz67)\kt-20\k400}Fj");
        var document = Import(source);
        Assert.Equal(new MediaTime(1, 5), Assert.Single(document.Layers).AnimationOffset);
        var written = RoundTrip(document);
        var dialogue = Assert.Single(Dialogues(written.Text));
        AssertOrigin(dialogue, new(205, 155));
        AssertEventTime(dialogue, 0, 4000);
        AssertPositionAt(dialogue, 733, new(240, 180));
        Assert.Contains(@"\t(", dialogue, StringComparison.Ordinal);
        AssertCoverage(source, written.Text, [0, 499, 500, 733, 1500, 2499, 2500, 3100, 3999]);
    }

    /// <summary>宽裁切的原生动画强制拆事件但不改变墨迹，分别验证固定原点和局部移动时钟。</summary>
    [LibassReferenceFact]
    public void MaskEventSplittingKeepsOneOriginAndDoesNotRestartNegativeMove()
    {
        var source = ReferenceSubtitleProject.Script(@"{\q2\4a&HFF&\move(240,180,300,210,-500,2000)\org(205,155)\fscx125\fscy80\frz37\clip(m 0 0 l 640 0 640 360 0 360)}Fj");
        var document = Import(source);
        var layer = Assert.Single(document.Layers);
        Assert.IsType<VectorClipMask>(layer.Mask);
        layer = layer with
        {
            Tracks = layer.Tracks.Add(new(AnimationProperty.MASK_POSITION,
            [
                new(MediaTime.Zero, new ScenePoint(0, 0)),
                new(new(4), new ScenePoint(10, 0))
            ]))
        };
        document = document with { FrameRate = new(4, 1), Layers = [layer] };
        var written = RoundTrip(document);
        var dialogues = Dialogues(written.Text);
        Assert.Equal(16, dialogues.Length);
        Assert.Contains(written.Diagnostics, diagnostic => diagnostic.Code == "Ass.MaskAnimationExpanded");
        for (var index = 0; index < dialogues.Length; index++)
        {
            var start = index * 250;
            var dialogue = dialogues[index];
            AssertOrigin(dialogue, new(205, 155));
            AssertEventTime(dialogue, start, start + 250);
            AssertPositionAt(dialogue, start + 125, SourceMoveAt(start + 125, -500, 2000));
        }
        AssertCoverage(source, written.Text, [0, 249, 250, 251, 733, 999, 1000, 1001, 1999, 2000, 2001, 3100, 3999]);
    }

    private static ProjectDocument Import(string source)
    {
        var parsed = AssSubtitleFormat.Parse(source, 640, 360);
        Assert.DoesNotContain(parsed.Diagnostics, diagnostic => diagnostic.Code is "Ass.RotationOrigin" or "Ass.UnsupportedTag");
        return ProjectEditingOperations.ImportSubtitleLines(new() { Width = 640, Height = 360 }, parsed, "Reference ASS");
    }

    private static SubtitleFormatWriteResult RoundTrip(ProjectDocument document)
    {
        var bytes = ProjectStore.Serialize(document);
        var restored = ProjectStore.Deserialize(bytes);
        Assert.Equal(bytes, ProjectStore.Serialize(restored));
        var written = AssSubtitleFormat.Write(restored);
        Assert.DoesNotContain(written.Diagnostics, diagnostic => diagnostic.Code is "Ass.RotationOrigin" or
            "Ass.TransformPivotAnimation" or "Subtitle.Composition");
        return written;
    }

    private static void AssertCoverage(string source, string exported, long[] times)
    {
        using var reference = new LibassReferenceRenderer();
        foreach (var time in times)
        {
            var expected = reference.Render(source, time);
            var actual = reference.Render(exported, time);
            var expectedEnergy = expected.Energy(3);
            var actualEnergy = actual.Energy(3);
            Assert.True(expectedEnergy > 100 && actualEnergy > 100, $"Empty reference ink at {time} ms.");
            Assert.InRange(Math.Abs(actualEnergy / expectedEnergy - 1), 0, 0.02);
            Assert.InRange(Math.Abs(actual.MaximumAlpha - expected.MaximumAlpha), 0, 0.005);
            for (var channel = 0; channel < 4; channel++)
            {
                var difference = 0d;
                for (var index = channel; index < expected.Pixels.Length; index += 4)
                {
                    difference += Math.Abs(actual.Pixels[index] - expected.Pixels[index]);
                }
                Assert.InRange(difference / expectedEnergy, 0, 0.02);
            }
            var expectedBounds = expected.InkBounds();
            var actualBounds = actual.InkBounds();
            Assert.InRange(Math.Abs(actualBounds.Left - expectedBounds.Left), 0, 1);
            Assert.InRange(Math.Abs(actualBounds.Top - expectedBounds.Top), 0, 1);
            Assert.InRange(Math.Abs(actualBounds.Right - expectedBounds.Right), 0, 1);
            Assert.InRange(Math.Abs(actualBounds.Bottom - expectedBounds.Bottom), 0, 1);
        }
    }

    private static string[] Dialogues(string text) => text.Split('\n')
        .Where(line => line.StartsWith("Dialogue:", StringComparison.Ordinal)).Select(line => line.TrimEnd('\r')).ToArray();

    private static void AssertOrigin(string dialogue, ScenePoint expected)
    {
        var origin = Numbers(Assert.Single(Regex.Matches(dialogue, @"\\org\(([^)]*)\)")).Groups[1].Value);
        Assert.Equal(2, origin.Length);
        AssertPoint(expected, new(origin[0], origin[1]));
    }

    private static void AssertEventTime(string dialogue, long start, long end)
    {
        var fields = dialogue.Split(',', 10);
        Assert.Equal(start, Timestamp(fields[1]));
        Assert.Equal(end, Timestamp(fields[2]));
    }

    private static void AssertPositionAt(string dialogue, long absoluteTime, ScenePoint expected)
    {
        var position = Regex.Match(dialogue, @"\\pos\(([^)]*)\)");
        if (position.Success)
        {
            Assert.DoesNotContain(@"\move(", dialogue, StringComparison.Ordinal);
            var point = Numbers(position.Groups[1].Value);
            Assert.Equal(2, point.Length);
            AssertPoint(expected, new(point[0], point[1]));
            return;
        }
        var move = Numbers(Assert.Single(Regex.Matches(dialogue, @"\\move\(([^)]*)\)")).Groups[1].Value);
        Assert.True(move.Length is 4 or 6);
        var fields = dialogue.Split(',', 10);
        var relativeTime = absoluteTime - Timestamp(fields[1]);
        var start = move.Length == 6 ? move[4] : 0;
        var end = move.Length == 6 ? move[5] : Timestamp(fields[2]) - Timestamp(fields[1]);
        Assert.True(end > start);
        var amount = Math.Clamp((relativeTime - start) / (end - start), 0, 1);
        AssertPoint(expected, new(move[0] + (move[2] - move[0]) * amount,
            move[1] + (move[3] - move[1]) * amount));
    }

    private static ScenePoint SourceMoveAt(long time, long start, long end)
    {
        var amount = Math.Clamp((double)(time - start) / (end - start), 0, 1);
        return new(240 + 60 * amount, 180 + 30 * amount);
    }

    private static void AssertPoint(ScenePoint expected, ScenePoint actual)
    {
        Assert.InRange(Math.Abs(actual.X - expected.X), 0, 1e-8);
        Assert.InRange(Math.Abs(actual.Y - expected.Y), 0, 1e-8);
    }

    private static double[] Numbers(string text) => text.Split(',')
        .Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();

    private static long Timestamp(string text)
    {
        var parts = text.Split(':');
        var seconds = double.Parse(parts[0], CultureInfo.InvariantCulture) * 3600 +
            double.Parse(parts[1], CultureInfo.InvariantCulture) * 60 + double.Parse(parts[2], CultureInfo.InvariantCulture);
        return (long)Math.Round(seconds * 1000);
    }
}
