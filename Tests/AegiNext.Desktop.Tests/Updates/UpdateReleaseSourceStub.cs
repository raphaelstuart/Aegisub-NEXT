using AegiNext.Desktop.Updates;

namespace AegiNext.Desktop.Tests.Updates;

internal sealed class UpdateReleaseSourceStub(Func<UpdateChannel, CancellationToken, Task<UpdateRelease?>> query)
    : IUpdateReleaseSource
{
    private int callCount;

    internal int CallCount => Volatile.Read(ref callCount);

    /// <inheritdoc />
    public Task<UpdateRelease?> GetLatestAsync(UpdateChannel channel, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref callCount);
        return query(channel, cancellationToken);
    }
}
