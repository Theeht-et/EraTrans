using System.Text.Json;
using EraTrans.Core;
using Xunit;

namespace EraTrans.Tests;

public sealed class AiReasoningTests
{
    private const string OpenAiOn = """{"reasoning_effort":"high"}""";
    private const string OpenAiOff = """{"reasoning_effort":"none"}""";
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
            foreach (string name in new[]
                     {
                         "reasoning_effort", "reasoning", "thinking", "thinking_budget",
                         "thinking_mode", "enable_thinking", "chat_template_kwargs"
                     })
            {
                Assert.False(body.TryGetProperty(name, out _), $"自动模式不应写入 {name}：{body}");
            }
        }
    }

    // One case per distinct payload shape; providers that share a shape are
    // covered by the same assertion instead of repeating it.
    [Theory]
    [InlineData("https://api.openai.com/v1", OpenAiOn)]
    [InlineData("https://openrouter.ai/api/v1", OpenRouterOn)]
    [InlineData("https://api.deepseek.com", ThinkingOn)]
    [InlineData("https://dashscope.aliyuncs.com/compatible-mode/v1", EnableThinkingOn)]
    [InlineData("https://my-proxy.example.com/v1", OpenAiOn)]
    public void On_WritesThePresetShapeOfTheEndpoint(string baseUrl, string expected)
    {
        AssertHas(ParseBody(baseUrl, new AiReasoningOptions(AiReasoningMode.On)), expected);
    }

    [Theory]
    [InlineData("https://api.openai.com/v1", OpenAiOff)]
    [InlineData("https://openrouter.ai/api/v1", OpenRouterOff)]
    [InlineData("https://api.deepseek.com", ThinkingOff)]
    [InlineData("https://dashscope.aliyuncs.com/compatible-mode/v1", EnableThinkingOff)]
    [InlineData("https://my-proxy.example.com/v1", OpenAiOff)]
    public void Off_WritesThePresetShapeOfTheEndpoint(string baseUrl, string expected)
    {
        AssertHas(ParseBody(baseUrl, new AiReasoningOptions(AiReasoningMode.Off)), expected);
    }

    [Theory]
    [InlineData("https://my-proxy.example.com/v1", ThinkingOn)]
    [InlineData("https://api.deepseek.com", """{"thinking":{"type":"enabled"},"thinking_budget":4096}""")]
    public void CustomJson_ReplacesThePresetInsteadOfMergingIntoIt(string baseUrl, string custom)
    {
        JsonElement body = ParseBody(baseUrl, new AiReasoningOptions(AiReasoningMode.On, custom));
        AssertHas(body, custom);
        Assert.False(body.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public void InvalidCustomJson_FallsBackToThePreset()
    {
        JsonElement body = ParseBody(
            "https://api.openai.com/v1",
            new AiReasoningOptions(AiReasoningMode.On, "{\"reasoning_effort\":"));

        AssertHas(body, OpenAiOn);
    }

    [Theory]
    [InlineData("{\"model\":\"other\"}")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"reasoning_effort\":")]
    public void TryParseShape_RejectsUnusableFragments(string json)
    {
        Assert.False(AiReasoning.TryParseShape(json, out _, out string? error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    public void TryParseShape_AcceptsEmptyInput(string? json)
    {
        Assert.True(AiReasoning.TryParseShape(json, out _, out string? error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData("https://api.openai.com/v1", "reasoning_effort")]
    [InlineData("https://openrouter.ai/api/v1", "reasoning.enabled")]
    [InlineData("https://dashscope.aliyuncs.com/compatible-mode/v1", "enable_thinking")]
    public void PresetLabel_DescribesTheActiveField(string baseUrl, string expected)
    {
        Assert.Equal(expected, AiReasoning.PresetLabel(baseUrl));
    }

    [Fact]
    public void ReasoningStore_RoundTripsAndSurvivesUnreadableFiles()
    {
        string directory = Path.Combine(Path.GetTempPath(), "eratrans-reasoning-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "ai-reasoning.json");
        try
        {
            Assert.Empty(new AiReasoningSettingsStore(path).Load());

            new AiReasoningSettingsStore(path).Save(
                new Dictionary<string, AiReasoningOptions>(StringComparer.Ordinal)
                {
                    ["a"] = new(AiReasoningMode.On),
                    ["b"] = new(AiReasoningMode.Off, """{"enable_thinking":true}""", """{"enable_thinking":false}""")
                });

            Dictionary<string, AiReasoningOptions> loaded = new AiReasoningSettingsStore(path).Load();
            Assert.Equal(AiReasoningMode.On, loaded["a"].Mode);
            Assert.Null(loaded["a"].OnJson);
            Assert.Equal("""{"enable_thinking":true}""", loaded["b"].OnJson);
            Assert.Equal(2, loaded.Count);

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
}
