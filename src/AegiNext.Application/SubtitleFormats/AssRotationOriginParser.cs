using AegiNext.Core.Projects;

namespace AegiNext.Application.SubtitleFormats;

internal sealed class AssRotationOriginParser(Guid subtitleId, double scaleX, double scaleY)
{
    private readonly List<SubtitleFormatDiagnostic> diagnostics = [];

    internal ScenePoint? Origin { get; private set; }
    internal int SourceStart { get; private set; }
    internal int SourceLength { get; private set; }
    internal IEnumerable<SubtitleFormatDiagnostic> Diagnostics => diagnostics;

    internal void Apply(string value, int sourceStart, int sourceLength)
    {
        if (Origin.HasValue)
        {
            return;
        }
        try
        {
            var arguments = AssOverrideTags.Arguments(value);
            if (arguments.Length != 2)
            {
                throw new InvalidDataException("ASS org 必须有两个坐标。");
            }
            var x = AssFormatValues.Number(arguments[0]);
            var y = AssFormatValues.Number(arguments[1]);
            Origin = new(x * scaleX, y * scaleY);
            SourceStart = sourceStart;
            SourceLength = sourceLength;
        }
        catch (InvalidDataException)
        {
            diagnostics.Add(new("Ass.RotationOrigin", "ASS 旋转原点参数无效，已忽略该标签并保留其他内容。",
                sourceStart, sourceLength, subtitleId));
        }
    }

    internal void CollectTransform(string value, int sourceStart, int sourceLength)
    {
        var pending = new Stack<AssOverrideTag>();
        pending.Push(new("t", value, 0, 0));
        while (pending.TryPop(out var tag) && !Origin.HasValue)
        {
            if (tag.Name == "org")
            {
                Apply(tag.Value, sourceStart, sourceLength);
                continue;
            }
            var tagList = TransformTagList(tag.Value);
            if (tagList is null)
            {
                continue;
            }
            foreach (var child in AssOverrideTags.Parse(tagList).Reverse())
            {
                if (child.Name is "org" or "t")
                {
                    pending.Push(child);
                }
            }
        }
    }

    internal static bool ContainsOnlyOrigins(string value)
    {
        var pending = new Stack<AssOverrideTag>();
        pending.Push(new("t", value, 0, 0));
        var hasOrigin = false;
        while (pending.TryPop(out var tag))
        {
            if (tag.Name == "org")
            {
                hasOrigin = true;
                continue;
            }
            if (tag.Name != "t")
            {
                return false;
            }
            var tagList = TransformTagList(tag.Value);
            if (tagList is null)
            {
                return false;
            }
            foreach (var child in AssOverrideTags.Parse(tagList))
            {
                pending.Push(child);
            }
        }
        return hasOrigin;
    }

    private static string? TransformTagList(string value)
    {
        var arguments = AssOverrideTags.Arguments(value);
        return arguments.Length is >= 1 and <= 4 && arguments[^1].Contains('\\') ? arguments[^1] : null;
    }
}
