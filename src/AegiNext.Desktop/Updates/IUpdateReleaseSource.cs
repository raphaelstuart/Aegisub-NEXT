namespace AegiNext.Desktop.Updates;

internal interface IUpdateReleaseSource
{
    Task<UpdateRelease?> GetLatestAsync(UpdateChannel channel, CancellationToken cancellationToken);
}
