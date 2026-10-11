using AegiNext.Core.Timing;

namespace AegiNext.Desktop.Workspace;

internal readonly record struct PreviewInteractionEvent(string Stage, long Session, long Sequence,
    MediaTime? Target, MediaTime? FrameTime, MediaTime? FrameEnd, long Timestamp);
