using EraTrans.Core;
using Xunit;

namespace EraTrans.Tests;

public sealed class AiTranslationServiceTests
{
    [Fact]
    public async Task TranslateWindow_PersistsRequestAndRawResponse_AndRedactsApiKey()
    {
        string root = Path.Combine(Path.GetTempPath(), "EraTransAiLogTests", Guid.NewGuid().ToString("N"));
        var store = new AiTranslationLogStore(Path.Combine(root, "logs"));
        var fake = new FakeChatClient("""{"1":"测试译文"} echoed test-key""");
        var service = new AiTranslationService(fake, store);

        WindowTranslationOutcome outcome = await service.TranslateWindowAsync(
            CreateProfile(),
            Window((1, "测试 key", "原文中包含 test-key")));

        Assert.Equal("测试译文", Assert.Single(outcome.Translations).Translation);
        Assert.True(outcome.IsComplete);

        AiTranslationLogEntry log = Assert.Single(store.LoadAll());
        Assert.Equal(AiTranslationLogStatus.Succeeded, log.Status);
        Assert.Equal("Translation", log.RequestKind);
        Assert.Equal("Test", log.ProviderName);
        Assert.Equal("test-model", log.Model);
        Assert.Equal("POST", log.RequestMethod);
        Assert.EndsWith("/chat/completions", log.Endpoint);
        Assert.Equal(1, log.RequestedEntryCount);
        Assert.Contains("只返回一个 JSON 对象", log.SystemPrompt);
        Assert.Contains("原文中包含", log.RequestJson);
        Assert.Contains("[已隐藏]", log.RequestJson);
        Assert.Contains("测试译文", log.RawResponse);
        Assert.Contains("[已隐藏]", log.RawResponse);
        Assert.Null(log.ErrorMessage);
        Assert.NotNull(log.DurationMilliseconds);
        Assert.EndsWith(" 秒", log.DurationDescription);

        string savedJson = File.ReadAllText(Directory.GetFiles(store.DirectoryPath, "*.json").Single());
        Assert.DoesNotContain("test-key", savedJson);
    }

    [Fact]
    public async Task TranslateWindow_RedactsTrimmedApiKeyWhenProviderEchoesIt()
    {
        string root = Path.Combine(Path.GetTempPath(), "EraTransAiLogTests", Guid.NewGuid().ToString("N"));
        var store = new AiTranslationLogStore(Path.Combine(root, "logs"));
        var fake = new FakeChatClient("""{"1":"测试译文"} echoed secret-key""");
        var service = new AiTranslationService(fake, store);
        AiProviderProfile profile = CreateProfile();
        profile.ApiKey = "  secret-key  ";

        await service.TranslateWindowAsync(profile, Window((1, "测试 key", "原文")));

        AiTranslationLogEntry log = Assert.Single(store.LoadAll());
        Assert.Contains("[已隐藏]", log.RawResponse);
        Assert.DoesNotContain("secret-key", log.RawResponse);

        string savedJson = File.ReadAllText(Directory.GetFiles(store.DirectoryPath, "*.json").Single());
        Assert.DoesNotContain("secret-key", savedJson);
    }

    [Fact]
    public async Task TranslateWindow_PersistsRawReplyWhenResponseParsingFails()
    {
        string root = Path.Combine(Path.GetTempPath(), "EraTransAiLogTests", Guid.NewGuid().ToString("N"));
        var store = new AiTranslationLogStore(Path.Combine(root, "logs"));
        var service = new AiTranslationService(new FakeChatClient("not a JSON response"), store);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.TranslateWindowAsync(CreateProfile(), Window((1, "文本1", "原文"))));

        AiTranslationLogEntry log = Assert.Single(store.LoadAll());
        Assert.Equal(AiTranslationLogStatus.Failed, log.Status);
        Assert.Equal("not a JSON response", log.RawResponse);
        Assert.Contains("JSON", log.ErrorMessage);
    }

    [Fact]
    public async Task TranslateWindow_PersistsProviderErrors()
    {
        string root = Path.Combine(Path.GetTempPath(), "EraTransAiLogTests", Guid.NewGuid().ToString("N"));
        var store = new AiTranslationLogStore(Path.Combine(root, "logs"));
        var service = new AiTranslationService(new FailingChatClient(), store);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.TranslateWindowAsync(CreateProfile(), Window((1, "文本1", "原文"))));

        AiTranslationLogEntry log = Assert.Single(store.LoadAll());
        Assert.Equal(AiTranslationLogStatus.Failed, log.Status);
        Assert.Equal(typeof(InvalidOperationException).FullName, log.ErrorType);
        Assert.Contains("模拟服务商错误", log.ErrorMessage);
        Assert.Null(log.RawResponse);
        Assert.NotNull(log.CompletedAtUtc);
    }

    [Fact]
    public async Task TranslateWindow_RejectsVariableEntriesBeforeCallingProvider()
    {
        var fake = new FakeChatClient("""{"1":"译文"}""");
        var service = new AiTranslationService(fake);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.TranslateWindowAsync(
                CreateProfile(),
                Window((1, "变量CSV\\Abl00001", "BASE"))));

        Assert.False(fake.WasCalled);
    }

    [Fact]
    public void NormalizeBaseUrl_AddsHttpsAndRemovesChatCompletionSuffix()
    {
        Assert.Equal(
            "https://example.com/v1",
            AiProviderProfileValidator.NormalizeBaseUrl(" example.com/v1/chat/completions/ "));
    }

    [Theory]
    [InlineData("ftp://example.com/v1")]
    [InlineData("https://user:pass@example.com/v1")]
    [InlineData("https://example.com/v1?key=value")]
    public void NormalizeBaseUrl_RejectsUnsafeOrUnsupportedUris(string value)
    {
        Assert.Throws<InvalidOperationException>(() =>
            AiProviderProfileValidator.NormalizeBaseUrl(value));
    }

    [Fact]
    public void DictionaryEntry_AiTranslationChangesOnlyAnEmptyPendingTranslation()
    {
        string root = Path.Combine(Path.GetTempPath(), "EraTransAiTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "items.json");
        File.WriteAllText(path,
            """
            [
              {
                "key": "文本ERB\\OPENING00001",
                "original": "原始文本",
                "translation": "",
                "stage": 0,
                "context": "上下文",
                "unknown": { "preserve": true }
              },
              {
                "key": "文本ERB\\OPENING00002",
                "original": "已有译文",
                "translation": "手工译文",
                "stage": 0
              }
            ]
            """);

        DictionaryEditorDocument document = new DictionaryEditorService().Load(path);
        DictionaryEditorEntry target = document.Entries[0];

        Assert.True(target.TryApplyAiTranslation(target.Key, target.Original, "AI 译文"));
        Assert.False(document.Entries[1].TryApplyAiTranslation(
            document.Entries[1].Key, document.Entries[1].Original, "覆盖译文"));
        Assert.Equal(1, target.Stage);

        document.Save();
        string saved = File.ReadAllText(path);
        Assert.Contains("AI 译文", saved);
        Assert.Contains("手工译文", saved);
        Assert.Contains("preserve", saved);
    }

    private static TranslationWindow Window(params (int Id, string Key, string Text)[] lines) =>
        new("@TEST", 1, lines
            .Select(line => new WindowLine(
                line.Id, line.Key, line.Text, "@TEST",
                Translate: true, Reference: false, Join: 0, InlineCondition: false, AltGroup: null))
            .ToArray());

    private static AiProviderProfile CreateProfile() => new()
    {
        Name = "Test",
        BaseUrl = "https://example.com/v1",
        Model = "test-model",
        ApiKey = "test-key"
    };

    private sealed class FailingChatClient : IAiChatCompletionClient
    {
        public Task<string> CompleteAsync(
            AiProviderProfile profile,
            string systemPrompt,
            string userPrompt,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("模拟服务商错误");
    }

    private sealed class FakeChatClient(string response) : IAiChatCompletionClient
    {
        public bool WasCalled { get; private set; }

        public Task<string> CompleteAsync(
            AiProviderProfile profile,
            string systemPrompt,
            string userPrompt,
            CancellationToken cancellationToken)
        {
            WasCalled = true;
            return Task.FromResult(response);
        }
    }
}
