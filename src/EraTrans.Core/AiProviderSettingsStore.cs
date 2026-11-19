using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EraTrans.Core;

public sealed class AiProviderProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string Model { get; set; } = "";
    public string ApiKey { get; set; } = "";
}

public sealed class AiProviderSettings
{
    public List<AiProviderProfile> Providers { get; set; } = [];
    public string? ActiveProviderId { get; set; }

    public AiProviderProfile? ActiveProvider =>
        Providers.FirstOrDefault(provider => provider.Id == ActiveProviderId) ??
        Providers.FirstOrDefault();
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
/// encrypted with Windows DPAPI (CurrentUser). Existing AppData settings are
/// migrated by decrypting and re-encrypting them for the same Windows user.
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
    private readonly string? _legacyFilePath;

    public AiProviderSettingsStore(string? filePath = null, string? legacyFilePath = null)
    {
        bool useDefaultPaths = filePath is null;
        _filePath = filePath ?? EraTransPaths.AiProviderSettingsFilePath;
        _legacyFilePath = legacyFilePath ??
            (useDefaultPaths ? EraTransPaths.LegacyAiProviderSettingsFilePath : null);
    }

    public AiProviderSettings Load()
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
            // Re-save the successfully decrypted profile into the portable directory.
            // The original DPAPI-protected AppData file is retained as a backup.
            AiProviderSettings legacySettings = LoadFrom(_legacyFilePath);
            Save(legacySettings);
            return legacySettings;
        }

        return new AiProviderSettings();
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
                byte[] protectedKey = Convert.FromBase64String(item.ProtectedApiKey);
                byte[] keyBytes = ProtectedData.Unprotect(
                    protectedKey,
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
                        ApiKey = Encoding.UTF8.GetString(keyBytes)
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
                AiProviderProfileValidator.Validate(profile);
                byte[] keyBytes = Encoding.UTF8.GetBytes(profile.ApiKey);
                byte[] protectedKey;
                try
                {
                    protectedKey = ProtectedData.Protect(
                        keyBytes,
                        optionalEntropy: null,
                        DataProtectionScope.CurrentUser);
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
                    ProtectedApiKey = Convert.ToBase64String(protectedKey)
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
        public string ProtectedApiKey { get; set; } = "";
    }
}
