using AegiNext.Application.Tasks;

namespace AegiNext.Desktop.Updates;

internal sealed class CheckUpdatesTask(IUpdateReleaseSource source, UpdateChannel channel, string currentVersion)
    : AegiTask<UpdateCheckResult>
{
    public override string Name => "Tasks.CheckUpdates";

    /// <inheritdoc />
    protected override Task<UpdateCheckResult> ExecuteResultAsync(AegiTaskExecutionContext context)
    {
        return Task.Run(async () =>
        {
            if (!ReleaseVersion.TryParse(currentVersion, out var localVersion))
            {
                throw new InvalidDataException("The application does not have a supported numeric product version.");
            }
            var release = await source.GetLatestAsync(channel, context.CancellationToken).ConfigureAwait(false);
            context.CancellationToken.ThrowIfCancellationRequested();
            var status = release is null ? UpdateCheckStatus.NO_RELEASE
                : release.Version > localVersion ? UpdateCheckStatus.UPDATE_AVAILABLE : UpdateCheckStatus.UP_TO_DATE;
            return new UpdateCheckResult(status, currentVersion, release);
        }, context.CancellationToken);
    }
}
