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
                            Created = 1720000000,
                            Family = "gpt-4o",
                            Status = "active",
                            SupportsTools = true,
                            SupportsReasoning = true,
                            SupportsStructuredOutput = true,
                            SupportsAttachments = true,
                            SupportsTemperature = true,
                            OpenWeights = true,
                            Knowledge = "2024-10",
                            ReleaseDate = "2024-07-18",
                            LastUpdated = "2025-01-01",
                            InputCostPerMillion = 2.5,
                            OutputCostPerMillion = 10.0,
                            ReasoningCostPerMillion = 12.0,
                            CacheReadCostPerMillion = 0.25,
                            CacheWriteCostPerMillion = 3.0,
                            InputAudioCostPerMillion = 40.0,
                            OutputAudioCostPerMillion = 80.0,
                            InputModalities = ["text", "image"],
                            OutputModalities = ["text"],
                            ContextLimit = 128000,
                            InputLimit = 120000,
                            OutputLimit = 16000
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
                Assert.Equal("gpt-4o", model.Family);
                Assert.Equal("active", model.Status);
                Assert.True(model.SupportsTools);
                Assert.True(model.SupportsReasoning);
                Assert.True(model.SupportsStructuredOutput);
                Assert.True(model.SupportsAttachments);
                Assert.True(model.SupportsTemperature);
                Assert.True(model.OpenWeights);
                Assert.Equal("2024-10", model.Knowledge);
                Assert.Equal("2024-07-18", model.ReleaseDate);
                Assert.Equal("2025-01-01", model.LastUpdated);
                Assert.Equal(2.5, model.InputCostPerMillion);
                Assert.Equal(10.0, model.OutputCostPerMillion);
                Assert.Equal(12.0, model.ReasoningCostPerMillion);
                Assert.Equal(0.25, model.CacheReadCostPerMillion);
                Assert.Equal(3.0, model.CacheWriteCostPerMillion);
                Assert.Equal(40.0, model.InputAudioCostPerMillion);
                Assert.Equal(80.0, model.OutputAudioCostPerMillion);
                Assert.Equal(new[] { "text", "image" }, model.InputModalities);
                Assert.Equal(new[] { "text" }, model.OutputModalities);
                Assert.Equal(128000, model.ContextLimit);
                Assert.Equal(120000, model.InputLimit);
                Assert.Equal(16000, model.OutputLimit);
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


    [Fact]
    public void Store_MigratesLegacyNativeProtocolNumbersToOpenAiCompatibleAndFlagsEndpoint()
    {
        using var temporary = new TemporaryDirectory();
        string path = Path.Combine(temporary.Path, "Config", "ai-providers.json");
        var store = new AiProviderSettingsStore(path);
        store.Save(new AiProviderSettings
        {
            ActiveProviderId = "legacy-native",
            Providers =
            [
                new AiProviderProfile
                {
                    Id = "legacy-native",
                    Name = "Legacy Gemini",
                    BaseUrl = "https://generativelanguage.googleapis.com/v1beta",
                    Model = "gemini-model",
                    ApiKey = "legacy-secret"
                }
            ]
        });

        JsonObject root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        root["Providers"]![0]!.AsObject()["Protocol"] = 1;
        File.WriteAllText(path, root.ToJsonString());

        AiProviderProfile loaded = Assert.Single(store.Load().Providers);
        Assert.Equal(AiProviderProtocol.OpenAiCompatible, loaded.Protocol);
        Assert.True(loaded.RequiresEndpointReview);
        Assert.Equal("legacy-secret", loaded.ApiKey);

        store.Save(store.Load());
        JsonObject saved = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal(0, (int)saved["Providers"]![0]!["Protocol"]!);
    }

    [Fact]
    public void Store_AllowsSavingMultipleIncompleteProviderProfiles()
    {
        using var temporary = new TemporaryDirectory();
        string path = Path.Combine(temporary.Path, "Config", "ai-providers.json");
        var store = new AiProviderSettingsStore(path);

        store.Save(new AiProviderSettings
        {
            ActiveProviderId = "openai",
            Providers =
            [
                new AiProviderProfile { Id = "openai", Name = "OpenAI", BaseUrl = "https://api.openai.com/v1" },
                new AiProviderProfile { Id = "custom", Name = "Custom", BaseUrl = "" }
            ]
        });

        AiProviderSettings loaded = store.Load();
        Assert.Equal(2, loaded.Providers.Count);
        Assert.All(loaded.Providers, provider => Assert.Empty(provider.ApiKey));
        Assert.Equal("", loaded.Providers.Single(provider => provider.Id == "custom").BaseUrl);
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
