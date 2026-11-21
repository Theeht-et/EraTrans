using System.Text.Json;
using EraTrans.Core;
using Xunit;

namespace EraTrans.Tests;

public sealed class AiReasoningTests
{
    private const string OpenAiPresetOn = """{"reasoning_effort":"high"}""";
    private const string OpenAiPresetOff = """{"reasoning_effort":"none"}""";
    private const string ThinkingOn = """{"thinking":{"type":"enabled"}}""";
    private const string ThinkingOff = """{"thinking":{"type":"disabled"}}""";
    private const string EnableThinkingOn = """{"enable_thinking":true}""";
    private const string EnableThinkingOff = """{"enable_thinking":false}""";
    private const string OpenRouterOn = """{"reasoning":{"enabled":true}}""";
    private const string OpenRouterOff = """{"reasoning":{"enabled":false}}""";

    [Fact]
    public void Auto_NeverWritesReasoningFields()
    {
        foreach (AiReasoningOptions? options in new AiReasoningOptions?[]
                 {
                     null,
                     AiReasoningOptions.Auto,
                     new(AiReasoningMode.Auto, """{"thinking":{"type":"enabled"}}""", """{"thinking":{"type":"disabled"}}""")
                 })
        {
            JsonElement body = ParseBody("https://api.deepseek.com", options);
            AssertNoReasoningFields(body);
            Assert.Equal("test-model", body.GetProperty("model").GetString());
        }
    }

    [Theory]
    [InlineData("https://api.openai.com/v1", OpenAiPresetOn)]
    [InlineData("https://api.deepseek.com", ThinkingOn)]
    [InlineData("https://dashscope.aliyuncs.com/compatible-mode/v1", EnableThinkingOn)]
    [InlineData("https://open.bigmodel.cn/api/paas/v4", ThinkingOn)]
    [InlineData("https://api.moonshot.cn/v1", ThinkingOn)]
    [InlineData("https://ark.cn-beijing.volces.com/api/v3", ThinkingOn)]
    [InlineData("https://api.siliconflow.cn/v1", EnableThinkingOn)]
    [InlineData("https://openrouter.ai/api/v1", OpenRouterOn)]
    [InlineData("https://api.x.ai/v1", OpenAiPresetOn)]
    [InlineData("https://my-proxy.example.com/v1", OpenAiPresetOn)]
    public void On_WritesPresetShape(string baseUrl, string expected)
    {
        JsonElement body = ParseBody(baseUrl, new AiReasoningOptions(AiReasoningMode.On));
        AssertHas(body, expected);
    }

    [Theory]
    [InlineData("https://api.openai.com/v1", OpenAiPresetOff)]
    [InlineData("https://api.deepseek.com", ThinkingOff)]
    [InlineData("https://dashscope.aliyuncs.com/compatible-mode/v1", EnableThinkingOff)]
    [InlineData("https://open.bigmodel.cn/api/paas/v4", ThinkingOff)]
    [InlineData("https://api.moonshot.cn/v1", ThinkingOff)]
    [InlineData("https://ark.cn-beijing.volces.com/api/v3", ThinkingOff)]
    [InlineData("https://api.siliconflow.cn/v1", EnableThinkingOff)]
    [InlineData("https://openrouter.ai/api/v1", OpenRouterOff)]
    [InlineData("https://api.x.ai/v1", OpenAiPresetOff)]
    [InlineData("https://my-proxy.example.com/v1", OpenAiPresetOff)]
    public void Off_WritesPresetShape(string baseUrl, string expected)
    {
        JsonElement body = ParseBody(baseUrl, new AiReasoningOptions(AiReasoningMode.Off));
        AssertHas(body, expected);
    }

    [Fact]
    public void CustomJson_ReplacesPresetForThatSideOnly()
    {
        JsonElement onBody = ParseBody(
            "https://my-proxy.example.com/v1",
            new AiReasoningOptions(AiReasoningMode.On, """{"thinking":{"type":"enabled"}}"""));
        AssertHas(onBody, ThinkingOn);
        Assert.False(onBody.TryGetProperty("reasoning_effort", out _));

        JsonElement offBody = ParseBody(
            "https://my-proxy.example.com/v1",
            new AiReasoningOptions(AiReasoningMode.Off, null, """{"enable_thinking":false}"""));
        AssertHas(offBody, EnableThinkingOff);
        Assert.False(offBody.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public void CustomJson_AppliesToBuiltInProvidersToo()
    {
        JsonElement body = ParseBody(
            "https://api.deepseek.com",
            new AiReasoningOptions(
                AiReasoningMode.On,
                """{"thinking":{"type":"enabled"},"thinking_budget":4096}"""));

        AssertHas(body, """{"thinking":{"type":"enabled"},"thinking_budget":4096}""");
    }

    [Fact]
    public void InvalidCustomJson_FallsBackToPreset()
    {
        JsonElement body = ParseBody(
            "https://api.openai.com/v1",
            new AiReasoningOptions(AiReasoningMode.On, "{\"reasoning_effort\":"));

        AssertHas(body, OpenAiPresetOn);
    }

    [Theory]
    [InlineData("{\"model\":\"other\"}")]
    [InlineData("{\"messages\":[]}")]
    [InlineData("{\"stream\":true}")]
    [InlineData("[1,2,3]")]
    [InlineData("\"text\"")]
    [InlineData("{\"reasoning_effort\":")]
    public void TryParseShape_RejectsUnusableFragments(string json)
    {
        Assert.False(AiReasoning.TryParseShape(json, out _, out string? error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{}")]
    public void TryParseShape_AcceptsEmptyAndEmptyObject(string? json)
    {
        Assert.True(AiReasoning.TryParseShape(json, out _, out string? error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData("https://api.openai.com/v1", "reasoning_effort")]
    [InlineData("https://openrouter.ai/api/v1", "reasoning.enabled")]
    [InlineData("https://dashscope.aliyuncs.com/compatible-mode/v1", "enable_thinking")]
    [InlineData("https://api.siliconflow.cn/v1", "enable_thinking")]
    [InlineData("https://open.bigmodel.cn/api/paas/v4", "thinking.type")]
    [InlineData("https://api.deepseek.com", "thinking.type")]
    [InlineData("https://custom.example.com/v1", "reasoning_effort")]
    public void PresetLabel_DescribesActiveField(string baseUrl, string expected)
    {
        Assert.Equal(expected, AiReasoning.PresetLabel(baseUrl));
    }

    [Fact]
    public void ReasoningStore_RoundTripsPerProviderChoices()
    {
        string directory = Path.Combine(Path.GetTempPath(), "eratrans-reasoning-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "ai-reasoning.json");
        try
        {
            var store = new AiReasoningSettingsStore(path);
            store.Save(new Dictionary<string, AiReasoningOptions>(StringComparer.Ordinal)
            {
                ["a"] = new(AiReasoningMode.On),
                ["b"] = new(AiReasoningMode.Off, """{"enable_thinking":true}""", """{"enable_thinking":false}""")
            });

            Dictionary<string, AiReasoningOptions> loaded = new AiReasoningSettingsStore(path).Load();
            Assert.Equal(AiReasoningMode.On, loaded["a"].Mode);
            Assert.Null(loaded["a"].OnJson);
            Assert.Equal(AiReasoningMode.Off, loaded["b"].Mode);
            Assert.Equal("""{"enable_thinking":true}""", loaded["b"].OnJson);
            Assert.Equal(2, loaded.Count);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ReasoningStore_MissingFileLoadsEmptyAndBrokenFileDoesNotThrow()
    {
        string directory = Path.Combine(Path.GetTempPath(), "eratrans-reasoning-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "ai-reasoning.json");
        try
        {
            Assert.Empty(new AiReasoningSettingsStore(path).Load());

            Directory.CreateDirectory(directory);
            File.WriteAllText(path, "{ not json");
            Assert.Empty(new AiReasoningSettingsStore(path).Load());
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static JsonElement ParseBody(string baseUrl, AiReasoningOptions? options)
    {
        var profile = new AiProviderProfile
        {
            Name = "test",
            BaseUrl = baseUrl,
            Model = "test-model",
            ApiKey = "secret"
        };

        AiProviderHttpRequestDetails details =
            AiProviderHttpRequestFactory.Create(profile, "system", "user", options);
        using JsonDocument document = JsonDocument.Parse(details.JsonBody);
        return document.RootElement.Clone();
    }

    private static void AssertHas(JsonElement body, string expectedJson)
    {
        using JsonDocument expected = JsonDocument.Parse(expectedJson);
        foreach (JsonProperty property in expected.RootElement.EnumerateObject())
        {
            Assert.True(
                body.TryGetProperty(property.Name, out JsonElement actual),
                $"请求体缺少字段 {property.Name}：{body}");
            Assert.True(
                JsonElement.DeepEquals(property.Value, actual),
                $"字段 {property.Name} 与预期不符：{actual}");
        }
    }

    private static void AssertNoReasoningFields(JsonElement body)
    {
        foreach (string name in new[]
                 {
                     "reasoning_effort", "reasoning", "thinking", "thinking_budget",
                     "thinking_mode", "enable_thinking", "chat_template_kwargs"
                 })
        {
            Assert.False(
                body.TryGetProperty(name, out _),
                $"自动模式不应写入 {name}：{body}");
        }
    }
}
