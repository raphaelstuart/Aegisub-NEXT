using System.Collections.Immutable;
using AegiNext.Core.Projects;

namespace AegiNext.Core.Effects;

internal sealed record EffectScriptPropertyStream(AnimationTrackTarget Target, ImmutableArray<EffectScriptKeyframe> Frames);
