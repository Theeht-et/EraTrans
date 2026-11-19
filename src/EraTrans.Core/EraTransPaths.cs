namespace EraTrans.Core;

/// <summary>
/// Paths for portable user configuration and the legacy AppData locations.
/// The application is distributed as a single-file executable, so BaseDirectory
/// resolves to the directory that contains EraTrans.App.exe.
/// </summary>
public static class EraTransPaths
{
    public static string ConfigurationDirectory =>
        Path.Combine(AppContext.BaseDirectory, "配置");

    public static string UserSettingsFilePath =>
        Path.Combine(ConfigurationDirectory, "settings.json");

    public static string AiProviderSettingsFilePath =>
        Path.Combine(ConfigurationDirectory, "ai-providers.json");

    public static string LegacyAppDataDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "EraTrans");

    public static string LegacyUserSettingsFilePath =>
        Path.Combine(LegacyAppDataDirectory, "settings.json");

    public static string LegacyAiProviderSettingsFilePath =>
        Path.Combine(LegacyAppDataDirectory, "ai-providers.json");
}
