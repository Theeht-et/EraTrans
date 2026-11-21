using System.Net;
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
        ArgumentNullException.ThrowIfNull(profile);
        string endpoint = AiProviderProfileValidator.BuildApiEndpoint(
            profile.BaseUrl, profile.ChatCompletionsPath, "/chat/completions");
        string json = JsonSerializer.Serialize(new
        {
            model = profile.Model,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            }
        }, JsonOptions);

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

    public async Task<string> CompleteAsync(
        AiProviderProfile profile,
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken = default)
    {
        AiProviderProfileValidator.Validate(profile);
        AiProviderHttpRequestDetails details =
            AiProviderHttpRequestFactory.Create(profile, systemPrompt, userPrompt);

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
            string message = TryReadApiError(responseBody)
                ?? $"服务商返回 HTTP {(int)response.StatusCode} {response.ReasonPhrase}。";
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