using AegiNext.Desktop.Shortcuts;
using AegiNext.Desktop.Tests.Workspace;

namespace AegiNext.Desktop.Tests.Updates;

[Collection("Workspace session")]
public sealed class UpdateCommandWorkflowTests
{
    [Fact]
    public async Task ManualUpdateCommandPreservesProjectErrorAndActiveTimingPreview()
    {
        await using var context = new WorkspaceSessionTestContext();
        await context.InitializeAsync();
        var session = context.Session;
        await session.Controller.OpenAsync("update-command.mkv");
        await session.ExecuteCommandAsync(WorkbenchCommand.TIMING_ENTER);
        var timingPreview = session.ViewModel.Timeline.TimingPreview;
        Assert.NotNull(timingPreview);
        var document = context.Editor.Snapshot;
        var error = new IOException("Existing project failure.");
        session.ShowError(error);
        var forwardedCommands = 0;
        session.ViewModel.HostCommandHandler = request =>
        {
            Assert.Equal(WorkbenchCommand.CHECK_UPDATES, request.Command);
            forwardedCommands++;
            return Task.CompletedTask;
        };

        await session.ExecuteCommandAsync(WorkbenchCommand.CHECK_UPDATES);

        Assert.Equal(1, forwardedCommands);
        Assert.Same(error, session.LastError);
        Assert.Equal(error.Message, session.ViewModel.Error);
        Assert.Same(timingPreview, session.ViewModel.Timeline.TimingPreview);
        Assert.Same(document, context.Editor.Snapshot);
        Assert.True(session.CanExecuteCommand(WorkbenchCommand.TIMING_EXIT));
    }
}
