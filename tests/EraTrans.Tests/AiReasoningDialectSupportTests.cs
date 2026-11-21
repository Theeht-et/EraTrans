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
    public void ProviderDialectOverridesConflictingCatalogDialectLikeKelivo()
    {
        AiProviderModel openAiModel = new()
        {
            Id = "gpt-5.6",
            ReasoningSupport = true,
            ReasoningDialect = "openaiResponsesReasoning"
        };
        AiReasoningDialectSupport.ApplyKnownModelDefaults(
            openAiModel, "https://api.openai.com/v1");
        Assert.Equal("openaiReasoningEffort", openAiModel.ReasoningDialect);

        AiProviderModel qwenModel = new()
        {
            Id = "qwen3.7-plus",
            ReasoningSupport = true,
            ReasoningDialect = "openaiReasoningEffort"
        };
        AiReasoningDialectSupport.ApplyKnownModelDefaults(
            qwenModel, "https://dashscope.aliyuncs.com/compatible-mode/v1");
        Assert.Equal("qwenEnableThinking", qwenModel.ReasoningDialect);
    }

    [Theory]
    [InlineData("gemma-4", "minimal,high")]
    [InlineData("muse-spark-1", "low,medium,high,xhigh")]
    [InlineData("muse-spark-1.3", "low,medium,high,xhigh,max")]
    [InlineData("muse-spark-1.3-contributor", "low,medium,high,xhigh")]
    public void GemmaAndMuseSpark_UseKelivoModelReasoningDefaults(
        string modelId,
        string expectedLevels)
    {
        AiProviderModel model = new() { Id = modelId };
        AiReasoningDialectSupport.ApplyKnownModelDefaults(
            model, "https://proxy.example.com/v1");

        Assert.True(model.ReasoningSupport);
        Assert.Equal(
            expectedLevels.Split(','),
            model.ReasoningLevels);
        Assert.False(model.ReasoningCanDisable);
        Assert.Null(model.ReasoningDialect);
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
    public void Qwen3ModelGuess_MatchesKelivoSubstringDetection()
    {
        AiProviderModel model = new() { Id = "relay-qwen3-custom" };
        AiReasoningDialectSupport.ApplyKnownModelDefaults(
            model, "https://custom.example/v1");

        Assert.True(model.ReasoningSupport);
        Assert.True(model.ReasoningCanDisable);
        Assert.Null(model.ReasoningDialect);
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
        Assert.False(offBody.RootElement.TryGetProperty("reasoning_effort", out _));
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
    public void KimiK3ColonSuffix_UsesKelivoEffortDefaults()
    {
        AiProviderModel model = new() { Id = "kimi-k3:latest" };
        AiReasoningDialectSupport.ApplyKnownModelDefaults(
            model, "https://api.moonshot.cn/v1");

        Assert.Equal("openaiReasoningEffort", model.ReasoningDialect);
        Assert.Equal(new[] { "low", "high", "max" }, model.ReasoningLevels);
    }

    [Fact]
    public void KimiForCodingGuess_RequiresKelivoModelIdBoundary()
    {
        AiProviderModel unrelated = new() { Id = "not-kimi-for-coding-experimental" };
        AiReasoningDialectSupport.ApplyKnownModelDefaults(
            unrelated, "https://api.moonshot.cn/v1");

        Assert.Null(unrelated.ReasoningSupport);
        Assert.Empty(unrelated.ReasoningLevels);
        Assert.Null(unrelated.ReasoningCanDisable);
    }

    [Fact]
    public void KimiForCodingHighspeed_HasNoEffortLadderButCanDisableLikeKelivo()
    {
        const string endpoint = "https://api.moonshot.cn/v1";
        AiProviderModel model = new() { Id = "kimi-for-coding-highspeed" };
        AiReasoningDialectSupport.ApplyKnownModelDefaults(model, endpoint);

        Assert.True(model.ReasoningSupport);
        Assert.Equal("kimiThinking", model.ReasoningDialect);
        Assert.Empty(model.ReasoningLevels);
        Assert.True(model.ReasoningCanDisable);
        Assert.True(AiReasoningDialectSupport.IsCompatible(endpoint, model));
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
    public void OpenRouterBudgetPresets_UseKelivoCatalogScalingAndMinimum()
    {
        const string endpoint = "https://openrouter.ai/api/v1";
        AiProviderModel model = new()
        {
            Id = "vendor/budget-model",
            ReasoningSupport = true,
            ReasoningDialect = "openrouterReasoning",
            ReasoningBudgetMin = 4096,
            ReasoningBudgetMax = 50000
        };

        using JsonDocument lowBody = CreateBody(
            endpoint, model, new AiModelReasoningSettings { Enabled = true, Effort = "low" });
        Assert.Equal(4096, lowBody.RootElement.GetProperty("reasoning").GetProperty("max_tokens").GetInt32());

        using JsonDocument mediumBody = CreateBody(
            endpoint, model, new AiModelReasoningSettings { Enabled = true, Effort = "medium" });
        Assert.Equal(24832, mediumBody.RootElement.GetProperty("reasoning").GetProperty("max_tokens").GetInt32());
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

        using JsonDocument openRouterAutoBody = CreateBody(
            "https://openrouter.ai/api/v1", model, new AiModelReasoningSettings());
        Assert.True(openRouterAutoBody.RootElement.GetProperty("reasoning").GetProperty("enabled").GetBoolean());
        Assert.False(openRouterAutoBody.RootElement.GetProperty("reasoning").TryGetProperty("effort", out _));

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

    [Fact]
    public void LegacyDeepSeekModelHint_DoesNotEnableUnverifiedOfficialEndpointDialect()
    {
        AiProviderModel model = new() { Id = "deepseek-reasoner" };
        AiReasoningDialectSupport.ApplyKnownModelDefaults(model, "https://api.deepseek.com/v1");

        Assert.True(model.ReasoningSupport);
        Assert.Empty(model.ReasoningLevels);
        Assert.Null(model.ReasoningCanDisable);
        Assert.Null(model.ReasoningDialect);
        Assert.False(AiReasoningDialectSupport.IsCompatible("https://api.deepseek.com/v1", model));
    }

    [Fact]
    public void DeepSeekOfficialCatalogCapabilityEnablesDialectOnlyWhenExplicitlyAdvertised()
    {
        const string endpoint = "https://api.deepseek.com/v1";

        AiProviderModel advertised = new()
        {
            Id = "deepseek-new-model",
            ReasoningSupport = true,
            ReasoningLevels = ["low", "high", "max"],
            ReasoningCanDisable = false
        };
        AiReasoningDialectSupport.ApplyKnownModelDefaults(advertised, endpoint);
        Assert.Equal("deepseekThinking", advertised.ReasoningDialect);
        Assert.True(AiReasoningDialectSupport.IsCompatible(endpoint, advertised));

        using JsonDocument effortBody = CreateBody(
            endpoint, advertised, new AiModelReasoningSettings { Enabled = true, Effort = "high" });
        Assert.Equal("enabled", effortBody.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("high", effortBody.RootElement.GetProperty("reasoning_effort").GetString());

        AiProviderModel unknown = new() { Id = "deepseek-unlisted-model" };
        AiReasoningDialectSupport.ApplyKnownModelDefaults(unknown, endpoint);
        Assert.Null(unknown.ReasoningSupport);
        Assert.Null(unknown.ReasoningDialect);
        Assert.False(AiReasoningDialectSupport.IsCompatible(endpoint, unknown));
    }

    [Fact]
    public void LegacyDeepSeekModelHint_CanUseKnownOpenRouterDialect()
    {
        AiProviderModel model = new() { Id = "deepseek-reasoner" };
        const string endpoint = "https://openrouter.ai/api/v1";
        AiReasoningDialectSupport.ApplyKnownModelDefaults(model, endpoint);

        Assert.Equal("openrouterReasoning", model.ReasoningDialect);
        using JsonDocument body = CreateBody(
            endpoint, model, new AiModelReasoningSettings { Enabled = true, Effort = "high" });
        Assert.Equal("high", body.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
    }

    [Fact]
    public void Grok47CapabilityIsRecognizedButNotSentThroughLegacyChatCompletions()
    {
        AiProviderModel model = new() { Id = "grok-4.7" };
        const string endpoint = "https://api.x.ai/v1";
        AiReasoningDialectSupport.ApplyKnownModelDefaults(model, endpoint);

        Assert.True(model.ReasoningSupport);
        Assert.False(model.ReasoningCanDisable);
        Assert.Null(model.ReasoningDialect);
        Assert.Equal(new[] { "low", "medium", "high", "xhigh" }, model.ReasoningLevels);
        Assert.False(AiReasoningDialectSupport.IsCompatible(endpoint, model));

        using JsonDocument body = CreateBody(
            endpoint, model, new AiModelReasoningSettings { Enabled = true, Effort = "xhigh" });
        Assert.False(body.RootElement.TryGetProperty("reasoning_effort", out _));
        Assert.False(body.RootElement.TryGetProperty("reasoning", out _));
    }

    [Fact]
    public void Qwen4Max_UsesAliyunReasoningEffortAndModelSpecificLevels()
    {
        AiProviderModel model = new() { Id = "qwen4-max" };
        const string endpoint = "https://dashscope.aliyuncs.com/compatible-mode/v1";
        AiReasoningDialectSupport.ApplyKnownModelDefaults(model, endpoint);

        Assert.True(model.ReasoningSupport);
        Assert.True(model.ReasoningCanDisable);
        Assert.Equal("qwenReasoningEffort", model.ReasoningDialect);
        Assert.Equal(new[] { "low", "high", "xhigh" }, model.ReasoningLevels);

        using JsonDocument effortBody = CreateBody(
            endpoint, model, new AiModelReasoningSettings { Enabled = true, Effort = "xhigh" });
        Assert.Equal("xhigh", effortBody.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.False(effortBody.RootElement.TryGetProperty("thinking_budget", out _));
        Assert.False(effortBody.RootElement.TryGetProperty("enable_thinking", out _));

        using JsonDocument offBody = CreateBody(
            endpoint, model, new AiModelReasoningSettings { Disabled = true });
        Assert.Equal("none", offBody.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.False(offBody.RootElement.TryGetProperty("enable_thinking", out _));
    }

    [Fact]
    public void Qwen38_UsesOnlyItsDocumentedEffortLevelsAndNoBudgetCombination()
    {
        AiProviderModel model = new() { Id = "qwen3.8-flash" };
        const string endpoint = "https://dashscope-intl.aliyuncs.com/compatible-mode/v1";
        AiReasoningDialectSupport.ApplyKnownModelDefaults(model, endpoint);

        Assert.Equal("qwenReasoningEffort", model.ReasoningDialect);
        Assert.True(model.ReasoningCanDisable);
        Assert.Equal(new[] { "low", "medium", "xhigh" }, model.ReasoningLevels);

        using JsonDocument effortBody = CreateBody(
            endpoint, model, new AiModelReasoningSettings { Enabled = true, Effort = "medium" });
        Assert.Equal("medium", effortBody.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.False(effortBody.RootElement.TryGetProperty("thinking_budget", out _));

        using JsonDocument offBody = CreateBody(
            endpoint, model, new AiModelReasoningSettings { Disabled = true });
        Assert.False(offBody.RootElement.GetProperty("enable_thinking").GetBoolean());
        Assert.False(offBody.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public void Qwen38ThinkingOnlyOpenWeightModel_DoesNotExposeOff()
    {
        AiProviderModel model = new() { Id = "qwen3.8-2.4t-a95b" };
        const string endpoint = "https://dashscope.aliyuncs.com/compatible-mode/v1";
        AiReasoningDialectSupport.ApplyKnownModelDefaults(model, endpoint);

        Assert.True(model.ReasoningSupport);
        Assert.False(model.ReasoningCanDisable);
        Assert.Equal("qwenReasoningEffort", model.ReasoningDialect);
        Assert.Equal(new[] { "low", "medium", "xhigh" }, model.ReasoningLevels);

        using JsonDocument offBody = CreateBody(
            endpoint, model, new AiModelReasoningSettings { Disabled = true });
        Assert.False(offBody.RootElement.TryGetProperty("enable_thinking", out _));
        Assert.False(offBody.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public void Qwen37MaxOnlyThinkingSnapshotsAreDistinguishedFromMixedThinkingModels()
    {
        const string endpoint = "https://dashscope.aliyuncs.com/compatible-mode/v1";
        foreach (string id in new[] { "qwen3.7-max-preview", "qwen3.7-max-2026-05-17" })
        {
            AiProviderModel onlyThinking = new() { Id = id };
            AiReasoningDialectSupport.ApplyKnownModelDefaults(onlyThinking, endpoint);
            Assert.True(onlyThinking.ReasoningSupport);
            Assert.False(onlyThinking.ReasoningCanDisable);
        }

        foreach (string id in new[] { "qwen3.7-max-2026-05-20", "qwen3.7-max-2026-06-08" })
        {
            AiProviderModel mixed = new() { Id = id };
            AiReasoningDialectSupport.ApplyKnownModelDefaults(mixed, endpoint);
            Assert.True(mixed.ReasoningSupport);
            Assert.True(mixed.ReasoningCanDisable);
        }
    }

    [Fact]
    public void AliyunWorkspaceDomain_ResolvesDialectFromModelFamilyNotHostAlone()
    {
        const string endpoint = "https://llm-abc123.cn-beijing.maas.aliyuncs.com/compatible-mode/v1";
        AiProviderModel qwen = new() { Id = "qwen4-max" };
        AiReasoningDialectSupport.ApplyKnownModelDefaults(qwen, endpoint);
        Assert.Equal("qwenReasoningEffort", qwen.ReasoningDialect);

        AiProviderModel glm = new() { Id = "glm-5.3" };
        AiReasoningDialectSupport.ApplyKnownModelDefaults(glm, endpoint);
        Assert.Equal("dashScopeGlmEffort", glm.ReasoningDialect);
        Assert.False(glm.ReasoningCanDisable);
        Assert.Equal(new[] { "low", "high", "max" }, glm.ReasoningLevels);

        AiProviderModel glm52 = new() { Id = "glm-5.2" };
        AiReasoningDialectSupport.ApplyKnownModelDefaults(glm52, endpoint);
        Assert.Equal("dashScopeGlmEffort", glm52.ReasoningDialect);
        Assert.True(glm52.ReasoningCanDisable);
        Assert.Equal(new[] { "low", "medium", "high", "xhigh", "max" }, glm52.ReasoningLevels);

        AiProviderModel unknown = new() { Id = "custom-reasoning-model", ReasoningSupport = true };
        AiReasoningDialectSupport.ApplyKnownModelDefaults(unknown, endpoint);
        Assert.Null(unknown.ReasoningDialect);
        Assert.False(AiReasoningDialectSupport.IsCompatible(endpoint, unknown));

        AiProviderModel deepseek = new() { Id = "deepseek-v4-pro" };
        AiReasoningDialectSupport.ApplyKnownModelDefaults(deepseek, endpoint);
        Assert.Equal("dashScopeDeepSeekEffort", deepseek.ReasoningDialect);
        Assert.True(deepseek.ReasoningCanDisable);
        Assert.Equal(new[] { "high", "max" }, deepseek.ReasoningLevels);
        Assert.True(AiReasoningDialectSupport.IsCompatible(endpoint, deepseek));
    }

    [Fact]
    public void DashScopeGlm53_UsesReasoningEffortAndDoesNotAllowDisable()
    {
        AiProviderModel model = new() { Id = "glm-5.3" };
        const string endpoint = "https://dashscope.aliyuncs.com/compatible-mode/v1";
        AiReasoningDialectSupport.ApplyKnownModelDefaults(model, endpoint);

        Assert.Equal("dashScopeGlmEffort", model.ReasoningDialect);
        Assert.False(model.ReasoningCanDisable);
        Assert.Equal(new[] { "low", "high", "max" }, model.ReasoningLevels);

        using JsonDocument effortBody = CreateBody(
            endpoint, model, new AiModelReasoningSettings { Enabled = true, Effort = "max" });
        Assert.Equal("max", effortBody.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.False(effortBody.RootElement.TryGetProperty("thinking", out _));

        using JsonDocument offBody = CreateBody(
            endpoint, model, new AiModelReasoningSettings { Disabled = true });
        Assert.False(offBody.RootElement.TryGetProperty("enable_thinking", out _));
        Assert.False(offBody.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public void DashScopeGlm52_UsesReasoningEffortAndEnableThinkingOff()
    {
        AiProviderModel model = new() { Id = "glm-5.2" };
        const string endpoint = "https://dashscope.aliyuncs.com/compatible-mode/v1";
        AiReasoningDialectSupport.ApplyKnownModelDefaults(model, endpoint);

        Assert.True(model.ReasoningCanDisable);
        Assert.Equal(new[] { "low", "medium", "high", "xhigh", "max" }, model.ReasoningLevels);
        using JsonDocument effortBody = CreateBody(
            endpoint, model, new AiModelReasoningSettings { Enabled = true, Effort = "max" });
        Assert.Equal("max", effortBody.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.False(effortBody.RootElement.TryGetProperty("thinking", out _));

        using JsonDocument offBody = CreateBody(
            endpoint, model, new AiModelReasoningSettings { Disabled = true });
        Assert.False(offBody.RootElement.GetProperty("enable_thinking").GetBoolean());
        Assert.False(offBody.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public void ZhipuGlm53OnAliyun_UsesModelStudioReasoningEffortDialect()
    {
        AiProviderModel model = new() { Id = "ZHIPU/GLM-5.3" };
        const string endpoint = "https://llm-workspace.cn-beijing.maas.aliyuncs.com/compatible-mode/v1";
        AiReasoningDialectSupport.ApplyKnownModelDefaults(model, endpoint);

        Assert.Equal("dashScopeGlmEffort", model.ReasoningDialect);
        Assert.False(model.ReasoningCanDisable);
        Assert.Equal(new[] { "low", "high", "max" }, model.ReasoningLevels);

        using JsonDocument body = CreateBody(
            endpoint, model, new AiModelReasoningSettings { Enabled = true, Effort = "high" });
        Assert.Equal("high", body.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.False(body.RootElement.TryGetProperty("thinking", out _));
        Assert.False(body.RootElement.TryGetProperty("enable_thinking", out _));
    }

    [Fact]
    public void InternS2_UsesDocumentedThinkingModeSwitch()
    {
        AiProviderModel model = new() { Id = "intern-s2" };
        const string endpoint = "https://chat.intern-ai.org.cn/v1";
        AiReasoningDialectSupport.ApplyKnownModelDefaults(model, endpoint);

        Assert.True(model.ReasoningSupport);
        Assert.True(model.ReasoningCanDisable);
        Assert.Equal("internThinkingMode", model.ReasoningDialect);
        Assert.Empty(model.ReasoningLevels);

        using JsonDocument offBody = CreateBody(
            endpoint, model, new AiModelReasoningSettings { Disabled = true });
        Assert.False(offBody.RootElement.GetProperty("thinking_mode").GetBoolean());
    }

    [Fact]
    public void UnsupportedOpenAiGptVariantsRemainUnknownInsteadOfBeingOverclassified()
    {
        foreach (string id in new[] { "gpt-5.5-codex", "gpt-5.4-codex", "gpt-5.3-pro", "gpt-5.1-pro" })
        {
            AiProviderModel model = new() { Id = id };
            AiReasoningDialectSupport.ApplyKnownModelDefaults(model, "https://api.openai.com/v1");
            Assert.Null(model.ReasoningSupport);
            Assert.Null(model.ReasoningCanDisable);
            Assert.Empty(model.ReasoningLevels);
        }
    }

    [Fact]
    public void DoubaoSeed_UsesVolcanoDialectAndDisabledThinkingMinimalEffort()
    {
        AiProviderModel model = new() { Id = "doubao-seed-1.6" };
        const string endpoint = "https://ark.cn-beijing.volces.com/api/v3";
        AiReasoningDialectSupport.ApplyKnownModelDefaults(model, endpoint);

        Assert.True(model.ReasoningSupport);
        Assert.True(model.ReasoningCanDisable);
        Assert.Equal("volcanoThinkingType", model.ReasoningDialect);

        using JsonDocument body = CreateBody(
            endpoint, model, new AiModelReasoningSettings { Disabled = true });
        Assert.Equal("disabled", body.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("minimal", body.RootElement.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public void VolcanoArkRegionalHost_UsesVolcanoDialectRatherThanGenericThinkingType()
    {
        AiProviderModel model = new()
        {
            Id = "doubao-seed-2-1-pro-260915",
            ReasoningSupport = true,
            ReasoningCanDisable = true,
            ReasoningDialect = "volcanoThinkingType"
        };
        const string endpoint = "https://ark.us-east-1.volces.com/api/v3";

        Assert.True(AiReasoningDialectSupport.IsCompatible(endpoint, model));
        // This Doubao Seed model is treated as fixed-thinking by Kelivo; only
        // the off switch is inferred here because no effort ladder is advertised.
        using JsonDocument offBody = CreateBody(
            endpoint, model, new AiModelReasoningSettings { Disabled = true });
        Assert.Equal("disabled", offBody.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("minimal", offBody.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.False(offBody.RootElement.TryGetProperty("enable_thinking", out _));
    }

    [Fact]
    public void Laguna_UsesPoolsideChatTemplateToggle()
    {
        AiProviderModel model = new() { Id = "laguna-reasoner" };
        const string endpoint = "https://api.poolside.ai/v1";
        AiReasoningDialectSupport.ApplyKnownModelDefaults(model, endpoint);

        Assert.True(model.ReasoningSupport);
        Assert.True(model.ReasoningCanDisable);
        Assert.Equal("chatTemplateKwargs", model.ReasoningDialect);

        using JsonDocument body = CreateBody(
            endpoint, model, new AiModelReasoningSettings { Disabled = true });
        Assert.False(body.RootElement.GetProperty("chat_template_kwargs")
            .GetProperty("enable_thinking").GetBoolean());
    }

    [Fact]
    public void AliyunDeepSeekV4Pro_UsesOnlyHighAndMaxEffort()
    {
        AiProviderModel model = new() { Id = "deepseek-v4-pro" };
        const string endpoint = "https://dashscope.aliyuncs.com/compatible-mode/v1";
        AiReasoningDialectSupport.ApplyKnownModelDefaults(model, endpoint);

        Assert.Equal("dashScopeDeepSeekEffort", model.ReasoningDialect);
        Assert.True(model.ReasoningCanDisable);
        Assert.Equal(new[] { "high", "max" }, model.ReasoningLevels);

        using JsonDocument body = CreateBody(
            endpoint, model, new AiModelReasoningSettings { Enabled = true, Effort = "max" });
        Assert.Equal("max", body.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.False(body.RootElement.TryGetProperty("thinking", out _));

        using JsonDocument offBody = CreateBody(
            endpoint, model, new AiModelReasoningSettings { Disabled = true });
        Assert.False(offBody.RootElement.GetProperty("enable_thinking").GetBoolean());
        Assert.False(offBody.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public void AliyunDeepSeekV4Flash0731_UsesLowHighAndMaxEffort()
    {
        AiProviderModel model = new() { Id = "deepseek-v4-flash-0731" };
        const string endpoint = "https://dashscope.aliyuncs.com/compatible-mode/v1";
        AiReasoningDialectSupport.ApplyKnownModelDefaults(model, endpoint);

        Assert.Equal("dashScopeDeepSeekEffort", model.ReasoningDialect);
        Assert.Equal(new[] { "low", "high", "max" }, model.ReasoningLevels);
        using JsonDocument body = CreateBody(
            endpoint, model, new AiModelReasoningSettings { Enabled = true, Effort = "low" });
        Assert.Equal("low", body.RootElement.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public void AliyunKimiK3AndK31Pro_UseTheirDistinctEffortLaddersAndOffSwitch()
    {
        const string endpoint = "https://llm-workspace.cn-beijing.maas.aliyuncs.com/compatible-mode/v1";
        AiProviderModel k3 = new() { Id = "kimi/kimi-k3" };
        AiReasoningDialectSupport.ApplyKnownModelDefaults(k3, endpoint);
        Assert.Equal("dashScopeKimiEffort", k3.ReasoningDialect);
        Assert.True(k3.ReasoningCanDisable);
        Assert.Equal(new[] { "low", "high", "max" }, k3.ReasoningLevels);

        using JsonDocument k3Body = CreateBody(
            endpoint, k3, new AiModelReasoningSettings { Enabled = true, Effort = "low" });
        Assert.Equal("low", k3Body.RootElement.GetProperty("reasoning_effort").GetString());
        using JsonDocument k3Off = CreateBody(
            endpoint, k3, new AiModelReasoningSettings { Disabled = true });
        Assert.False(k3Off.RootElement.GetProperty("enable_thinking").GetBoolean());

        AiProviderModel k31 = new() { Id = "kimi-k3.1-pro" };
        AiReasoningDialectSupport.ApplyKnownModelDefaults(k31, endpoint);
        Assert.Equal("dashScopeKimiEffort", k31.ReasoningDialect);
        Assert.True(k31.ReasoningCanDisable);
        Assert.Equal(new[] { "low", "medium", "high", "max" }, k31.ReasoningLevels);

        using JsonDocument k31Body = CreateBody(
            endpoint, k31, new AiModelReasoningSettings { Enabled = true, Effort = "medium" });
        Assert.Equal("medium", k31Body.RootElement.GetProperty("reasoning_effort").GetString());
        using JsonDocument k31Off = CreateBody(
            endpoint, k31, new AiModelReasoningSettings { Disabled = true });
        Assert.False(k31Off.RootElement.GetProperty("enable_thinking").GetBoolean());
    }

    [Theory]
    [InlineData("Pro/deepseek-ai/DeepSeek-V4")]
    [InlineData("deepseek-ai/DeepSeek-V4-Flash")]
    [InlineData("Pro/zai-org/GLM-5.2")]
    public void SiliconFlowSpecialReasoningModels_UseTopLevelReasoningEffort(string modelId)
    {
        AiProviderModel model = new() { Id = modelId };
        const string endpoint = "https://api.siliconflow.cn/v1";
        AiReasoningDialectSupport.ApplyKnownModelDefaults(model, endpoint);

        Assert.Equal("siliconFlowReasoningEffort", model.ReasoningDialect);
        Assert.True(model.ReasoningCanDisable);
        Assert.Equal(
            modelId.Contains("GLM", StringComparison.OrdinalIgnoreCase)
                ? new[] { "low", "medium", "high", "xhigh", "max" }
                : new[] { "high", "max" },
            model.ReasoningLevels);

        using JsonDocument effortBody = CreateBody(
            endpoint, model, new AiModelReasoningSettings { Enabled = true, Effort = "high" });
        Assert.Equal("high", effortBody.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.False(effortBody.RootElement.TryGetProperty("thinking_budget", out _));

        using JsonDocument offBody = CreateBody(
            endpoint, model, new AiModelReasoningSettings { Disabled = true });
        Assert.False(offBody.RootElement.GetProperty("enable_thinking").GetBoolean());
        Assert.False(offBody.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public void UnlistedSiliconFlowModel_DoesNotReceiveSpecialReasoningEffortDialect()
    {
        AiProviderModel model = new() { Id = "deepseek-v4-pro" };
        const string endpoint = "https://api.siliconflow.cn/v1";
        AiReasoningDialectSupport.ApplyKnownModelDefaults(model, endpoint);

        Assert.Equal("siliconflowEnableThinking", model.ReasoningDialect);
        Assert.NotEqual("siliconFlowReasoningEffort", model.ReasoningDialect);
        using JsonDocument body = CreateBody(
            endpoint, model, new AiModelReasoningSettings { Enabled = true, Effort = "high" });
        Assert.False(body.RootElement.TryGetProperty("reasoning_effort", out _));
        Assert.True(body.RootElement.TryGetProperty("thinking_budget", out _));
    }

    [Fact]
    public void DirectZhipuGlm52_PreservesKelivoModelEffortLadder()
    {
        AiProviderModel model = new() { Id = "glm-5.2" };
        const string endpoint = "https://open.bigmodel.cn/api/paas/v4";
        AiReasoningDialectSupport.ApplyKnownModelDefaults(model, endpoint);

        Assert.Equal("thinkingType", model.ReasoningDialect);
        Assert.Equal(new[] { "low", "medium", "high", "xhigh", "max" }, model.ReasoningLevels);
        Assert.True(model.ReasoningCanDisable);
    }

    [Theory]
    [InlineData("kimi-k2.5")]
    [InlineData("kimi-k2.6")]
    public void AliyunKimiHybridModels_UseEnableThinkingOff(string modelId)
    {
        AiProviderModel model = new() { Id = modelId };
        const string endpoint = "https://dashscope.aliyuncs.com/compatible-mode/v1";
        AiReasoningDialectSupport.ApplyKnownModelDefaults(model, endpoint);

        Assert.Equal("dashScopeKimiToggle", model.ReasoningDialect);
        Assert.True(model.ReasoningCanDisable);
        Assert.Empty(model.ReasoningLevels);

        using JsonDocument offBody = CreateBody(
            endpoint, model, new AiModelReasoningSettings { Disabled = true });
        Assert.False(offBody.RootElement.GetProperty("enable_thinking").GetBoolean());
        Assert.False(offBody.RootElement.TryGetProperty("thinking", out _));
        Assert.False(offBody.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public void AliyunKimiK27Code_RemainsThinkingOnlyWithoutOffControl()
    {
        AiProviderModel model = new() { Id = "kimi-k2.7-code" };
        const string endpoint = "https://dashscope.aliyuncs.com/compatible-mode/v1";
        AiReasoningDialectSupport.ApplyKnownModelDefaults(model, endpoint);

        Assert.Equal("dashScopeKimiToggle", model.ReasoningDialect);
        Assert.False(model.ReasoningCanDisable);
        Assert.Empty(model.ReasoningLevels);
        Assert.True(AiReasoningDialectSupport.IsCompatible(endpoint, model));

        using JsonDocument offBody = CreateBody(
            endpoint, model, new AiModelReasoningSettings { Disabled = true });
        Assert.False(offBody.RootElement.TryGetProperty("enable_thinking", out _));
    }

    [Fact]
    public void LegacyDeepSeekFlashModel_UsesKnownOpenRouterDialect()
    {
        AiProviderModel model = new() { Id = "deepseek-flash" };
        const string endpoint = "https://openrouter.ai/api/v1";
        AiReasoningDialectSupport.ApplyKnownModelDefaults(model, endpoint);

        Assert.True(model.ReasoningSupport);
        Assert.True(model.ReasoningCanDisable);
        Assert.Equal(new[] { "low", "high", "max" }, model.ReasoningLevels);
        Assert.Equal("openrouterReasoning", model.ReasoningDialect);
        using JsonDocument body = CreateBody(
            endpoint, model, new AiModelReasoningSettings { Enabled = true, Effort = "max" });
        Assert.Equal("max", body.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
    }

    [Fact]
    public void AliyunUnknownLegacyGlmVariant_DoesNotGuessModelStudioWireDialect()
    {
        AiProviderModel model = new() { Id = "glm-4.7" };
        const string endpoint = "https://dashscope.aliyuncs.com/compatible-mode/v1";
        AiReasoningDialectSupport.ApplyKnownModelDefaults(model, endpoint);

        Assert.True(model.ReasoningSupport);
        Assert.Null(model.ReasoningDialect);
        Assert.False(AiReasoningDialectSupport.IsCompatible(endpoint, model));
    }

    [Fact]
    public void ExplicitFalseReasoningMetadata_IsNotOverriddenByKelivoModelGuesses()
    {
        AiProviderModel unsupported = new()
        {
            Id = "gpt-5.6",
            ReasoningSupport = false,
            ReasoningCanDisable = false,
            ReasoningLevels = ["provider-level"]
        };
        AiReasoningDialectSupport.ApplyKnownModelDefaults(
            unsupported, "https://api.openai.com/v1");

        Assert.False(unsupported.ReasoningSupport);
        Assert.False(unsupported.SupportsReasoning);
        Assert.False(unsupported.ReasoningCanDisable);
        Assert.Equal(new[] { "provider-level" }, unsupported.ReasoningLevels);
        Assert.Null(unsupported.ReasoningDialect);

        AiProviderModel supportedButCannotDisable = new()
        {
            Id = "gpt-5.6",
            ReasoningSupport = true,
            ReasoningCanDisable = false
        };
        AiReasoningDialectSupport.ApplyKnownModelDefaults(
            supportedButCannotDisable, "https://api.openai.com/v1");

        Assert.True(supportedButCannotDisable.ReasoningSupport);
        Assert.False(supportedButCannotDisable.ReasoningCanDisable);
        Assert.Equal(
            new[] { "low", "medium", "high", "xhigh", "max" },
            supportedButCannotDisable.ReasoningLevels);
        Assert.Equal("openaiReasoningEffort", supportedButCannotDisable.ReasoningDialect);
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
