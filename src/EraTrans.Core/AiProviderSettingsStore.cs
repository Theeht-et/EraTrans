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
    public string ChatCompletionsPath { get; set; } = "/chat/completions";
    public string ModelsPath { get; set; } = "/models";
    public AiProviderProtocol Protocol { get; set; } = AiProviderProtocol.OpenAiCompatible;
    public bool IsEnabled { get; set; } = true;
    public bool IsBuiltIn { get; set; }
    [JsonIgnore]
    public bool RequiresEndpointReview { get; set; }
    public List<AiProviderModel> AvailableModels { get; set; } = [];

    [JsonIgnore]
    public bool IsReadyForTranslation =>
        IsEnabled &&
        Protocol == AiProviderProtocol.OpenAiCompatible &&
        !RequiresEndpointReview &&
        AiProviderProfileValidator.IsValidBaseUrl(BaseUrl) &&
        AiProviderProfileValidator.IsValidApiPath(ChatCompletionsPath, "/chat/completions") &&
        !string.IsNullOrWhiteSpace(Model) &&
        !string.IsNullOrWhiteSpace(ApiKey);
}

public sealed class AiProviderSettings
{
    public List<AiProviderProfile> Providers { get; set; } = [];
    public string? ActiveProviderId { get; set; }
    public bool BuiltInDefaultDisableMigrationApplied { get; set; }

    public AiProviderProfile? ActiveProvider =>
        Providers.FirstOrDefault(provider =>
            provider.IsReadyForTranslation &&
            string.Equals(provider.Id, ActiveProviderId, StringComparison.Ordinal)) ??
        Providers.FirstOrDefault(provider => provider.IsReadyForTranslation);

    /// <summary>
    /// Adds missing application-owned profiles while preserving the user's saved
    /// provider order and credentials. Legacy custom entries are never promoted
    /// to built-ins based only on their name or endpoint.
    /// </summary>
    public void EnsureBuiltInProviders()
    {
        if (!BuiltInDefaultDisableMigrationApplied)
        {
            foreach (AiProviderProfile builtIn in Providers.Where(provider => provider.IsBuiltIn))
                builtIn.IsEnabled = false;
            BuiltInDefaultDisableMigrationApplied = true;
        }

        var seenBuiltIns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (AiProviderProfile provider in Providers)
        {
            string canonicalName = CanonicalBuiltInName(provider.Name);
            AiProviderPreset? preset = provider.IsBuiltIn
                ? AiProviderPresets.All.FirstOrDefault(item =>
                    string.Equals(item.Name, canonicalName, StringComparison.OrdinalIgnoreCase))
                : null;

            if (preset is not null && seenBuiltIns.Add(preset.Name))
            {
                provider.Name = preset.Name;
                if (string.IsNullOrWhiteSpace(provider.BaseUrl))
                    provider.BaseUrl = preset.BaseUrl;
                provider.IsBuiltIn = true;
                continue;
            }

            provider.IsBuiltIn = false;
            if (AiProviderPresets.All.Any(item =>
                    string.Equals(item.Name, canonicalName, StringComparison.OrdinalIgnoreCase)))
            {
                provider.Name = canonicalName + " 自定义";
            }
        }

        foreach (AiProviderPreset preset in AiProviderPresets.All)
        {
            if (seenBuiltIns.Add(preset.Name))
            {
                Providers.Add(new AiProviderProfile
                {
                    Name = preset.Name,
                    BaseUrl = preset.BaseUrl,
                    Protocol = AiProviderProtocol.OpenAiCompatible,
                    IsEnabled = false,
                    IsBuiltIn = true
                });
            }
        }

        AiProviderProfile? active = Providers.FirstOrDefault(provider =>
                provider.IsReadyForTranslation &&
                string.Equals(provider.Id, ActiveProviderId, StringComparison.Ordinal)) ??
            Providers.FirstOrDefault(provider => provider.IsReadyForTranslation);
        ActiveProviderId = active?.Id;
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

    public static bool IsValidBaseUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        try
        {
            _ = NormalizeBaseUrl(value);
            return true;
        }
        catch (Exception ex) when (
            ex is ArgumentException or InvalidOperationException or UriFormatException)
        {
            return false;
        }
    }

    public static string NormalizeApiPath(string? value, string defaultPath)
    {
        string path = string.IsNullOrWhiteSpace(value) ? defaultPath : value.Trim();
        if (!path.StartsWith("/", StringComparison.Ordinal))
            path = "/" + path;

        if (path.StartsWith("//", StringComparison.Ordinal) ||
            path.Contains('?') ||
            path.Contains('#') ||
            path.Contains('\\') ||
            path.Contains("://", StringComparison.Ordinal) ||
            path.Split('/').Any(segment => segment is "." or ".."))
        {
            throw new InvalidOperationException(
                "API 路径必须是站点内的相对路径，例如 /chat/completions，不能包含域名、查询参数或片段。");
        }

        return path;
    }

    public static bool IsValidApiPath(string? value, string defaultPath)
    {
        try
        {
            _ = NormalizeApiPath(value, defaultPath);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public static string BuildApiEndpoint(string baseUrl, string? apiPath, string defaultPath)
    {
        string normalizedBaseUrl = NormalizeBaseUrl(baseUrl).TrimEnd('/');
        string normalizedPath = NormalizeApiPath(apiPath, defaultPath);
        return normalizedBaseUrl + normalizedPath;
    }

    public static void ValidateForStorage(AiProviderProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(profile.Name))
            throw new InvalidOperationException("请填写服务商名称。");
        if (!string.IsNullOrWhiteSpace(profile.BaseUrl))
            profile.BaseUrl = NormalizeBaseUrl(profile.BaseUrl);
        profile.ChatCompletionsPath = NormalizeApiPath(
            profile.ChatCompletionsPath, "/chat/completions");
        profile.ModelsPath = string.IsNullOrWhiteSpace(profile.ModelsPath)
            ? "/models"
            : profile.ModelsPath.Trim();
    }

    public static void ValidateForModelDiscovery(AiProviderProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(profile.Name))
            throw new InvalidOperationException("请填写服务商名称。");

        profile.BaseUrl = NormalizeBaseUrl(profile.BaseUrl);
        profile.ModelsPath = NormalizeApiPath(profile.ModelsPath, "/models");
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
        profile.ChatCompletionsPath = NormalizeApiPath(
            profile.ChatCompletionsPath, "/chat/completions");
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
        if (!File.Exists(_filePath))
            return new AiProviderSettings();

        AiProviderSettings settings = LoadFrom(_filePath);
        ApplyBuiltInDefaultDisableMigration(settings);
        return settings;
    }

    private static void ApplyBuiltInDefaultDisableMigration(AiProviderSettings settings)
    {
        if (settings.BuiltInDefaultDisableMigrationApplied)
            return;

        foreach (AiProviderProfile provider in settings.Providers.Where(provider => provider.IsBuiltIn))
            provider.IsEnabled = false;

        settings.BuiltInDefaultDisableMigrationApplied = true;
    }

    private static AiProviderSettings LoadFrom(string path)
    {
        try
        {
            string json = File.ReadAllText(path, Encoding.UTF8);
            StoredSettings stored = JsonSerializer.Deserialize<StoredSettings>(json, JsonOptions)
                ?? throw new InvalidDataException("AI 服务商配置文件为空或格式无效。");

            if (stored.Providers is null)
                throw new InvalidDataException("AI 服务商配置缺少 Providers 列表。");

            var settings = new AiProviderSettings
            {
                ActiveProviderId = stored.ActiveProviderId,
                BuiltInDefaultDisableMigrationApplied = stored.BuiltInDefaultDisableMigrationApplied
            };
            var providerIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (StoredProvider item in stored.Providers)
            {
                if (item is null ||
                    string.IsNullOrWhiteSpace(item.Id) ||
                    string.IsNullOrWhiteSpace(item.Name))
                {
                    throw new InvalidDataException("AI 服务商配置中存在缺少 ID 或名称的项目。");
                }
                if (!providerIds.Add(item.Id))
                    throw new InvalidDataException($"AI 服务商配置包含重复 ID：{item.Id}。");

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
                        BaseUrl = item.BaseUrl ?? "",
                        Model = item.Model ?? "",
                        ApiKey = Encoding.UTF8.GetString(keyBytes),
                        ChatCompletionsPath = AiProviderProfileValidator.NormalizeApiPath(
                            item.ChatCompletionsPath, "/chat/completions"),
                        ModelsPath = string.IsNullOrWhiteSpace(item.ModelsPath)
                            ? "/models"
                            : item.ModelsPath.Trim(),
                        Protocol = AiProviderProtocol.OpenAiCompatible,
                        IsEnabled = item.IsEnabled,
                        IsBuiltIn = item.IsBuiltIn,
                        RequiresEndpointReview = item.RequiresEndpointReview ||
                            item.Protocol != (int)AiProviderProtocol.OpenAiCompatible,
                        AvailableModels = (item.Models ?? [])
                            .Where(model => model is not null && !string.IsNullOrWhiteSpace(model.Id))
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
                                ReasoningLevels = [.. (model.ReasoningLevels ?? [])],
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
        if (settings.Providers is null)
            throw new InvalidDataException("AI 服务商列表不能为 null。");

        var providerIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (AiProviderProfile profile in settings.Providers)
        {
            if (profile is null || string.IsNullOrWhiteSpace(profile.Id))
                throw new InvalidDataException("AI 服务商配置包含无效项目或空 ID。");
            if (!providerIds.Add(profile.Id))
                throw new InvalidDataException($"AI 服务商配置包含重复 ID：{profile.Id}。");
        }

        string directory = Path.GetDirectoryName(_filePath)
            ?? throw new InvalidOperationException("AI 配置文件路径无效。");
        Directory.CreateDirectory(directory);

        var stored = new StoredSettings
        {
            ActiveProviderId = settings.ActiveProviderId,
            BuiltInDefaultDisableMigrationApplied = settings.BuiltInDefaultDisableMigrationApplied,
            Providers = settings.Providers.Select(profile =>
            {
                AiProviderProfileValidator.ValidateForStorage(profile);
                byte[] keyBytes = Encoding.UTF8.GetBytes(profile.ApiKey ?? "");
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
                    ChatCompletionsPath = AiProviderProfileValidator.NormalizeApiPath(
                        profile.ChatCompletionsPath, "/chat/completions"),
                    ModelsPath = string.IsNullOrWhiteSpace(profile.ModelsPath)
                        ? "/models"
                        : profile.ModelsPath.Trim(),
                    Protocol = (int)AiProviderProtocol.OpenAiCompatible,
                    IsEnabled = profile.IsEnabled,
                    IsBuiltIn = profile.IsBuiltIn,
                    RequiresEndpointReview = profile.RequiresEndpointReview,
                    ProtectedApiKey = protectedKey.Length == 0 ? "" : Convert.ToBase64String(protectedKey),
                    Models = (profile.AvailableModels ?? [])
                        .Where(model => model is not null && !string.IsNullOrWhiteSpace(model.Id))
                        .Select(model => new StoredModel
                        {
                            Id = model.Id,
                            DisplayName = model.DisplayName,
                            GroupName = model.GroupName,
                            OwnedBy = model.OwnedBy,
                            Created = model.Created,
                            ReasoningSupport = model.ReasoningSupport,
                            ReasoningLevels = [.. (model.ReasoningLevels ?? [])],
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
        AtomicFileWriter.WriteAllText(_filePath, json);
    }

    private sealed class StoredSettings
    {
        public string? ActiveProviderId { get; set; }
        public bool BuiltInDefaultDisableMigrationApplied { get; set; }
        public List<StoredProvider> Providers { get; set; } = [];
    }

    private sealed class StoredProvider
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string BaseUrl { get; set; } = "";
        public string Model { get; set; } = "";
        public string ChatCompletionsPath { get; set; } = "/chat/completions";
        public string ModelsPath { get; set; } = "/models";
        public int Protocol { get; set; } = (int)AiProviderProtocol.OpenAiCompatible;
        public bool IsEnabled { get; set; } = true;
        public bool IsBuiltIn { get; set; }
        public bool RequiresEndpointReview { get; set; }
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
        public List<string>? ReasoningLevels { get; set; } = [];
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
