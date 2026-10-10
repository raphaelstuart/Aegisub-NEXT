using AegiNext.Core.Projects;
using AegiNext.Core.Timing;

namespace AegiNext.Core.Effects;

/// <summary>脚本对完整动画目标实际声明的内容时间区间及诊断源位置。</summary>
public sealed record EffectScriptInterval(AnimationTrackTarget Target, MediaTime Start, MediaTime End,
    int Line, int Column);
