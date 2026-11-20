using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace EraTrans.Core;

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

    public static AiProviderHttpRequestDetails Create(
        AiProviderProfile profile,
        string systemPrompt,
        string userPrompt)
    {
        string baseUrl = AiProviderProfileValidator.NormalizeBaseUrl(profile.BaseUrl).TrimEnd('/');
        return profile.Protocol switch
        {
            AiProviderProtocol.GoogleGemini => CreateGemini(profile, baseUrl, systemPrompt, userPrompt),
            AiProviderProtocol.AnthropicClaude => CreateClaude(profile, baseUrl, systemPrompt, userPrompt),
            _ => CreateOpenAiCompatible(profile, baseUrl, systemPrompt, userPrompt)
        };
    }

    private static AiProviderHttpRequestDetails CreateOpenAiCompatible(
        AiProviderProfile profile, string baseUrl, string systemPrompt, string userPrompt)
    {
        string json = JsonSerializer.Serialize(new
        {
            model = profile.Model,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            }
        }, JsonOptions);
        return new("POST", baseUrl + "/chat/completions", json,
            "Authorization", "Bearer " + profile.ApiKey, null, null);
    }

    private static AiProviderHttpRequestDetails CreateClaude(
        AiProviderProfile profile, string baseUrl, string systemPrompt, string userPrompt)
    {
        if (baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            baseUrl = baseUrl[..^3];

        int? outputLimit = profile.AvailableModels
            .FirstOrDefault(model => string.Equals(model.Id, profile.Model, StringComparison.Ordinal))
            ?.OutputLimit;
        int maxTokens = Math.Clamp(outputLimit ?? 8192, 1, 8192);
        string json = JsonSerializer.Serialize(new
        {
            model = profile.Model,
            max_tokens = maxTokens,
            system = systemPrompt,
            messages = new[] { new { role = "user", content = userPrompt } }
        }, JsonOptions);
        return new("POST", baseUrl + "/v1/messages", json,
            "x-api-key", profile.ApiKey, "anthropic-version", "2023-06-01");
    }

    private static AiProviderHttpRequestDetails CreateGemini(
        AiProviderProfile profile, string baseUrl, string systemPrompt, string userPrompt)
    {
        string modelId = profile.Model.StartsWith("models/", StringComparison.OrdinalIgnoreCase)
            ? profile.Model["models/".Length..]
            : profile.Model;
        int? outputLimit = profile.AvailableModels
            .FirstOrDefault(model => string.Equals(model.Id, profile.Model, StringComparison.Ordinal))
            ?.OutputLimit;
        int maxOutputTokens = Math.Clamp(outputLimit ?? 8192, 1, 8192);
        string json = JsonSerializer.Serialize(new
        {
            systemInstruction = new { parts = new[] { new { text = systemPrompt } } },
            contents = new[]
            {
                new { role = "user", parts = new[] { new { text = userPrompt } } }
            },
            generationConfig = new { maxOutputTokens }
        }, JsonOptions);
        return new("POST",
            baseUrl + "/models/" + Uri.EscapeDataString(modelId) + ":generateContent",
            json,
            "x-goog-api-key", profile.ApiKey, null, null);
    }
}

public sealed class NativeAiProviderClient
{
    private static readonly HttpClient SharedHttpClient = new();
    private readonly HttpClient _httpClient;

    public NativeAiProviderClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? SharedHttpClient;
    }

    public async Task<string> CompleteAsync(
        AiProviderProfile profile,
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken = default)
    {
        if (profile.Protocol == AiProviderProtocol.OpenAiCompatible)
            throw new InvalidOperationException("原生协议客户端不能处理 OpenAI 兼容请求。");

        AiProviderHttpRequestDetails details =
            AiProviderHttpRequestFactory.Create(profile, systemPrompt, userPrompt);
        using var request = new HttpRequestMessage(HttpMethod.Post, details.Endpoint);
        request.Content = new StringContent(details.JsonBody, Encoding.UTF8, "application/json");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (!string.IsNullOrWhiteSpace(details.AuthenticationHeaderName) &&
            !string.IsNullOrWhiteSpace(details.AuthenticationHeaderValue))
            request.Headers.TryAddWithoutValidation(
                details.AuthenticationHeaderName, details.AuthenticationHeaderValue);

        if (!string.IsNullOrWhiteSpace(details.VersionHeaderName) &&
            !string.IsNullOrWhiteSpace(details.VersionHeaderValue))
            request.Headers.TryAddWithoutValidation(
                details.VersionHeaderName, details.VersionHeaderValue);

        using HttpResponseMessage response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        string responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string message = TryReadApiError(responseJson)
                ?? $"服务商返回 HTTP {(int)response.StatusCode} {response.ReasonPhrase}。";
            throw new HttpRequestException(message, inner: null, statusCode: response.StatusCode);
        }

        return profile.Protocol switch
        {
            AiProviderProtocol.GoogleGemini => ParseGeminiTextResponse(responseJson),
            AiProviderProtocol.AnthropicClaude => ParseClaudeTextResponse(responseJson),
            _ => throw new InvalidOperationException("不支持的 AI 服务商协议类型。")
        };
    }

    private static string ParseClaudeTextResponse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("content", out JsonElement content) ||
            content.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Claude 响应中没有 content 文本数组。");

        string text = string.Concat(content.EnumerateArray()
            .Where(part => part.ValueKind == JsonValueKind.Object &&
                           part.TryGetProperty("type", out JsonElement type) &&
                           type.GetString() == "text" &&
                           part.TryGetProperty("text", out _))
            .Select(part => part.GetProperty("text").GetString() ?? ""));
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidDataException("Claude 没有返回可用的文本内容。");
        return text;
    }

    private static string ParseGeminiTextResponse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("candidates", out JsonElement candidates) ||
            candidates.ValueKind != JsonValueKind.Array ||
            candidates.GetArrayLength() == 0)
        {
            string? blockReason =
                document.RootElement.TryGetProperty("promptFeedback", out JsonElement feedback) &&
                feedback.TryGetProperty("blockReason", out JsonElement reason)
                    ? reason.GetString()
                    : null;
            throw new InvalidDataException(string.IsNullOrWhiteSpace(blockReason)
                ? "Gemini 响应中没有候选译文。"
                : $"Gemini 请求被安全策略拦截：{blockReason}。");
        }

        JsonElement candidate = candidates[0];
        if (!candidate.TryGetProperty("content", out JsonElement content) ||
            !content.TryGetProperty("parts", out JsonElement parts) ||
            parts.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Gemini 响应中没有文本 parts。");

        string text = string.Concat(parts.EnumerateArray()
            .Where(part => part.ValueKind == JsonValueKind.Object &&
                           part.TryGetProperty("text", out _))
            .Select(part => part.GetProperty("text").GetString() ?? ""));
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidDataException("Gemini 没有返回可用的文本内容。");
        return text;
    }

    private static string? TryReadApiError(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("error", out JsonElement error))
            {
                if (error.ValueKind == JsonValueKind.String)
                    return error.GetString();
                if (error.ValueKind == JsonValueKind.Object &&
                    error.TryGetProperty("message", out JsonElement message))
                    return message.GetString();
            }
        }
        catch (JsonException)
        {
            // HTTP status remains useful if the provider returned non-JSON error content.
        }
        return null;
    }
}
