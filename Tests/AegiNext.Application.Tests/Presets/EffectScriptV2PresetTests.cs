using AegiNext.Application.Presets;
using AegiNext.Core.Effects;
using AegiNext.Core.Projects;

namespace AegiNext.Application.Tests.Presets;

public sealed class EffectScriptV2PresetTests
{
    [Fact]
    public async Task ScopedSourceSurvivesLibraryReloadAndSourceExchangeThenGeneratesEditableRanges()
    {
        const string SOURCE = """
            effect "personal-split" version 2
            short-clip compress
            scope tokens subtitle
                unit split(" # ", "\"", "\\")
                stagger 60ms
                segment pulse fixed 150ms pingpong
                    at 0 scale base ease-in-out
                    at 1 scale factor(1.25, 1.25)
                end
                segment rest flex 1
                end
            end
            """;
        using var directory = new TemporaryProjectDirectory();
        var libraryPath = Path.Combine(directory.Path, "effects.json");
        var exportPath = Path.Combine(directory.Path, "分组 特效.aegifx");
        var preset = new EffectScriptPreset(Guid.NewGuid(), "按分隔符往返", SOURCE);
        using (var library = new EffectScriptPresetLibrary(libraryPath))
        {
            await library.UpsertAsync(preset);
            await library.ExportAsync(preset.Id, exportPath);
        }

        using var reopened = new EffectScriptPresetLibrary(libraryPath);
        await reopened.LoadAsync();
        Assert.Equal(preset, Assert.Single(reopened.Snapshot.Presets));
        var exported = await EffectScriptPresetStore.ReadScriptAsync(exportPath);
        Assert.Equal(SOURCE, exported.Source);
        Assert.Equal(2, exported.Script.Version);
        Assert.Equal([" # ", "\"", "\\"], Assert.Single(exported.Script.Scopes).Unit.Delimiters.ToArray());
        using var imported = new EffectScriptPresetLibrary(Path.Combine(directory.Path, "imported.json"));
        await imported.ImportAsync(exportPath);
        Assert.Equal(SOURCE, Assert.Single(imported.Snapshot.Presets).Source);

        var line = new SubtitleLine { Text = "AB # CD\"EF\\GH", Start = new(0), End = new(2) };
        var layer = new ProjectLayer { SubtitleId = line.Id, Start = line.Start, End = line.End };
        var document = new ProjectDocument { Subtitles = [line], Layers = [layer] };
        var result = ProjectEditingOperations.ApplyEffectScript(document, [layer.Id], exported.Script);
        Assert.Equal(4, result.Subtitles[0].AnimationRanges.Length);
        Assert.Equal(["AB", "CD", "EF", "GH"], result.Subtitles[0].AnimationRanges.Select(range =>
            line.Text.Substring(range.Utf16Start, range.Utf16Length)));
        Assert.Equal(4, result.Layers[0].Tracks.Length);
        Assert.All(result.Subtitles[0].AnimationRanges, range => Assert.Equal("personal-split", range.GeneratedOrigin!.EffectId));
        Assert.All(result.Layers[0].Tracks, track => Assert.Equal(AnimationProperty.SCALE, track.Property));
    }

    [Theory]
    [InlineData("letter-bounce")]
    [InlineData("letter-pulse")]
    public async Task GroupedBuiltinIdentifiersStayReservedDuringPersonalImport(string id)
    {
        using var directory = new TemporaryProjectDirectory();
        var sourcePath = Path.Combine(directory.Path, id + ".aegifx");
        using var library = new EffectScriptPresetLibrary(Path.Combine(directory.Path, "effects.json"));
        await library.LoadAsync();
        var before = library.Snapshot;
        await File.WriteAllTextAsync(sourcePath, BuiltinEffectScripts.Get(id).Source);

        await Assert.ThrowsAsync<InvalidDataException>(() => library.ImportAsync(sourcePath));

        Assert.Same(before, library.Snapshot);
        Assert.Empty(library.Snapshot.Presets);
    }
}
