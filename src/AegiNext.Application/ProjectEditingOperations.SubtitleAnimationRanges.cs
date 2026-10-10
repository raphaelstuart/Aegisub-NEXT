using AegiNext.Core.Projects;

namespace AegiNext.Application;

public static partial class ProjectEditingOperations
{
    /// <summary>创建或更新稳定文本动画范围，可显式调整覆盖顺序，保留既有动画轨道。</summary>
    public static ProjectDocument SetSubtitleAnimationRange(ProjectDocument document, Guid subtitleId,
        SubtitleAnimationRange range, int? index = null)
    {
        ArgumentNullException.ThrowIfNull(range);
        ProjectValidator.Validate(document);
        var lineIndex = SubtitleIndex(document, subtitleId);
        var line = document.Subtitles[lineIndex];
        SubtitleTextEditMap.ValidateRange(line.Text, new(line.Text), range.Utf16Start, range.Utf16Length);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(range.Utf16Length);
        var previousIndex = AnimationRangeIndex(line, range.Id, false);
        var ranges = line.AnimationRanges;
        if (previousIndex >= 0)
        {
            ranges = ranges.SetItem(previousIndex, range);
            if (index is { } destination)
            {
                ArgumentOutOfRangeException.ThrowIfNegative(destination);
                ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(destination, ranges.Length);
                ranges = ranges.RemoveAt(previousIndex).Insert(destination, range);
            }
        }
        else
        {
            var destination = index ?? ranges.Length;
            ArgumentOutOfRangeException.ThrowIfNegative(destination);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(destination, ranges.Length);
            ranges = ranges.Insert(destination, range);
        }

        return ranges.SequenceEqual(line.AnimationRanges) ? document :
            WithSubtitleContent(document, lineIndex, line with { AnimationRanges = ranges });
    }

    /// <summary>原子删除文本动画范围及所有引用它的状态和属性轨道。</summary>
    public static ProjectDocument RemoveSubtitleAnimationRange(ProjectDocument document, Guid subtitleId, Guid rangeId)
    {
        ProjectValidator.Validate(document);
        var lineIndex = SubtitleIndex(document, subtitleId);
        var line = document.Subtitles[lineIndex];
        var index = AnimationRangeIndex(line, rangeId);
        return WithSubtitleContent(document, lineIndex, line with
        {
            AnimationRanges = SubtitleAnimationRangeEditing.PruneOrphanedOrigins(line.AnimationRanges.RemoveAt(index))
        });
    }

    /// <summary>修改文本动画范围覆盖顺序，保留范围身份、静态变换和轨道。</summary>
    public static ProjectDocument MoveSubtitleAnimationRange(ProjectDocument document, Guid subtitleId, Guid rangeId, int index)
    {
        ProjectValidator.Validate(document);
        var lineIndex = SubtitleIndex(document, subtitleId);
        var line = document.Subtitles[lineIndex];
        var previousIndex = AnimationRangeIndex(line, rangeId);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, line.AnimationRanges.Length);
        return previousIndex == index ? document : WithSubtitleContent(document, lineIndex, line with
        {
            AnimationRanges = line.AnimationRanges.RemoveAt(previousIndex).Insert(index, line.AnimationRanges[previousIndex])
        });
    }

    private static int AnimationRangeIndex(SubtitleLine line, Guid rangeId, bool required = true)
    {
        for (var index = 0; index < line.AnimationRanges.Length; index++)
        {
            if (line.AnimationRanges[index].Id == rangeId)
            {
                return index;
            }
        }

        return required ? throw new KeyNotFoundException("文本动画范围不存在。") : -1;
    }
}
