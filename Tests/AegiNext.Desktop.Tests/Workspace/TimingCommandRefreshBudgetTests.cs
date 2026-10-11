using AegiNext.Core.Presets;
using AegiNext.Core.Projects;
using AegiNext.Desktop.Shortcuts;

namespace AegiNext.Desktop.Tests.Workspace;

[Collection("Workspace session")]
public sealed class TimingCommandRefreshBudgetTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public async Task RepeatedCommandQueriesDoNotAllocateForUnselectedSubtitles(int selection, bool expected)
    {
        var preset = CreatePreset();
        var document = CreateDocument(preset, 3000);
        await using var context = new WorkspaceSessionTestContext(document);
        await context.InitializeAsync();
        var session = context.Session;
        await session.Styles.UpsertAsync(preset);
        switch (selection)
        {
            case 0:
                Assert.True(session.SelectTrack(document.Tracks[0].Id));
                break;
            case 1:
            case 2:
                Assert.True(session.SelectTimelineLayers(new(document.Layers[selection - 1].Id)));
                break;
            case 3:
                Assert.True(session.SelectTimelineLayers(new(document.Layers[^1].Id,
                    [document.Layers[^1].Id, document.Layers[1].Id])));
                break;
        }
        var command = session.ViewModel.GetCommand(WorkbenchCommand.APPLY_TIMING_POST_PROCESSOR);
        Assert.Equal(expected, command.CanExecute(null));
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 64; index++)
        {
            Assert.Equal(expected, command.CanExecute(null));
        }
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 128 * 1024);
        Assert.Same(document, context.Editor.Snapshot);
    }

    [Fact]
    public async Task CachedAvailabilityTracksSelectionDocumentUndoAndPresetReplacement()
    {
        var preset = CreatePreset();
        var document = CreateDocument(preset, 3);
        await using var context = new WorkspaceSessionTestContext(document);
        await context.InitializeAsync();
        var session = context.Session;
        await session.Styles.UpsertAsync(preset);
        var command = session.ViewModel.GetCommand(WorkbenchCommand.APPLY_TIMING_POST_PROCESSOR);
        Assert.True(session.SelectTimelineLayers(new(document.Layers[0].Id)));
        Assert.False(command.CanExecute(null));
        Assert.True(session.SelectTimelineLayers(new(document.Layers[1].Id)));
        Assert.True(command.CanExecute(null));

        context.Editor.Apply("Replace associated identity", source => source with
        {
            Subtitles = source.Subtitles.SetItem(1, source.Subtitles[1] with { StylePresetId = Guid.NewGuid() })
        });
        Assert.False(command.CanExecute(null));
        Assert.True(context.Editor.Undo());
        Assert.True(command.CanExecute(null));
        Assert.True(context.Editor.Redo());
        Assert.False(command.CanExecute(null));
        Assert.True(context.Editor.Undo());
        Assert.True(command.CanExecute(null));

        await session.Styles.UpsertAsync(preset with { TimingPostProcessor = null });
        Assert.False(command.CanExecute(null));
        await session.Styles.UpsertAsync(preset);
        Assert.True(command.CanExecute(null));
        await session.ApplicationContext.RunStyleOperationAsync(() => session.StyleLibrary.RemoveAsync(preset.Id));
        Assert.False(command.CanExecute(null));
        await session.Styles.UpsertAsync(preset with { Id = Guid.NewGuid() });
        Assert.False(command.CanExecute(null));
        Assert.True(session.SelectTrack(document.Tracks[0].Id));
        Assert.False(command.CanExecute(null));
    }

    private static SubtitleStylePreset CreatePreset()
    {
        return new(Guid.NewGuid(), "Timing", new(), TimingPostProcessor: new()
        {
            LeadInEnabled = true, LeadInMilliseconds = 100,
            LeadOutEnabled = false, AdjacencyEnabled = false, KeyframeSnapEnabled = false
        });
    }

    private static ProjectDocument CreateDocument(SubtitleStylePreset preset, int count)
    {
        var lines = Enumerable.Range(0, count).Select(index => new SubtitleLine
        {
            Start = new(10 + index * 2), End = new(11 + index * 2), Text = $"Subtitle {index}",
            StyleName = index == 1 ? preset.Name : "Plain", StylePresetId = index == 1 ? preset.Id : null
        }).ToArray();
        var shapes = new ProjectTrack { Name = "Shapes" };
        return new()
        {
            Tracks = [ProjectTrack.Default, shapes], Subtitles = [.. lines],
            Layers = [.. lines.Select(line => new ProjectLayer
            {
                SubtitleId = line.Id, Start = line.Start, End = line.End
            }), new()
            {
                TrackId = shapes.Id, Kind = LayerKind.SHAPE, Shape = new(ShapeKind.RECTANGLE, 20, 20), End = new(1)
            }]
        };
    }
}
