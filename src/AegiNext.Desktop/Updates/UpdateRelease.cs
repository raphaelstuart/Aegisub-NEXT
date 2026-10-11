namespace AegiNext.Desktop.Updates;

internal sealed record UpdateRelease(Version Version, string TagName, string Name, string ReleaseNotesMarkdown, Uri PageUri);
