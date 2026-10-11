using AegiNext.Core.Presets;
using AegiNext.Core.Projects;

namespace AegiNext.Desktop.Workspace;

internal sealed record SubtitleTimingAssociationCacheEntry(ProjectDocument Document, SubtitleStylePresetCollection Presets,
    IReadOnlyDictionary<Guid, SubtitleStylePreset> Associations);
