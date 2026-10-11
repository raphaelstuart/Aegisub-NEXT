using AegiNext.Core.Timing;
using AegiNext.Desktop.Workspace;

namespace AegiNext.Desktop.Tests.Workspace;

public sealed class PreviewInteractionDiagnosticsTests
{
    [Fact]
    public void DeliveryRetainsAcceptedInputIdentityWhileNewTargetsAreQueued()
    {
        var diagnostics = new PreviewInteractionDiagnostics { Enabled = true };
        diagnostics.Begin();
        diagnostics.Input(new(1));
        diagnostics.Accept(new(1));
        var accepted = diagnostics.Events[^1];
        diagnostics.Input(new(2));
        diagnostics.Record("delivered", new(1), new(3, 2));
        var delivered = diagnostics.Events[^1];
        Assert.Equal(accepted.Sequence, delivered.Sequence);
        Assert.Equal(new MediaTime(1), delivered.Target);
        Assert.Equal(accepted.Session, delivered.Session);
        Assert.True(delivered.Timestamp >= accepted.Timestamp);
    }

    [Fact]
    public void DiagnosticsRemainBoundedAndAreDisabledByDefault()
    {
        var diagnostics = new PreviewInteractionDiagnostics();
        diagnostics.Begin();
        diagnostics.Input(new(1));
        Assert.Empty(diagnostics.Events);
        diagnostics.Enabled = true;
        for (var index = 0; index < 2000; index++)
        {
            diagnostics.Input(new(index));
        }
        Assert.Equal(512, diagnostics.Events.Count);
        Assert.Equal(new MediaTime(1999), diagnostics.Events[^1].Target);
    }
}
