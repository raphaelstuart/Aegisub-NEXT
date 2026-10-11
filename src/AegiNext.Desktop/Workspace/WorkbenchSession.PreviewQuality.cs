using AegiNext.Core.Projects;
using AegiNext.Desktop.Rendering;

namespace AegiNext.Desktop.Workspace;

internal sealed partial class WorkbenchSession
{
    private long previewQualityRevision;

    internal ProjectPreviewState GetPreviewState() => Volatile.Read(ref previewState);

    private ProjectPreviewState CreatePreviewState(ProjectDocument? document = null) => new(document ?? PreviewDocument, ProjectDirectory, ProjectPosition,
        playback.UsesInteractiveQuality, Preferences.PreviewQuality, previewQualityRevision);
}
