using AegiNext.Application;
using AegiNext.Core.Presets;
using AegiNext.Core.Projects;

namespace AegiNext.Desktop.Tests.Workspace;

public sealed class SubtitleTimingAssociationResolverTests
{
    [Fact]
    public void SerializedLegacyDefaultClipResolvesItsInheritedTrackPresetWithoutChangingTheProject()
    {
        var preset = Preset();
        var line = new SubtitleLine { Style = preset.Style };
        var document = ProjectStore.Deserialize(ProjectStore.Serialize(Document(preset, line)));
        var before = ProjectStore.Serialize(document);

        var resolved = Resolve(document, line.Id, preset);

        Assert.Same(preset, Assert.Single(resolved).Value);
        Assert.Null(Assert.Single(document.Subtitles).StylePresetId);
        Assert.Equal(before, ProjectStore.Serialize(document));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void LegacyDefaultDoesNotInheritDisabledOrDifferentTrackStyles(bool autoApply, bool differentStyle)
    {
        var preset = Preset();
        var line = new SubtitleLine
        {
            Style = differentStyle ? preset.Style with { FontSize = preset.Style.FontSize + 1 } : preset.Style
        };
        var document = Document(preset, line);
        document = document with
        {
            Tracks = [document.Tracks[0] with { AutoApplyStyle = autoApply }]
        };

        Assert.Empty(Resolve(document, line.Id, preset));
    }

    [Theory]
    [InlineData("Default", true)]
    [InlineData("default", false)]
    [InlineData("DEFAULT", false)]
    public void AnExplicitDefaultPresetNameBlocksLegacyTrackInference(string name, bool exactName)
    {
        var trackPreset = Preset();
        var namedPreset = trackPreset with { Id = Guid.NewGuid(), Name = name };
        var line = new SubtitleLine { Style = trackPreset.Style };

        var resolved = Resolve(Document(trackPreset, line), line.Id, trackPreset, namedPreset);

        if (exactName)
        {
            Assert.Same(namedPreset, Assert.Single(resolved).Value);
        }
        else
        {
            Assert.Empty(resolved);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnExplicitMissingOrUnassociatedIdentityDoesNotInheritItsTrack(bool exists)
    {
        var trackPreset = Preset();
        var explicitPreset = trackPreset with { Id = Guid.NewGuid(), Name = "Explicit", TimingPostProcessor = null };
        var line = new SubtitleLine { Style = trackPreset.Style, StylePresetId = explicitPreset.Id };

        var resolved = Resolve(Document(trackPreset, line), line.Id,
            exists ? [trackPreset, explicitPreset] : [trackPreset]);

        Assert.Empty(resolved);
    }

    [Fact]
    public void AReplacementPresetWithTheSameNameDoesNotRepairAMissingTrackIdentity()
    {
        var trackPreset = Preset();
        var replacement = trackPreset with { Id = Guid.NewGuid() };
        var line = new SubtitleLine { Style = trackPreset.Style };

        Assert.Empty(Resolve(Document(trackPreset, line), line.Id, replacement));
    }

    [Fact]
    public void ASeparateStyleNameDoesNotInheritAnIdenticalTrackStyle()
    {
        var preset = Preset();
        var line = new SubtitleLine { Style = preset.Style, StyleName = "Separate" };

        Assert.Empty(Resolve(Document(preset, line), line.Id, preset));
    }

    private static SubtitleStylePreset Preset()
    {
        return new(Guid.NewGuid(), "Track preset", new()
        {
            FontFamily = "Inherited family", FontSize = 42,
            FontVariant = new() { Name = "Regular", PostScriptName = "Inherited-Regular" }
        }, TimingPostProcessor: new()
        {
            LeadInEnabled = true, LeadInMilliseconds = 100,
            LeadOutEnabled = false, AdjacencyEnabled = false, KeyframeSnapEnabled = false
        });
    }

    private static ProjectDocument Document(SubtitleStylePreset preset, SubtitleLine line)
    {
        return new()
        {
            Tracks = [ProjectTrack.Default with
            {
                DefaultStyle = preset.Style, StylePresetId = preset.Id, StylePresetName = preset.Name
            }],
            Subtitles = [line],
            Layers = [new() { Kind = LayerKind.SUBTITLE, SubtitleId = line.Id, Start = line.Start, End = line.End }]
        };
    }

    private static Dictionary<Guid, SubtitleStylePreset> Resolve(ProjectDocument document, Guid subtitleId,
        params SubtitleStylePreset[] presets)
    {
        return AegiNext.Desktop.Workspace.SubtitleTimingAssociationResolver.Resolve(new(document), presets)
            .Where(pair => pair.Key == subtitleId).ToDictionary();
    }
}
