using AegiNext.Desktop.Updates;

namespace AegiNext.Desktop.Ui.Tests.Updates;

internal sealed class UpdateUiReleaseSource(Func<UpdateChannel, CancellationToken, Task<UpdateRelease?>> operation)
    : IUpdateReleaseSource
{
    private int callCount;

    internal int CallCount => Volatile.Read(ref callCount);
    internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>返回受测试控制的发布结果并记录请求次数。</summary>
    public Task<UpdateRelease?> GetLatestAsync(UpdateChannel channel, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref callCount);
        Started.TrySetResult();
        return operation(channel, cancellationToken);
    }
}
