using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace EraTrans.Core;

public sealed record AiChatMessage(string Role, string Content);

public sealed record AiProviderHttpRequestDetails(
    string Method,
    string Endpoint,
    string JsonBody,
    string? AuthenticationHeaderName,
    string? AuthenticationHeaderValue,
    string? VersionHeaderName,
    string? VersionHeaderValue);

public static class AiProviderHttpRequestFactory
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// Builds a Chat Completions request. The reasoning argument overrides the
    /// provider's saved choice; omitting it uses whatever the user selected in
    /// the dictionary editor (Auto by default, which writes nothing).
    /// </summary>
    public static AiProviderHttpRequestDetails Create(
        AiProviderProfile profile,
        string systemPrompt,
        string userPrompt,
        AiReasoningOptions? reasoning = null) =>
        Create(
            profile,
            [
                new AiChatMessage("system", systemPrompt),
                new AiChatMessage("user", userPrompt)
            ],
            reasoning);

    /// <summary>
    /// Builds a multi-turn Chat Completions request from an ordered message list.
    /// </summary>
    public static AiProviderHttpRequestDetails Create(
        AiProviderProfile profile,
        IReadOnlyList<AiChatMessage> messages,
        AiReasoningOptions? reasoning = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count == 0)
            throw new ArgumentException("聊天消息列表不能为空。", nameof(messages));

        string endpoint = AiProviderProfileValidator.BuildApiEndpoint(
            profile.BaseUrl, profile.ChatCompletionsPath, "/chat/completions");

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = profile.Model,
            ["messages"] = messages.Select(message => new { role = message.Role, content = message.Content }).ToArray()
        };

        AiReasoning.Apply(
            payload,
            profile.BaseUrl,
            reasoning ?? AiReasoningSettingsStore.ResolveForProvider(profile.Id));

        string json = JsonSerializer.Serialize(payload, JsonOptions);

        return new AiProviderHttpRequestDetails(
            "POST", endpoint, json,
            "Authorization", "Bearer " + profile.ApiKey.Trim(), null, null);
    }
}

/// <summary>
/// Sends translation requests using the single supported OpenAI Chat Completions format.
/// </summary>
public sealed class OpenAiCompatibleChatCompletionClient : IAiChatCompletionClient
{
    private static readonly HttpClient SharedHttpClient = new();
    private readonly HttpClient _httpClient;

    public OpenAiCompatibleChatCompletionClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? SharedHttpClient;
    }

    public Task<string> CompleteAsync(
        AiProviderProfile profile,
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken = default) =>
        CompleteAsync(profile, systemPrompt, userPrompt, reasoning: null, cancellationToken);

    public Task<string> CompleteAsync(
        AiProviderProfile profile,
        string systemPrompt,
        string userPrompt,
        AiReasoningOptions? reasoning,
        CancellationToken cancellationToken = default) =>
        CompleteAsync(
            profile,
            [
                new AiChatMessage("system", systemPrompt),
                new AiChatMessage("user", userPrompt)
            ],
            reasoning,
            cancellationToken);

    public async Task<string> CompleteAsync(
        AiProviderProfile profile,
        IReadOnlyList<AiChatMessage> messages,
        AiReasoningOptions? reasoning = null,
        CancellationToken cancellationToken = default)
    {
        AiProviderProfileValidator.Validate(profile);
        AiProviderHttpRequestDetails details =
            AiProviderHttpRequestFactory.Create(profile, messages, reasoning);

        using var request = new HttpRequestMessage(HttpMethod.Post, details.Endpoint);
        request.Content = new StringContent(details.JsonBody, Encoding.UTF8, "application/json");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", profile.ApiKey.Trim());

        using HttpResponseMessage response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            string? providerMessage = TryReadApiError(responseBody);
            string message = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized =>
                    "服务商拒绝了身份认证（HTTP 401）。本程序当前发送 Authorization: Bearer <API Key>。" +
                    "请检查 API Key 是否正确、是否已失效、Base URL/地区是否正确，以及该服务商是否要求其他认证请求头或原生协议。" +
                    "若服务商要求 api-key 等非 Bearer 认证，本版本目前不支持，不能仅靠更换模型解决。",
                HttpStatusCode.Forbidden =>
                    "服务商拒绝了访问权限（HTTP 403）。API Key 可能有效，但当前账号、模型或套餐没有此接口的调用权限。",
                HttpStatusCode.NotFound =>
                    "服务商找不到该 API 路径（HTTP 404）。请检查 Base URL、API 路径和服务商文档中的接口版本。",
                _ => $"服务商返回 HTTP {(int)response.StatusCode} {response.ReasonPhrase}。"
            };

            if (!string.IsNullOrWhiteSpace(providerMessage))
                message += $" 服务商返回：{providerMessage}";
            message += $" 请求地址：{details.Endpoint}";
            throw new HttpRequestException(message, inner: null, statusCode: response.StatusCode);
        }

        return ParseChatCompletionResponse(responseBody);
    }

    public static string ParseChatCompletionResponse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (!TryGetProperty(root, "choices", out JsonElement choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0 ||
            !TryGetProperty(choices[0], "message", out JsonElement message) ||
            !TryGetProperty(message, "content", out JsonElement content))
            throw new InvalidDataException(
                "OpenAI Chat Completions 响应中没有 choices[0].message.content。");

        string result;
        if (content.ValueKind == JsonValueKind.String)
        {
            result = content.GetString() ?? "";
        }
        else if (content.ValueKind == JsonValueKind.Array)
        {
            result = string.Concat(content.EnumerateArray()
                .Where(part => part.ValueKind == JsonValueKind.Object &&
                               TryGetProperty(part, "text", out JsonElement text) &&
                               text.ValueKind == JsonValueKind.String)
                .Select(part => TryGetProperty(part, "text", out JsonElement text)
                    ? text.GetString() ?? ""
                    : ""));
        }
        else
        {
            throw new InvalidDataException("服务商返回的 message.content 不是文本内容。");
        }

        if (string.IsNullOrWhiteSpace(result))
            throw new InvalidDataException("服务商没有返回可用的文本内容。");
        return result;
    }

    private static string? TryReadApiError(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (TryGetProperty(document.RootElement, "error", out JsonElement error))
            {
                if (error.ValueKind == JsonValueKind.String)
                    return error.GetString();
                if (error.ValueKind == JsonValueKind.Object &&
                    TryGetProperty(error, "message", out JsonElement message) &&
                    message.ValueKind == JsonValueKind.String)
                    return message.GetString();
            }
        }
        catch (JsonException)
        {
            // For non-JSON error bodies, the HTTP status is more reliable.
        }
        return null;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }
}
