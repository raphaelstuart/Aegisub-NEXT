using System.Collections.Immutable;
using System.Globalization;
using AegiNext.Core.Projects;
using AegiNext.Core.Editing;
using AegiNext.Core.Timing;

namespace AegiNext.Application;

/// <summary>平面轨道片段及字幕拆合的纯不可变事务，可直接传给 ProjectEditor.Apply。</summary>
public static partial class ProjectEditingOperations
{
    /// <summary>在严格内部播放头与字素边界拆分；左句保留标识，右句新建标识并延续原动画相位。</summary>
    public static ProjectDocument SplitSubtitle(ProjectDocument document, Guid subtitleId, MediaTime playhead, int utf16Offset)
    {
        ProjectValidator.Validate(document);
        var index = SubtitleIndex(document, subtitleId);
        var original = document.Subtitles[index];
        if (playhead <= original.Start || playhead >= original.End || utf16Offset <= 0 || utf16Offset >= original.Text.Length ||
            !StringInfo.ParseCombiningCharacters(original.Text).Contains(utf16Offset))
        {
            throw new ArgumentException("拆分必须位于字幕时间内部和完整字素边界。", nameof(utf16Offset));
        }

        var leftText = original.Text[..utf16Offset];
        var rightText = original.Text[utf16Offset..];
        if (string.IsNullOrWhiteSpace(leftText) || string.IsNullOrWhiteSpace(rightText))
        {
            throw new ArgumentException("拆分不能产生空白字幕。", nameof(utf16Offset));
        }

        var layer = SubtitleLayer(document.Layers, subtitleId);
        var contentTime = playhead - original.Start + layer.AnimationOffset;
        var (leftKaraoke, rightKaraoke) = SplitKaraoke(original.Karaoke, utf16Offset);
        var (leftInactiveKaraoke, rightInactiveKaraoke) = SplitKaraoke(original.InactiveKaraoke, utf16Offset);
        var rightRangeIds = original.AnimationRanges.Where(range => range.Utf16Start + range.Utf16Length > utf16Offset)
            .ToDictionary(range => range.Id, _ => Guid.NewGuid());

        var left = original with
        {
            End = playhead, Text = leftText, Karaoke = leftKaraoke, InactiveKaraoke = leftInactiveKaraoke,
            InlineSpans = SubtitleContentSplitMerge.SplitSpans(original.InlineSpans, utf16Offset, false),
            AnimationRanges = SubtitleAnimationRangeEditing.Split(original.AnimationRanges, utf16Offset, false),
            KaraokeStyleSpans = SubtitleContentSplitMerge.SplitKaraokeStyleSpans(original.KaraokeStyleSpans, utf16Offset, false),
            KaraokeStyle = !leftKaraoke.IsEmpty || !leftInactiveKaraoke.IsEmpty ? original.KaraokeStyle : null
        };
        var right = original with
        {
            Id = Guid.NewGuid(), Start = playhead, Text = rightText, Karaoke = rightKaraoke, InactiveKaraoke = rightInactiveKaraoke,
            InlineSpans = SubtitleContentSplitMerge.SplitSpans(original.InlineSpans, utf16Offset, true),
            AnimationRanges = SubtitleAnimationRangeEditing.Split(original.AnimationRanges, utf16Offset, true, rightRangeIds),
            KaraokeStyleSpans = SubtitleContentSplitMerge.SplitKaraokeStyleSpans(original.KaraokeStyleSpans, utf16Offset, true),
            KaraokeStyle = !rightKaraoke.IsEmpty || !rightInactiveKaraoke.IsEmpty ? original.KaraokeStyle : null
        };
        var layerIndex = document.Layers.IndexOf(layer);
        var layers = document.Layers
            .SetItem(layerIndex, LayerAnimationTiming.Clip(SubtitleAnimationRangeEditing.PruneTargets(layer, left) with { End = playhead }))
            .Insert(layerIndex + 1, LayerAnimationTiming.Clip(SubtitleAnimationRangeEditing.PruneTargets(layer with
            {
                Id = right.Id, SubtitleId = right.Id, Start = playhead,
                AnimationOffset = contentTime,
                Tracks = layer.Tracks.Where(track => track.Target.TextRangeId is not { } id || rightRangeIds.ContainsKey(id))
                    .Select(track => track with
                    {
                        Target = track.Target.TextRangeId is { } id ? track.Target with { TextRangeId = rightRangeIds[id] } : track.Target,
                        Transforms = track.Transforms.Select(operation => operation with { Id = Guid.NewGuid() }).ToImmutableArray()
                    }).ToImmutableArray()
            }, right)));
        return Verified(document with
        {
            Subtitles = document.Subtitles.SetItem(index, left)
                .Insert(index + 1, right),
            Layers = layers
        });
    }

    /// <summary>合并相邻字幕，以首句为基础样式并用局部覆盖保留后句外观与完整高亮时钟。</summary>
    public static ProjectDocument MergeSubtitles(ProjectDocument document, Guid firstId, Guid secondId, string separator = "\n")
    {
        ProjectValidator.Validate(document);
        ArgumentNullException.ThrowIfNull(separator);
        ProjectValidator.ValidateText(separator);
        var firstIndex = SubtitleIndex(document, firstId);
        var secondIndex = SubtitleIndex(document, secondId);
        var first = document.Subtitles[firstIndex];
        var second = document.Subtitles[secondIndex];
        var clips = new ProjectClipIndex(document);
        var firstLayer = clips.GetSubtitleClip(firstId);
        var secondLayer = clips.GetSubtitleClip(secondId);
        var next = clips.GetTrackClips(firstLayer.TrackId).FirstOrDefault(clip => clip.Start > first.Start);
        if (firstLayer.TrackId != secondLayer.TrackId || next?.SubtitleId != secondId)
        {
            throw new InvalidOperationException("只能合并同一字幕轨道按时间相邻的两句。");
        }
        if (first.End > second.Start)
        {
            throw new InvalidOperationException("重叠字幕不能合并为单个连续句。");
        }

        if (!HasNeutralVisuals(firstLayer) || !HasNeutralVisuals(secondLayer) ||
            first.AnimationRanges.Concat(second.AnimationRanges).Any(range => range.Scale != new ScenePoint(1, 1) ||
                range.Rotation != 0 || range.Offset != new ScenePoint(0, 0)))
        {
            throw new InvalidOperationException("包含片段动画、变换、蒙版或混合效果的字幕不能无损合并。");
        }

        var firstOrigin = first.Start - firstLayer.AnimationOffset;
        var secondOrigin = second.Start - secondLayer.AnimationOffset;
        var mergedOrigin = firstOrigin < secondOrigin ? firstOrigin : secondOrigin;
        var mergedOffset = first.Start - mergedOrigin;
        var firstHasKaraoke = !first.Karaoke.IsEmpty || !first.InactiveKaraoke.IsEmpty;
        var secondHasKaraoke = !second.Karaoke.IsEmpty || !second.InactiveKaraoke.IsEmpty;
        var compatibleKaraokeStyles = first.KaraokeStyle is null
            ? second.KaraokeStyle is null
            : first.KaraokeStyle.VisuallyEquals(second.KaraokeStyle);
        var preserveHighlight = firstHasKaraoke && secondHasKaraoke && !compatibleKaraokeStyles;
        var mergedKaraokeStyle = preserveHighlight ? null : firstHasKaraoke ? first.KaraokeStyle : second.KaraokeStyle;
        var secondTextOffset = checked(first.Text.Length + separator.Length);
        var secondRangeIds = second.AnimationRanges.ToDictionary(range => range.Id, _ => Guid.NewGuid());
        var firstKaraoke = RebaseKaraoke(first, first.Karaoke, firstLayer, mergedOrigin, 0);
        var firstInactiveKaraoke = RebaseKaraoke(first, first.InactiveKaraoke, firstLayer, mergedOrigin, 0);
        var secondKaraoke = RebaseKaraoke(second, second.Karaoke, secondLayer, mergedOrigin, secondTextOffset);
        var secondInactiveKaraoke = RebaseKaraoke(second, second.InactiveKaraoke, secondLayer, mergedOrigin, secondTextOffset);
        var clipIds = firstKaraoke.Concat(firstInactiveKaraoke).Select(clip => clip.Id).ToHashSet();
        secondKaraoke = DeduplicateKaraokeIds(secondKaraoke, clipIds);
        secondInactiveKaraoke = DeduplicateKaraokeIds(secondInactiveKaraoke, clipIds);
        var merged = first with
        {
            End = second.End,
            Text = first.Text + separator + second.Text,
            InlineSpans = SubtitleContentSplitMerge.MergeSpans(first, second, checked(first.Text.Length + separator.Length)),
            AnimationRanges = first.AnimationRanges.AddRange(SubtitleAnimationRangeEditing.Clone(
                second.AnimationRanges, secondRangeIds, secondTextOffset)),
            Karaoke = firstKaraoke.AddRange(secondKaraoke),
            InactiveKaraoke = firstInactiveKaraoke.AddRange(secondInactiveKaraoke),
            KaraokeStyleSpans = SubtitleContentSplitMerge.MergeKaraokeStyleSpans(first, second, secondTextOffset, mergedKaraokeStyle),
            KaraokeStyle = mergedKaraokeStyle
        };
        var layers = document.Layers.SetItem(document.Layers.IndexOf(firstLayer),
            firstLayer with { End = second.End, AnimationOffset = mergedOffset }).Remove(secondLayer);
        return Verified(document with
        {
            Subtitles = document.Subtitles.SetItem(firstIndex, merged).RemoveAt(secondIndex),
            Layers = layers
        });
    }

    private static (ImmutableArray<KaraokeSegment> Left, ImmutableArray<KaraokeSegment> Right) SplitKaraoke(
        ImmutableArray<KaraokeSegment> segments, int utf16Offset)
    {
        var left = ImmutableArray.CreateBuilder<KaraokeSegment>();
        var right = ImmutableArray.CreateBuilder<KaraokeSegment>();
        foreach (var segment in segments)
        {
            var end = checked(segment.Utf16Start + segment.Utf16Length);
            if (end <= utf16Offset)
            {
                left.Add(segment);
            }
            else if (segment.Utf16Start >= utf16Offset)
            {
                right.Add(segment with { Utf16Start = segment.Utf16Start - utf16Offset });
            }
            else
            {
                throw new InvalidOperationException("请先显式拆分跨越分句位置的计时组。");
            }
        }

        return (left.ToImmutable(), right.ToImmutable());
    }

    private static ImmutableArray<KaraokeSegment> RebaseKaraoke(SubtitleLine line, ImmutableArray<KaraokeSegment> segments,
        ProjectLayer layer, MediaTime mergedOrigin, int textOffset)
    {
        var result = ImmutableArray.CreateBuilder<KaraokeSegment>();
        var offset = line.Start - layer.AnimationOffset - mergedOrigin;
        foreach (var segment in segments)
        {
            result.Add(segment with
            {
                Utf16Start = checked(segment.Utf16Start + textOffset), Start = segment.Start + offset, End = segment.End + offset
            });
        }

        return result.ToImmutable();
    }

    private static ImmutableArray<KaraokeSegment> DeduplicateKaraokeIds(ImmutableArray<KaraokeSegment> segments, HashSet<Guid> ids)
    {
        return segments.Select(segment =>
        {
            if (ids.Add(segment.Id))
            {
                return segment;
            }

            Guid id;
            do
            {
                id = Guid.NewGuid();
            }
            while (!ids.Add(id));
            return segment with { Id = id };
        }).ToImmutableArray();
    }

    private static bool HasNeutralVisuals(ProjectLayer layer)
    {
        return layer.Transform == new LayerTransform() && layer.Opacity.Equals(1d) && layer.Blend == BlendMode.NORMAL &&
            layer.Blur == 0 && layer.Tracks.IsEmpty && layer.MotionPath is null && layer.Mask is null;
    }

    private static int SubtitleIndex(ProjectDocument document, Guid id)
    {
        for (var index = 0; index < document.Subtitles.Length; index++)
        {
            if (document.Subtitles[index].Id == id)
            {
                return index;
            }
        }

        throw new KeyNotFoundException("字幕不存在。");
    }

    private static ProjectLayer SubtitleLayer(ImmutableArray<ProjectLayer> layers, Guid subtitleId)
    {
        return layers.First(layer => layer.SubtitleId == subtitleId);
    }

    private static ProjectDocument Verified(ProjectDocument document)
    {
        ProjectValidator.Validate(document);
        return document;
    }
}
