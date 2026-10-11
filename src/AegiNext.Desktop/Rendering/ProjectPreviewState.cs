using AegiNext.Core.Projects;
using AegiNext.Core.Timing;
using AegiNext.Desktop.Settings;

namespace AegiNext.Desktop.Rendering;

internal sealed record ProjectPreviewState(ProjectDocument Document, string Directory, MediaTime? TargetTime = null,
    bool IsInteractive = false, PreviewQuality Quality = PreviewQuality.LOW, long QualityRevision = 0,
    bool EvaluateAtTarget = false);
