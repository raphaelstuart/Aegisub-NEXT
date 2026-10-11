using AegiNext.Core.Timing;

namespace AegiNext.Desktop.Workspace;

internal sealed class InteractiveSeekingState(TimeProvider? timeProvider = null)
{
    internal static readonly TimeSpan UPDATE_INTERVAL = TimeSpan.FromMilliseconds(16);
    internal static readonly TimeSpan SETTLE_DELAY = TimeSpan.FromMilliseconds(150);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private long lastInput;

    internal bool IsActive { get; private set; }
    internal bool UsesInteractiveQuality { get; private set; }
    internal bool ResumePlayback { get; private set; }
    internal MediaTime? Target { get; private set; }

    internal void Begin(bool resumePlayback)
    {
        IsActive = UsesInteractiveQuality = true;
        ResumePlayback = resumePlayback;
        Target = null;
    }

    internal void Move(MediaTime target)
    {
        Target = target;
        UsesInteractiveQuality = true;
        lastInput = clock.GetTimestamp();
    }

    internal bool TrySettle()
    {
        if (!IsActive || !UsesInteractiveQuality || Target is null || clock.GetElapsedTime(lastInput) < SETTLE_DELAY)
        {
            return false;
        }
        UsesInteractiveQuality = false;
        return true;
    }

    internal void End()
    {
        IsActive = UsesInteractiveQuality = ResumePlayback = false;
        Target = null;
    }
}
