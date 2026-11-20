using System.Text;
using System.Text.Json;

namespace EraTrans.Core;

/// <summary>
/// Persists EraTrans application settings beside the executable, in the Config folder.
/// Existing AppData settings are copied into the portable location on first launch.
/// </summary>
public sealed class EraTransUserSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _filePath;
    private readonly string? _legacyFilePath;

    public EraTransUserSettingsStore(string? filePath = null, string? legacyFilePath = null)
    {
        bool useDefaultPaths = filePath is null;
        _filePath = filePath ?? EraTransPaths.UserSettingsFilePath;
        _legacyFilePath = legacyFilePath ??
            (useDefaultPaths ? EraTransPaths.LegacyUserSettingsFilePath : null);
    }

    public EraTransUserSettings Load()
    {
        if (File.Exists(_filePath))
        {
            return LoadFrom(_filePath);
        }

        if (_legacyFilePath is not null &&
            !Path.GetFullPath(_legacyFilePath).Equals(
                Path.GetFullPath(_filePath),
                StringComparison.OrdinalIgnoreCase) &&
            File.Exists(_legacyFilePath))
        {
            // Save a new copy next to the executable. Keep the AppData source untouched
            // as a recoverable backup in case the user needs to roll back.
            EraTransUserSettings legacySettings = LoadFrom(_legacyFilePath);
            Save(legacySettings);
            return legacySettings;
        }

        return EraTransUserSettings.CreateDefault();
    }

    private static EraTransUserSettings LoadFrom(string path)
    {
        try
        {
            string json = File.ReadAllText(path, Encoding.UTF8);
            EraTransUserSettings settings = JsonSerializer.Deserialize<EraTransUserSettings>(
                json,
                JsonOptions) ?? throw new InvalidDataException("设置文件为空或格式无效。");
            // Before SettingsVersion existed, HideVariableEntries defaulted to false.
            // Upgrade once to the new first-run default; subsequent explicit user
            // choices are preserved by the versioned setting.
            if (settings.SettingsVersion.GetValueOrDefault() < EraTransUserSettings.CurrentSettingsVersion)
            {
                settings.HideVariableEntries = true;
                settings.SettingsVersion = EraTransUserSettings.CurrentSettingsVersion;
            }

            settings.Validate();
            return settings;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new InvalidDataException(
                $"无法读取 EraTrans 设置文件：{path}",
                ex);
        }
    }

    public void Save(EraTransUserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.SettingsVersion ??= EraTransUserSettings.CurrentSettingsVersion;
        settings.Validate();

        string directory = Path.GetDirectoryName(_filePath)
            ?? throw new InvalidOperationException("设置文件路径无效。");
        Directory.CreateDirectory(directory);

        string json = JsonSerializer.Serialize(settings, JsonOptions);
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_filePath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       options: FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_filePath))
            {
                File.Replace(temporaryPath, _filePath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporaryPath, _filePath);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
