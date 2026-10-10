using System.Collections.Immutable;
using AegiNext.Core.Projects;

namespace AegiNext.Core.Effects;

/// <summary>包含动画输出、声明区间及生成范围的完整编译结果；准备层已清理被替换的来源轨道。</summary>
public sealed record EffectScriptCompilation(ImmutableArray<AnimationTrack> Tracks,
    ImmutableArray<EffectScriptInterval> Intervals)
{
    public ProjectLayer? PreparedLayer { get; init; }
    public SubtitleLine? Subtitle { get; init; }
}
