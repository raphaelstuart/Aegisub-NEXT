using System.Numerics;
using AegiNext.Core.Timing;

namespace AegiNext.Core.Effects;

internal sealed record EffectScriptSegmentTiming(EffectScriptSegment Segment, MediaTime Length, MediaTime LegLength,
    BigInteger Cycles);
