using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EraTrans.Core;

/// <summary>
/// Stores the three-state reasoning choice for every provider id in
/// Config/ai-reasoning.json. It is deliberately separate from the provider
/// credentials file so that file format stays untouched.
/// </summary>
public sealed class AiReasoningSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _filePath;

    public AiReasoningSettingsStore(string? filePath = null)
    {
        _filePath = filePath ??
            Path.Combine(EraTransPaths.ConfigurationDirectory, "ai-reasoning.json");
    }

    public string FilePath => _filePath;

    /// <summary>
    /// Reads the saved choice of one provider for request building. A missing or
    /// unreadable file simply means Auto, so translation never depends on it.
    /// </summary>
    public static AiReasoningOptions ResolveForProvider(string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
            return AiReasoningOptions.Auto;

        try
        {
            Dictionary<string, AiReasoningOptions> settings =
                new AiReasoningSettingsStore().Load();
            return settings.TryGetValue(providerId, out AiReasoningOptions? options)
                ? options
                : AiReasoningOptions.Auto;
        }
        catch
        {
            return AiReasoningOptions.Auto;
        }
    }

    public Dictionary<string, AiReasoningOptions> Load()
    {
        var result = new Dictionary<string, AiReasoningOptions>(StringComparer.Ordinal);
        if (!File.Exists(_filePath))
            return result;

        StoredSettings? stored;
        try
        {
            stored = JsonSerializer.Deserialize<StoredSettings>(
                File.ReadAllText(_filePath, Encoding.UTF8), JsonOptions);
        }
        catch (Exception ex) when (
            ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A broken reasoning file must never block translation.
            return result;
        }

        foreach (KeyValuePair<string, StoredReasoning> pair in stored?.Providers
                     ?? new Dictionary<string, StoredReasoning>())
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null)
                continue;

            result[pair.Key] = new AiReasoningOptions(
                Enum.IsDefined(pair.Value.Mode) ? pair.Value.Mode : AiReasoningMode.Auto,
                string.IsNullOrWhiteSpace(pair.Value.OnJson) ? null : pair.Value.OnJson,
                string.IsNullOrWhiteSpace(pair.Value.OffJson) ? null : pair.Value.OffJson);
        }

        return result;
    }

    public void Save(IReadOnlyDictionary<string, AiReasoningOptions> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var stored = new StoredSettings
        {
            Providers = settings
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value is not null)
                .ToDictionary(
                    pair => pair.Key,
                    pair => new StoredReasoning
                    {
                        Mode = pair.Value.Mode,
                        OnJson = string.IsNullOrWhiteSpace(pair.Value.OnJson)
                            ? null
                            : pair.Value.OnJson.Trim(),
                        OffJson = string.IsNullOrWhiteSpace(pair.Value.OffJson)
                            ? null
                            : pair.Value.OffJson.Trim()
                    },
                    StringComparer.Ordinal)
        };

        string directory = Path.GetDirectoryName(_filePath)
            ?? throw new InvalidOperationException("AI 推理配置路径无效。");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            _filePath,
            JsonSerializer.Serialize(stored, JsonOptions),
            new UTF8Encoding(false));
    }

    private sealed class StoredSettings
    {
        public Dictionary<string, StoredReasoning>? Providers { get; set; } =
            new(StringComparer.Ordinal);
    }

    private sealed class StoredReasoning
    {
        public AiReasoningMode Mode { get; set; } = AiReasoningMode.Auto;
        public string? OnJson { get; set; }
        public string? OffJson { get; set; }
    }
}
