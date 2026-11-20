namespace EraTrans.Core;

/// <summary>
/// Paths for user configuration stored beside the application executable.
/// </summary>
public static class EraTransPaths
{
    public static string ConfigurationDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Config");

    public static string UserSettingsFilePath =>
        Path.Combine(ConfigurationDirectory, "settings.json");

    public static string AiProviderSettingsFilePath =>
        Path.Combine(ConfigurationDirectory, "ai-providers.json");

}
