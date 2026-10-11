namespace AegiNext.Desktop.Tests.Updates;

internal sealed class UpdateImmediateSynchronizationContext : SynchronizationContext
{
    /// <inheritdoc />
    public override void Post(SendOrPostCallback callback, object? state)
    {
        callback(state);
    }
}
