using System.Globalization;
using AegiNext.Desktop.I18n;
using AegiNext.Desktop.Settings;

namespace AegiNext.Desktop.Ui.Tests;

internal sealed class UiTestEnvironment : IDisposable
{
    private readonly string? previousDirectory = Environment.GetEnvironmentVariable("AEGINEXT_PREFERENCES_DIRECTORY");
    private readonly CultureInfo previousCulture = CultureInfo.CurrentUICulture;
    private readonly CultureInfo? previousDefaultCulture = CultureInfo.DefaultThreadCurrentUICulture;
    private readonly string previousLanguageID = Localization.SelectedLanguageID;

    internal UiTestEnvironment(bool disableAutomaticUpdates = false)
    {
        DirectoryPath = Path.Combine(Path.GetTempPath(), "AegiNext.Ui.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
        if (disableAutomaticUpdates)
        {
            File.WriteAllBytes(Path.Combine(DirectoryPath, "preferences.json"),
                WorkbenchPreferencesStore.Serialize(new() { AutoCheckUpdates = false }));
        }
        Environment.SetEnvironmentVariable("AEGINEXT_PREFERENCES_DIRECTORY", DirectoryPath);
        Localization.SetLanguage("en-US");
    }

    internal string DirectoryPath { get; }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("AEGINEXT_PREFERENCES_DIRECTORY", previousDirectory);
        Localization.SetLanguage(previousLanguageID);
        CultureInfo.CurrentUICulture = previousCulture;
        CultureInfo.DefaultThreadCurrentUICulture = previousDefaultCulture;
        Directory.Delete(DirectoryPath, true);
    }
}
