using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace EraTrans.Core;

/// <summary>
/// One model returned by an OpenAI-compatible GET /models endpoint.
/// The raw model id remains the value used when sending chat requests.
/// </summary>
public sealed class AiProviderModel
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string GroupName { get; set; } = "其他模型";
    public string? OwnedBy { get; set; }
    public long? Created { get; set; }
    public bool IsEmbedding { get; set; }
    public bool IsRerank { get; set; }

    public bool IsChatModel => !IsEmbedding && !IsRerank;

    public string MetadataSummary
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(OwnedBy))
            {
                parts.Add(OwnedBy);
            }

            if (Created is > 0)
            {
                try
                {
                    parts.Add(DateTimeOffset.FromUnixTimeSeconds(Created.Value)
                        .ToLocalTime()
                        .ToString("yyyy-MM-dd"));
                }
                catch (ArgumentOutOfRangeException)
                {
                    // Some compatible endpoints return non-standard timestamps.
                }
            }

            if (IsEmbedding)
            {
                parts.Add("嵌入模型");
            }
            else if (IsRerank)
            {
                parts.Add("重排模型");
            }

            return string.Join(" · ", parts);
        }
    }
}

/// <summary>
/// Fetches the model inventory exposed by an OpenAI-compatible provider.
/// Unsupported or unavailable model-list endpoints leave manual model entry available.
/// </summary>
public sealed class AiModelDiscoveryService
{
    private static readonly HttpClient SharedHttpClient = new();
    private readonly HttpClient _httpClient;

    public AiModelDiscoveryService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? SharedHttpClient;
    }

    public async Task<IReadOnlyList<AiProviderModel>> FetchModelsAsync(
        AiProviderProfile profile,
        CancellationToken cancellationToken = default)
    {
        AiProviderProfileValidator.ValidateForModelDiscovery(profile);

        string baseUrl = AiProviderProfileValidator.NormalizeBaseUrl(profile.BaseUrl);
        var endpoint = new Uri($"{baseUrl.TrimEnd('/')}/models", UriKind.Absolute);
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (!string.IsNullOrWhiteSpace(profile.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                profile.ApiKey.Trim());
        }

        using HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            string detail = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "API Key 无效或没有访问模型列表的权限。",
                HttpStatusCode.Forbidden => "当前 API Key 没有访问模型列表的权限。",
                HttpStatusCode.NotFound => "服务商未提供 GET /models 接口，请手动填写模型 ID。",
                HttpStatusCode.TooManyRequests => "请求过于频繁，请稍后重试。",
                _ => "请检查 Base URL、API Key 和服务商接口兼容性。"
            };

            throw new HttpRequestException(
                $"获取模型列表失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}。{detail}",
                inner: null,
                statusCode: response.StatusCode);
        }

        string content = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseModelsResponse(content);
    }

    public static IReadOnlyList<AiProviderModel> ParseModelsResponse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement rows = FindModelRows(document.RootElement);
        if (rows.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                "模型列表响应中没有找到 data 数组。请确认该服务商的 GET /models 接口是否兼容 OpenAI 格式。");
        }

        var models = new List<AiProviderModel>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? id = ReadString(row, "id")
                ?? ReadString(row, "model")
                ?? ReadString(row, "name");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            id = id.Trim();
            if (!seen.Add(id))
            {
                continue;
            }

            string displayName = ReadString(row, "display_name")
                ?? ReadString(row, "displayName")
                ?? ReadString(row, "name")
                ?? id;
            string? ownedBy = ReadString(row, "owned_by")
                ?? ReadString(row, "ownedBy")
                ?? ReadString(row, "provider")
                ?? ReadString(row, "owner");
            long? created = ReadInt64(row, "created");

            bool isEmbedding = LooksLikeEmbeddingModel(id);
            bool isRerank = LooksLikeRerankModel(id);
            models.Add(new AiProviderModel
            {
                Id = id,
                DisplayName = displayName,
                GroupName = GetGroupName(id, ownedBy, isEmbedding, isRerank),
                OwnedBy = ownedBy,
                Created = created,
                IsEmbedding = isEmbedding,
                IsRerank = isRerank
            });
        }

        return models
            .OrderBy(model => model.GroupName == "其他模型" ? 1 : 0)
            .ThenBy(model => model.GroupName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static JsonElement FindModelRows(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return default;
        }

        if (TryGetProperty(root, "data", out JsonElement data) &&
            data.ValueKind == JsonValueKind.Array)
        {
            return data;
        }

        if (TryGetProperty(root, "models", out JsonElement models) &&
            models.ValueKind == JsonValueKind.Array)
        {
            return models;
        }

        if (TryGetProperty(root, "result", out JsonElement result) &&
            result.ValueKind == JsonValueKind.Object &&
            TryGetProperty(result, "data", out JsonElement resultData) &&
            resultData.ValueKind == JsonValueKind.Array)
        {
            return resultData;
        }

        return default;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (JsonProperty property in element.EnumerateObject())
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

    private static string? ReadString(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? text = value.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static long? ReadInt64(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String &&
            long.TryParse(value.GetString(), out number))
        {
            return number;
        }

        return null;
    }

    private static bool LooksLikeEmbeddingModel(string id)
    {
        string value = id.ToLowerInvariant();
        return value.Contains("embedding", StringComparison.Ordinal) ||
               value.Contains("embed-", StringComparison.Ordinal) ||
               value.StartsWith("bge-", StringComparison.Ordinal) ||
               value.StartsWith("e5-", StringComparison.Ordinal) ||
               value.StartsWith("gte-", StringComparison.Ordinal);
    }

    private static bool LooksLikeRerankModel(string id)
    {
        return id.Contains("rerank", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetGroupName(
        string id,
        string? ownedBy,
        bool isEmbedding,
        bool isRerank)
    {
        if (isEmbedding)
        {
            return "嵌入模型";
        }

        if (isRerank)
        {
            return "重排模型";
        }

        string normalized = id.Trim().ToLowerInvariant();
        if (normalized.StartsWith("models/", StringComparison.Ordinal))
        {
            normalized = normalized["models/".Length..];
        }

        // Aggregators often prefix model ids with a vendor, e.g.
        // anthropic/claude-sonnet-4 or openai/gpt-4o.
        int slash = normalized.IndexOf('/');
        if (slash > 0)
        {
            string vendor = normalized[..slash];
            return vendor switch
            {
                "x-ai" => "Grok",
                "meta-llama" => "Llama",
                "mistralai" => "Mistral",
                "qwen" => "Qwen",
                _ => vendor
            };
        }

        if (normalized.StartsWith("gpt-", StringComparison.Ordinal) ||
            normalized.StartsWith("chatgpt", StringComparison.Ordinal) ||
            normalized.StartsWith("o1", StringComparison.Ordinal) ||
            normalized.StartsWith("o3", StringComparison.Ordinal) ||
            normalized.StartsWith("o4", StringComparison.Ordinal))
        {
            return "OpenAI";
        }

        if (normalized.StartsWith("claude", StringComparison.Ordinal)) return "Claude";
        if (normalized.StartsWith("gemini", StringComparison.Ordinal)) return "Gemini";
        if (normalized.StartsWith("deepseek", StringComparison.Ordinal)) return "DeepSeek";
        if (normalized.StartsWith("qwen", StringComparison.Ordinal) ||
            normalized.StartsWith("qwq", StringComparison.Ordinal) ||
            normalized.StartsWith("qvq", StringComparison.Ordinal)) return "Qwen";
        if (normalized.StartsWith("glm", StringComparison.Ordinal) ||
            normalized.StartsWith("chatglm", StringComparison.Ordinal)) return "GLM";
        if (normalized.StartsWith("doubao", StringComparison.Ordinal) ||
            normalized.StartsWith("ark", StringComparison.Ordinal)) return "Doubao";
        if (normalized.StartsWith("kimi", StringComparison.Ordinal) ||
            normalized.StartsWith("moonshot", StringComparison.Ordinal)) return "Kimi";
        if (normalized.StartsWith("llama", StringComparison.Ordinal)) return "Llama";
        if (normalized.StartsWith("mistral", StringComparison.Ordinal) ||
            normalized.StartsWith("mixtral", StringComparison.Ordinal)) return "Mistral";
        if (normalized.StartsWith("grok", StringComparison.Ordinal)) return "Grok";
        if (normalized.StartsWith("command", StringComparison.Ordinal)) return "Cohere";
        if (normalized.StartsWith("ernie", StringComparison.Ordinal)) return "ERNIE";
        if (normalized.StartsWith("yi-", StringComparison.Ordinal)) return "Yi";

        return string.IsNullOrWhiteSpace(ownedBy) ? "其他模型" : ownedBy.Trim();
    }
}
