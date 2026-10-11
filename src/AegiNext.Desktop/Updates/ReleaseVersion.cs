using System.Diagnostics.CodeAnalysis;

namespace AegiNext.Desktop.Updates;

internal static class ReleaseVersion
{
    internal static bool TryParse(string? text, [NotNullWhen(true)] out Version? version)
    {
        version = null;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }
        if (text.StartsWith("rel/", StringComparison.Ordinal))
        {
            var parts = text.Split('/');
            if (parts.Length != 3 || parts[1].Length == 0)
            {
                return false;
            }
            text = parts[2];
        }
        if (text.StartsWith('v'))
        {
            text = text[1..];
        }
        if (text.Any(character => !char.IsAsciiDigit(character) && character != '.') ||
            !Version.TryParse(text, out var parsed) || parsed.Build < 0)
        {
            return false;
        }
        version = new(parsed.Major, parsed.Minor, parsed.Build, Math.Max(0, parsed.Revision));
        return true;
    }
}
