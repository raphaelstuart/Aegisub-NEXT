using System.Collections.Immutable;
using System.Globalization;
using AegiNext.Core.Projects;

namespace AegiNext.Application;

public static partial class ProjectEditingOperations
{
    /// <summary>收集工程内容实际使用的字体和图片身份；包含轨道默认字体、整行与局部字体及图片片段。</summary>
    public static IReadOnlyCollection<Guid> GetMergeAssetIds(ProjectDocument document)
    {
        ProjectValidator.Validate(document);
        return CollectMergeAssetIds(document);
    }

    /// <summary>合并多个工程的完整内容副本，保留绝对时间与目标设置；资源路径必须已准备为目标工程路径。</summary>
    public static ProjectMergeResult MergeProjects(ProjectDocument document, IReadOnlyList<ProjectMergeSource> sources)
    {
        ProjectValidator.Validate(document);
        ArgumentNullException.ThrowIfNull(sources);
        var importedSources = sources.ToArray();
        foreach (var source in importedSources)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentException.ThrowIfNullOrWhiteSpace(source.Name);
            ProjectValidator.ValidateText(source.Name);
            if (source.Name.Any(char.IsControl))
            {
                throw new ArgumentException("合并来源名称不能包含控制字符。", nameof(sources));
            }

            ProjectValidator.Validate(source.Document);
            if (source.Document.Width != document.Width || source.Document.Height != document.Height ||
                !source.Document.ReferenceWhiteNits.Equals(document.ReferenceWhiteNits))
            {
                throw new InvalidDataException("合并工程必须具有相同的画布尺寸和参考白亮度。");
            }
        }

        if (importedSources.Length == 0)
        {
            return new(document, [], [], []);
        }

        ValidateMergeBudget(document, importedSources);
        var reservedIds = new HashSet<Guid>();
        ReserveMergeIds(document, reservedIds);
        foreach (var source in importedSources)
        {
            ReserveMergeIds(source.Document, reservedIds);
        }

        var assets = document.Assets.ToBuilder();
        var tracks = document.Tracks.ToBuilder();
        var subtitles = document.Subtitles.ToBuilder();
        var layers = document.Layers.ToBuilder();
        var presets = document.Presets.ToBuilder();
        var colorTags = document.ColorTags.ToBuilder();
        var importedTrackIds = ImmutableArray.CreateBuilder<Guid>();
        var importedLayerIds = ImmutableArray.CreateBuilder<Guid>();
        var importedSubtitleIds = ImmutableArray.CreateBuilder<Guid>();
        var names = document.Tracks.Select(track => track.Name).ToHashSet(StringComparer.Ordinal);
        var reusableAssets = new Dictionary<(ProjectAssetKind Kind, string Hash, string Path), Guid>();
        foreach (var asset in document.Assets)
        {
            if (GetMergeAssetKey(asset) is { } key)
            {
                reusableAssets.TryAdd(key, asset.Id);
            }
        }
        foreach (var source in importedSources)
        {
            var colorTagIds = ImportColorTags(colorTags, source.Document.ColorTags, reservedIds);
            var dependencyIds = CollectMergeAssetIds(source.Document).ToHashSet();
            var assetIds = new Dictionary<Guid, Guid>();
            var trackIds = source.Document.Tracks.ToDictionary(track => track.Id, _ => NewMergeId(reservedIds));
            var subtitleIds = source.Document.Subtitles.ToDictionary(line => line.Id, _ => NewMergeId(reservedIds));
            var rangeIds = source.Document.Subtitles.ToDictionary(line => line.Id,
                line => line.AnimationRanges.ToDictionary(range => range.Id, _ => NewMergeId(reservedIds)));
            foreach (var asset in source.Document.Assets.Where(asset => dependencyIds.Contains(asset.Id)))
            {
                var key = GetMergeAssetKey(asset);
                if (key is { } existingKey && reusableAssets.TryGetValue(existingKey, out var existingId))
                {
                    assetIds.Add(asset.Id, existingId);
                    continue;
                }
                var id = NewMergeId(reservedIds);
                assetIds.Add(asset.Id, id);
                assets.Add(asset with { Id = id });
                if (key is { } newKey)
                {
                    reusableAssets.Add(newKey, id);
                }
            }

            var sourceTracks = ImmutableArray.CreateBuilder<ProjectTrack>();
            foreach (var track in source.Document.Tracks)
            {
                var id = trackIds[track.Id];
                sourceTracks.Add(track with
                {
                    Id = id,
                    Name = CreateMergeTrackName(source.Name, track.Name, names),
                    DefaultStyle = track.DefaultStyle is { } style ? RemapMergeStyle(style, assetIds) : null
                });
                importedTrackIds.Add(id);
            }

            tracks.InsertRange(0, sourceTracks);

            foreach (var line in source.Document.Subtitles)
            {
                var id = subtitleIds[line.Id];
                subtitles.Add(line with
                {
                    Id = id,
                    AnimationRanges = SubtitleAnimationRangeEditing.Clone(line.AnimationRanges, rangeIds[line.Id]),
                    ColorTagId = line.ColorTagId is { } tagId ? colorTagIds[tagId] : null,
                    Style = RemapMergeStyle(line.Style, assetIds),
                    InlineSpans = line.InlineSpans.Select(span => span with
                    {
                        Style = span.Style.FontAssetId is { } fontId
                            ? span.Style with { FontAssetId = assetIds[fontId] } : span.Style
                    }).ToImmutableArray(),
                    Karaoke = line.Karaoke.Select(segment => segment with { Id = NewMergeId(reservedIds) }).ToImmutableArray(),
                    InactiveKaraoke = line.InactiveKaraoke.Select(segment => segment with { Id = NewMergeId(reservedIds) }).ToImmutableArray()
                });
                importedSubtitleIds.Add(id);
            }

            foreach (var layer in source.Document.Layers)
            {
                layers.Add(CloneMergeLayer(layer, trackIds, subtitleIds, rangeIds, assetIds, reservedIds, importedLayerIds));
            }

            foreach (var preset in source.Document.Presets)
            {
                var nodeIds = preset.Tracks.Where(track => track.Target.NodeId.HasValue)
                    .Select(track => track.Target.NodeId!.Value).Distinct()
                    .ToDictionary(id => id, _ => NewMergeId(reservedIds));
                presets.Add(preset with
                {
                    Id = NewMergeId(reservedIds),
                    Tracks = CloneMergeAnimationTracks(preset.Tracks, nodeIds, reservedIds)
                });
            }
        }

        var merged = document with
        {
            Assets = assets.ToImmutable(), Tracks = tracks.ToImmutable(), Subtitles = subtitles.ToImmutable(),
            Layers = layers.ToImmutable(), Presets = presets.ToImmutable(),
            ColorTags = colorTags.Count == document.ColorTags.Length ? document.ColorTags : colorTags.ToImmutable()
        };
        ProjectValidator.Validate(merged);
        return new(merged, importedTrackIds.ToImmutable(), importedLayerIds.ToImmutable(), importedSubtitleIds.ToImmutable());
    }

    private static Guid[] CollectMergeAssetIds(ProjectDocument document)
    {
        var ids = new HashSet<Guid>();
        foreach (var track in document.Tracks)
        {
            if (track.DefaultStyle?.FontAssetId is { } fontId)
            {
                ids.Add(fontId);
            }
        }

        foreach (var line in document.Subtitles)
        {
            if (line.Style.FontAssetId is { } fontId)
            {
                ids.Add(fontId);
            }
            foreach (var span in line.InlineSpans)
            {
                if (span.Style.FontAssetId is { } inlineFontId)
                {
                    ids.Add(inlineFontId);
                }
            }
        }

        foreach (var layer in document.Layers)
        {
            if (layer.Image is { } image)
            {
                ids.Add(image.AssetId);
            }
        }
        return document.Assets.Where(asset => ids.Contains(asset.Id)).Select(asset => asset.Id).ToArray();
    }

    private static void ValidateMergeBudget(ProjectDocument document, ProjectMergeSource[] sources)
    {
        var assets = (long)document.Assets.Length;
        var tracks = (long)document.Tracks.Length;
        var subtitles = (long)document.Subtitles.Length;
        var layers = (long)document.Layers.Length;
        var presets = (long)document.Presets.Length;
        var assetKeys = new HashSet<(ProjectAssetKind Kind, string Hash, string Path)>();
        foreach (var asset in document.Assets)
        {
            if (GetMergeAssetKey(asset) is { } key)
            {
                assetKeys.Add(key);
            }
        }
        foreach (var source in sources)
        {
            var dependencies = CollectMergeAssetIds(source.Document).ToHashSet();
            foreach (var asset in source.Document.Assets.Where(asset => dependencies.Contains(asset.Id)))
            {
                if (GetMergeAssetKey(asset) is { } key && !assetKeys.Add(key))
                {
                    continue;
                }
                assets++;
            }
            tracks += source.Document.Tracks.Length;
            subtitles += source.Document.Subtitles.Length;
            layers += source.Document.Layers.Length;
            presets += source.Document.Presets.Length;
            if (assets > 10000 || tracks > 10000 || subtitles > 100000 || layers > 10000 || presets > 10000)
            {
                throw new InvalidDataException("合并后工程集合超过数量预算。");
            }
        }
    }

    private static (ProjectAssetKind Kind, string Hash, string Path)? GetMergeAssetKey(ProjectAsset asset)
    {
        return asset.Kind is ProjectAssetKind.FONT or ProjectAssetKind.IMAGE && asset.Sha256 is { } hash
            ? (asset.Kind, hash.ToUpperInvariant(), asset.RelativePath) : null;
    }

    private static SubtitleStyle RemapMergeStyle(SubtitleStyle style, Dictionary<Guid, Guid> assetIds)
    {
        return style.FontAssetId is { } fontId ? style with { FontAssetId = assetIds[fontId] } : style;
    }

    private static ProjectLayer CloneMergeLayer(ProjectLayer layer, Dictionary<Guid, Guid> trackIds, Dictionary<Guid, Guid> subtitleIds,
        Dictionary<Guid, Dictionary<Guid, Guid>> rangeIds, Dictionary<Guid, Guid> assetIds,
        HashSet<Guid> reservedIds, ImmutableArray<Guid>.Builder importedLayerIds)
    {
        var id = layer.SubtitleId is { } subtitleId && layer.Id == subtitleId
            ? subtitleIds[subtitleId] : NewMergeId(reservedIds);
        importedLayerIds.Add(id);
        var maskIds = layer.Mask is VectorClipMask vector
            ? vector.Contours.SelectMany(contour => contour.Nodes.Select(node => node.Id).Prepend(contour.Id))
                .ToDictionary(identity => identity, _ => NewMergeId(reservedIds))
            : new Dictionary<Guid, Guid>();
        var mask = layer.Mask is VectorClipMask sourceMask ? sourceMask with
        {
            Contours = sourceMask.Contours.Select(contour => contour with
            {
                Id = maskIds[contour.Id],
                Nodes = contour.Nodes.Select(node => node with { Id = maskIds[node.Id] }).ToImmutableArray()
            }).ToImmutableArray()
        } : layer.Mask;
        return layer with
        {
            Id = id,
            TrackId = trackIds[layer.TrackId],
            SubtitleId = layer.SubtitleId is { } referencedId ? subtitleIds[referencedId] : null,
            Image = layer.Image is { } image ? image with { AssetId = assetIds[image.AssetId] } : null,
            Mask = mask,
            Tracks = CloneMergeAnimationTracks(layer.Tracks, maskIds, reservedIds,
                layer.SubtitleId is { } referencedSubtitle ? rangeIds[referencedSubtitle] : null)
        };
    }

    private static ImmutableArray<AnimationTrack> CloneMergeAnimationTracks(ImmutableArray<AnimationTrack> tracks,
        Dictionary<Guid, Guid> nodeIds, HashSet<Guid> reservedIds, Dictionary<Guid, Guid>? rangeIds = null)
    {
        return tracks.Select(track => track with
        {
            Target = track.Target with
            {
                NodeId = track.Target.NodeId is { } nodeId ? nodeIds[nodeId] : null,
                TextRangeId = track.Target.TextRangeId is { } rangeId ? rangeIds![rangeId] : null
            },
            Transforms = track.Transforms.Select(operation => operation with { Id = NewMergeId(reservedIds) }).ToImmutableArray()
        }).ToImmutableArray();
    }

    private static void ReserveMergeIds(ProjectDocument document, HashSet<Guid> ids)
    {
        ids.Add(document.Id);
        ids.UnionWith(document.Assets.Select(asset => asset.Id));
        ids.UnionWith(document.Tracks.Select(track => track.Id));
        ids.UnionWith(document.ColorTags.Select(tag => tag.Id));
        foreach (var line in document.Subtitles)
        {
            ids.Add(line.Id);
            ids.UnionWith(line.AnimationRanges.Select(range => range.Id));
            ids.UnionWith(line.Karaoke.Concat(line.InactiveKaraoke).Select(segment => segment.Id));
        }
        foreach (var layer in document.Layers)
        {
            ids.Add(layer.Id);
            ids.UnionWith(layer.Tracks.SelectMany(track => track.Transforms).Select(operation => operation.Id));
            if (layer.Mask is VectorClipMask mask)
            {
                ids.UnionWith(mask.Contours.SelectMany(contour => contour.Nodes.Select(node => node.Id).Prepend(contour.Id)));
            }
        }
        foreach (var preset in document.Presets)
        {
            ids.Add(preset.Id);
            ids.UnionWith(preset.Tracks.SelectMany(track => track.Transforms).Select(operation => operation.Id));
            ids.UnionWith(preset.Tracks.Where(track => track.Target.NodeId.HasValue).Select(track => track.Target.NodeId!.Value));
        }
    }

    private static Guid NewMergeId(HashSet<Guid> ids)
    {
        Guid id;
        do
        {
            id = Guid.NewGuid();
        }
        while (!ids.Add(id));
        return id;
    }

    private static string CreateMergeTrackName(string sourceName, string trackName, HashSet<string> names)
    {
        var desired = sourceName + "／" + trackName;
        for (var index = 1; ; index++)
        {
            var suffix = index == 1 ? string.Empty : " (" + index.ToString(CultureInfo.InvariantCulture) + ")";
            var prefix = TruncateMergeName(desired, 128 - suffix.Length);
            if (prefix.Length == 0)
            {
                prefix = TruncateMergeName(trackName, 128 - suffix.Length);
            }
            if (prefix.Length == 0)
            {
                prefix = "字幕";
            }
            var candidate = prefix + suffix;
            if (names.Add(candidate))
            {
                return candidate;
            }
        }
    }

    private static string TruncateMergeName(string value, int maximumLength)
    {
        if (value.Length <= maximumLength)
        {
            return value;
        }
        var elements = StringInfo.GetTextElementEnumerator(value);
        var length = 0;
        while (elements.MoveNext())
        {
            var end = elements.ElementIndex + elements.GetTextElement().Length;
            if (end > maximumLength)
            {
                break;
            }
            length = end;
        }
        return value[..length];
    }
}
