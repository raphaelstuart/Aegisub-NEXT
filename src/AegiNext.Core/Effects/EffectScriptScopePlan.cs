using System.Collections.Immutable;

namespace AegiNext.Core.Effects;

internal sealed record EffectScriptScopePlan(EffectScriptScope Scope, ImmutableArray<Guid?> RangeIds, bool Generated);
