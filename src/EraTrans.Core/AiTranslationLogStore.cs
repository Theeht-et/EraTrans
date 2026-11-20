using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EraTrans.Core;

public enum AiTranslationLogStatus
{
    InProgress,
    Succeeded,
    Failed,
    Cancelled
}

/// <summary>
/// One logical Chat Completions call. Credentials are deliberately not part of
/// this model; request messages and the model reply are stored for diagnosis.
/// </summary>
public sealed class AiTranslationLogEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public long? DurationMilliseconds { get; set; }
    public string RequestKind { get; set; } = "Translation";
    public string ProviderName { get; set; } = "";
    public string RequestMethod { get; set; } = "POST";
    public string Endpoint { get; set; } = "";
    public string Model { get; set; } = "";
    public int RequestedEntryCount { get; set; }
    public string SystemPrompt { get; set; } = "";
    public string UserPrompt { get; set; } = "";
    public string RequestJson { get; set; } = "";
    public string? RawResponse { get; set; }
    public AiTranslationLogStatus Status { get; set; } = AiTranslationLogStatus.InProgress;
    public string? ErrorType { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ErrorDetails { get; set; }

    [JsonIgnore]
    public string DisplayTime => StartedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    [JsonIgnore]
    public string DisplayTitle => $"{ProviderName} / {Model}";

    [JsonIgnore]
    public string StatusDescription => Status switch
    {
        AiTranslationLogStatus.InProgress => "进行中",
        AiTranslationLogStatus.Succeeded => "成功",
        AiTranslationLogStatus.Failed => "失败",
        AiTranslationLogStatus.Cancelled => "已取消",
        _ => "未知"
    };

    [JsonIgnore]
    public string Summary => RequestKind == "ConnectionTest"
        ? $"连接测试 · {FormatDuration()}"
        : $"{RequestedEntryCount} 个词条 · {FormatDuration()}";

    [JsonIgnore]
    public string RequestKindDescription => RequestKind == "ConnectionTest" ? "连接测试" : "AI 翻译";

    private string FormatDuration() =>
        DurationMilliseconds is long duration ? $"{duration:N0} ms" : "尚未完成";
}

public interface IAiTranslationLogStore
{
    void Save(AiTranslationLogEntry entry);
    IReadOnlyList<AiTranslationLogEntry> LoadAll();
    void DeleteAll();
}

/// <summary>
/// Stores one JSON file per logical request under Config/ai-translation-logs.
/// A record is first written as InProgress and then atomically replaced on finish.
/// </summary>
public sealed class AiTranslationLogStore : IAiTranslationLogStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly object _gate = new();
    private readonly string _directoryPath;

    public AiTranslationLogStore(string? directoryPath = null)
    {
        _directoryPath = Path.GetFullPath(
            string.IsNullOrWhiteSpace(directoryPath)
                ? EraTransPaths.AiTranslationLogDirectoryPath
                : directoryPath);
    }

    public string DirectoryPath => _directoryPath;

    public void Save(AiTranslationLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!Guid.TryParseExact(entry.Id, "N", out _))
            throw new InvalidDataException("AI 请求记录 ID 无效。");

        lock (_gate)
        {
            Directory.CreateDirectory(_directoryPath);
            string fileName =
                $"{entry.StartedAtUtc.UtcDateTime:yyyyMMddTHHmmssfffZ}_{entry.Id}.json";
            string filePath = Path.Combine(_directoryPath, fileName);
            string temporaryPath = Path.Combine(
                _directoryPath,
                $"{fileName}.{Guid.NewGuid():N}.tmp");
            string json = JsonSerializer.Serialize(entry, JsonOptions);

            try
            {
                using (var stream = new FileStream(
                           temporaryPath,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None,
                           bufferSize: 8192,
                           options: FileOptions.WriteThrough))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(json);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }

                if (File.Exists(filePath))
                    File.Replace(temporaryPath, filePath, destinationBackupFileName: null);
                else
                    File.Move(temporaryPath, filePath);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }
    }

    public IReadOnlyList<AiTranslationLogEntry> LoadAll()
    {
        if (!Directory.Exists(_directoryPath))
            return [];

        var entries = new List<AiTranslationLogEntry>();
        foreach (string filePath in Directory.EnumerateFiles(
                     _directoryPath, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                string json = File.ReadAllText(filePath, Encoding.UTF8);
                AiTranslationLogEntry? entry =
                    JsonSerializer.Deserialize<AiTranslationLogEntry>(json, JsonOptions);
                if (entry is not null)
                    entries.Add(entry);
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // A single damaged log must not make the remaining history inaccessible.
            }
        }

        return entries
            .OrderByDescending(entry => entry.StartedAtUtc)
            .ThenByDescending(entry => entry.Id, StringComparer.Ordinal)
            .ToArray();
    }

    public void DeleteAll()
    {
        if (!Directory.Exists(_directoryPath))
            return;

        lock (_gate)
        {
            foreach (string filePath in Directory.EnumerateFiles(
                         _directoryPath, "*", SearchOption.TopDirectoryOnly))
            {
                string extension = Path.GetExtension(filePath);
                if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(filePath);
                }
            }
        }
    }
}
