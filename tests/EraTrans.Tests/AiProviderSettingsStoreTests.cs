using EraTrans.Core;
using System.Text.Json.Nodes;
using System.Runtime.Versioning;
using Xunit;

namespace EraTrans.Tests;

[SupportedOSPlatform("windows")]
public sealed class AiProviderSettingsPortablePersistenceTests
{
    [Fact]
    public void Store_SaveAndLoad_RoundTripsProviderSettingsAndEncryptsApiKey()
    {
        using var temporary = new TemporaryDirectory();
        string path = Path.Combine(temporary.Path, "Config", "ai-providers.json");
        var originalSettings = new AiProviderSettings
        {
            ActiveProviderId = "provider-1",
            Providers =
            [
                new AiProviderProfile
                {
                    Id = "provider-1",
                    Name = "Test provider",
                    BaseUrl = "https://api.example.com/v1",
                    Model = "test-model",
                    ApiKey = "secret-test-api-key",
                    AvailableModels =
                    [
                        new AiProviderModel
                        {
                            Id = "gpt-4o-mini",
                            DisplayName = "GPT-4o mini",
                            GroupName = "OpenAI",
                            OwnedBy = "openai",
                            Created = 1720000000
                        },
                        new AiProviderModel
                        {
                            Id = "text-embedding-3-large",
                            DisplayName = "Text Embedding 3 Large",
                            GroupName = "嵌入模型",
                            OwnedBy = "openai",
                            IsEmbedding = true
                        }
                    ]
                }
            ]
        };

        var store = new AiProviderSettingsStore(path);
        store.Save(originalSettings);
        AiProviderSettings loaded = store.Load();

        Assert.Equal("provider-1", loaded.ActiveProviderId);
        AiProviderProfile provider = Assert.Single(loaded.Providers);
        Assert.Equal("Test provider", provider.Name);
        Assert.Equal("https://api.example.com/v1", provider.BaseUrl);
        Assert.Equal("test-model", provider.Model);
        Assert.Equal("secret-test-api-key", provider.ApiKey);
        Assert.Collection(
            provider.AvailableModels,
            model =>
            {
                Assert.Equal("gpt-4o-mini", model.Id);
                Assert.Equal("GPT-4o mini", model.DisplayName);
                Assert.Equal("OpenAI", model.GroupName);
                Assert.Equal("openai", model.OwnedBy);
                Assert.Equal(1720000000, model.Created);
                Assert.True(model.IsChatModel);
            },
            model =>
            {
                Assert.Equal("text-embedding-3-large", model.Id);
                Assert.Equal("嵌入模型", model.GroupName);
                Assert.True(model.IsEmbedding);
                Assert.False(model.IsChatModel);
            });
        Assert.True(File.Exists(path));
        Assert.DoesNotContain("secret-test-api-key", File.ReadAllText(path));
    }


    [Fact]
    public void Store_LoadsExistingSettingsWithoutModelCache()
    {
        using var temporary = new TemporaryDirectory();
        string path = Path.Combine(temporary.Path, "Config", "ai-providers.json");
        var store = new AiProviderSettingsStore(path);
        store.Save(new AiProviderSettings
        {
            ActiveProviderId = "legacy",
            Providers =
            [
                new AiProviderProfile
                {
                    Id = "legacy",
                    Name = "Legacy provider",
                    BaseUrl = "https://api.example.com/v1",
                    Model = "legacy-model",
                    ApiKey = "legacy-secret"
                }
            ]
        });

        JsonObject root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        root["Providers"]![0]!.AsObject().Remove("Models");
        File.WriteAllText(path, root.ToJsonString());

        AiProviderProfile loaded = Assert.Single(store.Load().Providers);
        Assert.Equal("legacy-model", loaded.Model);
        Assert.Empty(loaded.AvailableModels);
        Assert.Equal("legacy-secret", loaded.ApiKey);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "EraTransAiSettingsTests",
            Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
