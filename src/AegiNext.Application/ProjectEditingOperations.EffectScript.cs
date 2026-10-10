using System.Collections.Immutable;
using AegiNext.Core.Effects;
using AegiNext.Core.Projects;

namespace AegiNext.Application;

public static partial class ProjectEditingOperations
{
    /// <summary>在冻结快照上编译并组合目标片段的范围及轨道；取消或任一目标失败不发布部分结果。</summary>
    public static ProjectDocument ApplyEffectScript(ProjectDocument document, IReadOnlyCollection<Guid> layerIds,
        EffectScript script, CancellationToken cancellationToken = default)
    {
        return ApplyEffectScript(document, layerIds, script, null, cancellationToken);
    }

    /// <summary>按完整文字范围和视觉状态上下文原子应用脚本；范围限制单字幕，取消或诊断失败不发布部分结果。</summary>
    public static ProjectDocument ApplyEffectScript(ProjectDocument document, IReadOnlyCollection<Guid> layerIds,
        EffectScript script, AnimationTrackTarget? targetContext, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layerIds);
        ArgumentNullException.ThrowIfNull(script);
        cancellationToken.ThrowIfCancellationRequested();
        ProjectValidator.Validate(document);
        if (targetContext is { } context)
        {
            try
            {
                SubtitleAnimationTargetValidation.ValidateIdentity(context);
            }
            catch (InvalidDataException error)
            {
                throw new EffectScriptException(error.Message, innerException: error);
            }
            if (context.NodeId.HasValue)
            {
                throw new EffectScriptException("脚本上下文只指定文字范围和视觉状态；蒙版节点由脚本选择器指定。");
            }
        }
        var remaining = layerIds.ToHashSet();
        if (targetContext?.TextRangeId.HasValue == true && remaining.Count != 1)
        {
            throw new EffectScriptException("文字范围脚本必须应用于一个字幕片段。");
        }
        if (remaining.Count == 0)
        {
            return document;
        }
        var subtitles = document.Subtitles.ToDictionary(line => line.Id);
        var subtitlesChanged = false;
        var layers = MapTrackLayers(document.Layers, layer =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!remaining.Remove(layer.Id))
            {
                return layer;
            }
            var subtitle = layer.SubtitleId is { } id ? subtitles[id] : null;
            var compilation = EffectScriptComposer.ComposeTarget(script, layer, subtitle?.Style, targetContext, subtitle);
            cancellationToken.ThrowIfCancellationRequested();
            if (compilation.Subtitle is { } updated && updated != subtitle)
            {
                subtitles[updated.Id] = updated;
                subtitlesChanged = true;
            }
            var prepared = compilation.PreparedLayer ?? layer;
            return compilation.Tracks == prepared.Tracks ? prepared : prepared with { Tracks = compilation.Tracks };
        });
        if (remaining.Count > 0)
        {
            throw new KeyNotFoundException("图层不存在。");
        }
        cancellationToken.ThrowIfCancellationRequested();
        var result = layers == document.Layers && !subtitlesChanged ? document : document with
        {
            Layers = layers,
            Subtitles = subtitlesChanged ? document.Subtitles.Select(line => subtitles[line.Id]).ToImmutableArray() : document.Subtitles
        };
        ProjectValidator.Validate(result);
        return result;
    }
}
