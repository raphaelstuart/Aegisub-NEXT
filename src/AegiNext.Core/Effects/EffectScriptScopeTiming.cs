using System.Collections.Immutable;
using AegiNext.Core.Timing;

namespace AegiNext.Core.Effects;

internal sealed record EffectScriptScopeTiming(MediaTime Delay, MediaTime Stagger,
    ImmutableArray<EffectScriptSegmentTiming> Segments);
