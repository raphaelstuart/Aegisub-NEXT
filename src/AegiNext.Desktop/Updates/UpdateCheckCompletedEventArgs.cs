namespace AegiNext.Desktop.Updates;

internal sealed class UpdateCheckCompletedEventArgs(UpdateCheckTrigger trigger, UpdateChannel channel, UpdateCheckResult result)
    : EventArgs
{
    internal UpdateCheckTrigger Trigger { get; } = trigger;
    internal UpdateChannel Channel { get; } = channel;
    internal UpdateCheckResult Result { get; } = result;
}
