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

    [Fact]
    public void RequestFactory_UsesDeepSeekThinkingDialectForEffortSelection()
    {
        var profile = new AiProviderProfile
        {
            Name = "DeepSeek",
            BaseUrl = "https://api.deepseek.com/v1",
            Model = "deepseek-flash",
            ApiKey = "secret",
            AvailableModels =
            [
                new AiProviderModel
                {
                    Id = "deepseek-flash",
                    ReasoningSupport = true,
                    ReasoningCanDisable = true,
                    ReasoningDialect = "deepseekThinking",
                    ReasoningLevels = ["low", "high", "max"]
                }
            ],
            ReasoningSettingsByModel = new Dictionary<string, AiModelReasoningSettings>
            {
                ["deepseek-flash"] = new() { Enabled = true, Effort = "max" }
            }
        };

        AiProviderHttpRequestDetails details =
            AiProviderHttpRequestFactory.Create(profile, "system", "user");
        using JsonDocument body = JsonDocument.Parse(details.JsonBody);
        Assert.Equal("enabled", body.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("max", body.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.False(body.RootElement.TryGetProperty("reasoning", out _));
    }

    [Fact]
    public void RequestFactory_DisablesDeepSeekThinkingOnlyWhenModelAllowsIt()
    {
        var profile = new AiProviderProfile
        {
            Name = "DeepSeek",
            BaseUrl = "https://api.deepseek.com",
            Model = "deepseek-v4-pro",
            ApiKey = "secret",
            AvailableModels =
            [
                new AiProviderModel
                {
                    Id = "deepseek-v4-pro",
                    ReasoningSupport = true,
                    ReasoningCanDisable = true,
                    ReasoningDialect = "deepseekThinking",
                    ReasoningLevels = ["low", "high", "max"]
                }
            ],
            ReasoningSettingsByModel = new Dictionary<string, AiModelReasoningSettings>
            {
                ["deepseek-v4-pro"] = new() { Disabled = true }
            }
        };

        AiProviderHttpRequestDetails details =
            AiProviderHttpRequestFactory.Create(profile, "system", "user");
        using JsonDocument body = JsonDocument.Parse(details.JsonBody);
        Assert.Equal("disabled", body.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.False(body.RootElement.TryGetProperty("reasoning_effort", out _));

        profile.AvailableModels[0].ReasoningCanDisable = false;
        details = AiProviderHttpRequestFactory.Create(profile, "system", "user");
        using JsonDocument notAllowed = JsonDocument.Parse(details.JsonBody);
        Assert.False(notAllowed.RootElement.TryGetProperty("thinking", out _));
    }

    [Fact]
    public void RequestFactory_DoesNotApplyDeepSeekDialectToAnotherHostOrMismatchedMetadata()
    {
        var profile = new AiProviderProfile
        {
            Name = "Custom",
            BaseUrl = "https://proxy.example.com/v1",
            Model = "deepseek-flash",
            ApiKey = "secret",
            AvailableModels =
            [
                new AiProviderModel
                {
                    Id = "deepseek-flash",
                    ReasoningSupport = true,
                    ReasoningCanDisable = true,
                    ReasoningDialect = "deepseekThinking",
                    ReasoningLevels = ["low", "high", "max"]
                }
            ],
            ReasoningSettingsByModel = new Dictionary<string, AiModelReasoningSettings>
            {
                ["deepseek-flash"] = new() { Enabled = true, Effort = "high" }
            }
        };

        AiProviderHttpRequestDetails details =
            AiProviderHttpRequestFactory.Create(profile, "system", "user");
        using JsonDocument body = JsonDocument.Parse(details.JsonBody);
        Assert.False(body.RootElement.TryGetProperty("thinking", out _));
        Assert.False(body.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public void RequestFactory_DoesNotInferDeepSeekDialectFromHostAlone()
    {
        var profile = new AiProviderProfile
        {
            Name = "DeepSeek",
            BaseUrl = "https://api.deepseek.com/v1",
            Model = "unverified-reasoning-model",
            ApiKey = "secret",
            AvailableModels =
            [
                new AiProviderModel
                {
                    Id = "unverified-reasoning-model",
                    ReasoningSupport = true,
                    ReasoningLevels = ["low", "high", "max"]
                }
            ],
            ReasoningSettingsByModel = new Dictionary<string, AiModelReasoningSettings>
            {
                ["unverified-reasoning-model"] = new() { Enabled = true, Effort = "high" }
            }
        };

        AiProviderHttpRequestDetails details =
            AiProviderHttpRequestFactory.Create(profile, "system", "user");
        using JsonDocument body = JsonDocument.Parse(details.JsonBody);
        Assert.False(body.RootElement.TryGetProperty("thinking", out _));
        Assert.False(body.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public void RequestFactory_AppliesExplicitReasoningSettingsToManuallyAddedCustomModel()
    {
        var profile = new AiProviderProfile
        {
            Name = "Custom provider",
            BaseUrl = "https://custom-provider.example/v1",
            Model = "my-custom-reasoner",
            ApiKey = "secret",
            // Deliberately absent from AvailableModels: the model was entered
            // manually and its capability was explicitly configured by user.
            ReasoningCapabilitiesByModel = new Dictionary<string, AiModelReasoningCapabilityOverride>
            {
                ["my-custom-reasoner"] = new()
                {
                    Dialect = AiReasoningDialectSupport.CustomOpenAiEffortDialect,
                    CanDisable = true,
                    Levels = ["low", "medium", "high"]
                }
            },
            ReasoningSettingsByModel = new Dictionary<string, AiModelReasoningSettings>
            {
                ["my-custom-reasoner"] = new() { Enabled = true, Effort = "high" }
            }
        };

        AiProviderHttpRequestDetails details =
            AiProviderHttpRequestFactory.Create(profile, "system", "user");
        using (JsonDocument body = JsonDocument.Parse(details.JsonBody))
        {
            Assert.Equal("high", body.RootElement.GetProperty("reasoning_effort").GetString());
        }

        profile.ReasoningSettingsByModel["my-custom-reasoner"].Disabled = true;
        profile.ReasoningSettingsByModel["my-custom-reasoner"].Enabled = false;
        details = AiProviderHttpRequestFactory.Create(profile, "system", "user");
        using (JsonDocument body = JsonDocument.Parse(details.JsonBody))
        {
            Assert.Equal("none", body.RootElement.GetProperty("reasoning_effort").GetString());
        }

        // Auto means no override; provider/model defaults remain in control.
        profile.ReasoningSettingsByModel["my-custom-reasoner"].Disabled = false;
        details = AiProviderHttpRequestFactory.Create(profile, "system", "user");
        using (JsonDocument body = JsonDocument.Parse(details.JsonBody))
        {
            Assert.False(body.RootElement.TryGetProperty("reasoning_effort", out _));
        }
    }

    [Fact]
    public void RequestFactory_UsesExplicitCustomReasoningDialectRatherThanGuessing()
    {
        (string Dialect, string ExpectedPayload)[] dialects =
        [
            (AiReasoningDialectSupport.CustomOpenAiEffortDialect, "openai"),
            (AiReasoningDialectSupport.CustomOpenRouterDialect, "openrouter"),
            (AiReasoningDialectSupport.CustomThinkingTypeDialect, "thinking"),
            (AiReasoningDialectSupport.CustomQwenDialect, "qwen")
        ];

        foreach ((string dialect, string expectedPayload) in dialects)
        {
            var profile = new AiProviderProfile
            {
                BaseUrl = "https://custom-provider.example/v1",
                Model = "manual-reasoner",
                ApiKey = "secret",
                ReasoningCapabilitiesByModel = new Dictionary<string, AiModelReasoningCapabilityOverride>
                {
                    ["manual-reasoner"] = new()
                    {
                        Dialect = dialect,
                        CanDisable = true,
                        Levels = ["low", "medium", "high"]
                    }
                },
                ReasoningSettingsByModel = new Dictionary<string, AiModelReasoningSettings>
                {
                    ["manual-reasoner"] = new() { Enabled = true, Effort = "high" }
                }
            };

            AiProviderHttpRequestDetails request =
                AiProviderHttpRequestFactory.Create(profile, "system", "user");
            using (JsonDocument effortBody = JsonDocument.Parse(request.JsonBody))
            {
                switch (expectedPayload)
                {
                    case "openai":
                        Assert.Equal("high", effortBody.RootElement.GetProperty("reasoning_effort").GetString());
                        break;
                    case "openrouter":
                        Assert.Equal("high", effortBody.RootElement.GetProperty("reasoning")
                            .GetProperty("effort").GetString());
                        break;
                    case "thinking":
                        Assert.Equal("enabled", effortBody.RootElement.GetProperty("thinking")
                            .GetProperty("type").GetString());
                        Assert.Equal("high", effortBody.RootElement.GetProperty("reasoning_effort").GetString());
                        break;
                    case "qwen":
                        Assert.Equal(8192, effortBody.RootElement.GetProperty("thinking_budget").GetInt32());
                        break;
                }
            }

            profile.ReasoningSettingsByModel["manual-reasoner"].Enabled = false;
            profile.ReasoningSettingsByModel["manual-reasoner"].Disabled = true;
            request = AiProviderHttpRequestFactory.Create(profile, "system", "user");
            using JsonDocument offBody = JsonDocument.Parse(request.JsonBody);
            switch (expectedPayload)
            {
                case "openai":
                    Assert.Equal("none", offBody.RootElement.GetProperty("reasoning_effort").GetString());
                    break;
                case "openrouter":
                    Assert.False(offBody.RootElement.GetProperty("reasoning")
                        .GetProperty("enabled").GetBoolean());
                    break;
                case "thinking":
                    Assert.Equal("disabled", offBody.RootElement.GetProperty("thinking")
                        .GetProperty("type").GetString());
                    break;
                case "qwen":
                    Assert.False(offBody.RootElement.GetProperty("enable_thinking").GetBoolean());
                    break;
            }
        }
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