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

        Assert.Equal("dashScopeGlmEffort", model.ReasoningDialect);
        Assert.False(model.ReasoningCanDisable);
        Assert.Equal(new[] { "low", "high", "max" }, model.ReasoningLevels);

        using JsonDocument effortBody = CreateBody(
            endpoint, model, new AiModelReasoningSettings { Enabled = true, Effort = "max" });
        Assert.Equal("max", effortBody.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.False(effortBody.RootElement.TryGetProperty("thinking", out _));
        Assert.False(effortBody.RootElement.TryGetProperty("enable_thinking", out _));

        using JsonDocument offBody = CreateBody(
            endpoint, model, new AiModelReasoningSettings { Disabled = true });
        Assert.False(offBody.RootElement.TryGetProperty("enable_thinking", out _));
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
    public void Grok47_ExposesVerifiedEffortLevelsAndNeverOffersOff()
    {
        AiProviderModel model = new() { Id = "grok-4.7" };
        const string endpoint = "https://api.x.ai/v1";
        AiReasoningDialectSupport.ApplyKnownModelDefaults(model, endpoint);

        Assert.True(model.ReasoningSupport);
        Assert.False(model.ReasoningCanDisable);
        Assert.Equal("openaiReasoningEffort", model.ReasoningDialect);
        Assert.Equal(new[] { "low", "medium", "high", "xhigh" }, model.ReasoningLevels);

        using JsonDocument body = CreateBody(
            endpoint, model, new AiModelReasoningSettings { Enabled = true, Effort = "xhigh" });
        Assert.Equal("xhigh", body.RootElement.GetProperty("reasoning_effort").GetString());

        using JsonDocument offBody = CreateBody(
            endpoint, model, new AiModelReasoningSettings { Disabled = true });
        Assert.False(offBody.RootElement.TryGetProperty("reasoning_effort", out _));
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
        Assert.False(offBody.RootElement.GetProperty("enable_thinking").GetBoolean());
        Assert.False(offBody.RootElement.TryGetProperty("reasoning_effort", out _));
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
        Assert.Equal("none", offBody.RootElement.GetProperty("reasoning_effort").GetString());
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
        Assert.Equal(new[] { "minimal", "low", "medium", "high", "xhigh", "max" }, glm52.ReasoningLevels);

        AiProviderModel unknown = new() { Id = "custom-reasoning-model", ReasoningSupport = true };
        AiReasoningDialectSupport.ApplyKnownModelDefaults(unknown, endpoint);
        Assert.Null(unknown.ReasoningDialect);
        Assert.False(AiReasoningDialectSupport.IsCompatible(endpoint, unknown));

        AiProviderModel unrelatedVendor = new() { Id = "deepseek-v4-pro", ReasoningSupport = true };
        AiReasoningDialectSupport.ApplyKnownModelDefaults(unrelatedVendor, endpoint);
        Assert.Null(unrelatedVendor.ReasoningDialect);
        Assert.False(AiReasoningDialectSupport.IsCompatible(endpoint, unrelatedVendor));
    }

    [Fact]
    public void DashScopeGlm53_UsesThinkingTypeAndDoesNotAllowDisable()
    {
        AiProviderModel model = new() { Id = "glm-5.3" };
        const string endpoint = "https://dashscope.aliyuncs.com/compatible-mode/v1";
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
    public void DashScopeGlm52_UsesReasoningEffortAndEnableThinkingOff()
    {
        AiProviderModel model = new() { Id = "glm-5.2" };
        const string endpoint = "https://dashscope.aliyuncs.com/compatible-mode/v1";
        AiReasoningDialectSupport.ApplyKnownModelDefaults(model, endpoint);

        Assert.True(model.ReasoningCanDisable);
        Assert.Equal(new[] { "minimal", "low", "medium", "high", "xhigh", "max" }, model.ReasoningLevels);
        using JsonDocument effortBody = CreateBody(
            endpoint, model, new AiModelReasoningSettings { Enabled = true, Effort = "xhigh" });
        Assert.Equal("xhigh", effortBody.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.False(effortBody.RootElement.TryGetProperty("thinking", out _));

        using JsonDocument offBody = CreateBody(
            endpoint, model, new AiModelReasoningSettings { Disabled = true });
        Assert.False(offBody.RootElement.GetProperty("enable_thinking").GetBoolean());
        Assert.False(offBody.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public void ZhipuGlm53_UsesSupportedThinkingTypeControlOnAliyunModelStudio()
    {
        AiProviderModel model = new() { Id = "ZHIPU/GLM-5.3" };
        const string endpoint = "https://llm-workspace.cn-beijing.maas.aliyuncs.com/compatible-mode/v1";
        AiReasoningDialectSupport.ApplyKnownModelDefaults(model, endpoint);

        Assert.Equal("thinkingType", model.ReasoningDialect);
        Assert.False(model.ReasoningCanDisable);
        Assert.Equal(new[] { "low", "high", "max" }, model.ReasoningLevels);

        using JsonDocument body = CreateBody(
            endpoint, model, new AiModelReasoningSettings { Enabled = true, Effort = "high" });
        Assert.Equal("enabled", body.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("high", body.RootElement.GetProperty("reasoning_effort").GetString());
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
        using JsonDocument effortBody = CreateBody(
            endpoint, model, new AiModelReasoningSettings { Enabled = true, Effort = "high" });
        Assert.Equal("enabled", effortBody.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("high", effortBody.RootElement.GetProperty("reasoning_effort").GetString());

        using JsonDocument offBody = CreateBody(
            endpoint, model, new AiModelReasoningSettings { Disabled = true });
        Assert.Equal("disabled", offBody.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("minimal", offBody.RootElement.GetProperty("reasoning_effort").GetString());
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
