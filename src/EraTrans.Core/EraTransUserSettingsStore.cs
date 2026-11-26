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
            settings.Legacy ??= LegacyExtractionSettings.CreateDefault();
            MigrateFlatLegacySettings(json, settings.Legacy);
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

    /// <summary>
    /// Migrates legacy settings stored as flat top-level properties in older settings.json
    /// files into the nested <see cref="EraTransUserSettings.Legacy"/> section.
    /// </summary>
    private static void MigrateFlatLegacySettings(string json, LegacyExtractionSettings target)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        bool hasNestedLegacy = TryGetPropertyIgnoreCase(root, nameof(EraTransUserSettings.Legacy), out JsonElement nestedLegacy)
                               && nestedLegacy.ValueKind == JsonValueKind.Object;

        TryReadFlatBool(root, hasNestedLegacy, nestedLegacy, nameof(LegacyExtractionSettings.HideEnglishText), value => target.HideEnglishText = value);
        TryReadFlatBool(root, hasNestedLegacy, nestedLegacy, nameof(LegacyExtractionSettings.MergeDuplicateEntries), value => target.MergeDuplicateEntries = value);
        TryReadFlatBool(root, hasNestedLegacy, nestedLegacy, nameof(LegacyExtractionSettings.HideVariableEntries), value => target.HideVariableEntries = value);
        TryReadFlatBool(root, hasNestedLegacy, nestedLegacy, nameof(LegacyExtractionSettings.MergeConcatenatedStrings), value => target.MergeConcatenatedStrings = value);
        TryReadFlatBool(root, hasNestedLegacy, nestedLegacy, nameof(LegacyExtractionSettings.EnableVariableAutoCorrection), value => target.EnableVariableAutoCorrection = value);

        TryReadFlatStringList(root, hasNestedLegacy, nestedLegacy, nameof(LegacyExtractionSettings.AutoReplaceReferenceFiles), value => target.AutoReplaceReferenceFiles = value);
        TryReadFlatStringList(root, hasNestedLegacy, nestedLegacy, nameof(LegacyExtractionSettings.VariableNameCharacters), value => target.VariableNameCharacters = value);
        TryReadFlatStringList(root, hasNestedLegacy, nestedLegacy, nameof(LegacyExtractionSettings.Operators), value => target.Operators = value);
    }

    private static void TryReadFlatBool(
        JsonElement root,
        bool hasNestedLegacy,
        JsonElement nestedLegacy,
        string propertyName,
        Action<bool> apply)
    {
        if (hasNestedLegacy && TryGetPropertyIgnoreCase(nestedLegacy, propertyName, out _))
        {
            return;
        }

        if (TryGetPropertyIgnoreCase(root, propertyName, out JsonElement value) &&
            value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            apply(value.GetBoolean());
        }
    }

    private static void TryReadFlatStringList(
        JsonElement root,
        bool hasNestedLegacy,
        JsonElement nestedLegacy,
        string propertyName,
        Action<List<string>> apply)
    {
        if (hasNestedLegacy && TryGetPropertyIgnoreCase(nestedLegacy, propertyName, out _))
        {
            return;
        }

        if (TryGetPropertyIgnoreCase(root, propertyName, out JsonElement value) &&
            value.ValueKind == JsonValueKind.Array)
        {
            List<string>? parsed = JsonSerializer.Deserialize<List<string>>(value.GetRawText(), JsonOptions);
            if (parsed is not null)
            {
                apply(parsed);
            }
        }
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement obj, string name, out JsonElement value)
    {
        foreach (JsonProperty property in obj.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    public void Save(EraTransUserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();

        if (string.IsNullOrEmpty(Path.GetDirectoryName(_filePath)))
        {
            throw new InvalidOperationException("设置文件路径无效。");
        }

        string json = JsonSerializer.Serialize(settings, JsonOptions);
        AtomicFileWriter.WriteAllText(_filePath, json);
    }
}
