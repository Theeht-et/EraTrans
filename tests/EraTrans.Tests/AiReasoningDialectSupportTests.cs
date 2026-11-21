using System.Text.Json;
using EraTrans.Core;
using Xunit;

namespace EraTrans.Tests;

public sealed class AiReasoningDialectSupportTests
{
    [Fact]
    public void OpenAiGpt56_UsesKelivoModelDefaultsAndChatCompletionsEffortDialect()
    {
        AiProviderModel model = new() { Id = "gpt-5.6" };
        AiReasoningDialectSupport.ApplyKnownModelDefaults(model, "https://api.openai.com/v1");

        Assert.True(model.ReasoningSupport);
        Assert.True(model.ReasoningCanDisable);
        Assert.Equal("openaiReasoningEffort", model.ReasoningDialect);
        Assert.Equal(new[] { "low", "medium", "high", "xhigh", "max" }, model.ReasoningLevels);

        using JsonDocument body = CreateBody(
            "https://api.openai.com/v1", model,
            new AiModelReasoningSettings { Enabled = true, Effort = "max" });
        Assert.Equal("max", body.RootElement.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public void OpenAiGpt56_UsesNoneOnlyWhenModelAllowsDisabling()
    {
        AiProviderModel model = new() { Id = "gpt-5.6" };
        AiReasoningDialectSupport.ApplyKnownModelDefaults(model, "https://api.openai.com/v1");

        using JsonDocument body = CreateBody(
            "https://api.openai.com/v1", model,
            new AiModelReasoningSettings { Disabled = true });
        Assert.Equal("none", body.RootElement.GetProperty("reasoning_effort").GetString());

        AiProviderModel pro = new() { Id = "gpt-5.5-pro" };
        AiReasoningDialectSupport.ApplyKnownModelDefaults(pro, "https://api.openai.com/v1");
        Assert.False(pro.ReasoningCanDisable);

        using JsonDocument proBody = CreateBody(
            "https://api.openai.com/v1", pro,
            new AiModelReasoningSettings { Disabled = true });
        Assert.False(proBody.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public void OpenAiResponsesDialect_IsNotSentThroughChatCompletionsTransport()
    {
        AiProviderModel model = new()
        {
            Id = "gpt-5.6",
            ReasoningSupport = true,
            ReasoningCanDisable = true,
            ReasoningDialect = "openaiResponsesReasoning",
            ReasoningLevels = ["low", "high"]
        };

        Assert.False(AiReasoningDialectSupport.IsCompatible("https://api.openai.com/v1", model));
        using JsonDocument body = CreateBody(
            "https://api.openai.com/v1", model,
            new AiModelReasoningSettings { Enabled = true, Effort = "high" });
        Assert.False(body.RootElement.TryGetProperty("reasoning_effort", out _));
        Assert.False(body.RootElement.TryGetProperty("reasoning", out _));
    }

    [Fact]
    public void QwenDialect_UsesEnableThinkingAndBudgetOnlyForSupportedLevels()
    {
        AiProviderModel model = new()
        {
            Id = "qwen3.7-plus",
            ReasoningLevels = ["low", "medium", "high"]
        };
        const string endpoint = "https://dashscope.aliyuncs.com/compatible-mode/v1";
        AiReasoningDialectSupport.ApplyKnownModelDefaults(model, endpoint);

        Assert.Equal("qwenEnableThinking", model.ReasoningDialect);
        Assert.True(model.ReasoningCanDisable);

        using JsonDocument offBody = CreateBody(
            endpoint, model, new AiModelReasoningSettings { Disabled = true });
        Assert.False(offBody.RootElement.GetProperty("enable_thinking").GetBoolean());

        using JsonDocument effortBody = CreateBody(
            endpoint, model, new AiModelReasoningSettings { Enabled = true, Effort = "high" });
        Assert.True(effortBody.RootElement.GetProperty("enable_thinking").GetBoolean());
        Assert.Equal(8192, effortBody.RootElement.GetProperty("thinking_budget").GetInt32());
    }

    [Fact]
    public void QwenThinkingOnlyModel_DoesNotSendDisableFlag()
    {
        AiProviderModel model = new() { Id = "qwen3.7-max-preview" };
        const string endpoint = "https://dashscope.aliyuncs.com/compatible-mode/v1";
        AiReasoningDialectSupport.ApplyKnownModelDefaults(model, endpoint);

        Assert.True(model.ReasoningSupport);
        Assert.False(model.ReasoningCanDisable);

        using JsonDocument body = CreateBody(
            endpoint, model, new AiModelReasoningSettings { Disabled = true });
        Assert.False(body.RootElement.TryGetProperty("enable_thinking", out _));
    }

    [Fact]
    public void Glm53_UsesThinkingTypeDialectAndDoesNotExposeOff()
    {
        AiProviderModel model = new() { Id = "glm-5.3" };
        const string endpoint = "https://open.bigmodel.cn/api/paas/v4";
        AiReasoningDialectSupport.ApplyKnownModelDefaults(model, endpoint);

        Assert.Equal("thinkingType", model.ReasoningDialect);
        Assert.False(model.ReasoningCanDisable);
        Assert.Equal(new[] { "low", "high", "max" }, model.ReasoningLevels);

        using JsonDocument effortBody = CreateBody(
            endpoint, model, new AiModelReasoningSettings { Enabled = true, Effort = "max" });
        Assert.Equal("enabled", effortBody.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("max", effortBody.RootElement.GetProperty("reasoning_effort").GetString());

        using JsonDocument offBody = CreateBody(
            endpoint, model, new AiModelReasoningSettings { Disabled = true });
        Assert.False(offBody.RootElement.TryGetProperty("thinking", out _));
    }

    [Fact]
    public void KimiK3_UsesOpenAiEffortWhileKimiK25UsesKimiThinkingDialect()
    {
        const string endpoint = "https://api.moonshot.cn/v1";
        AiProviderModel k3 = new() { Id = "kimi-k3" };
        AiReasoningDialectSupport.ApplyKnownModelDefaults(k3, endpoint);
        Assert.Equal("openaiReasoningEffort", k3.ReasoningDialect);
        Assert.Equal(new[] { "low", "high", "max" }, k3.ReasoningLevels);

        using JsonDocument k3Body = CreateBody(
            endpoint, k3, new AiModelReasoningSettings { Enabled = true, Effort = "high" });
        Assert.Equal("high", k3Body.RootElement.GetProperty("reasoning_effort").GetString());

        AiProviderModel k25 = new() { Id = "kimi-k2.5" };
        AiReasoningDialectSupport.ApplyKnownModelDefaults(k25, endpoint);
        Assert.Equal("kimiThinking", k25.ReasoningDialect);
        Assert.True(k25.ReasoningCanDisable);

        using JsonDocument k25Body = CreateBody(
            endpoint, k25, new AiModelReasoningSettings { Disabled = true });
        Assert.Equal("disabled", k25Body.RootElement.GetProperty("thinking").GetProperty("type").GetString());
    }

    [Fact]
    public void OpenRouter_UsesItsOwnReasoningObjectAndPreservesExplicitCapability()
    {
        AiProviderModel model = new()
        {
            Id = "openai/gpt-5.6",
            ReasoningSupport = true,
            ReasoningLevels = ["low", "high"],
            ReasoningCanDisable = true
        };
        const string endpoint = "https://openrouter.ai/api/v1";
        AiReasoningDialectSupport.ApplyKnownModelDefaults(model, endpoint);

        Assert.Equal("openrouterReasoning", model.ReasoningDialect);
        using JsonDocument body = CreateBody(
            endpoint, model, new AiModelReasoningSettings { Enabled = true, Effort = "high" });
        Assert.Equal("high", body.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.False(body.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public void InternDialect_UsesThinkingModeBoolean()
    {
        AiProviderModel model = new()
        {
            Id = "intern-custom-reasoner",
            ReasoningSupport = true,
            ReasoningCanDisable = true
        };
        const string endpoint = "https://chat.intern-ai.org.cn/v1";
        AiReasoningDialectSupport.ApplyKnownModelDefaults(model, endpoint);

        Assert.Equal("internThinkingMode", model.ReasoningDialect);
        using JsonDocument body = CreateBody(
            endpoint, model, new AiModelReasoningSettings { Disabled = true });
        Assert.False(body.RootElement.GetProperty("thinking_mode").GetBoolean());
    }

    [Fact]
    public void AutoAndUnknownEndpoints_DoNotAddReasoningOverrides()
    {
        AiProviderModel model = new()
        {
            Id = "reasoning-model",
            ReasoningSupport = true,
            ReasoningCanDisable = true,
            ReasoningLevels = ["low", "high"]
        };

        using JsonDocument autoBody = CreateBody(
            "https://api.openai.com/v1", model, new AiModelReasoningSettings());
        Assert.False(autoBody.RootElement.TryGetProperty("reasoning_effort", out _));

        using JsonDocument unknownEndpointBody = CreateBody(
            "https://proxy.example.com/v1", model,
            new AiModelReasoningSettings { Enabled = true, Effort = "high" });
        Assert.False(unknownEndpointBody.RootElement.TryGetProperty("reasoning_effort", out _));
        Assert.False(unknownEndpointBody.RootElement.TryGetProperty("reasoning", out _));
    }

    [Fact]
    public void QwenWithoutCatalogEffortLevels_UsesKelivoBudgetPresets()
    {
        AiProviderModel model = new() { Id = "qwen3.7-plus" };
        const string endpoint = "https://dashscope.aliyuncs.com/compatible-mode/v1";
        AiReasoningDialectSupport.ApplyKnownModelDefaults(model, endpoint);
        Assert.Empty(model.ReasoningLevels);

        using JsonDocument body = CreateBody(
            endpoint, model, new AiModelReasoningSettings { Enabled = true, Effort = "high" });
        Assert.True(body.RootElement.GetProperty("enable_thinking").GetBoolean());
        Assert.Equal(8192, body.RootElement.GetProperty("thinking_budget").GetInt32());
    }

    [Fact]
    public void OpenRouterWithoutEffortLevels_UsesMaxTokensBudget()
    {
        AiProviderModel model = new()
        {
            Id = "vendor/reasoner",
            ReasoningSupport = true,
            ReasoningCanDisable = true
        };
        const string endpoint = "https://openrouter.ai/api/v1";
        AiReasoningDialectSupport.ApplyKnownModelDefaults(model, endpoint);

        using JsonDocument body = CreateBody(
            endpoint, model, new AiModelReasoningSettings { Enabled = true, Effort = "high" });
        JsonElement reasoning = body.RootElement.GetProperty("reasoning");
        Assert.True(reasoning.GetProperty("enabled").GetBoolean());
        Assert.Equal(8192, reasoning.GetProperty("max_tokens").GetInt32());
        Assert.False(reasoning.TryGetProperty("effort", out _));
    }

    [Fact]
    public void SiliconFlowWithoutEffortLevels_UsesThinkingBudget()
    {
        AiProviderModel model = new()
        {
            Id = "Qwen/Qwen3-8B",
            ReasoningSupport = true,
            ReasoningCanDisable = true
        };
        const string endpoint = "https://api.siliconflow.cn/v1";
        AiReasoningDialectSupport.ApplyKnownModelDefaults(model, endpoint);

        using JsonDocument body = CreateBody(
            endpoint, model, new AiModelReasoningSettings { Enabled = true, Effort = "medium" });
        Assert.Equal(4096, body.RootElement.GetProperty("thinking_budget").GetInt32());
        Assert.False(body.RootElement.TryGetProperty("enable_thinking", out _));
    }

    [Fact]
    public void MimoV2_UsesThinkingTypeDialectAndModelEffortLevels()
    {
        AiProviderModel model = new() { Id = "mimo-v2" };
        const string endpoint = "https://api.xiaomimimo.com/v1";
        AiReasoningDialectSupport.ApplyKnownModelDefaults(model, endpoint);

        Assert.Equal("thinkingType", model.ReasoningDialect);
        Assert.True(model.ReasoningCanDisable);
        Assert.Equal(new[] { "low", "medium", "high" }, model.ReasoningLevels);

        using JsonDocument body = CreateBody(
            endpoint, model, new AiModelReasoningSettings { Enabled = true, Effort = "medium" });
        Assert.Equal("enabled", body.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("medium", body.RootElement.GetProperty("reasoning_effort").GetString());
    }

    private static JsonDocument CreateBody(
        string baseUrl,
        AiProviderModel model,
        AiModelReasoningSettings setting)
    {
        var profile = new AiProviderProfile
        {
            BaseUrl = baseUrl,
            Model = model.Id,
            ApiKey = "test-key",
            AvailableModels = [model],
            ReasoningSettingsByModel = new Dictionary<string, AiModelReasoningSettings>(
                StringComparer.OrdinalIgnoreCase)
            {
                [model.Id] = setting
            }
        };

        AiProviderHttpRequestDetails request =
            AiProviderHttpRequestFactory.Create(profile, "system", "user");
        return JsonDocument.Parse(request.JsonBody);
    }
}
