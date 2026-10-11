namespace AegiNext.Desktop.Updates;

internal sealed record UpdateCheckResult(UpdateCheckStatus Status, string CurrentVersion,
    UpdateRelease? Release = null, Exception? Error = null);
