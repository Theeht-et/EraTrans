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
    public string? Family { get; set; }
    public string? Status { get; set; }
    public bool SupportsTools { get; set; }
    public bool SupportsReasoning { get; set; }
    public bool SupportsStructuredOutput { get; set; }
    public List<string> InputModalities { get; set; } = [];
    public List<string> OutputModalities { get; set; } = [];
    public int? ContextLimit { get; set; }
    public int? InputLimit { get; set; }
    public int? OutputLimit { get; set; }

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

            if (ContextLimit is > 0)
            {
                parts.Add($"上下文 {FormatTokenLimit(ContextLimit.Value)}");
            }

            if (OutputLimit is > 0)
            {
                parts.Add($"最大输出 {FormatTokenLimit(OutputLimit.Value)}");
            }

            if (InputModalities.Contains("image", StringComparer.OrdinalIgnoreCase))
            {
                parts.Add("图像输入");
            }

            if (OutputModalities.Contains("image", StringComparer.OrdinalIgnoreCase))
            {
                parts.Add("图像输出");
            }

            if (SupportsTools)
            {
                parts.Add("工具调用");
            }

            if (SupportsReasoning)
            {
                parts.Add("推理");
            }

            if (SupportsStructuredOutput)
            {
                parts.Add("结构化输出");
            }

            if (IsEmbedding)
            {
                parts.Add("嵌入模型");
            }
            else if (IsRerank)
            {
                parts.Add("重排模型");
            }

            if (!string.IsNullOrWhiteSpace(Status) &&
                !Status.Equals("active", StringComparison.OrdinalIgnoreCase))
            {
                parts.Add(Status);
            }

            return string.Join(" · ", parts);
        }
    }

    private static string FormatTokenLimit(int value) =>
        value >= 1_000_000
            ? $"{value / 1_000_000d:0.#}M"
            : value >= 1_000
                ? $"{value / 1_000d:0.#}K"
                : value.ToString();
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
            string modelId = normalized[(slash + 1)..];
            string? vendorGroup = vendor switch
            {
                "openai" => "OpenAI",
                "anthropic" => "Claude",
                "google" => "Gemini",
                "deepseek" => "DeepSeek",
                "moonshotai" => "Kimi",
                "alibaba" or "qwen" => "Qwen",
                "z-ai" or "zai" => "GLM",
                "mistralai" => "Mistral",
                "meta-llama" => "Llama",
                "x-ai" => "Grok",
                "bytedance" or "volcengine" => "Doubao",
                "cohere" => "Cohere",
                _ => null
            };

            return vendorGroup is null
                ? vendor
                : GetFamilyGroup(modelId, vendorGroup);
        }

        string? knownGroup = normalized.StartsWith("gpt-", StringComparison.Ordinal) ||
                             normalized.StartsWith("chatgpt", StringComparison.Ordinal) ||
                             normalized.StartsWith("o1", StringComparison.Ordinal) ||
                             normalized.StartsWith("o3", StringComparison.Ordinal) ||
                             normalized.StartsWith("o4", StringComparison.Ordinal)
            ? "OpenAI"
            : normalized.StartsWith("claude", StringComparison.Ordinal) ? "Claude"
            : normalized.StartsWith("gemini", StringComparison.Ordinal) ? "Gemini"
            : normalized.StartsWith("deepseek", StringComparison.Ordinal) ? "DeepSeek"
            : normalized.StartsWith("qwen", StringComparison.Ordinal) ||
              normalized.StartsWith("qwq", StringComparison.Ordinal) ||
              normalized.StartsWith("qvq", StringComparison.Ordinal) ? "Qwen"
            : normalized.StartsWith("glm", StringComparison.Ordinal) ||
              normalized.StartsWith("chatglm", StringComparison.Ordinal) ? "GLM"
            : normalized.StartsWith("doubao", StringComparison.Ordinal) ||
              normalized.StartsWith("ark", StringComparison.Ordinal) ? "Doubao"
            : normalized.StartsWith("kimi", StringComparison.Ordinal) ||
              normalized.StartsWith("moonshot", StringComparison.Ordinal) ? "Kimi"
            : normalized.StartsWith("llama", StringComparison.Ordinal) ? "Llama"
            : normalized.StartsWith("mistral", StringComparison.Ordinal) ||
              normalized.StartsWith("mixtral", StringComparison.Ordinal) ? "Mistral"
            : normalized.StartsWith("grok", StringComparison.Ordinal) ? "Grok"
            : normalized.StartsWith("command", StringComparison.Ordinal) ? "Cohere"
            : normalized.StartsWith("ernie", StringComparison.Ordinal) ? "ERNIE"
            : normalized.StartsWith("yi-", StringComparison.Ordinal) ? "Yi"
            : null;

        if (knownGroup is not null)
        {
            return GetFamilyGroup(normalized, knownGroup);
        }

        return string.IsNullOrWhiteSpace(ownedBy)
            ? "其他模型"
            : NormalizeOwnerGroup(ownedBy);
    }

    private static string GetFamilyGroup(string modelId, string vendor)
    {
        string model = modelId.ToLowerInvariant();
        return vendor switch
        {
            "OpenAI" when model.StartsWith("gpt-5", StringComparison.Ordinal) => "OpenAI · GPT-5",
            "OpenAI" when model.StartsWith("gpt-4.1", StringComparison.Ordinal) => "OpenAI · GPT-4.1",
            "OpenAI" when model.StartsWith("gpt-4o", StringComparison.Ordinal) => "OpenAI · GPT-4o",
            "OpenAI" when model.StartsWith("o1", StringComparison.Ordinal) => "OpenAI · O1",
            "OpenAI" when model.StartsWith("o3", StringComparison.Ordinal) => "OpenAI · O3",
            "OpenAI" when model.StartsWith("o4", StringComparison.Ordinal) => "OpenAI · O4",
            "Claude" when model.Contains("opus", StringComparison.Ordinal) => "Claude · Opus",
            "Claude" when model.Contains("sonnet", StringComparison.Ordinal) => "Claude · Sonnet",
            "Claude" when model.Contains("haiku", StringComparison.Ordinal) => "Claude · Haiku",
            "Claude" when model.Contains("claude-4", StringComparison.Ordinal) => "Claude 4",
            "Claude" when model.Contains("claude-3.7", StringComparison.Ordinal) => "Claude 3.7",
            "Claude" when model.Contains("claude-3.5", StringComparison.Ordinal) ||
                          model.Contains("claude-3-5", StringComparison.Ordinal) => "Claude 3.5",
            "Gemini" when model.Contains("gemini-3", StringComparison.Ordinal) => "Gemini 3",
            "Gemini" when model.Contains("gemini-2.5", StringComparison.Ordinal) => "Gemini 2.5",
            "Gemini" when model.Contains("gemini-2.0", StringComparison.Ordinal) => "Gemini 2.0",
            "DeepSeek" when model.Contains("r1", StringComparison.Ordinal) => "DeepSeek · R1",
            "DeepSeek" when model.Contains("v3", StringComparison.Ordinal) => "DeepSeek · V3",
            "Qwen" when model.Contains("qwen3", StringComparison.Ordinal) => "Qwen 3",
            "Qwen" when model.Contains("qwen2.5", StringComparison.Ordinal) => "Qwen 2.5",
            "Grok" when model.Contains("grok-4", StringComparison.Ordinal) => "Grok 4",
            "Grok" when model.Contains("grok-3", StringComparison.Ordinal) => "Grok 3",
            _ => vendor
        };
    }

    private static string NormalizeOwnerGroup(string owner)
    {
        string value = owner.Trim().ToLowerInvariant();
        return value switch
        {
            "openai" => "OpenAI",
            "anthropic" => "Claude",
            "google" or "google-ai" => "Gemini",
            "deepseek" => "DeepSeek",
            "qwen" or "alibaba" => "Qwen",
            "zhipu" or "zhipuai" or "z-ai" => "GLM",
            "bytedance" or "volcengine" => "Doubao",
            "moonshot" or "moonshotai" => "Kimi",
            "meta" or "meta-llama" => "Llama",
            "mistral" or "mistralai" => "Mistral",
            "xai" or "x-ai" => "Grok",
            "cohere" => "Cohere",
            _ => owner.Trim()
        };
    }
}
