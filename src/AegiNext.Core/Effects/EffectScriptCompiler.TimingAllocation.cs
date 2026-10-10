using System.Collections.Immutable;
using AegiNext.Core.Timing;

namespace AegiNext.Core.Effects;

public static partial class EffectScriptCompiler
{
    private static EffectScriptScopeTiming AllocateScope(EffectScript script, EffectScriptScopePlan plan, MediaTime duration)
    {
        var scope = plan.Scope;
        var staggerSpan = EffectScriptTiming.Scale(scope.Stagger, plan.RangeIds.Length - 1);
        var fixedTotal = MediaTime.Zero;
        var weightTotal = EffectScriptTiming.Scale(new(1), scope.Segments.Sum(segment => segment.FlexWeight));
        var minimumFlex = MediaTime.Zero;
        foreach (var segment in scope.Segments)
        {
            if (segment.FixedDuration is { } fixedLength)
            {
                fixedTotal += EffectScriptTiming.Scale(fixedLength, segment.RepeatCount * (segment.PingPong ? 2m : 1m));
            }
            if (segment.CycleDuration is { } cycle)
            {
                var required = EffectScriptTiming.Scale(cycle, weightTotal, EffectScriptTiming.Scale(new(1), segment.FlexWeight));
                if (required > minimumFlex)
                {
                    minimumFlex = required;
                }
            }
        }
        var envelope = scope.Delay + staggerSpan + fixedTotal + minimumFlex;
        var compress = duration < envelope;
        if (compress && script.ShortClipPolicy == EffectScriptShortClipPolicy.REJECT)
        {
            throw new EffectScriptException($"片段时长 {duration} 小于作用域所需包络 {envelope}，该脚本拒绝应用。", scope.Line, scope.Column);
        }
        var numerator = compress ? duration : new(1);
        var denominator = compress ? envelope : new(1);
        var delay = EffectScriptTiming.Scale(scope.Delay, numerator, denominator);
        var stagger = EffectScriptTiming.Scale(scope.Stagger, numerator, denominator);
        var remaining = duration - EffectScriptTiming.Scale(scope.Delay + staggerSpan + fixedTotal, numerator, denominator);
        var timings = ImmutableArray.CreateBuilder<EffectScriptSegmentTiming>();
        foreach (var segment in scope.Segments)
        {
            var legs = segment.PingPong ? 2 : 1;
            if (segment.FixedDuration is { } fixedLength)
            {
                var leg = EffectScriptTiming.Scale(fixedLength, numerator, denominator);
                timings.Add(new(segment, EffectScriptTiming.Scale(leg, segment.RepeatCount * (decimal)legs), leg, segment.RepeatCount));
            }
            else
            {
                var length = EffectScriptTiming.Scale(remaining, EffectScriptTiming.Scale(new(1), segment.FlexWeight), weightTotal);
                if (segment.CycleDuration is { } cycle)
                {
                    var period = EffectScriptTiming.Scale(cycle, numerator, denominator);
                    timings.Add(new(segment, length, EffectScriptTiming.Scale(period, new(1), new(legs)), EffectScriptTiming.ExactFloor(length, period)));
                }
                else
                {
                    timings.Add(new(segment, length, length, 1));
                }
            }
        }
        return new(delay, stagger, timings.ToImmutable());
    }
}
