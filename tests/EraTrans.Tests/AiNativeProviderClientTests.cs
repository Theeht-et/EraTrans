using EraTrans.Core;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace EraTrans.Tests;

public sealed class AiNativeProviderClientTests
{
    [Fact]
    public async Task CompleteAsync_SendsGeminiGenerateContentRequestWithApiKeyHeader()
    {
        using var handler = new StubHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(
                "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent",
                request.RequestUri?.ToString());
            Assert.Equal("gemini-secret", request.Headers.GetValues("x-goog-api-key").Single());

            string body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using JsonDocument json = JsonDocument.Parse(body);
            Assert.Equal("system prompt", json.RootElement
                .GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString());
            Assert.Equal("user prompt", json.RootElement
                .GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString());
            Assert.Equal(64000, json.RootElement
                .GetProperty("generationConfig").GetProperty("maxOutputTokens").GetInt32());
            Assert.DoesNotContain("gemini-secret", body);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"candidates":[{"content":{"parts":[{"text":"native Gemini reply"}]}}]}""",
                    Encoding.UTF8,
                    "application/json")
            };
        });
        using var httpClient = new HttpClient(handler);
        var client = new NativeAiProviderClient(httpClient);
        var profile = new AiProviderProfile
        {
            Name = "Gemini",
            BaseUrl = "https://generativelanguage.googleapis.com/v1beta",
            Model = "gemini-2.5-flash",
            ApiKey = "gemini-secret",
            Protocol = AiProviderProtocol.GoogleGemini,
            AvailableModels = [new AiProviderModel { Id = "gemini-2.5-flash", OutputLimit = 64000 }]
        };

        string reply = await client.CompleteAsync(profile, "system prompt", "user prompt");
        Assert.Equal("native Gemini reply", reply);
    }

    [Fact]
    public async Task CompleteAsync_SendsClaudeMessagesRequestWithAnthropicHeaders()
    {
        using var handler = new StubHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.anthropic.com/v1/messages", request.RequestUri?.ToString());
            Assert.Equal("claude-secret", request.Headers.GetValues("x-api-key").Single());
            Assert.Equal("2023-06-01", request.Headers.GetValues("anthropic-version").Single());

            string body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using JsonDocument json = JsonDocument.Parse(body);
            Assert.Equal("claude-sonnet-4-5", json.RootElement.GetProperty("model").GetString());
            Assert.Equal("system prompt", json.RootElement.GetProperty("system").GetString());
            Assert.Equal("user prompt", json.RootElement
                .GetProperty("messages")[0].GetProperty("content").GetString());
            Assert.Equal(32000, json.RootElement.GetProperty("max_tokens").GetInt32());
            Assert.DoesNotContain("claude-secret", body);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"content":[{"type":"text","text":"native Claude reply"}]}""",
                    Encoding.UTF8,
                    "application/json")
            };
        });
        using var httpClient = new HttpClient(handler);
        var client = new NativeAiProviderClient(httpClient);
        var profile = new AiProviderProfile
        {
            Name = "Claude",
            BaseUrl = "https://api.anthropic.com",
            Model = "claude-sonnet-4-5",
            ApiKey = "claude-secret",
            Protocol = AiProviderProtocol.AnthropicClaude,
            AvailableModels = [new AiProviderModel { Id = "claude-sonnet-4-5", OutputLimit = 32000 }]
        };

        string reply = await client.CompleteAsync(profile, "system prompt", "user prompt");
        Assert.Equal("native Claude reply", reply);
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responseFactory(request));
        }
    }
}
