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
            BuiltInDefaultDisableMigrationApplied = true,
            Providers =
            [
                new AiProviderProfile
                {
                    Id = "provider-1",
                    Name = "Test provider",
                    BaseUrl = "https://api.example.com/v1",
                    Model = "test-model",
                    ApiKey = "secret-test-api-key",
                    ChatCompletionsPath = "/v2/chat/respond",
                    ModelsPath = "/v2/models",
                    AvailableModels =
                    [
                        new AiProviderModel
                        {
                            Id = "gpt-4o-mini",
                            DisplayName = "GPT-4o mini",
                            GroupName = "OpenAI",
                            OwnedBy = "openai",
                            Created = 1720000000,
                            ReasoningLevels = ["low", "medium", "high"],
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
        Assert.True(loaded.BuiltInDefaultDisableMigrationApplied);
        AiProviderProfile provider = Assert.Single(loaded.Providers);
        Assert.Equal("Test provider", provider.Name);
        Assert.Equal("https://api.example.com/v1", provider.BaseUrl);
        Assert.Equal("test-model", provider.Model);
        Assert.Equal("secret-test-api-key", provider.ApiKey);
        Assert.Equal("/v2/chat/respond", provider.ChatCompletionsPath);
        Assert.Equal("/v2/models", provider.ModelsPath);
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
                Assert.Equal(new[] { "low", "medium", "high" }, model.ReasoningLevels);
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


    [Fact]
    public void Store_RoundTripsEnabledAndBuiltInFlags()
    {
        using var temporary = new TemporaryDirectory();
        string path = Path.Combine(temporary.Path, "Config", "ai-providers.json");
        var store = new AiProviderSettingsStore(path);
        store.Save(new AiProviderSettings
        {
            ActiveProviderId = "built-in",
            Providers =
            [
                new AiProviderProfile
                {
                    Id = "built-in",
                    Name = "OpenAI",
                    BaseUrl = "https://api.openai.com/v1",
                    IsBuiltIn = true,
                    IsEnabled = false
                },
                new AiProviderProfile
                {
                    Id = "custom",
                    Name = "自定义服务商",
                    IsEnabled = true
                }
            ]
        });

        AiProviderSettings loaded = store.Load();
        AiProviderProfile builtIn = Assert.Single(loaded.Providers, item => item.Id == "built-in");
        Assert.True(builtIn.IsBuiltIn);
        Assert.False(builtIn.IsEnabled);
        AiProviderProfile custom = Assert.Single(loaded.Providers, item => item.Id == "custom");
        Assert.False(custom.IsBuiltIn);
        Assert.True(custom.IsEnabled);
        Assert.Null(loaded.ActiveProvider);
    }

    [Fact]
    public void EnsureBuiltInProviders_AddsMissingDefaultsAndKeepsCustomFirst()
    {
        var settings = new AiProviderSettings
        {
            ActiveProviderId = "custom",
            Providers =
            [
                new AiProviderProfile
                {
                    Id = "custom",
                    Name = "我的接口",
                    BaseUrl = "https://custom.example.com/v1"
                },
                new AiProviderProfile
                {
                    Id = "old-deepseek",
                    Name = "DeepSeek 深度求索",
                    BaseUrl = "https://api.deepseek.com",
                    ApiKey = "preserved-key"
                }
            ]
        };

        settings.EnsureBuiltInProviders();

        Assert.Equal("custom", settings.Providers[0].Id);
        AiProviderProfile legacyDeepseek = Assert.Single(
            settings.Providers, item => item.Id == "old-deepseek");
        Assert.False(legacyDeepseek.IsBuiltIn);
        Assert.Equal("DeepSeek 自定义", legacyDeepseek.Name);
        Assert.Equal("preserved-key", legacyDeepseek.ApiKey);

        AiProviderProfile builtInDeepseek = Assert.Single(
            settings.Providers, item => item.IsBuiltIn && item.Name == "DeepSeek");
        Assert.Equal("https://api.deepseek.com", builtInDeepseek.BaseUrl);
        Assert.Equal(AiProviderPresets.All.Count, settings.Providers.Count(item => item.IsBuiltIn));
        Assert.All(settings.Providers.Where(item => item.IsBuiltIn), item => Assert.False(item.IsEnabled));
    }


    [Fact]
    public void EnsureBuiltInProviders_DoesNotTurnCustomEndpointIntoProtectedBuiltIn()
    {
        var settings = new AiProviderSettings
        {
            Providers =
            [
                new AiProviderProfile
                {
                    Id = "custom-deepseek-proxy",
                    Name = "DeepSeek",
                    BaseUrl = "https://proxy.example.com/v1",
                    ApiKey = "private-key"
                }
            ]
        };

        settings.EnsureBuiltInProviders();

        AiProviderProfile custom = Assert.Single(
            settings.Providers,
            provider => provider.Id == "custom-deepseek-proxy");
        Assert.False(custom.IsBuiltIn);
        Assert.Equal("DeepSeek 自定义", custom.Name);
        Assert.Equal("private-key", custom.ApiKey);
        Assert.Single(settings.Providers, provider =>
            provider.IsBuiltIn && provider.Name == "DeepSeek");
    }

    [Fact]
    public void ActiveProvider_RequiresACompleteEnabledProfileAndHonorsTheSelectedId()
    {
        var disabled = new AiProviderProfile
        {
            Id = "disabled",
            Name = "Disabled",
            BaseUrl = "https://disabled.example.com/v1",
            Model = "model",
            ApiKey = "key",
            IsEnabled = false
        };
        var incomplete = new AiProviderProfile
        {
            Id = "incomplete",
            Name = "Incomplete",
            BaseUrl = "https://incomplete.example.com/v1",
            IsEnabled = true
        };
        var readyA = new AiProviderProfile
        {
            Id = "ready-a",
            Name = "Ready A",
            BaseUrl = "https://a.example.com/v1",
            Model = "model-a",
            ApiKey = "key-a",
            IsEnabled = true
        };
        var readyB = new AiProviderProfile
        {
            Id = "ready-b",
            Name = "Ready B",
            BaseUrl = "https://b.example.com/v1",
            Model = "model-b",
            ApiKey = "key-b",
            IsEnabled = true
        };
        var settings = new AiProviderSettings
        {
            ActiveProviderId = "ready-b",
            Providers = [disabled, incomplete, readyA, readyB]
        };

        Assert.Same(readyB, settings.ActiveProvider);

        settings.ActiveProviderId = "incomplete";
        Assert.Same(readyA, settings.ActiveProvider);

        settings.ActiveProviderId = "disabled";
        Assert.Same(readyA, settings.ActiveProvider);

        readyA.RequiresEndpointReview = true;
        Assert.Same(readyB, settings.ActiveProvider);

        readyA.RequiresEndpointReview = false;
        readyA.BaseUrl = "ftp://a.example.com/v1";
        settings.ActiveProviderId = "ready-a";
        Assert.Same(readyB, settings.ActiveProvider);
    }

    [Fact]
    public void EnsureBuiltInProviders_PreservesExistingOrderAndAppendsMissingBuiltIns()
    {
        var customFirst = new AiProviderProfile
        {
            Id = "custom-first",
            Name = "Custom first",
            BaseUrl = "https://first.example.com/v1"
        };
        var builtInInMiddle = new AiProviderProfile
        {
            Id = "builtin-deepseek",
            Name = "DeepSeek",
            BaseUrl = "https://api.deepseek.com",
            IsBuiltIn = true
        };
        var customLast = new AiProviderProfile
        {
            Id = "custom-last",
            Name = "Custom last",
            BaseUrl = "https://last.example.com/v1"
        };
        var settings = new AiProviderSettings
        {
            Providers = [customFirst, builtInInMiddle, customLast]
        };

        settings.EnsureBuiltInProviders();

        Assert.Equal(
            new[] { "custom-first", "builtin-deepseek", "custom-last" },
            settings.Providers.Take(3).Select(provider => provider.Id));
        Assert.Equal(AiProviderPresets.All.Count, settings.Providers.Count(provider => provider.IsBuiltIn));
        Assert.Equal(settings.Providers.Count, settings.Providers.Select(provider => provider.Id).Distinct().Count());
    }

    [Fact]
    public void ApiPaths_DefaultToChatCompletionsAndModelsAndRejectUnsafeValues()
    {
        var profile = new AiProviderProfile();
        Assert.Equal("/chat/completions", profile.ChatCompletionsPath);
        Assert.Equal("/models", profile.ModelsPath);
        Assert.Equal(
            "https://api.example.com/v1/chat/completions",
            AiProviderProfileValidator.BuildApiEndpoint(
                "https://api.example.com/v1", profile.ChatCompletionsPath, "/chat/completions"));

        Assert.Throws<InvalidOperationException>(() =>
            AiProviderProfileValidator.NormalizeApiPath("https://other.example.com/path", "/models"));
        Assert.Throws<InvalidOperationException>(() =>
            AiProviderProfileValidator.NormalizeApiPath("/models?key=secret", "/models"));
        Assert.Throws<InvalidOperationException>(() =>
            AiProviderProfileValidator.NormalizeApiPath("/v1/../admin", "/models"));

        var manualModel = new AiProviderProfile
        {
            Name = "Manual model provider",
            BaseUrl = "https://api.example.com/v1",
            ChatCompletionsPath = "/chat/completions",
            ModelsPath = "/models?unsupported-query",
            Model = "manual-model",
            ApiKey = "key",
            IsEnabled = true
        };
        Assert.True(manualModel.IsReadyForTranslation);
        AiProviderProfileValidator.ValidateForStorage(manualModel);
        AiProviderProfileValidator.Validate(manualModel);
        Assert.Throws<InvalidOperationException>(() =>
            AiProviderProfileValidator.ValidateForModelDiscovery(manualModel));
    }

    [Fact]
    public void Store_LoadRejectsNullProviderListAndDuplicateProviderIds()
    {
        using var temporary = new TemporaryDirectory();
        string path = Path.Combine(temporary.Path, "Config", "ai-providers.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var store = new AiProviderSettingsStore(path);

        File.WriteAllText(path, """{"Providers":null}""");
        Assert.Throws<InvalidDataException>(() => store.Load());

        File.WriteAllText(path,
            """{"Providers":[{"Id":"same","Name":"A"},{"Id":"same","Name":"B"}]}""");
        Assert.Throws<InvalidDataException>(() => store.Load());
    }

    [Fact]
    public void Presets_OfferOnlyOpenAiCompatibleProvidersWithPlainNames()
    {
        string[] names = AiProviderPresets.All.Select(preset => preset.Name).ToArray();

        // Native Gemini / Claude protocols are not supported, so they must not
        // be offered as built-in services.
        Assert.DoesNotContain(names, name => name.Contains("Gemini", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("Claude", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains('（') || name.Contains('('));

        Assert.Equal(
            "https://api.deepseek.com",
            Assert.Single(AiProviderPresets.All, item => item.Name == "DeepSeek").BaseUrl);
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
