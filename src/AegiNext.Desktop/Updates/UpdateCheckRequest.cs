using AegiNext.Application.Tasks;

namespace AegiNext.Desktop.Updates;

internal sealed class UpdateCheckRequest(UpdateCheckTrigger trigger, UpdateChannel channel)
{
    internal UpdateCheckTrigger Trigger { get; set; } = trigger;
    internal UpdateChannel Channel { get; } = channel;
    internal AegiTaskHandle<UpdateCheckResult>? Handle { get; set; }
    internal TaskCompletionSource<UpdateCheckResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
