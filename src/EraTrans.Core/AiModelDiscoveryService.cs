using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace EraTrans.Core;

/// <summary>
/// One model returned by a supported AI provider model-list endpoint.
/// The raw model id remains the value used when sending chat requests.
/// </summary>
public sealed class AiProviderModel
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string GroupName { get; set; } = "其他模型";
    public string? OwnedBy { get; set; }
    public long? Created { get; set; }
    public bool? ReasoningSupport { get; set; }
    public List<string> ReasoningLevels { get; set; } = [];
    public bool? ReasoningCanDisable { get; set; }
    public string? ReasoningDialect { get; set; }
    public int? ReasoningBudgetMin { get; set; }
    public int? ReasoningBudgetMax { get; set; }
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
    public List<string> InputModalities { get; set; } = [];
    public List<string> OutputModalities { get; set; } = [];
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

    public bool IsChatModel => !IsEmbedding && !IsRerank;

    public bool HasKnownReasoningSupport =>
        ReasoningSupport.HasValue || SupportsReasoning;

    public bool IsReasoningSupported =>
        ReasoningSupport ?? SupportsReasoning;

    public string ReasoningSupportDescription =>
        !HasKnownReasoningSupport
            ? ""
            : IsReasoningSupported ? "推理支持" : "不支持推理";

    public bool HasKnownContextLimit => ContextLimit is > 0;

    public string ContextLimitDescription => HasKnownContextLimit
        ? FormatTokenLimit(ContextLimit!.Value)
        : "";

    public string ContextLimitLabel => HasKnownContextLimit
        ? $"上下文大小：{ContextLimitDescription}"
        : "";

    public string MetadataSummary
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(OwnedBy))
            {
                parts.Add(OwnedBy);
            }

            if (!string.IsNullOrWhiteSpace(Family) &&
                !Family.Equals(GroupName, StringComparison.OrdinalIgnoreCase))
            {
                parts.Add(Family);
            }

            if (InputLimit is > 0)
            {
                parts.Add($"最大输入 {FormatTokenLimit(InputLimit.Value)}");
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

            if (InputModalities.Contains("audio", StringComparer.OrdinalIgnoreCase))
            {
                parts.Add("音频输入");
            }

            if (OutputModalities.Contains("audio", StringComparer.OrdinalIgnoreCase))
            {
                parts.Add("音频输出");
            }

            if (InputModalities.Contains("video", StringComparer.OrdinalIgnoreCase))
            {
                parts.Add("视频输入");
            }

            if (InputModalities.Contains("pdf", StringComparer.OrdinalIgnoreCase))
            {
                parts.Add("PDF 输入");
            }

            if (SupportsStructuredOutput)
            {
                parts.Add("结构化输出");
            }

            if (SupportsAttachments)
            {
                parts.Add("文件附件");
            }

            if (SupportsTemperature)
            {
                parts.Add("支持温度参数");
            }

            if (OpenWeights)
            {
                parts.Add("开放权重");
            }

            if (!string.IsNullOrWhiteSpace(Knowledge))
            {
                parts.Add($"知识截止 {Knowledge}");
            }

            if (!string.IsNullOrWhiteSpace(ReleaseDate))
            {
                parts.Add($"发布 {ReleaseDate}");
            }

            if (InputCostPerMillion is not null)
            {
                parts.Add($"输入 ${FormatCost(InputCostPerMillion.Value)}/1M");
            }

            if (OutputCostPerMillion is not null)
            {
                parts.Add($"输出 ${FormatCost(OutputCostPerMillion.Value)}/1M");
            }

            if (ReasoningCostPerMillion is not null)
            {
                parts.Add($"推理 ${FormatCost(ReasoningCostPerMillion.Value)}/1M");
            }

            if (CacheReadCostPerMillion is not null)
            {
                parts.Add($"缓存读 ${FormatCost(CacheReadCostPerMillion.Value)}/1M");
            }

            if (CacheWriteCostPerMillion is not null)
            {
                parts.Add($"缓存写 ${FormatCost(CacheWriteCostPerMillion.Value)}/1M");
            }

            if (InputAudioCostPerMillion is not null)
            {
                parts.Add($"音频输入 ${FormatCost(InputAudioCostPerMillion.Value)}/1M");
            }

            if (OutputAudioCostPerMillion is not null)
            {
                parts.Add($"音频输出 ${FormatCost(OutputAudioCostPerMillion.Value)}/1M");
            }

            if (!string.IsNullOrWhiteSpace(LastUpdated))
            {
                parts.Add($"更新 {LastUpdated}");
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
                parts.Add(Status.Equals("deprecated", StringComparison.OrdinalIgnoreCase)
                    ? "已弃用"
                    : Status.Equals("preview", StringComparison.OrdinalIgnoreCase)
                        ? "预览"
                        : Status);
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

    private static string FormatCost(double value) =>
        value.ToString("0.####", CultureInfo.InvariantCulture);
}

/// <summary>
/// Fetches model inventories through the OpenAI-compatible GET /models endpoint.
/// Unsupported model-list endpoints leave manual model entry available.
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
        string endpointText = AiProviderProfileValidator.BuildApiEndpoint(
            profile.BaseUrl, profile.ModelsPath, "/models");
        var endpoint = new Uri(endpointText, UriKind.Absolute);
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(profile.ApiKey))
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", profile.ApiKey.Trim());

        using HttpResponseMessage response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string detail = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "API Key 无效或没有访问模型列表的权限。",
                HttpStatusCode.Forbidden => "当前 API Key 没有访问模型列表的权限。",
                HttpStatusCode.NotFound => "服务商未提供 OpenAI 兼容的 GET /models 接口。请检查 API 地址，也可以手动填写模型 ID。",
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
            List<string> reasoningLevels = ReadReasoningLevels(row);
            bool hasNoneEffort = reasoningLevels.Any(level =>
                level.Equals("none", StringComparison.OrdinalIgnoreCase));
            reasoningLevels = reasoningLevels
                .Where(level => !level.Equals("none", StringComparison.OrdinalIgnoreCase))
                .ToList();
            bool? reasoningCanDisable =
                ReadNullableBoolean(row, "reasoning_can_disable")
                ?? ReadNullableBoolean(row, "can_disable")
                ?? ReadNestedBoolean(row, "reasoning", "can_disable")
                ?? ReadNestedBoolean(row, "reasoning", "canDisable")
                ?? (hasNoneEffort ? true : null);
            bool? reasoningSupport = ReadNullableBoolean(row, "reasoning")
                ?? ReadNullableBoolean(row, "supports_reasoning")
                ?? ReadNullableBoolean(row, "thinking")
                ?? ReadNestedBoolean(row, "reasoning", "supported")
                ?? ReadNestedBoolean(row, "thinking", "supported")
                ?? ReadNestedBoolean(row, "capabilities", "reasoning");
            reasoningCanDisable ??= ReadCatalogReasoningCanDisable(row, hasNoneEffort);
            if (reasoningSupport is null &&
                (reasoningLevels.Count > 0 || hasNoneEffort || HasProviderReasoningOptions(row)))
                reasoningSupport = true;

            models.Add(new AiProviderModel
            {
                Id = id,
                DisplayName = displayName,
                GroupName = GetGroupName(id, ownedBy, isEmbedding, isRerank),
                OwnedBy = ownedBy,
                Created = created,
                ContextLimit =
                    ReadFirstInt32(row, "context_length", "context_window", "context_limit",
                        "max_context_length", "max_context_window", "inputTokenLimit", "input_token_limit")
                    ?? ReadNestedInt(row, "top_provider", "context_length")
                    ?? ReadNestedInt(row, "limit", "context"),
                InputLimit =
                    ReadFirstInt32(row, "max_input_tokens", "input_limit"),
                OutputLimit =
                    ReadFirstInt32(row, "max_output_tokens", "output_limit", "max_tokens",
                        "max_completion_tokens", "outputTokenLimit", "output_token_limit")
                    ?? ReadNestedInt(row, "top_provider", "max_completion_tokens")
                    ?? ReadNestedInt(row, "limit", "output"),
                ReasoningSupport = reasoningSupport,
                ReasoningLevels = reasoningLevels,
                ReasoningCanDisable = reasoningCanDisable,
                ReasoningDialect = ReadString(row, "reasoning_dialect")
                    ?? ReadNestedString(row, "reasoning", "dialect"),
                SupportsReasoning = reasoningSupport == true,
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

    private static int? ReadFirstInt32(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            long? value = ReadInt64(element, name);
            if (value is > 0 and <= int.MaxValue)
                return (int)value.Value;
        }

        return null;
    }

    private static bool? ReadCatalogReasoningCanDisable(JsonElement row, bool hasNoneEffort)
    {
        if (hasNoneEffort)
            return true;

        bool hasEffortOption = false;
        foreach (string propertyName in new[] { "reasoning_options", "reasoningOptions" })
        {
            if (!TryGetProperty(row, propertyName, out JsonElement options) ||
                options.ValueKind != JsonValueKind.Array)
                continue;

            foreach (JsonElement option in options.EnumerateArray())
            {
                if (option.ValueKind != JsonValueKind.Object)
                    continue;

                string? type = ReadString(option, "type");
                if (type?.Equals("toggle", StringComparison.OrdinalIgnoreCase) == true)
                    return true;

                if (type?.Equals("effort", StringComparison.OrdinalIgnoreCase) != true ||
                    !TryGetProperty(option, "values", out JsonElement values) ||
                    values.ValueKind != JsonValueKind.Array)
                    continue;

                hasEffortOption = true;
                if (values.EnumerateArray().Any(value =>
                    value.ValueKind == JsonValueKind.String &&
                    string.Equals(value.GetString(), "none", StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
        }

        if (TryGetProperty(row, "effort", out JsonElement effort) &&
            effort.ValueKind == JsonValueKind.Object &&
            TryGetProperty(effort, "supported_levels", out JsonElement topLevels) &&
            topLevels.ValueKind == JsonValueKind.Array &&
            topLevels.GetArrayLength() > 0)
        {
            hasEffortOption = true;
            if (topLevels.EnumerateArray().Any(value =>
                value.ValueKind == JsonValueKind.String &&
                string.Equals(value.GetString(), "none", StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        if (TryGetProperty(row, "reasoning", out JsonElement reasoning) &&
            reasoning.ValueKind == JsonValueKind.Object &&
            TryGetProperty(reasoning, "effort", out JsonElement nestedEffort) &&
            nestedEffort.ValueKind == JsonValueKind.Object &&
            TryGetProperty(nestedEffort, "supported_levels", out JsonElement nestedLevels) &&
            nestedLevels.ValueKind == JsonValueKind.Array &&
            nestedLevels.GetArrayLength() > 0)
        {
            hasEffortOption = true;
            if (nestedLevels.EnumerateArray().Any(value =>
                value.ValueKind == JsonValueKind.String &&
                string.Equals(value.GetString(), "none", StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        return hasEffortOption ? false : null;
    }

    private static bool HasProviderReasoningOptions(JsonElement row)
    {
        foreach (string propertyName in new[] { "reasoning_options", "reasoningOptions" })
        {
            if (!TryGetProperty(row, propertyName, out JsonElement options) ||
                options.ValueKind != JsonValueKind.Array)
                continue;

            foreach (JsonElement option in options.EnumerateArray())
            {
                if (option.ValueKind != JsonValueKind.Object)
                    continue;
                string? type = ReadString(option, "type");
                if (type is not null &&
                    (type.Equals("effort", StringComparison.OrdinalIgnoreCase) ||
                     type.Equals("budget_tokens", StringComparison.OrdinalIgnoreCase) ||
                     type.Equals("toggle", StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
        }

        if (TryGetProperty(row, "effort", out JsonElement effort) &&
            effort.ValueKind == JsonValueKind.Object &&
            TryGetProperty(effort, "supported_levels", out JsonElement levels) &&
            levels.ValueKind == JsonValueKind.Array &&
            levels.GetArrayLength() > 0)
            return true;

        return false;
    }

    private static List<string> ReadReasoningLevels(JsonElement row)
    {
        var levels = new List<string>();
        foreach (string propertyName in new[]
        {
            "supported_reasoning_levels", "reasoning_efforts", "reasoningLevels",
            "reasoning_levels"
        })
        {
            ReadStringArray(row, propertyName, levels);
        }

        if (TryGetProperty(row, "effort", out JsonElement topLevelEffort) &&
            topLevelEffort.ValueKind == JsonValueKind.Object)
        {
            ReadStringArray(topLevelEffort, "supported_levels", levels);
        }

        foreach (string objectName in new[] { "reasoning", "thinking" })
        {
            if (!TryGetProperty(row, objectName, out JsonElement nested) ||
                nested.ValueKind != JsonValueKind.Object)
                continue;

            foreach (string propertyName in new[] { "effort", "levels", "supported_levels", "supportedReasoningLevels" })
                ReadStringArray(nested, propertyName, levels);

            if (TryGetProperty(nested, "effort", out JsonElement nestedEffort) &&
                nestedEffort.ValueKind == JsonValueKind.Object)
                ReadStringArray(nestedEffort, "supported_levels", levels);
        }

        foreach (string propertyName in new[] { "reasoning_options", "reasoningOptions" })
        {
            if (!TryGetProperty(row, propertyName, out JsonElement options) ||
                options.ValueKind != JsonValueKind.Array)
                continue;
            foreach (JsonElement option in options.EnumerateArray())
            {
                if (option.ValueKind == JsonValueKind.Object &&
                    string.Equals(ReadString(option, "type"), "effort", StringComparison.OrdinalIgnoreCase))
                    ReadStringArray(option, "values", levels);
            }
        }

        return levels
            .Select(value => value.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void ReadStringArray(JsonElement element, string propertyName, List<string> output)
    {
        if (!TryGetProperty(element, propertyName, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Array)
            return;

        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(item.GetString()))
                output.Add(item.GetString()!);
        }
    }

    private static bool? ReadNullableBoolean(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out JsonElement value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static string? ReadNestedString(JsonElement element, string objectName, string propertyName)
    {
        if (!TryGetProperty(element, objectName, out JsonElement nested) ||
            nested.ValueKind != JsonValueKind.Object)
            return null;
        return ReadString(nested, propertyName);
    }

    private static int? ReadNestedInt(JsonElement element, string objectName, string propertyName)
    {
        if (!TryGetProperty(element, objectName, out JsonElement nested) ||
            nested.ValueKind != JsonValueKind.Object)
            return null;

        long? value = ReadInt64(nested, propertyName);
        return value is > 0 and <= int.MaxValue ? (int)value.Value : null;
    }

    private static bool? ReadNestedBoolean(JsonElement element, string objectName, string propertyName)
    {
        if (!TryGetProperty(element, objectName, out JsonElement nested) ||
            nested.ValueKind != JsonValueKind.Object ||
            !TryGetProperty(nested, propertyName, out JsonElement value))
            return null;

        if (value.ValueKind == JsonValueKind.True) return true;
        if (value.ValueKind == JsonValueKind.False) return false;
        if (value.ValueKind != JsonValueKind.Object) return null;

        return ReadNullableBoolean(value, "supported")
            ?? ReadNullableBoolean(value, "enabled");
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
        int slash = value.LastIndexOf('/');
        if (slash >= 0)
        {
            value = value[(slash + 1)..];
        }

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
