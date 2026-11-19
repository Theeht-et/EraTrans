using EraTrans.Core;
using System.Runtime.Versioning;
using Xunit;

namespace EraTrans.Tests;

public sealed class AiTranslationServiceTests
{
    [Fact]
    public async Task TranslateAsync_MapsResultsById_AndIncludesDictionaryContext()
    {
        var fake = new FakeChatClient(
            """
            [
              { "id": 8, "translation": "第二条译文" },
              { "id": 3, "translation": "第一条译文" }
            ]
            """);
        var service = new AiTranslationService(fake);
        var requests = new[]
        {
            new AiTranslationRequest(3, "文本ERB\\OPENING00001", "First line", "Opening scene"),
            new AiTranslationRequest(8, "文本ERB\\OPENING00002", "Second line", "Opening scene")
        };

        IReadOnlyList<AiTranslationResult> results =
            await service.TranslateAsync(CreateProfile(), requests);

        Assert.Collection(
            results,
            first =>
            {
                Assert.Equal(3, first.Id);
                Assert.Equal("第一条译文", first.Translation);
            },
            second =>
            {
                Assert.Equal(8, second.Id);
                Assert.Equal("第二条译文", second.Translation);
            });

        Assert.Contains("Opening scene", fake.LastUserPrompt);
        Assert.Contains("文本ERB", fake.LastUserPrompt);
        Assert.Contains("First line", fake.LastUserPrompt);
        Assert.Contains("JSON 数组", fake.LastSystemPrompt);
    }

    [Fact]
    public async Task TranslateAsync_RejectsVariableEntriesBeforeCallingProvider()
    {
        var fake = new FakeChatClient("[]");
        var service = new AiTranslationService(fake);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.TranslateAsync(
                CreateProfile(),
                [new AiTranslationRequest(1, "变量CSV\\Abl00001", "BASE", "")]));

        Assert.False(fake.WasCalled);
    }

    [Theory]
    [InlineData("""[{ "id": 1, "translation": "译文" }]""")]
    [InlineData("""[{ "id": 1, "translation": "译文" }, { "id": 1, "translation": "重复" }]""")]
    [InlineData("""[{ "id": 99, "translation": "未知" }]""")]
    [InlineData("""[{ "id": 1, "translation": "" }]""")]
    public void ParseResponse_RejectsIncompleteDuplicateUnknownOrEmptyResults(string response)
    {
        var requests = new[]
        {
            new AiTranslationRequest(1, "文本1", "原文1", ""),
            new AiTranslationRequest(2, "文本2", "原文2", "")
        };

        Assert.Throws<InvalidDataException>(() =>
            AiTranslationService.ParseResponse(response, requests));
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
              },
              {
                "key": "文本ERB\\OPENING00003",
                "original": "已忽略",
                "translation": "",
                "stage": -1
              },
              {
                "key": "变量CSV\\Abl00001",
                "original": "BASE",
                "translation": "",
                "stage": 0
              }
            ]
            """);

        DictionaryEditorDocument document = new DictionaryEditorService().Load(path);
        DictionaryEditorEntry target = document.Entries[0];

        Assert.True(target.TryApplyAiTranslation(target.Key, target.Original, "AI 译文"));
        Assert.False(document.Entries[1].TryApplyAiTranslation(
            document.Entries[1].Key, document.Entries[1].Original, "覆盖译文"));
        Assert.False(document.Entries[2].TryApplyAiTranslation(
            document.Entries[2].Key, document.Entries[2].Original, "忽略项译文"));
        Assert.False(document.Entries[3].TryApplyAiTranslation(
            document.Entries[3].Key, document.Entries[3].Original, "变量译文"));
        Assert.False(target.TryApplyAiTranslation("其他 key", target.Original, "再次修改"));

        Assert.Equal("原始文本", target.Original);
        Assert.Equal("文本ERB\\OPENING00001", target.Key);
        Assert.Equal(0, target.Stage);
        document.Save();

        string saved = File.ReadAllText(path);
        Assert.Contains("AI 译文", saved);
        Assert.Contains("手工译文", saved);
        Assert.Contains("preserve", saved);
        Assert.Contains("上下文", saved);
    }

    private static AiProviderProfile CreateProfile() => new()
    {
        Name = "Test",
        BaseUrl = "https://example.com/v1",
        Model = "test-model",
        ApiKey = "test-key"
    };

    private sealed class FakeChatClient(string response) : IAiChatCompletionClient
    {
        public bool WasCalled { get; private set; }
        public string LastSystemPrompt { get; private set; } = "";
        public string LastUserPrompt { get; private set; } = "";

        public Task<string> CompleteAsync(
            AiProviderProfile profile,
            string systemPrompt,
            string userPrompt,
            CancellationToken cancellationToken)
        {
            WasCalled = true;
            LastSystemPrompt = systemPrompt;
            LastUserPrompt = userPrompt;
            return Task.FromResult(response);
        }
    }
}

public sealed class AiProviderSettingsStoreTests
{
    [Fact]
    [SupportedOSPlatform("windows")]
    public void SaveAndLoad_EncryptsApiKeyWithCurrentUserProtection()
    {
        string root = Path.Combine(Path.GetTempPath(), "EraTransProviderTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "providers.json");
        var store = new AiProviderSettingsStore(path);
        var settings = new AiProviderSettings
        {
            ActiveProviderId = "provider-1",
            Providers =
            [
                new AiProviderProfile
                {
                    Id = "provider-1",
                    Name = "Private provider",
                    BaseUrl = "https://example.com/v1",
                    Model = "model-a",
                    ApiKey = "very-secret-api-key"
                }
            ]
        };

        store.Save(settings);

        string raw = File.ReadAllText(path);
        Assert.DoesNotContain("very-secret-api-key", raw);
        Assert.Contains("ProtectedApiKey", raw);

        AiProviderSettings loaded = store.Load();
        Assert.Equal("provider-1", loaded.ActiveProviderId);
        Assert.Equal("very-secret-api-key", Assert.Single(loaded.Providers).ApiKey);
    }
}