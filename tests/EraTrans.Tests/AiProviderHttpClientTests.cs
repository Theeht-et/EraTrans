using EraTrans.Core;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace EraTrans.Tests;

public sealed class AiProviderHttpClientTests
{
    [Fact]
    public async Task CompleteAsync_SendsOpenAiChatCompletionsRequest()
    {
        using var handler = new StubHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.example.com/v1/chat/completions", request.RequestUri?.ToString());
            Assert.Equal("Bearer api-secret", request.Headers.Authorization?.ToString());

            string body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using JsonDocument json = JsonDocument.Parse(body);
            Assert.Equal("translation-model", json.RootElement.GetProperty("model").GetString());
            Assert.Equal("system prompt", json.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
            Assert.Equal("user prompt", json.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
            Assert.DoesNotContain("api-secret", body);

            return JsonResponse(HttpStatusCode.OK,
                """{"choices":[{"message":{"role":"assistant","content":"translated reply"}}]}""");
        });
        using var httpClient = new HttpClient(handler);
        var client = new OpenAiCompatibleChatCompletionClient(httpClient);
        var profile = new AiProviderProfile
        {
            Name = "Test provider",
            BaseUrl = "https://api.example.com/v1",
            Model = "translation-model",
            ApiKey = "api-secret"
        };

        string reply = await client.CompleteAsync(profile, "system prompt", "user prompt");
        Assert.Equal("translated reply", reply);
    }

    [Fact]
    public void ParseChatCompletionResponse_AcceptsTextParts()
    {
        string response = OpenAiCompatibleChatCompletionClient.ParseChatCompletionResponse(
            """{"choices":[{"message":{"content":[{"type":"text","text":"part one"},{"type":"text","text":"part two"}]}}]}""");
        Assert.Equal("part onepart two", response);
    }

    [Fact]
    public void ParseChatCompletionResponse_IgnoresNonStringTextParts()
    {
        string response = OpenAiCompatibleChatCompletionClient.ParseChatCompletionResponse(
            """{"choices":[{"message":{"content":[{"type":"text","text":{"unexpected":"object"}},{"type":"text","text":"valid reply"},{"type":"image","image_url":"ignored"}]}}]}""");
        Assert.Equal("valid reply", response);
    }

    [Fact]
    public async Task CompleteAsync_ReportsProviderErrorAndHttpStatus()
    {
        using var handler = new StubHandler(_ =>
            JsonResponse(HttpStatusCode.Unauthorized, """{"error":{"message":"invalid API key"}}"""));
        using var httpClient = new HttpClient(handler);
        var client = new OpenAiCompatibleChatCompletionClient(httpClient);
        var profile = new AiProviderProfile
        {
            Name = "Test provider",
            BaseUrl = "https://api.example.com/v1",
            Model = "translation-model",
            ApiKey = "api-secret"
        };

        HttpRequestException error = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.CompleteAsync(profile, "system", "user"));
        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
        Assert.Contains("服务商拒绝了身份认证", error.Message);
        Assert.Contains("Authorization: Bearer", error.Message);
        Assert.Contains("invalid API key", error.Message);
        Assert.Contains("https://api.example.com/v1/chat/completions", error.Message);
        Assert.DoesNotContain("api-secret", error.Message);
    }

    [Fact]
    public void RequestFactory_UsesOnlyOpenAiChatCompletionsFields()
    {
        var profile = new AiProviderProfile
        {
            Name = "Test",
            BaseUrl = "https://api.example.com/v1",
            Model = "model",
            ApiKey = "secret",
            ChatCompletionsPath = "/v2/custom-chat"
        };

        AiProviderHttpRequestDetails details =
            AiProviderHttpRequestFactory.Create(profile, "system", "user");

        Assert.Equal("POST", details.Method);
        Assert.Equal("https://api.example.com/v1/v2/custom-chat", details.Endpoint);
        Assert.Equal("Authorization", details.AuthenticationHeaderName);
        Assert.Equal("Bearer secret", details.AuthenticationHeaderValue);
        Assert.Null(details.VersionHeaderName);

        using JsonDocument body = JsonDocument.Parse(details.JsonBody);
        Assert.Equal("model", body.RootElement.GetProperty("model").GetString());
        Assert.Equal(2, body.RootElement.GetProperty("messages").GetArrayLength());
        Assert.DoesNotContain("secret", details.JsonBody);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responseFactory(request));
        }
    }
}