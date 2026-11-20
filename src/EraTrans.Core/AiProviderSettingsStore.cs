using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EraTrans.Core;

public enum AiProviderProtocol
{
    OpenAiCompatible = 0
}

public sealed class AiProviderProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string Model { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public AiProviderProtocol Protocol { get; set; } = AiProviderProtocol.OpenAiCompatible;
    public bool IsEnabled { get; set; } = true;
    public bool IsBuiltIn { get; set; }
    [JsonIgnore]
    public bool RequiresEndpointReview { get; set; }
    public List<AiProviderModel> AvailableModels { get; set; } = [];
}

public sealed class AiProviderSettings
{
    public List<AiProviderProfile> Providers { get; set; } = [];
    public string? ActiveProviderId { get; set; }

    public AiProviderProfile? ActiveProvider =>
        Providers.FirstOrDefault(provider =>
            provider.IsEnabled && provider.Id == ActiveProviderId) ??
        Providers.FirstOrDefault(provider => provider.IsEnabled);

    /// <summary>
    /// Restores the application-owned provider list without replacing user credentials.
    /// User-created configurations stay ahead of built-in providers in the UI.
    /// </summary>
    public void EnsureBuiltInProviders()
    {
        var remaining = Providers.ToList();
        var builtIns = new List<AiProviderProfile>();

        foreach (AiProviderPreset preset in AiProviderPresets.All)
        {
            int matchIndex = remaining.FindIndex(provider =>
                provider.IsBuiltIn &&
                string.Equals(
                    CanonicalBuiltInName(provider.Name),
                    preset.Name,
                    StringComparison.OrdinalIgnoreCase));
            AiProviderProfile profile;
            if (matchIndex >= 0)
            {
                profile = remaining[matchIndex];
                remaining.RemoveAt(matchIndex);
                profile.Name = preset.Name;
                profile.BaseUrl = string.IsNullOrWhiteSpace(profile.BaseUrl)
                    ? preset.BaseUrl
                    : profile.BaseUrl;
            }
            else
            {
                profile = new AiProviderProfile
                {
                    Name = preset.Name,
                    BaseUrl = preset.BaseUrl,
                    Protocol = AiProviderProtocol.OpenAiCompatible,
                    IsEnabled = true
                };
            }

            profile.IsBuiltIn = true;
            builtIns.Add(profile);
        }

        foreach (AiProviderProfile provider in remaining)
        {
            provider.IsBuiltIn = false;
            string canonical = CanonicalBuiltInName(provider.Name);
            if (AiProviderPresets.All.Any(preset =>
                    string.Equals(preset.Name, canonical, StringComparison.OrdinalIgnoreCase)))
            {
                provider.Name = canonical + " 自定义";
            }
        }

        Providers = remaining.Concat(builtIns).ToList();
        if (ActiveProvider is null)
            ActiveProviderId = Providers.FirstOrDefault(provider => provider.IsEnabled)?.Id;
    }

    private static string CanonicalBuiltInName(string name) =>
        name.Trim() switch
        {
            "OpenAI 官方" => "OpenAI",
            "DeepSeek 深度求索" => "DeepSeek",
            "通义千问（中国大陆）" or "通义千问（国际站）" => "通义千问",
            "月之暗面 Kimi（中国大陆）" or "月之暗面 Kimi（国际站）" or "Kimi（月之暗面）" => "Kimi",
            "火山引擎方舟（北京）" or "火山引擎方舟（豆包）" => "火山引擎方舟",
            "硅基流动（中国大陆）" => "硅基流动",
            "Grok（xAI）" => "Grok",
            _ => name.Trim()
        };
}

public static class AiProviderProfileValidator
{
    public static string NormalizeBaseUrl(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string normalized = value.Trim();

        if (normalized.StartsWith("//", StringComparison.Ordinal))
        {
            normalized = "https:" + normalized;
        }
        else if (!normalized.Contains("://", StringComparison.Ordinal))
        {
            normalized = "https://" + normalized;
        }

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException(
                "API 地址必须是有效的 HTTP/HTTPS Base URL，不能包含账号密码、查询参数或片段。");
        }

        string path = uri.AbsolutePath.TrimEnd('/');
        const string completionSuffix = "/chat/completions";
        if (path.EndsWith(completionSuffix, StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^completionSuffix.Length].TrimEnd('/');
        }

        var builder = new UriBuilder(uri)
        {
            Path = path,
            Query = "",
            Fragment = ""
        };
        return builder.Uri.ToString().TrimEnd('/');
    }

    public static void ValidateForStorage(AiProviderProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(profile.Name))
            throw new InvalidOperationException("请填写服务商名称。");
        if (!string.IsNullOrWhiteSpace(profile.BaseUrl))
            profile.BaseUrl = NormalizeBaseUrl(profile.BaseUrl);
    }

    public static void ValidateForModelDiscovery(AiProviderProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(profile.Name))
            throw new InvalidOperationException("请填写服务商名称。");

        profile.BaseUrl = NormalizeBaseUrl(profile.BaseUrl);
    }

    public static void Validate(AiProviderProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(profile.Name))
            throw new InvalidOperationException("请填写服务商名称。");
        if (string.IsNullOrWhiteSpace(profile.Model))
            throw new InvalidOperationException("请填写模型名称。");
        if (string.IsNullOrWhiteSpace(profile.ApiKey))
            throw new InvalidOperationException("请填写 API Key。");

        profile.BaseUrl = NormalizeBaseUrl(profile.BaseUrl);
    }
}

/// <summary>
/// Stores provider settings in the executable's Config folder. API keys remain
/// encrypted with Windows DPAPI (CurrentUser).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AiProviderSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _filePath;

    public AiProviderSettingsStore(string? filePath = null)
    {
        _filePath = filePath ?? EraTransPaths.AiProviderSettingsFilePath;
    }

    public AiProviderSettings Load()
    {
        return File.Exists(_filePath)
            ? LoadFrom(_filePath)
            : new AiProviderSettings();
    }

    private static AiProviderSettings LoadFrom(string path)
    {
        try
        {
            string json = File.ReadAllText(path, Encoding.UTF8);
            StoredSettings stored = JsonSerializer.Deserialize<StoredSettings>(json, JsonOptions)
                ?? throw new InvalidDataException("AI 服务商配置文件为空或格式无效。");

            var settings = new AiProviderSettings
            {
                ActiveProviderId = stored.ActiveProviderId
            };

            foreach (StoredProvider item in stored.Providers)
            {
                byte[] keyBytes = string.IsNullOrEmpty(item.ProtectedApiKey)
                    ? []
                    : ProtectedData.Unprotect(
                        Convert.FromBase64String(item.ProtectedApiKey),
                        optionalEntropy: null,
                        DataProtectionScope.CurrentUser);
                try
                {
                    settings.Providers.Add(new AiProviderProfile
                    {
                        Id = item.Id,
                        Name = item.Name,
                        BaseUrl = item.BaseUrl,
                        Model = item.Model,
                        ApiKey = Encoding.UTF8.GetString(keyBytes),
                        Protocol = AiProviderProtocol.OpenAiCompatible,
                        IsEnabled = item.IsEnabled,
                        IsBuiltIn = item.IsBuiltIn,
                        RequiresEndpointReview = item.Protocol != (int)AiProviderProtocol.OpenAiCompatible,
                        AvailableModels = (item.Models ?? [])
                            .Where(model => !string.IsNullOrWhiteSpace(model.Id))
                            .Select(model => new AiProviderModel
                            {
                                Id = model.Id,
                                DisplayName = string.IsNullOrWhiteSpace(model.DisplayName)
                                    ? model.Id
                                    : model.DisplayName,
                                GroupName = string.IsNullOrWhiteSpace(model.GroupName)
                                    ? "其他模型"
                                    : model.GroupName,
                                OwnedBy = model.OwnedBy,
                                Created = model.Created,
                                ReasoningSupport = model.ReasoningSupport,
                                IsEmbedding = model.IsEmbedding,
                                IsRerank = model.IsRerank,
                                Family = model.Family,
                                Status = model.Status,
                                SupportsTools = model.SupportsTools,
                                SupportsReasoning = model.SupportsReasoning,
                                SupportsStructuredOutput = model.SupportsStructuredOutput,
                                SupportsAttachments = model.SupportsAttachments,
                                SupportsTemperature = model.SupportsTemperature,
                                OpenWeights = model.OpenWeights,
                                Knowledge = model.Knowledge,
                                ReleaseDate = model.ReleaseDate,
                                LastUpdated = model.LastUpdated,
                                InputModalities = [.. (model.InputModalities ?? [])],
                                OutputModalities = [.. (model.OutputModalities ?? [])],
                                ContextLimit = model.ContextLimit,
                                InputLimit = model.InputLimit,
                                OutputLimit = model.OutputLimit,
                                InputCostPerMillion = model.InputCostPerMillion,
                                OutputCostPerMillion = model.OutputCostPerMillion,
                                ReasoningCostPerMillion = model.ReasoningCostPerMillion,
                                CacheReadCostPerMillion = model.CacheReadCostPerMillion,
                                CacheWriteCostPerMillion = model.CacheWriteCostPerMillion,
                                InputAudioCostPerMillion = model.InputAudioCostPerMillion,
                                OutputAudioCostPerMillion = model.OutputAudioCostPerMillion
                            })
                            .ToList()
                    });
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(keyBytes);
                }
            }

            return settings;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or CryptographicException)
        {
            throw new InvalidDataException(
                $"无法读取 AI 服务商配置：{path}。配置可能已损坏，或当前 Windows 用户与保存配置的用户不同。",
                ex);
        }
    }

    public void Save(AiProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        string directory = Path.GetDirectoryName(_filePath)
            ?? throw new InvalidOperationException("AI 配置文件路径无效。");
        Directory.CreateDirectory(directory);

        var stored = new StoredSettings
        {
            ActiveProviderId = settings.ActiveProviderId,
            Providers = settings.Providers.Select(profile =>
            {
                AiProviderProfileValidator.ValidateForStorage(profile);
                byte[] keyBytes = Encoding.UTF8.GetBytes(profile.ApiKey);
                byte[] protectedKey = [];
                try
                {
                    if (keyBytes.Length > 0)
                    {
                        protectedKey = ProtectedData.Protect(
                            keyBytes,
                            optionalEntropy: null,
                            DataProtectionScope.CurrentUser);
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(keyBytes);
                }

                return new StoredProvider
                {
                    Id = profile.Id,
                    Name = profile.Name,
                    BaseUrl = profile.BaseUrl,
                    Model = profile.Model,
                    Protocol = (int)AiProviderProtocol.OpenAiCompatible,
                    IsEnabled = profile.IsEnabled,
                    IsBuiltIn = profile.IsBuiltIn,
                    ProtectedApiKey = protectedKey.Length == 0 ? "" : Convert.ToBase64String(protectedKey),
                    Models = (profile.AvailableModels ?? [])
                        .Where(model => !string.IsNullOrWhiteSpace(model.Id))
                        .Select(model => new StoredModel
                        {
                            Id = model.Id,
                            DisplayName = model.DisplayName,
                            GroupName = model.GroupName,
                            OwnedBy = model.OwnedBy,
                            Created = model.Created,
                            ReasoningSupport = model.ReasoningSupport,
                            IsEmbedding = model.IsEmbedding,
                            IsRerank = model.IsRerank,
                            Family = model.Family,
                            Status = model.Status,
                            SupportsTools = model.SupportsTools,
                            SupportsReasoning = model.SupportsReasoning,
                            SupportsStructuredOutput = model.SupportsStructuredOutput,
                            SupportsAttachments = model.SupportsAttachments,
                            SupportsTemperature = model.SupportsTemperature,
                            OpenWeights = model.OpenWeights,
                            Knowledge = model.Knowledge,
                            ReleaseDate = model.ReleaseDate,
                            LastUpdated = model.LastUpdated,
                            InputModalities = [.. (model.InputModalities ?? [])],
                            OutputModalities = [.. (model.OutputModalities ?? [])],
                            ContextLimit = model.ContextLimit,
                            InputLimit = model.InputLimit,
                            OutputLimit = model.OutputLimit,
                            InputCostPerMillion = model.InputCostPerMillion,
                            OutputCostPerMillion = model.OutputCostPerMillion,
                            ReasoningCostPerMillion = model.ReasoningCostPerMillion,
                            CacheReadCostPerMillion = model.CacheReadCostPerMillion,
                            CacheWriteCostPerMillion = model.CacheWriteCostPerMillion,
                            InputAudioCostPerMillion = model.InputAudioCostPerMillion,
                            OutputAudioCostPerMillion = model.OutputAudioCostPerMillion
                        })
                        .ToList()
                };
            }).ToList()
        };

        string json = JsonSerializer.Serialize(stored, JsonOptions);
        string temporaryPath = Path.Combine(
            directory,
            $"{Path.GetFileName(_filePath)}.{Guid.NewGuid():N}.tmp");
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

    private sealed class StoredSettings
    {
        public string? ActiveProviderId { get; set; }
        public List<StoredProvider> Providers { get; set; } = [];
    }

    private sealed class StoredProvider
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string BaseUrl { get; set; } = "";
        public string Model { get; set; } = "";
        public int Protocol { get; set; } = (int)AiProviderProtocol.OpenAiCompatible;
        public bool IsEnabled { get; set; } = true;
        public bool IsBuiltIn { get; set; }
        public string ProtectedApiKey { get; set; } = "";
        public List<StoredModel>? Models { get; set; } = [];
    }

    private sealed class StoredModel
    {
        public string Id { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string GroupName { get; set; } = "其他模型";
        public string? OwnedBy { get; set; }
        public long? Created { get; set; }
        public bool? ReasoningSupport { get; set; }
        public bool IsEmbedding { get; set; }
        public bool IsRerank { get; set; }
        public string? Family { get; set; }
        public string? Status { get; set; }
        public bool SupportsTools { get; set; }
        public bool SupportsReasoning { get; set; }
        public bool SupportsStructuredOutput { get; set; }
        public bool SupportsAttachments { get; set; }
        public bool SupportsTemperature { get; set; }
        public bool OpenWeights { get; set; }
        public string? Knowledge { get; set; }
        public string? ReleaseDate { get; set; }
        public string? LastUpdated { get; set; }
        public List<string>? InputModalities { get; set; } = [];
        public List<string>? OutputModalities { get; set; } = [];
        public int? ContextLimit { get; set; }
        public int? InputLimit { get; set; }
        public int? OutputLimit { get; set; }
        public double? InputCostPerMillion { get; set; }
        public double? OutputCostPerMillion { get; set; }
        public double? ReasoningCostPerMillion { get; set; }
        public double? CacheReadCostPerMillion { get; set; }
        public double? CacheWriteCostPerMillion { get; set; }
        public double? InputAudioCostPerMillion { get; set; }
        public double? OutputAudioCostPerMillion { get; set; }
    }
}
