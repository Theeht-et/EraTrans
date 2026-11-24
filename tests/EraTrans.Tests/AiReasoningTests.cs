using System.Net;
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

public sealed class AiReasoningFallbackTests
{
    [Fact]
    public async Task RejectedReasoning_RetriesOnceWithoutItAndSucceeds()
    {
        var client = new RecordingClient(RejectMode.OnlyWithReasoning);
        var service = new AiTranslationService(client);

        WindowTranslationOutcome outcome = await service.TranslateWindowAsync(
            CreateProfile(), Window(), new AiReasoningOptions(AiReasoningMode.On));

        Assert.Equal("译文", Assert.Single(outcome.Translations).Translation);
        Assert.Equal(2, client.Attempts.Count);
        Assert.Equal(AiReasoningMode.On, client.Attempts[0]!.Mode);
        Assert.Equal(AiReasoningMode.Auto, client.Attempts[1]!.Mode);
    }

    [Fact]
    public async Task ClientErrorWithoutReasoning_IsNotRetried()
    {
        // The provider fails for an unrelated reason: with reasoning inactive the
        // service must surface the error instead of silently sending twice.
        var client = new RecordingClient(RejectMode.Always);
        var service = new AiTranslationService(client);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            service.TranslateWindowAsync(CreateProfile(), Window(), AiReasoningOptions.Auto));

        Assert.Single(client.Attempts);
    }

    [Fact]
    public async Task SuccessfulReasoningRequest_IsSentOnce()
    {
        var client = new RecordingClient(RejectMode.Never);
        var service = new AiTranslationService(client);

        await service.TranslateWindowAsync(
            CreateProfile(), Window(), new AiReasoningOptions(AiReasoningMode.Off));

        Assert.Single(client.Attempts);
        Assert.Equal(AiReasoningMode.Off, client.Attempts[0]!.Mode);
    }

    [Fact]
    public void IsRejection_OnlyTreatsBadRequestAndUnprocessableAsReasoningFailures()
    {
        Assert.True(AiReasoningFallback.IsRejection(
            new HttpRequestException("bad", null, HttpStatusCode.BadRequest),
            new AiReasoningOptions(AiReasoningMode.On)));
        Assert.True(AiReasoningFallback.IsRejection(
            new HttpRequestException("bad", null, HttpStatusCode.UnprocessableEntity),
            new AiReasoningOptions(AiReasoningMode.Off)));
        Assert.False(AiReasoningFallback.IsRejection(
            new HttpRequestException("bad", null, HttpStatusCode.Unauthorized),
            new AiReasoningOptions(AiReasoningMode.On)));
        Assert.False(AiReasoningFallback.IsRejection(
            new HttpRequestException("bad", null, HttpStatusCode.BadRequest),
            AiReasoningOptions.Auto));
        Assert.False(AiReasoningFallback.IsRejection(
            new InvalidOperationException("bad"),
            new AiReasoningOptions(AiReasoningMode.On)));
    }

    private static TranslationWindow Window() =>
        new("@A", 1,
        [
            new WindowLine(1, "文本1", "原文", "@A",
                Translate: true, Reference: false, Join: 0, InlineCondition: false, AltGroup: null)
        ]);

    private static AiProviderProfile CreateProfile() => new()
    {
        Name = "测试服务",
        BaseUrl = "https://api.deepseek.com",
        Model = "deepseek-flash",
        ApiKey = "secret"
    };

    private enum RejectMode
    {
        Never,
        OnlyWithReasoning,
        Always
    }

    private sealed class RecordingClient(RejectMode mode) : IAiChatCompletionClient
    {
        public List<AiReasoningOptions?> Attempts { get; } = [];

        public Task<string> CompleteAsync(
            AiProviderProfile profile,
            string systemPrompt,
            string userPrompt,
            CancellationToken cancellationToken) =>
            CompleteAsync(profile, systemPrompt, userPrompt, null, cancellationToken);

        public Task<string> CompleteAsync(
            AiProviderProfile profile,
            string systemPrompt,
            string userPrompt,
            AiReasoningOptions? reasoning,
            CancellationToken cancellationToken = default)
        {
            Attempts.Add(reasoning);

            bool reject = mode switch
            {
                RejectMode.Always => true,
                RejectMode.OnlyWithReasoning => AiReasoning.IsActive(reasoning),
                _ => false
            };

            if (reject)
            {
                throw new HttpRequestException(
                    "服务商返回 HTTP 400。", null, HttpStatusCode.BadRequest);
            }

            return Task.FromResult("""{"1":"译文"}""");
        }
    }
}
