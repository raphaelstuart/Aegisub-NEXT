using AegiNext.Core.Projects;

namespace AegiNext.Desktop.Controls;

internal sealed record EffectScriptLanguageContext(bool VersionTwo, bool HasHeader, bool HasShortClip, string? Block,
    IReadOnlySet<string> ScopeFields, bool ScopeHasSegments, bool TextRange, SubtitleAnimationState State);
