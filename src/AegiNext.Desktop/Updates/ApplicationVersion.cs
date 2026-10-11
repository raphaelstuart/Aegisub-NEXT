using System.Reflection;

namespace AegiNext.Desktop.Updates;

internal static class ApplicationVersion
{
    internal static string Current { get; } = typeof(ApplicationVersion).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
}
