using AegiNext.Application.Tasks;

namespace AegiNext.Desktop.Tests.Updates;

internal sealed class UpdateGateTask(TaskCompletionSource entered, Task release) : AegiTask
{
    public override string Name => "Update test scheduling gate";

    /// <inheritdoc />
    protected override async Task ExecuteAsync(AegiTaskExecutionContext context)
    {
        entered.TrySetResult();
        await release.WaitAsync(context.CancellationToken);
    }
}
