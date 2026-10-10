using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using AegiNext.Core.Projects;

namespace AegiNext.Core.Effects;

public static partial class EffectScriptCompiler
{
    private const int MAXIMUM_RANGES = 256;
    private const int MAXIMUM_SCOPED_TRACKS = 8192;

    private static (ImmutableArray<EffectScriptScopePlan> Plans, ProjectLayer Layer, SubtitleLine? Subtitle) PrepareScopes(
        EffectScript script, ProjectLayer target, AnimationTrackTarget? context, SubtitleLine? subtitle)
    {
        ValidateContext(target, context, subtitle);
        var originalRanges = subtitle?.AnimationRanges ?? [];
        var oldOwned = originalRanges.Where(range => range.GeneratedOrigin is { } origin &&
            script.Scopes.Any(scope => SameFamily(origin, script.Id, scope.Name, ParentId(scope, context)))).Select(range => range.Id).ToHashSet();
        var regenerated = new Dictionary<Guid, SubtitleAnimationRange>();
        var plans = ImmutableArray.CreateBuilder<EffectScriptScopePlan>();
        foreach (var scope in script.Scopes)
        {
            var wholeLayer = scope.Unit.Kind == EffectScriptUnitKind.GROUP && scope.Target.Kind != EffectScriptTargetKind.RANGE &&
                (scope.Target.Kind == EffectScriptTargetKind.SUBTITLE || context?.TextRangeId is null);
            var requiresSubtitle = !wholeLayer || scope.Target.Kind != EffectScriptTargetKind.CURRENT || scope.State != SubtitleAnimationState.NORMAL;
            if (requiresSubtitle && (target.Kind != LayerKind.SUBTITLE || subtitle is null || target.SubtitleId != subtitle.Id))
            {
                throw new EffectScriptException("文字分组、字幕目标和视觉状态需要匹配的字幕片段及应用前内容。", scope.Line, scope.Column);
            }
            ValidateScopeProperties(scope, !wholeLayer);
            if (wholeLayer)
            {
                plans.Add(new(scope, [null], false));
                continue;
            }

            var groups = EffectScriptGroupResolver.Resolve(scope, subtitle!, context);
            if (scope.Unit.Kind == EffectScriptUnitKind.GROUP && scope.Target.Kind == EffectScriptTargetKind.CURRENT)
            {
                plans.Add(new(scope, groups.IsEmpty ? [] : [context!.Value.TextRangeId], false));
                continue;
            }

            var parentId = ParentId(scope, context);
            var definition = UnitSignature(scope.Unit);
            var origin = new SubtitleAnimationRangeOrigin(script.Id, scope.Name, parentId, definition);
            var candidates = originalRanges.Where(range => range.GeneratedOrigin is { } source &&
                SameFamily(source, script.Id, scope.Name, parentId) && source.UnitDefinition == definition).ToArray();
            var rangeIds = ImmutableArray.CreateBuilder<Guid?>();
            foreach (var group in groups)
            {
                var reusable = candidates.FirstOrDefault(range => !regenerated.ContainsKey(range.Id) &&
                    range.Utf16Start == group.Utf16Start && range.Utf16Length == group.Utf16Length);
                var id = reusable?.Id ?? Guid.NewGuid();
                regenerated.Add(id, new(id, group.Utf16Start, group.Utf16Length) { GeneratedOrigin = origin });
                rangeIds.Add(id);
            }
            plans.Add(new(scope, rangeIds.ToImmutable(), true));
        }

        var removed = oldOwned.Where(id => !regenerated.ContainsKey(id)).ToHashSet();
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var range in originalRanges)
            {
                if (range.GeneratedOrigin?.ParentRangeId is { } parent && removed.Contains(parent))
                {
                    changed |= removed.Add(range.Id);
                }
            }
        }
        foreach (var id in regenerated.Keys)
        {
            if (removed.Contains(id))
            {
                throw new EffectScriptException("新作用域引用的父范围在本次替换中已被删除。");
            }
        }

        var ranges = ImmutableArray.CreateBuilder<SubtitleAnimationRange>();
        var originalIds = originalRanges.Select(range => range.Id).ToHashSet();
        foreach (var range in originalRanges)
        {
            if (regenerated.TryGetValue(range.Id, out var replacement))
            {
                ranges.Add(replacement);
            }
            else if (!removed.Contains(range.Id))
            {
                ranges.Add(range);
            }
        }
        foreach (var id in plans.Where(plan => plan.Generated).SelectMany(plan => plan.RangeIds))
        {
            if (!originalIds.Contains(id!.Value))
            {
                ranges.Add(regenerated[id.Value]);
            }
        }
        if (ranges.Count > MAXIMUM_RANGES)
        {
            var scope = script.Scopes[^1];
            throw new EffectScriptException($"字幕动画范围总数超过 {MAXIMUM_RANGES} 个的预算。", scope.Line, scope.Column);
        }
        ValidateParents(ranges);
        var cleared = oldOwned.Union(removed).ToHashSet();
        var tracks = target.Tracks.Where(track => track.Target.TextRangeId is not { } id || !cleared.Contains(id)).ToImmutableArray();
        var prepared = tracks.SequenceEqual(target.Tracks) ? target : target with { Tracks = tracks };
        var finalRanges = ranges.ToImmutable();
        var preparedSubtitle = subtitle is null || finalRanges.SequenceEqual(originalRanges) ? subtitle : subtitle with { AnimationRanges = finalRanges };
        return (plans.ToImmutable(), prepared, preparedSubtitle);
    }

    private static void ValidateContext(ProjectLayer target, AnimationTrackTarget? context, SubtitleLine? subtitle)
    {
        if (context is { } value)
        {
            try
            {
                SubtitleAnimationTargetValidation.ValidateIdentity(value);
            }
            catch (InvalidDataException error)
            {
                throw new EffectScriptException(error.Message, innerException: error);
            }
            if (value.NodeId.HasValue)
            {
                throw new EffectScriptException("脚本上下文只指定文字范围和视觉状态；蒙版节点由脚本选择器指定。");
            }
            if ((value.TextRangeId.HasValue || value.State != SubtitleAnimationState.NORMAL) &&
                (target.Kind != LayerKind.SUBTITLE || subtitle is null || target.SubtitleId != subtitle.Id))
            {
                throw new EffectScriptException("文字范围和视觉状态脚本需要目标字幕及其应用前内容。");
            }
        }
        if (subtitle is not null && (target.Kind != LayerKind.SUBTITLE || target.SubtitleId != subtitle.Id))
        {
            throw new EffectScriptException("提供的字幕与目标片段不匹配。");
        }
    }

    private static Guid? ParentId(EffectScriptScope scope, AnimationTrackTarget? context) =>
        scope.Target.Kind == EffectScriptTargetKind.CURRENT ? context?.TextRangeId : null;

    private static void ValidateScopeProperties(EffectScriptScope scope, bool textRange)
    {
        foreach (var frame in scope.Segments.SelectMany(segment => segment.Keyframes))
        {
            var property = EffectScriptPropertyMetadata.GetAnimationProperty(frame.Property);
            if (textRange && !AnimationPropertyMetadata.IsTextRangeProperty(property))
            {
                throw new EffectScriptException($"{frame.Property} 不能应用到独立文字范围。", frame.Line, frame.Column);
            }
            if (scope.State != SubtitleAnimationState.NORMAL && !AnimationPropertyMetadata.IsSubtitleVisualProperty(property))
            {
                throw new EffectScriptException($"{frame.Property} 不能应用到字幕视觉状态。", frame.Line, frame.Column);
            }
        }
    }

    private static bool SameFamily(SubtitleAnimationRangeOrigin origin, string effectId, string scopeName, Guid? parentId) =>
        origin.EffectId == effectId && origin.ScopeName == scopeName && origin.ParentRangeId == parentId;

    private static string UnitSignature(EffectScriptUnit unit)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", unit.Kind.ToString());
            writer.WriteNumber("count", unit.Count);
            writer.WriteStartArray("delimiters");
            foreach (var delimiter in unit.Delimiters)
            {
                writer.WriteStringValue(delimiter);
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Convert.ToHexStringLower(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }

    private static void ValidateParents(ImmutableArray<SubtitleAnimationRange>.Builder ranges)
    {
        var byId = ranges.ToDictionary(range => range.Id);
        foreach (var range in ranges)
        {
            var visited = new HashSet<Guid> { range.Id };
            var child = range;
            while (child.GeneratedOrigin?.ParentRangeId is { } parentId)
            {
                if (!byId.TryGetValue(parentId, out var parent) || !visited.Add(parentId))
                {
                    throw new EffectScriptException("生成文字范围的父范围不存在或形成循环。");
                }
                child = parent;
            }
        }
    }

    internal static void ValidateScopedTrackBudget(IEnumerable<AnimationTrack> tracks)
    {
        if (tracks.Select(track => track.Target).Distinct().Count(target => target.TextRangeId.HasValue || target.State != SubtitleAnimationState.NORMAL) > MAXIMUM_SCOPED_TRACKS)
        {
            throw new EffectScriptException($"字幕范围和视觉状态轨道总数超过 {MAXIMUM_SCOPED_TRACKS} 条的预算。");
        }
    }
}
