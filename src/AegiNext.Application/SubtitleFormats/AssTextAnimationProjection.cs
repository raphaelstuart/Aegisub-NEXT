using System.Collections.Immutable;
using AegiNext.Core.Projects;

namespace AegiNext.Application.SubtitleFormats;

internal static class AssTextAnimationProjection
{
    internal static (SubtitleLine Line, ImmutableArray<AnimationTrack>? Tracks) Restore(SubtitleLine original,
        AssTextEditResult baseline, AssTextEditResult parsed, SubtitleLine restored, ProjectLayer? layer)
    {
        var editMap = SubtitleTextEditMap.Between(original.Text, restored.Text);
        var originalRanges = SubtitleAnimationRangeEditing.Remap(original.AnimationRanges, editMap);
        var expected = SubtitleAnimationRangeEditing.Remap(baseline.Line.AnimationRanges,
            SubtitleTextEditMap.Between(baseline.Line.Text, restored.Text));
        var expectedLine = baseline.Line with { Text = restored.Text, AnimationRanges = expected };
        if (Equivalent(expectedLine, baseline.NumericTracks, parsed.Line, parsed.NumericTracks))
        {
            return (restored with { AnimationRanges = originalRanges }, null);
        }
        var identities = new Dictionary<Guid, Guid>();
        var ranges = parsed.Line.AnimationRanges.Select(range =>
        {
            var previous = originalRanges.FirstOrDefault(candidate => candidate.Utf16Start == range.Utf16Start &&
                candidate.Utf16Length == range.Utf16Length);
            var id = previous?.Id ?? range.Id;
            identities.Add(range.Id, id);
            return range with
            {
                Id = id, Pivot = previous?.Pivot ?? range.Pivot,
                Offset = previous?.Offset ?? range.Offset,
                GeneratedOrigin = previous?.GeneratedOrigin ?? range.GeneratedOrigin
            };
        }).ToImmutableArray();
        ranges = ranges.AddRange(originalRanges.Where(range => !ranges.Any(candidate => candidate.Id == range.Id) &&
            !expected.Any(candidate => candidate.Utf16Start == range.Utf16Start && candidate.Utf16Length == range.Utf16Length)));
        var tracks = parsed.NumericTracks.Select(track => track.Target.TextRangeId is { } id ? track with
        {
            Target = track.Target with { TextRangeId = identities[id] }
        } : track).ToImmutableArray();
        var representableShadows = layer is null ? new HashSet<AnimationTrackTarget>() :
            RepresentableShadows(layer, original, baseline);
        var preserveFill = layer is not null && EquivalentFill(expectedLine, baseline.NumericTracks,
            parsed.Line, parsed.NumericTracks);
        var unchanged = UnchangedTargets(original with { Text = restored.Text, AnimationRanges = originalRanges },
            layer?.Tracks ?? [], expectedLine, baseline.NumericTracks, parsed.Line, parsed.NumericTracks);
        var preserved = layer is null ? ImmutableArray<AnimationTrack>.Empty : layer.Tracks
            .Where(track => !IsTextTrack(track) || !Representable(track, representableShadows) ||
                preserveFill && track.Property == AnimationProperty.FILL || unchanged.Native.Contains(track.Target)).ToImmutableArray();
        var preservedTargets = preserved.Select(track => track.Target).ToHashSet();
        var represented = tracks.Where((track, index) => !preservedTargets.Contains(track.Target) &&
            !(preserveFill && track.Property == AnimationProperty.FILL) &&
            !unchanged.Projected.Contains(parsed.NumericTracks[index].Target));
        ranges = RestorePreservedRanges(ranges, originalRanges, preserved, expected);
        return (restored with { AnimationRanges = ranges }, preserved.AddRange(represented));
    }

    internal static bool IsTextTrack(AnimationTrack track) => track.Target.TextRangeId is not null ||
        track.Property is AnimationProperty.FONT_SIZE or AnimationProperty.LETTER_SPACING or AnimationProperty.FILL or
        AnimationProperty.STROKE or AnimationProperty.STROKE_WIDTH or AnimationProperty.FILL_BLUR or AnimationProperty.STROKE_BLUR or
        AnimationProperty.SHADOW_OFFSET or AnimationProperty.SHADOW_BLUR or AnimationProperty.SHADOW_COLOR;

    private static (HashSet<AnimationTrackTarget> Native, HashSet<AnimationTrackTarget> Projected) UnchangedTargets(
        SubtitleLine nativeLine, ImmutableArray<AnimationTrack> native,
        SubtitleLine firstLine, ImmutableArray<AnimationTrack> first,
        SubtitleLine secondLine, ImmutableArray<AnimationTrack> second)
    {
        if (secondLine.Text.Length == 0)
        {
            return ([], []);
        }
        var nativeTracks = native.Where(track => track.Property != AnimationProperty.FILL).ToDictionary(track => track.Target);
        var firstTracks = first.Where(track => track.Property != AnimationProperty.FILL).ToDictionary(track => track.Target);
        var secondTracks = second.Where(track => track.Property != AnimationProperty.FILL).ToDictionary(track => track.Target);
        var unchanged = nativeTracks.Keys.ToHashSet();
        var observed = new HashSet<AnimationTrackTarget>();
        var changedChannels = new HashSet<(AnimationProperty Property, SubtitleAnimationState State)>();
        var redundant = secondTracks.Keys.ToHashSet();
        var coveredTargets = new Dictionary<AnimationTrackTarget, HashSet<AnimationTrackTarget>>();
        var comparisons = new Dictionary<(AnimationTrackTarget First, AnimationTrackTarget Second), bool>();
        var channels = nativeTracks.Keys.Concat(firstTracks.Keys).Concat(secondTracks.Keys)
            .Select(target => (target.Property, target.State)).Distinct().ToArray();
        var boundaries = new SortedSet<int> { 0, secondLine.Text.Length };
        foreach (var range in nativeLine.AnimationRanges.Concat(firstLine.AnimationRanges).Concat(secondLine.AnimationRanges))
        {
            boundaries.Add(range.Utf16Start);
            boundaries.Add(range.Utf16Start + range.Utf16Length);
        }
        foreach (var offset in boundaries.Where(offset => offset < secondLine.Text.Length))
        {
            foreach (var (property, state) in channels)
            {
                var nativeTrack = SelectedTrack(nativeTracks, nativeLine, property, offset, state);
                if (nativeTrack is not null)
                {
                    observed.Add(nativeTrack.Target);
                }
                var firstTrack = SelectedTrack(firstTracks, firstLine, property, offset, state);
                var secondTrack = SelectedTrack(secondTracks, secondLine, property, offset, state);
                var equivalent = firstTrack is null && secondTrack is null;
                if (firstTrack is not null && secondTrack is not null)
                {
                    var pair = (firstTrack.Target, secondTrack.Target);
                    if (!comparisons.TryGetValue(pair, out equivalent))
                    {
                        equivalent = EquivalentTrack(firstTrack, secondTrack);
                        comparisons.Add(pair, equivalent);
                    }
                }
                if (!equivalent)
                {
                    changedChannels.Add((property, state));
                    if (nativeTrack is not null)
                    {
                        unchanged.Remove(nativeTrack.Target);
                    }
                }
                if (secondTrack is null)
                {
                    continue;
                }
                if (nativeTrack is null)
                {
                    redundant.Remove(secondTrack.Target);
                    continue;
                }
                if (!coveredTargets.TryGetValue(secondTrack.Target, out var targets))
                {
                    targets = [];
                    coveredTargets.Add(secondTrack.Target, targets);
                }
                targets.Add(nativeTrack.Target);
            }
        }
        unchanged.RemoveWhere(target => !observed.Contains(target) && changedChannels.Contains((target.Property, target.State)));
        redundant.RemoveWhere(target => !coveredTargets.TryGetValue(target, out var targets) ||
            targets.Any(nativeTarget => !unchanged.Contains(nativeTarget)));
        return (unchanged, redundant);
    }

    private static bool Representable(AnimationTrack track, HashSet<AnimationTrackTarget> representableShadows)
    {
        if (track.Target.TextRangeId is not null && track.Property == AnimationProperty.POSITION ||
            AssCurveCompatibility.HasReversedNonlinearCurve(track))
        {
            return false;
        }
        return track.Property == AnimationProperty.SHADOW_BLUR ? representableShadows.Contains(track.Target) :
            track.Target.State == SubtitleAnimationState.NORMAL || track.Property == AnimationProperty.FILL;
    }

    private static ImmutableArray<SubtitleAnimationRange> RestorePreservedRanges(ImmutableArray<SubtitleAnimationRange> ranges,
        ImmutableArray<SubtitleAnimationRange> originalRanges, ImmutableArray<AnimationTrack> preserved,
        ImmutableArray<SubtitleAnimationRange> expected)
    {
        var referencedIds = preserved
            .Select(track => track.Target.TextRangeId).ToHashSet();
        referencedIds.UnionWith(originalRanges.Where(range => range.Offset != default || range.GeneratedOrigin is not null)
            .Select(range => (Guid?)range.Id));
        var byId = originalRanges.ToDictionary(range => range.Id);
        var pending = new Queue<Guid>(referencedIds.OfType<Guid>());
        while (pending.TryDequeue(out var id))
        {
            if (byId.TryGetValue(id, out var range) && range.GeneratedOrigin?.ParentRangeId is { } parentId &&
                referencedIds.Add(parentId))
            {
                pending.Enqueue(parentId);
            }
        }
        var protectedRanges = originalRanges.Where(range => referencedIds.Contains(range.Id)).ToDictionary(range => range.Id);
        var shadowRangeIds = preserved.Where(track => track.Property == AnimationProperty.SHADOW_BLUR)
            .Select(track => track.Target.TextRangeId).ToHashSet();
        var ambiguousScopes = originalRanges.GroupBy(range => (Start: range.Utf16Start, Length: range.Utf16Length))
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
        ranges = ranges.Select(range => protectedRanges.TryGetValue(range.Id, out var originalRange) &&
            shadowRangeIds.Contains(range.Id) && ambiguousScopes.Contains((originalRange.Utf16Start, originalRange.Utf16Length))
                ? originalRange : range).ToImmutableArray();
        var projectedScopes = expected.GroupBy(range => (range.Utf16Start, range.Utf16Length))
            .Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single());
        ranges = ranges.AddRange(originalRanges.Where(range => protectedRanges.ContainsKey(range.Id) &&
            !ranges.Any(candidate => candidate.Id == range.Id)).Select(range =>
        {
            var projected = projectedScopes.GetValueOrDefault((range.Utf16Start, range.Utf16Length));
            var geometryWasProjected = !shadowRangeIds.Contains(range.Id) &&
                range.Pivot == SubtitleAnimationPivot.SUBTITLE_ANCHOR && range.Scale.X >= 0 && range.Scale.Y >= 0 &&
                projected is not null && projected.Scale == range.Scale && projected.Rotation == range.Rotation &&
                !originalRanges.Any(candidate => candidate.Id != range.Id &&
                    candidate.Utf16Start < range.Utf16Start + range.Utf16Length &&
                    range.Utf16Start < candidate.Utf16Start + candidate.Utf16Length);
            return geometryWasProjected ? range with { Scale = new(1, 1), Rotation = 0 } : range;
        }));
        var originalOrder = originalRanges.Select((range, index) => (range.Id, Index: index))
            .ToDictionary(pair => pair.Id, pair => pair.Index);
        var orderedOriginals = ranges.Where(range => originalOrder.ContainsKey(range.Id))
            .OrderBy(range => originalOrder[range.Id]).ToArray();
        var cursor = 0;
        return ranges.Select(range => originalOrder.ContainsKey(range.Id) ? orderedOriginals[cursor++] : range).ToImmutableArray();
    }

    private static HashSet<AnimationTrackTarget> RepresentableShadows(ProjectLayer layer, SubtitleLine line, AssTextEditResult baseline)
    {
        var tracks = layer.Tracks.Where(track => track.Target.State == SubtitleAnimationState.NORMAL &&
            track.Property is AnimationProperty.SHADOW_BLUR or AnimationProperty.FILL_BLUR or AnimationProperty.STROKE_BLUR)
            .ToDictionary(track => track.Target);
        var baselineScopes = baseline.NumericTracks.Where(track => track.Property == AnimationProperty.SHADOW_BLUR &&
            track.Target.State == SubtitleAnimationState.NORMAL).Select(track => Scope(baseline.Line, track)).ToHashSet();
        var ambiguousScopes = line.AnimationRanges.GroupBy(range => (Start: range.Utf16Start, Length: range.Utf16Length))
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
        var candidates = new HashSet<AnimationTrackTarget>();
        foreach (var shadow in tracks.Values.Where(track => track.Property == AnimationProperty.SHADOW_BLUR))
        {
            var scope = Scope(line, shadow);
            if (baselineScopes.Contains(scope) && !ambiguousScopes.Contains((scope.Start, scope.Length)))
            {
                candidates.Add(shadow.Target);
            }
        }
        if (candidates.Count == 0)
        {
            return candidates;
        }
        var boundaries = new SortedSet<int> { 0, line.Text.Length };
        foreach (var span in line.InlineSpans)
        {
            boundaries.Add(span.Utf16Start);
            boundaries.Add(span.Utf16Start + span.Utf16Length);
        }
        foreach (var range in line.AnimationRanges)
        {
            boundaries.Add(range.Utf16Start);
            boundaries.Add(range.Utf16Start + range.Utf16Length);
        }
        var represented = new HashSet<AnimationTrackTarget>();
        var comparisons = new Dictionary<(AnimationTrackTarget Shadow, AnimationTrackTarget Blur), bool>();
        var spanIndex = 0;
        foreach (var offset in boundaries.Where(offset => offset < line.Text.Length))
        {
            var shadow = SelectedTrack(tracks, line, AnimationProperty.SHADOW_BLUR, offset);
            if (shadow is null || !candidates.Contains(shadow.Target))
            {
                continue;
            }
            while (spanIndex < line.InlineSpans.Length &&
                line.InlineSpans[spanIndex].Utf16Start + line.InlineSpans[spanIndex].Utf16Length <= offset)
            {
                spanIndex++;
            }
            var span = spanIndex < line.InlineSpans.Length && line.InlineSpans[spanIndex].Utf16Start <= offset
                ? line.InlineSpans[spanIndex] : null;
            var style = span?.Style.ApplyTo(line.Style) ?? line.Style;
            var blurProperty = style.StrokeWidth > 0 ? AnimationProperty.STROKE_BLUR : AnimationProperty.FILL_BLUR;
            var blur = SelectedTrack(tracks, line, blurProperty, offset);
            var equivalent = false;
            if (blur is not null)
            {
                var pair = (shadow.Target, blur.Target);
                if (!comparisons.TryGetValue(pair, out equivalent))
                {
                    equivalent = EquivalentValues(shadow, blur);
                    comparisons.Add(pair, equivalent);
                }
            }
            if (!equivalent)
            {
                candidates.Remove(shadow.Target);
                continue;
            }
            represented.Add(shadow.Target);
        }
        candidates.IntersectWith(represented);
        return candidates;
    }

    private static AnimationTrack? SelectedTrack(Dictionary<AnimationTrackTarget, AnimationTrack> tracks,
        SubtitleLine line, AnimationProperty property, int offset,
        SubtitleAnimationState state = SubtitleAnimationState.NORMAL)
    {
        tracks.TryGetValue(new(property, State: state), out var selected);
        foreach (var range in line.AnimationRanges)
        {
            if (range.Utf16Start > offset || offset >= range.Utf16Start + range.Utf16Length)
            {
                continue;
            }
            if (tracks.TryGetValue(new(property, TextRangeId: range.Id, State: state), out var scoped))
            {
                selected = scoped;
            }
        }
        return selected;
    }

    private static bool EquivalentValues(AnimationTrack first, AnimationTrack second)
    {
        if (first.IsOrdered != second.IsOrdered)
        {
            return false;
        }
        return first.IsOrdered ? first.InitialValue == second.InitialValue && first.Transforms.Length == second.Transforms.Length &&
            first.Transforms.Zip(second.Transforms).All(pair => pair.First with { Id = pair.Second.Id } == pair.Second) :
            first.Keyframes.SequenceEqual(second.Keyframes);
    }

    private static bool Equivalent(SubtitleLine firstLine, ImmutableArray<AnimationTrack> first,
        SubtitleLine secondLine, ImmutableArray<AnimationTrack> second)
    {
        var firstGeometry = firstLine.AnimationRanges.Where(range => range.Scale != new ScenePoint(1, 1) || range.Rotation != 0)
            .Select(range => (range.Utf16Start, range.Utf16Length, range.Scale, range.Rotation));
        var secondGeometry = secondLine.AnimationRanges.Where(range => range.Scale != new ScenePoint(1, 1) || range.Rotation != 0)
            .Select(range => (range.Utf16Start, range.Utf16Length, range.Scale, range.Rotation));
        if (!firstGeometry.SequenceEqual(secondGeometry))
        {
            return false;
        }
        return EquivalentTracks(firstLine, first, secondLine, second);
    }

    private static bool EquivalentTracks(SubtitleLine firstLine, ImmutableArray<AnimationTrack> first,
        SubtitleLine secondLine, ImmutableArray<AnimationTrack> second)
    {
        if (first.Length != second.Length)
        {
            return false;
        }
        foreach (var track in first)
        {
            var scope = Scope(firstLine, track);
            var candidate = second.FirstOrDefault(candidate => Scope(secondLine, candidate) == scope);
            if (candidate is null || !EquivalentTrack(track, candidate))
            {
                return false;
            }
        }
        return true;
    }

    private static bool EquivalentFill(SubtitleLine firstLine, ImmutableArray<AnimationTrack> first,
        SubtitleLine secondLine, ImmutableArray<AnimationTrack> second)
    {
        var firstTracks = first.Where(track => track.Property == AnimationProperty.FILL).ToDictionary(track => track.Target);
        var secondTracks = second.Where(track => track.Property == AnimationProperty.FILL).ToDictionary(track => track.Target);
        var boundaries = new SortedSet<int> { 0, secondLine.Text.Length };
        foreach (var range in firstLine.AnimationRanges.Concat(secondLine.AnimationRanges))
        {
            boundaries.Add(range.Utf16Start);
            boundaries.Add(range.Utf16Start + range.Utf16Length);
        }
        var states = Enum.GetValues<SubtitleAnimationState>();
        var comparisons = new Dictionary<(AnimationTrackTarget First, AnimationTrackTarget Second), bool>();
        foreach (var offset in boundaries.Where(offset => offset < secondLine.Text.Length))
        {
            foreach (var state in states)
            {
                var firstTrack = SelectedTrack(firstTracks, firstLine, AnimationProperty.FILL, offset, state);
                var secondTrack = SelectedTrack(secondTracks, secondLine, AnimationProperty.FILL, offset, state);
                if (firstTrack is null || secondTrack is null)
                {
                    if (firstTrack is not null || secondTrack is not null)
                    {
                        return false;
                    }
                    continue;
                }
                var pair = (firstTrack.Target, secondTrack.Target);
                if (!comparisons.TryGetValue(pair, out var equivalent))
                {
                    equivalent = EquivalentTrack(firstTrack, secondTrack);
                    comparisons.Add(pair, equivalent);
                }
                if (!equivalent)
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static bool EquivalentTrack(AnimationTrack first, AnimationTrack second)
    {
        return first.ColorSpace == second.ColorSpace && first.InitialValue == second.InitialValue &&
            first.Keyframes.SequenceEqual(second.Keyframes) && first.Transforms.Length == second.Transforms.Length &&
            first.Transforms.Zip(second.Transforms).All(pair => pair.First with { Id = pair.Second.Id } == pair.Second);
    }

    private static (AnimationProperty Property, SubtitleAnimationState State, int Start, int Length) Scope(SubtitleLine line, AnimationTrack track)
    {
        var range = line.AnimationRanges.FirstOrDefault(range => range.Id == track.Target.TextRangeId);
        return (track.Property, track.Target.State, range?.Utf16Start ?? -1, range?.Utf16Length ?? -1);
    }
}
