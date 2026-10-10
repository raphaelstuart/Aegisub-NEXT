using System.Collections.Immutable;
using AegiNext.Core.Projects;

namespace AegiNext.Application;

internal static class SubtitleAnimationRangeEditing
{
    internal static ImmutableArray<SubtitleAnimationRange> Remap(ImmutableArray<SubtitleAnimationRange> ranges, SubtitleTextEditMap map)
    {
        if (ranges.IsEmpty)
        {
            return ranges;
        }

        var sources = new int[map.NewBoundaries.Length - 1];
        for (var index = 0; index < sources.Length; index++)
        {
            sources[index] = map.StyleSourceOffset(map.NewBoundaries[index]);
        }

        var result = ImmutableArray.CreateBuilder<SubtitleAnimationRange>(ranges.Length);
        foreach (var range in ranges)
        {
            var first = LowerBound(sources, range.Utf16Start);
            var end = LowerBound(sources, checked(range.Utf16Start + range.Utf16Length));
            if (end > first)
            {
                result.Add(range with
                {
                    Utf16Start = map.NewBoundaries[first],
                    Utf16Length = map.NewBoundaries[end] - map.NewBoundaries[first]
                });
            }
        }

        var remapped = PruneOrphanedOrigins(result.ToImmutable());
        return remapped.SequenceEqual(ranges) ? ranges : remapped;
    }

    internal static SubtitleLine RemapTextChange(SubtitleLine original, SubtitleLine edited)
    {
        if (original.Text == edited.Text || original.AnimationRanges.IsEmpty ||
            !original.AnimationRanges.SequenceEqual(edited.AnimationRanges))
        {
            return edited;
        }

        return edited with { AnimationRanges = Remap(original.AnimationRanges, SubtitleTextEditMap.Between(original.Text, edited.Text)) };
    }

    internal static ImmutableArray<SubtitleAnimationRange> Split(ImmutableArray<SubtitleAnimationRange> ranges, int offset,
        bool right, IReadOnlyDictionary<Guid, Guid>? rangeIds = null)
    {
        var result = ImmutableArray.CreateBuilder<SubtitleAnimationRange>();
        foreach (var range in ranges)
        {
            var first = right ? Math.Max(offset, range.Utf16Start) : range.Utf16Start;
            var end = right ? checked(range.Utf16Start + range.Utf16Length) : Math.Min(offset, checked(range.Utf16Start + range.Utf16Length));
            if (end > first)
            {
                result.Add(range with
                {
                    Id = right ? rangeIds![range.Id] : range.Id,
                    GeneratedOrigin = right ? RemapOrigin(range.GeneratedOrigin, rangeIds!) : range.GeneratedOrigin,
                    Utf16Start = right ? first - offset : first,
                    Utf16Length = end - first
                });
            }
        }

        return PruneOrphanedOrigins(result.ToImmutable());
    }

    internal static ImmutableArray<SubtitleAnimationRange> Clone(ImmutableArray<SubtitleAnimationRange> ranges,
        IReadOnlyDictionary<Guid, Guid> rangeIds, int textOffset = 0)
    {
        return ranges.Select(range => range with
        {
            Id = rangeIds[range.Id],
            Utf16Start = checked(range.Utf16Start + textOffset),
            GeneratedOrigin = RemapOrigin(range.GeneratedOrigin, rangeIds)
        }).ToImmutableArray();
    }

    internal static ImmutableArray<SubtitleAnimationRange> PruneOrphanedOrigins(ImmutableArray<SubtitleAnimationRange> ranges)
    {
        var current = ranges;
        while (!current.IsEmpty)
        {
            var knownIds = current.Select(range => range.Id).ToHashSet();
            var retained = current.Where(range => range.GeneratedOrigin?.ParentRangeId is not { } parentId ||
                knownIds.Contains(parentId)).ToImmutableArray();
            if (retained.Length == current.Length)
            {
                return current;
            }
            current = retained;
        }
        return current;
    }

    private static SubtitleAnimationRangeOrigin? RemapOrigin(SubtitleAnimationRangeOrigin? origin,
        IReadOnlyDictionary<Guid, Guid> rangeIds)
    {
        return origin?.ParentRangeId is { } parentId && rangeIds.TryGetValue(parentId, out var replacement)
            ? origin with { ParentRangeId = replacement } : origin;
    }

    internal static ProjectLayer PruneTargets(ProjectLayer layer, SubtitleLine line)
    {
        if (!layer.Tracks.Any(track => track.Target.TextRangeId.HasValue))
        {
            return layer;
        }

        var rangeIds = line.AnimationRanges.Select(range => range.Id).ToHashSet();
        var tracks = layer.Tracks.Where(track => track.Target.TextRangeId is not { } id || rangeIds.Contains(id)).ToImmutableArray();
        return tracks.SequenceEqual(layer.Tracks) ? layer : layer with { Tracks = tracks };
    }

    private static int LowerBound(int[] sources, int offset)
    {
        var first = 0;
        var end = sources.Length;
        while (first < end)
        {
            var middle = first + (end - first) / 2;
            if (sources[middle] < offset)
            {
                first = middle + 1;
            }
            else
            {
                end = middle;
            }
        }

        return first;
    }
}
