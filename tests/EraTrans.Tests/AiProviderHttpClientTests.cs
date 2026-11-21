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

    [Fact]
    public void RequestFactory_AddsReasoningEffortOnlyWhenSelectedModelExplicitlyAdvertisesIt()
    {
        var profile = new AiProviderProfile
        {
            Name = "Test",
            BaseUrl = "https://api.openai.com/v1",
            Model = "reasoning-model",
            ApiKey = "secret",
            AvailableModels =
            [
                new AiProviderModel
                {
                    Id = "reasoning-model",
                    ReasoningSupport = true,
                    ReasoningLevels = ["low", "medium", "high"]
                }
            ],
            ReasoningSettingsByModel = new Dictionary<string, AiModelReasoningSettings>
            {
                ["reasoning-model"] = new() { Enabled = true, Effort = "high" }
            }
        };

        AiProviderHttpRequestDetails details =
            AiProviderHttpRequestFactory.Create(profile, "system", "user");
        using JsonDocument body = JsonDocument.Parse(details.JsonBody);
        Assert.Equal("high", body.RootElement.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public void RequestFactory_DoesNotSendReasoningForDisabledOrUnknownModelCapabilities()
    {
        var profile = new AiProviderProfile
        {
            Name = "Test",
            BaseUrl = "https://api.example.com/v1",
            Model = "unknown-model",
            ApiKey = "secret",
            ReasoningSettingsByModel = new Dictionary<string, AiModelReasoningSettings>
            {
                ["unknown-model"] = new() { Enabled = true, Effort = "high" }
            }
        };

        AiProviderHttpRequestDetails details =
            AiProviderHttpRequestFactory.Create(profile, "system", "user");
        using JsonDocument body = JsonDocument.Parse(details.JsonBody);
        Assert.False(body.RootElement.TryGetProperty("reasoning_effort", out _));

        profile.AvailableModels =
        [
            new AiProviderModel
            {
                Id = "unknown-model",
                ReasoningSupport = null,
                ReasoningLevels = ["low", "medium", "high"]
            }
        ];
        details = AiProviderHttpRequestFactory.Create(profile, "system", "user");
        using JsonDocument unknownCapabilityBody = JsonDocument.Parse(details.JsonBody);
        Assert.False(unknownCapabilityBody.RootElement.TryGetProperty("reasoning_effort", out _));

        profile.AvailableModels[0].ReasoningSupport = true;
        profile.ReasoningSettingsByModel["unknown-model"].Enabled = false;
        details = AiProviderHttpRequestFactory.Create(profile, "system", "user");
        using JsonDocument disabledBody = JsonDocument.Parse(details.JsonBody);
        Assert.False(disabledBody.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public void RequestFactory_UsesOpenRouterReasoningObjectForOpenRouterEndpoint()
    {
        var profile = new AiProviderProfile
        {
            Name = "OpenRouter",
            BaseUrl = "https://openrouter.ai/api/v1",
            Model = "reasoning-model",
            ApiKey = "secret",
            AvailableModels =
            [
                new AiProviderModel
                {
                    Id = "reasoning-model",
                    ReasoningSupport = true,
                    ReasoningLevels = ["low", "medium", "high"]
                }
            ],
            ReasoningSettingsByModel = new Dictionary<string, AiModelReasoningSettings>
            {
                ["reasoning-model"] = new() { Enabled = true, Effort = "medium" }
            }
        };

        AiProviderHttpRequestDetails details =
            AiProviderHttpRequestFactory.Create(profile, "system", "user");
        using JsonDocument body = JsonDocument.Parse(details.JsonBody);
        Assert.Equal("medium", body.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.False(body.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public void RequestFactory_DoesNotGuessReasoningDialectForOtherCompatibleEndpoints()
    {
        var profile = new AiProviderProfile
        {
            Name = "Custom compatible endpoint",
            BaseUrl = "https://proxy.example.com/v1",
            Model = "reasoning-model",
            ApiKey = "secret",
            AvailableModels =
            [
                new AiProviderModel
                {
                    Id = "reasoning-model",
                    ReasoningSupport = true,
                    ReasoningLevels = ["low", "medium", "high"]
                }
            ],
            ReasoningSettingsByModel = new Dictionary<string, AiModelReasoningSettings>
            {
                ["reasoning-model"] = new() { Enabled = true, Effort = "high" }
            }
        };

        AiProviderHttpRequestDetails details =
            AiProviderHttpRequestFactory.Create(profile, "system", "user");
        using JsonDocument body = JsonDocument.Parse(details.JsonBody);
        Assert.False(body.RootElement.TryGetProperty("reasoning_effort", out _));
        Assert.False(body.RootElement.TryGetProperty("reasoning", out _));
    }

    [Fact]
    public void RequestFactory_SendsOpenAiReasoningOffOnlyWhenModelExplicitlyAllowsIt()
    {
        var profile = new AiProviderProfile
        {
            Name = "OpenAI",
            BaseUrl = "https://api.openai.com/v1",
            Model = "reasoning-model",
            ApiKey = "secret",
            AvailableModels =
            [
                new AiProviderModel
                {
                    Id = "reasoning-model",
                    ReasoningSupport = true,
                    ReasoningCanDisable = true
                }
            ],
            ReasoningSettingsByModel = new Dictionary<string, AiModelReasoningSettings>
            {
                ["reasoning-model"] = new() { Disabled = true }
            }
        };

        AiProviderHttpRequestDetails details =
            AiProviderHttpRequestFactory.Create(profile, "system", "user");
        using JsonDocument body = JsonDocument.Parse(details.JsonBody);
        Assert.Equal("none", body.RootElement.GetProperty("reasoning_effort").GetString());

        profile.AvailableModels[0].ReasoningCanDisable = false;
        details = AiProviderHttpRequestFactory.Create(profile, "system", "user");
        using JsonDocument notAllowed = JsonDocument.Parse(details.JsonBody);
        Assert.False(notAllowed.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public void RequestFactory_SendsOpenRouterReasoningOffUsingEnabledFalse()
    {
        var profile = new AiProviderProfile
        {
            Name = "OpenRouter",
            BaseUrl = "https://openrouter.ai/api/v1",
            Model = "reasoning-model",
            ApiKey = "secret",
            AvailableModels =
            [
                new AiProviderModel
                {
                    Id = "reasoning-model",
                    ReasoningSupport = true,
                    ReasoningCanDisable = true
                }
            ],
            ReasoningSettingsByModel = new Dictionary<string, AiModelReasoningSettings>
            {
                ["reasoning-model"] = new() { Disabled = true }
            }
        };

        AiProviderHttpRequestDetails details =
            AiProviderHttpRequestFactory.Create(profile, "system", "user");
        using JsonDocument body = JsonDocument.Parse(details.JsonBody);
        Assert.False(body.RootElement.GetProperty("reasoning").GetProperty("enabled").GetBoolean());
        Assert.False(body.RootElement.GetProperty("reasoning").TryGetProperty("effort", out _));
    }

    [Fact]
    public void RequestFactory_DoesNotApplyOffWhenModelDialectConflictsWithEndpoint()
    {
        var profile = new AiProviderProfile
        {
            Name = "OpenAI",
            BaseUrl = "https://api.openai.com/v1",
            Model = "reasoning-model",
            ApiKey = "secret",
            AvailableModels =
            [
                new AiProviderModel
                {
                    Id = "reasoning-model",
                    ReasoningSupport = true,
                    ReasoningCanDisable = true,
                    ReasoningDialect = "openrouterReasoning"
                }
            ],
            ReasoningSettingsByModel = new Dictionary<string, AiModelReasoningSettings>
            {
                ["reasoning-model"] = new() { Disabled = true }
            }
        };

        AiProviderHttpRequestDetails details =
            AiProviderHttpRequestFactory.Create(profile, "system", "user");
        using JsonDocument body = JsonDocument.Parse(details.JsonBody);
        Assert.False(body.RootElement.TryGetProperty("reasoning_effort", out _));
        Assert.False(body.RootElement.TryGetProperty("reasoning", out _));
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