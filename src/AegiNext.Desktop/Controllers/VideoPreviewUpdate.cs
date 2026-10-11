using AegiNext.Media.Preview;
using AegiNext.Core.Projects;
using AegiNext.Core.Timing;

namespace AegiNext.Desktop.Controllers;

/// <summary>
/// 一次经过身份校验的 UI 更新；帧为空时保留画面，ClearFrame 明确要求清空。
/// </summary>
public sealed record VideoPreviewUpdate(VideoPreviewSnapshot Snapshot, SdrVideoFrame? Frame, bool ClearFrame)
{
    public SdrVideoFrame? BackgroundFrame { get; init; }
    public ProjectDocument? CompositionDocument { get; init; }
    public MediaTime? CompositionTime { get; init; }
    public bool IsInteractiveComposition { get; init; }
    public bool IsTransientPreview { get; init; }
    public MediaTime? SourceFrameTime { get; init; }
    public MediaTime? SourceFrameEnd { get; init; }
    public MediaTime? RequestedPosition { get; init; }
}
