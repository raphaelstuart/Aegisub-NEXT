using AegiNext.Core.Timing;
using AegiNext.Desktop.Workspace;

namespace AegiNext.Desktop.Tests.Workspace;

public sealed class InteractiveSeekingStateTests
{
    [Fact]
    public void SettlingRestoresQualityOnceWithoutEndingGestureOrLosingPlaybackIntent()
    {
        var clock = new ManualPlaybackTimeProvider();
        var state = new InteractiveSeekingState(clock);
        state.Begin(true);
        state.Move(new(2));
        clock.Advance(TimeSpan.FromMilliseconds(149));
        Assert.False(state.TrySettle());
        state.Move(new(3));
        clock.Advance(TimeSpan.FromMilliseconds(149));
        Assert.False(state.TrySettle());
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(state.TrySettle());
        Assert.False(state.TrySettle());
        Assert.True(state.IsActive);
        Assert.True(state.ResumePlayback);
        Assert.False(state.UsesInteractiveQuality);
        Assert.Equal(new MediaTime(3), state.Target);
        state.Move(new(2));
        Assert.True(state.UsesInteractiveQuality);
        Assert.False(state.TrySettle());
    }

    [Fact]
    public void CancelledOrNewGestureCannotSettleThePreviousTarget()
    {
        var clock = new ManualPlaybackTimeProvider();
        var state = new InteractiveSeekingState(clock);
        state.Begin(true);
        state.Move(new(2));
        state.End();
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(state.TrySettle());
        Assert.False(state.ResumePlayback);
        state.Begin(false);
        Assert.Null(state.Target);
        Assert.False(state.TrySettle());
        state.Move(MediaTime.Zero);
        clock.Advance(InteractiveSeekingState.SETTLE_DELAY);
        Assert.True(state.TrySettle());
        Assert.False(state.ResumePlayback);
    }
}
