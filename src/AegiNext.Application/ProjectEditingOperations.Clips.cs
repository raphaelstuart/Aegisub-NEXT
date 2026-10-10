using System.Collections.Immutable;
using AegiNext.Core.Projects;
using AegiNext.Core.Timing;

namespace AegiNext.Application;

public static partial class ProjectEditingOperations
{
    /// <summary>平移选中字幕、形状和图片的绝对时间；全部成员准备后一次校验，保留相对内容时钟。</summary>
    public static ProjectDocument ShiftClips(ProjectDocument document, IReadOnlyCollection<Guid> layerIds, MediaTime offset)
    {
        ProjectValidator.Validate(document);
        var selected = SelectClipLayers(document, layerIds);
        if (selected.IsEmpty || offset == MediaTime.Zero)
        {
            return document;
        }

        if (selected.Any(layer => layer.Start + offset < MediaTime.Zero))
        {
            throw new InvalidDataException("片段不能移动到工程零秒之前。");
        }

        var selection = selected.Select(layer => layer.Id).ToHashSet();
        var subtitles = selected.Where(layer => layer.SubtitleId.HasValue).Select(layer => layer.SubtitleId!.Value).ToHashSet();
        return Verified(document with
        {
            Subtitles = document.Subtitles.Select(line => subtitles.Contains(line.Id)
                ? line with { Start = line.Start + offset, End = line.End + offset } : line).ToImmutableArray(),
            Layers = MapTrackLayers(document.Layers, layer => selection.Contains(layer.Id)
                ? layer with { Start = layer.Start + offset, End = layer.End + offset } : layer)
        });
    }

    /// <summary>按合成顺序捕获片段、字幕行及源轨道顺序，以指定轨道或主选择片段轨道为复制基准。</summary>
    public static ClipClipboardContent CaptureClips(ProjectDocument document, IReadOnlyCollection<Guid> layerIds, Guid primaryId,
        Guid? referenceTrackId = null)
    {
        ProjectValidator.Validate(document);
        var selected = SelectClipLayers(document, layerIds);
        if (selected.IsEmpty || selected.All(layer => layer.Id != primaryId))
        {
            throw new ArgumentException("复制需要非空片段集合及位于其中的主选择。", nameof(primaryId));
        }

        var subtitles = selected.Where(layer => layer.SubtitleId.HasValue).Select(layer => layer.SubtitleId!.Value).ToHashSet();
        var lines = document.Subtitles.Where(line => subtitles.Contains(line.Id)).ToImmutableArray();
        if (referenceTrackId is { } reference)
        {
            TrackIndex(document, reference);
        }
        else
        {
            referenceTrackId = selected.Single(clip => clip.Id == primaryId).TrackId;
        }

        var tagIds = lines.Where(line => line.ColorTagId.HasValue).Select(line => line.ColorTagId!.Value).ToHashSet();
        return new(document.Id, primaryId, selected.Min(layer => layer.Start), selected,
            lines, document.Tracks.Select(track => track.Id).ToImmutableArray(), referenceTrackId)
        {
            ColorTags = document.ColorTags.Where(tag => tagIds.Contains(tag.Id)).ToImmutableArray()
        };
    }

    /// <summary>将最早起点对齐指定时间，保留原轨或将冻结的源基准对齐目标轨；越界或碰撞整批拒绝。</summary>
    public static ClipPasteResult PasteClips(ProjectDocument document, ClipClipboardContent content, MediaTime start,
        Guid? targetTrackId = null)
    {
        ProjectValidator.Validate(document);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfLessThan(start, MediaTime.Zero);
        var mappedLayers = ValidateClipClipboard(document, content, targetTrackId);
        var colorTags = document.ColorTags.ToBuilder();
        var colorTagIds = ImportColorTags(colorTags, content.ColorTags);

        var offset = start - content.EarliestStart;
        var subtitleIds = content.Subtitles.ToDictionary(line => line.Id, _ => Guid.NewGuid());
        var rangeIds = content.Subtitles.ToDictionary(line => line.Id,
            line => line.AnimationRanges.ToDictionary(range => range.Id, _ => Guid.NewGuid()));
        var layerIds = content.Layers.ToDictionary(layer => layer.Id,
            layer => layer.SubtitleId is { } subtitleId && layer.Id == subtitleId ? subtitleIds[subtitleId] : Guid.NewGuid());
        var subtitles = content.Subtitles.Select(line => line with
        {
            Id = subtitleIds[line.Id], Start = line.Start + offset, End = line.End + offset,
            ColorTagId = line.ColorTagId is { } tagId ? colorTagIds[tagId] : null,
            AnimationRanges = SubtitleAnimationRangeEditing.Clone(line.AnimationRanges, rangeIds[line.Id]),
            Karaoke = line.Karaoke.Select(clip => clip with { Id = Guid.NewGuid() }).ToImmutableArray(),
            InactiveKaraoke = line.InactiveKaraoke.Select(clip => clip with { Id = Guid.NewGuid() }).ToImmutableArray()
        }).ToImmutableArray();
        var layers = mappedLayers.Select(layer => CloneClipboardLayer(layer, layerIds[layer.Id], subtitleIds, rangeIds, offset)).ToImmutableArray();
        var result = Verified(document with
        {
            Subtitles = document.Subtitles.AddRange(subtitles),
            ColorTags = colorTags.Count == document.ColorTags.Length ? document.ColorTags : colorTags.ToImmutable(),
            Layers = document.Layers.AddRange(layers)
        });
        return new(result, layers.Select(layer => layer.Id).ToImmutableArray(), layerIds[content.PrimaryId]);
    }

    /// <summary>删除全部所选字幕、形状和图片，同步清理字幕行；不删除可供撤销或其他片段使用的资源。</summary>
    public static ProjectDocument RemoveClips(ProjectDocument document, IReadOnlyCollection<Guid> layerIds)
    {
        ProjectValidator.Validate(document);
        var selected = SelectClipLayers(document, layerIds);
        if (selected.IsEmpty)
        {
            return document;
        }

        var selection = selected.Select(layer => layer.Id).ToHashSet();
        var subtitles = selected.Where(layer => layer.SubtitleId.HasValue).Select(layer => layer.SubtitleId!.Value).ToHashSet();
        return Verified(document with
        {
            Subtitles = document.Subtitles.Where(line => !subtitles.Contains(line.Id)).ToImmutableArray(),
            Layers = RemoveSelectedClipLayers(document.Layers, selection)
        });
    }

    private static ImmutableArray<ProjectLayer> SelectClipLayers(ProjectDocument document, IReadOnlyCollection<Guid> layerIds)
    {
        ArgumentNullException.ThrowIfNull(layerIds);
        var selection = layerIds.ToHashSet();
        var layers = new ProjectClipIndex(document).LayersInDrawingOrder.Where(layer => selection.Contains(layer.Id)).ToImmutableArray();
        if (layers.Length != selection.Count)
        {
            throw new KeyNotFoundException("所选片段不存在。");
        }

        foreach (var layer in layers)
        {
            RequireSupportedClip(layer);
        }

        return layers;
    }

    private static void RequireSupportedClip(ProjectLayer layer)
    {
        if (layer.Kind is not (LayerKind.SUBTITLE or LayerKind.SHAPE or LayerKind.IMAGE))
        {
            throw new InvalidOperationException("片段操作只支持字幕、形状和图片。");
        }
    }

    private static ImmutableArray<ProjectLayer> ValidateClipClipboard(ProjectDocument document, ClipClipboardContent content,
        Guid? targetTrackId)
    {
        if (content.SourceProjectId != document.Id)
        {
            throw new InvalidOperationException("片段剪贴板只能粘贴到来源工程。");
        }

        if (content.Layers.IsDefaultOrEmpty || content.Subtitles.IsDefault ||
            content.Layers.Any(layer => layer is null) || content.Subtitles.Any(line => line is null) ||
            !content.Layers.Any(layer => layer.Id == content.PrimaryId))
        {
            throw new InvalidDataException("片段剪贴板内容或主选择无效。");
        }

        var layers = targetTrackId is { } target
            ? MapClipboardTracks(document, content, target) : content.Layers;
        ProjectValidator.Validate(document with { Layers = layers, Subtitles = content.Subtitles, ColorTags = content.ColorTags });
        foreach (var layer in layers)
        {
            RequireSupportedClip(layer);
        }

        var referenced = content.Layers.Where(layer => layer.SubtitleId.HasValue).Select(layer => layer.SubtitleId!.Value).ToHashSet();
        if (referenced.Count != content.Subtitles.Length || content.Subtitles.Any(line => !referenced.Contains(line.Id)) ||
            content.EarliestStart != content.Layers.Min(layer => layer.Start))
        {
            throw new InvalidDataException("片段剪贴板的字幕引用或起点无效。");
        }

        return layers;
    }

    private static ImmutableArray<ProjectLayer> MapClipboardTracks(ProjectDocument document, ClipClipboardContent content,
        Guid targetTrackId)
    {
        var targetIndex = TrackIndex(document, targetTrackId);
        if (content.SourceTrackIds.IsDefaultOrEmpty || content.SourceTrackIds.Any(id => id == Guid.Empty) ||
            content.SourceTrackIds.Distinct().Count() != content.SourceTrackIds.Length)
        {
            throw new InvalidDataException("跨轨粘贴需要完整且唯一的源轨道顺序。");
        }

        var sourceIndices = content.SourceTrackIds.Select((id, index) => (id, index)).ToDictionary(pair => pair.id, pair => pair.index);
        var referenceTrackId = content.ReferenceTrackId ?? content.Layers.Single(clip => clip.Id == content.PrimaryId).TrackId;
        if (!sourceIndices.TryGetValue(referenceTrackId, out var anchorIndex) ||
            content.Layers.Any(clip => !sourceIndices.ContainsKey(clip.TrackId)))
        {
            throw new InvalidDataException("片段剪贴板的源轨道或基准不在捕获的轨道顺序中。");
        }

        return content.Layers.Select(clip =>
        {
            var mappedIndex = targetIndex + sourceIndices[clip.TrackId] - anchorIndex;
            if (mappedIndex < 0 || mappedIndex >= document.Tracks.Length)
            {
                throw new InvalidOperationException("粘贴片段的轨道范围超出当前轨道。");
            }

            return clip with { TrackId = document.Tracks[mappedIndex].Id };
        }).ToImmutableArray();
    }

    private static ProjectLayer CloneClipboardLayer(ProjectLayer layer, Guid id, Dictionary<Guid, Guid> subtitleIds,
        Dictionary<Guid, Dictionary<Guid, Guid>> rangeIds, MediaTime offset)
    {
        var maskIds = layer.Mask is VectorClipMask vector
            ? vector.Contours.SelectMany(contour => contour.Nodes.Select(node => node.Id).Prepend(contour.Id))
                .ToDictionary(identity => identity, _ => Guid.NewGuid())
            : new Dictionary<Guid, Guid>();
        var mask = layer.Mask is VectorClipMask source ? source with
        {
            Contours = source.Contours.Select(contour => contour with
            {
                Id = maskIds[contour.Id],
                Nodes = contour.Nodes.Select(node => node with { Id = maskIds[node.Id] }).ToImmutableArray()
            }).ToImmutableArray()
        } : layer.Mask;

        return layer with
        {
            Id = id, SubtitleId = layer.SubtitleId is { } subtitleId ? subtitleIds[subtitleId] : null,
            Start = layer.Start + offset, End = layer.End + offset, Mask = mask,
            Tracks = layer.Tracks.Select(track => track with
            {
                Target = track.Target with
                {
                    NodeId = track.Target.NodeId is { } nodeId ? maskIds[nodeId] : null,
                    TextRangeId = track.Target.TextRangeId is { } rangeId ? rangeIds[layer.SubtitleId!.Value][rangeId] : null
                },
                Transforms = track.Transforms.Select(operation => operation with { Id = Guid.NewGuid() }).ToImmutableArray()
            }).ToImmutableArray()
        };
    }

    private static ImmutableArray<ProjectLayer> RemoveSelectedClipLayers(ImmutableArray<ProjectLayer> layers, HashSet<Guid> selection)
    {
        return layers.Where(layer => !selection.Contains(layer.Id)).ToImmutableArray();
    }
}
