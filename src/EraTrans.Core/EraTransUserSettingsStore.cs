using System.Text;
using System.Text.Json;

namespace EraTrans.Core;

/// <summary>
/// Persists EraTrans application settings beside the executable, in the Config folder.
/// </summary>
public sealed class EraTransUserSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _filePath;

    public EraTransUserSettingsStore(string? filePath = null)
    {
        _filePath = filePath ?? EraTransPaths.UserSettingsFilePath;
    }

    public EraTransUserSettings Load()
    {
        return File.Exists(_filePath)
            ? LoadFrom(_filePath)
            : EraTransUserSettings.CreateDefault();
    }

    private static EraTransUserSettings LoadFrom(string path)
    {
        try
        {
            string json = File.ReadAllText(path, Encoding.UTF8);
            EraTransUserSettings settings = JsonSerializer.Deserialize<EraTransUserSettings>(
                json,
                JsonOptions) ?? throw new InvalidDataException("设置文件为空或格式无效。");
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
