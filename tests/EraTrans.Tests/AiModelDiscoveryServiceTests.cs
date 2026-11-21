using EraTrans.Core;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Xunit;

namespace EraTrans.Tests;

public sealed class AiModelDiscoveryServiceTests
{
    [Fact]
    public void ContextLimitMetadata_IsHiddenWhenUnknownAndFormattedWhenKnown()
    {
        AiProviderModel unknown = new() { Id = "unknown-context-model" };
        Assert.False(unknown.HasKnownContextLimit);
        Assert.Equal("", unknown.ContextLimitLabel);

        AiProviderModel known = new() { Id = "known-context-model", ContextLimit = 131072 };
        Assert.True(known.HasKnownContextLimit);
        Assert.Equal("上下文大小：131.1K", known.ContextLimitLabel);
    }

    [Fact]
    public void ParseModelsResponse_ExtractsMetadataDeduplicatesAndGroupsModels()
    {
        const string json = """
            {
              "object": "list",
              "data": [
                {
                  "id": "gpt-4o-mini",
                  "object": "model",
                  "created": 1720000000,
                  "owned_by": "openai"
                },
                {
                  "id": "anthropic/claude-sonnet-4",
                  "display_name": "Claude Sonnet 4",
                  "owned_by": "openrouter"
                },
                {
                  "id": "text-embedding-3-large",
                  "owned_by": "openai"
                },
                {
                  "id": "gpt-4o-mini",
                  "owned_by": "duplicate"
                }
              ]
            }
            """;

        IReadOnlyList<AiProviderModel> models =
            AiModelDiscoveryService.ParseModelsResponse(json);

        Assert.Equal(3, models.Count);
        AiProviderModel gpt = Assert.Single(models, model => model.Id == "gpt-4o-mini");
        Assert.Equal("OpenAI · GPT-4o", gpt.GroupName);
        Assert.Equal("openai", gpt.OwnedBy);
        Assert.Equal(1720000000, gpt.Created);
        Assert.True(gpt.IsChatModel);

        AiProviderModel claude = Assert.Single(
            models,
            model => model.Id == "anthropic/claude-sonnet-4");
        Assert.Equal("Claude Sonnet 4", claude.DisplayName);
        Assert.Equal("Claude · Sonnet", claude.GroupName);

        AiProviderModel embedding = Assert.Single(
            models,
            model => model.Id == "text-embedding-3-large");
        Assert.Equal("嵌入模型", embedding.GroupName);
        Assert.True(embedding.IsEmbedding);
        Assert.False(embedding.IsChatModel);
    }

    [Fact]
    public void ReasoningSupportDescription_ShowsOnlyKnownCapabilityWithoutEffortLevels()
    {
        AiProviderModel unknown = new() { Id = "custom-model" };
        Assert.False(unknown.HasKnownReasoningSupport);
        Assert.Equal("", unknown.ReasoningSupportDescription);

        AiProviderModel supported = new()
        {
            Id = "reasoning-model",
            ReasoningSupport = true,
            ReasoningLevels = ["low", "medium", "high"]
        };
        Assert.True(supported.HasKnownReasoningSupport);
        Assert.True(supported.IsReasoningSupported);
        Assert.Equal("推理支持", supported.ReasoningSupportDescription);
        Assert.DoesNotContain("low", supported.ReasoningSupportDescription);

        AiProviderModel unsupported = new()
        {
            Id = "plain-model",
            ReasoningSupport = false
        };
        Assert.True(unsupported.HasKnownReasoningSupport);
        Assert.False(unsupported.IsReasoningSupported);
        Assert.Equal("不支持推理", unsupported.ReasoningSupportDescription);
    }

    [Fact]
    public void ParseModelsResponse_AcceptsModelsEnvelopeAndEmptyList()
    {
        IReadOnlyList<AiProviderModel> models =
            AiModelDiscoveryService.ParseModelsResponse(
                """{"models":[{"name":"deepseek-chat"}]}""");
        Assert.Equal("deepseek-chat", Assert.Single(models).Id);

        Assert.Empty(AiModelDiscoveryService.ParseModelsResponse("""{"data":[]}"""));
    }

    [Fact]
    public void ParseModelsResponse_RejectsUnknownResponseShape()
    {
        Assert.Throws<InvalidDataException>(() =>
            AiModelDiscoveryService.ParseModelsResponse("""{"items":[]}"""));
    }

    [Fact]
    public async Task FetchModelsAsync_UsesBaseUrlAndBearerAuthentication()
    {
        using var handler = new StubHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://api.example.com/v1/catalog/models", request.RequestUri?.ToString());
            Assert.Equal("Bearer test-secret", request.Headers.Authorization?.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"data":[{"id":"qwen-plus","owned_by":"qwen"}]}""",
                    Encoding.UTF8,
                    "application/json")
            };
        });
        using var client = new HttpClient(handler);
        var service = new AiModelDiscoveryService(client);
        var profile = new AiProviderProfile
        {
            Name = "Test",
            BaseUrl = "https://api.example.com/v1/",
            ModelsPath = "/catalog/models",
            ApiKey = "test-secret"
        };

        IReadOnlyList<AiProviderModel> models = await service.FetchModelsAsync(profile);

        Assert.Equal("Qwen", Assert.Single(models).GroupName);
        Assert.Equal("https://api.example.com/v1", profile.BaseUrl);
    }

    [Fact]
    public async Task FetchModelsAsync_ReportsUnsupportedModelsEndpointWithoutLeakingApiKey()
    {
        using var handler = new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound));
        using var client = new HttpClient(handler);
        var service = new AiModelDiscoveryService(client);
        var profile = new AiProviderProfile
        {
            Name = "Test",
            BaseUrl = "https://api.example.com/v1",
            ApiKey = "must-not-appear"
        };

        HttpRequestException exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => service.FetchModelsAsync(profile));

        Assert.Contains("HTTP 404", exception.Message);
        Assert.Contains("手动填写模型 ID", exception.Message);
        Assert.DoesNotContain("must-not-appear", exception.Message);
    }


    [Fact]
    public async Task CatalogEnrichment_AddsOfficialModelCapabilitiesAndLimitsWhenProviderMatches()
    {
        var catalogUri = new Uri("https://models.dev/api.json?test=era-trans-enrichment-1");
        using var handler = new StubHandler(request =>
        {
            Assert.Equal(catalogUri, request.RequestUri);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "openai": {
                        "id": "openai",
                        "name": "OpenAI",
                        "api": "https://api.openai.com/v1",
                        "models": {
                          "gpt-4o-mini": {
                            "id": "gpt-4o-mini",
                            "name": "GPT-4o mini",
                            "family": "gpt-4o",
                            "status": "active",
                            "tool_call": true,
                            "reasoning": true,
                            "structured_output": true,
                            "attachment": true,
                            "temperature": true,
                            "open_weights": true,
                            "knowledge": "2024-10",
                            "release_date": "2024-07-18",
                            "last_updated": "2025-01-01",
                            "modalities": {
                              "input": ["text", "image"],
                              "output": ["text"]
                            },
                            "limit": {
                              "context": 128000,
                              "input": 120000,
                              "output": 16000
                            },
                            "cost": {
                              "input": 2.5,
                              "output": 10.0,
                              "reasoning": 12.0,
                              "cache_read": 0.25,
                              "cache_write": 3.0,
                              "input_audio": 40.0,
                              "output_audio": 80.0
                            }
                          }
                        }
                      }
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            };
        });
        using var client = new HttpClient(handler);
        var enrichment = new AiModelCatalogEnrichmentService(
            client,
            catalogUri: catalogUri);
        AiProviderModel[] discovered =
        [
            new()
            {
                Id = "gpt-4o-mini",
                DisplayName = "gpt-4o-mini",
                GroupName = "OpenAI · GPT-4o"
            }
        ];

        IReadOnlyList<AiProviderModel> enriched = await enrichment.EnrichAsync(
            new AiProviderProfile
            {
                BaseUrl = "https://api.openai.com/v1"
            },
            discovered);

        AiProviderModel model = Assert.Single(enriched);
        Assert.Equal("GPT-4o mini", model.DisplayName);
        Assert.Equal("gpt-4o", model.Family);
        Assert.Equal(128000, model.ContextLimit);
        Assert.Equal(120000, model.InputLimit);
        Assert.Equal(16000, model.OutputLimit);
        Assert.True(model.SupportsTools);
        Assert.True(model.SupportsReasoning);
        Assert.True(model.SupportsStructuredOutput);
        Assert.True(model.SupportsAttachments);
        Assert.True(model.SupportsTemperature);
        Assert.True(model.OpenWeights);
        Assert.Equal("2024-10", model.Knowledge);
        Assert.Equal("2024-07-18", model.ReleaseDate);
        Assert.Equal("2025-01-01", model.LastUpdated);
        Assert.Contains("image", model.InputModalities);
        Assert.Contains("text", model.OutputModalities);
        Assert.Equal(2.5, model.InputCostPerMillion);
        Assert.Equal(10.0, model.OutputCostPerMillion);
        Assert.Equal(12.0, model.ReasoningCostPerMillion);
        Assert.Equal(0.25, model.CacheReadCostPerMillion);
        Assert.Equal(3.0, model.CacheWriteCostPerMillion);
        Assert.Equal(40.0, model.InputAudioCostPerMillion);
        Assert.Equal(80.0, model.OutputAudioCostPerMillion);
        Assert.Equal("上下文大小：128K", model.ContextLimitLabel);
        Assert.True(model.ReasoningSupport == true);
        Assert.Contains("输入 $2.5/1M", model.MetadataSummary);
        Assert.Contains("输出 $10/1M", model.MetadataSummary);
    }

    [Fact]
    public async Task CatalogEnrichment_ExtractsReasoningBudgetBoundsFromModelsDevOptions()
    {
        var catalogUri = new Uri("https://models.dev/api.json?test=reasoning-budget-bounds");
        const string json = """
            {
              "openrouter": {
                "id": "openrouter",
                "api": "https://openrouter.ai/api/v1",
                "models": {
                  "vendor/budget-model": {
                    "id": "vendor/budget-model",
                    "name": "Budget Model",
                    "reasoning": true,
                    "reasoning_options": [
                      { "type": "budget_tokens", "min": 4096, "max": 50000 }
                    ]
                  }
                }
              }
            }
            """;
        using var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });
        using var client = new HttpClient(handler);
        var enrichment = new AiModelCatalogEnrichmentService(client, catalogUri: catalogUri);

        IReadOnlyList<AiProviderModel> result = await enrichment.EnrichAsync(
            new AiProviderProfile { BaseUrl = "https://openrouter.ai/api/v1" },
            [new AiProviderModel { Id = "vendor/budget-model" }]);

        AiProviderModel model = Assert.Single(result);
        Assert.True(model.ReasoningSupport);
        Assert.Equal(4096, model.ReasoningBudgetMin);
        Assert.Equal(50000, model.ReasoningBudgetMax);
        Assert.Empty(model.ReasoningLevels);
    }

    [Fact]
    public async Task CatalogEnrichment_CatalogFailureDoesNotBlockDiscoveredModels()
    {
        var catalogUri = new Uri("https://models.dev/api.json?test=era-trans-enrichment-failure");
        using var handler = new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var client = new HttpClient(handler);
        var enrichment = new AiModelCatalogEnrichmentService(
            client,
            catalogUri: catalogUri);
        AiProviderModel original = new()
        {
            Id = "custom-model",
            DisplayName = "custom-model",
            GroupName = "其他模型"
        };

        IReadOnlyList<AiProviderModel> result = await enrichment.EnrichAsync(
            new AiProviderProfile { BaseUrl = "https://custom.example.com/v1" },
            [original]);

        Assert.Same(original, Assert.Single(result));
        Assert.Null(original.ContextLimit);
    }

    [Fact]
    public void ParseModelsResponse_ReadsNestedContextAndReasoningMetadata()
    {
        const string json = """
            {
              "data": [
                {
                  "id": "catalog-model",
                  "top_provider": {
                    "context_length": 262144,
                    "max_completion_tokens": 32768
                  },
                  "reasoning": { "supported": true }
                },
                {
                  "id": "limited-model",
                  "limit": {
                    "context": 65536,
                    "output": 8192
                  },
                  "thinking": { "supported": false }
                }
              ]
            }
            """;

        IReadOnlyList<AiProviderModel> models = AiModelDiscoveryService.ParseModelsResponse(json);
        AiProviderModel catalogModel = Assert.Single(models, model => model.Id == "catalog-model");
        Assert.Equal(262144, catalogModel.ContextLimit);
        Assert.Null(catalogModel.InputLimit);
        Assert.Equal(32768, catalogModel.OutputLimit);
        Assert.Equal(true, catalogModel.ReasoningSupport);
        Assert.True(catalogModel.SupportsReasoning);

        AiProviderModel limitedModel = Assert.Single(models, model => model.Id == "limited-model");
        Assert.Equal(65536, limitedModel.ContextLimit);
        Assert.Equal(8192, limitedModel.OutputLimit);
        Assert.Equal(false, limitedModel.ReasoningSupport);
        Assert.False(limitedModel.SupportsReasoning);
    }

    [Fact]
    public void ModelCapabilityLabels_HideUnknownAndShowOnlyKnownSupportState()
    {
        var unknown = new AiProviderModel { Id = "provider-model" };
        Assert.Equal("", unknown.ContextLimitLabel);
        Assert.False(unknown.HasKnownContextLimit);
        Assert.False(unknown.HasKnownReasoningSupport);
        Assert.Equal("", unknown.ReasoningSupportDescription);

        var supported = new AiProviderModel
        {
            Id = "reasoning-model",
            ContextLimit = 128000,
            ReasoningSupport = true
        };
        Assert.Equal("上下文大小：128K", supported.ContextLimitLabel);
        Assert.Equal("推理支持", supported.ReasoningSupportDescription);

        var unsupported = new AiProviderModel
        {
            Id = "standard-model",
            ReasoningSupport = false
        };
        Assert.Equal("不支持推理", unsupported.ReasoningSupportDescription);
    }

    [Fact]
    public async Task CatalogEnrichment_ResolvesProviderAliasHostAndPersistsCatalogCache()
    {
        using var temporary = new TemporaryDirectory();
        string cachePath = Path.Combine(temporary.Path, "models-dev-catalog.json");
        var firstUri = new Uri("https://models.dev/api.json?test=moonshot-host-alias-first");
        const string catalogJson = """
            {
              "moonshotai": {
                "id": "moonshotai",
                "name": "Moonshot AI",
                "api": "https://api.moonshot.ai/v1",
                "models": {
                  "kimi-k2-thinking": {
                    "id": "kimi-k2-thinking",
                    "name": "Kimi K2 Thinking",
                    "reasoning": true,
                    "limit": { "context": 262144, "input": 250000, "output": 32768 }
                  }
                }
              }
            }
            """;

        using var firstHandler = new StubHandler(request =>
        {
            Assert.Equal(firstUri, request.RequestUri);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(catalogJson, Encoding.UTF8, "application/json")
            };
        });
        using var firstClient = new HttpClient(firstHandler);
        var firstService = new AiModelCatalogEnrichmentService(
            firstClient,
            catalogUri: firstUri,
            catalogCacheFilePath: cachePath);

        AiProviderModel discovered = new()
        {
            Id = "kimi-k2-thinking",
            DisplayName = "kimi-k2-thinking",
            GroupName = "Kimi"
        };
        IReadOnlyList<AiProviderModel> firstResult = await firstService.EnrichAsync(
            new AiProviderProfile { BaseUrl = "https://api.moonshot.cn/v1" },
            [discovered]);

        AiProviderModel enriched = Assert.Single(firstResult);
        Assert.Equal("Kimi K2 Thinking", enriched.DisplayName);
        Assert.Equal(262144, enriched.ContextLimit);
        Assert.Equal(250000, enriched.InputLimit);
        Assert.Equal(32768, enriched.OutputLimit);
        Assert.Equal(true, enriched.ReasoningSupport);
        Assert.True(File.Exists(cachePath));

        // A different catalog URL simulates a fresh process with no in-memory entry.
        var secondUri = new Uri("https://models.dev/api.json?test=moonshot-host-alias-after-restart");
        int secondRequestCount = 0;
        using var secondHandler = new StubHandler(_ =>
        {
            secondRequestCount++;
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });
        using var secondClient = new HttpClient(secondHandler);
        var secondService = new AiModelCatalogEnrichmentService(
            secondClient,
            catalogUri: secondUri,
            catalogCacheFilePath: cachePath);

        IReadOnlyList<AiProviderModel> cachedResult = await secondService.EnrichAsync(
            new AiProviderProfile { BaseUrl = "https://api.moonshot.cn/v1" },
            [discovered]);

        AiProviderModel cached = Assert.Single(cachedResult);
        Assert.Equal(262144, cached.ContextLimit);
        Assert.Equal(true, cached.ReasoningSupport);
        Assert.Equal(0, secondRequestCount);

        File.SetLastWriteTimeUtc(cachePath, DateTime.UtcNow.AddDays(-2));
        var thirdUri = new Uri("https://models.dev/api.json?test=moonshot-host-alias-expired-cache");
        int thirdRequestCount = 0;
        using var thirdHandler = new StubHandler(_ =>
        {
            thirdRequestCount++;
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });
        using var thirdClient = new HttpClient(thirdHandler);
        var thirdService = new AiModelCatalogEnrichmentService(
            thirdClient,
            catalogUri: thirdUri,
            catalogCacheFilePath: cachePath);

        IReadOnlyList<AiProviderModel> expiredFallback = await thirdService.EnrichAsync(
            new AiProviderProfile { BaseUrl = "https://api.moonshot.cn/v1" },
            [discovered]);
        Assert.Equal(1, thirdRequestCount);
        Assert.Equal(262144, Assert.Single(expiredFallback).ContextLimit);
    }

    [Fact]
    public async Task CatalogEnrichment_MapsNamespacedModelIdsToFirstPartyProviderCatalog()
    {
        var catalogUri = new Uri("https://models.dev/api.json?test=provider-prefix-route");
        const string json = """
            {
              "openrouter": {
                "id": "openrouter",
                "api": "https://openrouter.ai/api/v1",
                "models": {}
              },
              "deepseek": {
                "id": "deepseek",
                "api": "https://api.deepseek.com/v1",
                "models": {
                  "deepseek-chat": {
                    "id": "deepseek-chat",
                    "name": "DeepSeek Chat",
                    "reasoning": false,
                    "ro": [{ "type": "effort", "values": ["low", "medium", "high"] }],
                    "limit": { "context": 131072 }
                  }
                }
              }
            }
            """;
        using var handler = new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        using var client = new HttpClient(handler);
        var enrichment = new AiModelCatalogEnrichmentService(client, catalogUri: catalogUri);

        IReadOnlyList<AiProviderModel> result = await enrichment.EnrichAsync(
            new AiProviderProfile { BaseUrl = "https://openrouter.ai/api/v1" },
            [new AiProviderModel
            {
                Id = "deepseek/deepseek-chat",
                DisplayName = "deepseek/deepseek-chat"
            }]);

        AiProviderModel model = Assert.Single(result);
        Assert.Equal("DeepSeek Chat", model.DisplayName);
        Assert.Equal(131072, model.ContextLimit);
        Assert.Equal(false, model.ReasoningSupport);
        Assert.Equal(new[] { "low", "medium", "high" }, model.ReasoningLevels);
        Assert.Equal("不支持推理", model.ReasoningSupportDescription);
        Assert.Equal(new[] { "low", "medium", "high" }, model.ReasoningLevels);
    }

    [Fact]
    public async Task CatalogEnrichment_UsesConservativeKelivoStyleReasoningDefaultsOnlyWhenUnknown()
    {
        var catalogUri = new Uri("https://models.dev/api.json?test=reasoning-defaults-fallback");
        using var handler = new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var client = new HttpClient(handler);
        var enrichment = new AiModelCatalogEnrichmentService(client, catalogUri: catalogUri);

        AiProviderModel[] models =
        [
            new() { Id = "deepseek-reasoner", DisplayName = "deepseek-reasoner" },
            new() { Id = "custom-reasoning-model", DisplayName = "custom-reasoning-model" },
            new()
            {
                Id = "deepseek-r1",
                DisplayName = "deepseek-r1",
                ReasoningSupport = false
            }
        ];

        IReadOnlyList<AiProviderModel> result = await enrichment.EnrichAsync(
            new AiProviderProfile { BaseUrl = "https://custom.example.com/v1" },
            models);

        AiProviderModel deepseekReasoner = Assert.Single(
            result, model => model.Id == "deepseek-reasoner");
        Assert.Equal(true, deepseekReasoner.ReasoningSupport);

        AiProviderModel custom = Assert.Single(
            result, model => model.Id == "custom-reasoning-model");
        Assert.Null(custom.ReasoningSupport);

        AiProviderModel explicitFalse = Assert.Single(
            result, model => model.Id == "deepseek-r1");
        Assert.Equal(false, explicitFalse.ReasoningSupport);
    }

    [Fact]
    public void ParseModelsResponse_DoesNotConfuseInputLimitWithTotalContextWindow()
    {
        const string json = """
            {
              "data": [
                { "id": "input-limited", "inputTokenLimit": 12000 },
                {
                  "id": "context-and-input-limited",
                  "context_length": 65536,
                  "max_input_tokens": 60000
                }
              ]
            }
            """;

        IReadOnlyList<AiProviderModel> models = AiModelDiscoveryService.ParseModelsResponse(json);
        AiProviderModel inputOnly = Assert.Single(models, model => model.Id == "input-limited");
        Assert.Equal(12000, inputOnly.ContextLimit);
        Assert.Null(inputOnly.InputLimit);

        AiProviderModel both = Assert.Single(models, model => model.Id == "context-and-input-limited");
        Assert.Equal(65536, both.ContextLimit);
        Assert.Equal(60000, both.InputLimit);
    }

    [Fact]
    public void ParseModelsResponse_ExtractsProviderReportedReasoningEffortLevels()
    {
        const string json = """
            {
              "data": [
                {
                  "id": "model-with-levels",
                  "reasoning": { "supported": true, "effort": ["low", "high"] }
                },
                {
                  "id": "model-with-level-list",
                  "supported_reasoning_levels": ["minimal", "low", "high"]
                },
                {
                  "id": "deepseek-model-list-shape",
                  "effort": { "supported_levels": ["low", "high", "max"], "default_level": "high" }
                },
                {
                  "id": "model-with-none-level",
                  "reasoning_options": [
                    { "type": "effort", "values": ["none", "low", "high"] }
                  ]
                }
              ]
            }
            """;

        IReadOnlyList<AiProviderModel> models = AiModelDiscoveryService.ParseModelsResponse(json);
        AiProviderModel nested = Assert.Single(models, model => model.Id == "model-with-levels");
        Assert.Equal(true, nested.ReasoningSupport);
        Assert.Equal(new[] { "low", "high" }, nested.ReasoningLevels);
        Assert.Equal("推理支持", nested.ReasoningSupportDescription);

        AiProviderModel direct = Assert.Single(models, model => model.Id == "model-with-level-list");
        Assert.Equal(true, direct.ReasoningSupport);
        Assert.Equal(new[] { "minimal", "low", "high" }, direct.ReasoningLevels);

        AiProviderModel deepseek = Assert.Single(models, model => model.Id == "deepseek-model-list-shape");
        Assert.True(deepseek.ReasoningSupport);
        Assert.Equal(new[] { "low", "high", "max" }, deepseek.ReasoningLevels);
        Assert.False(deepseek.ReasoningCanDisable);

        AiProviderModel none = Assert.Single(models, model => model.Id == "model-with-none-level");
        Assert.True(none.ReasoningSupport);
        Assert.True(none.ReasoningCanDisable);
        Assert.Equal(new[] { "low", "high" }, none.ReasoningLevels);
    }

    [Fact]
    public async Task CatalogEnrichment_UsesContextWindowMarkerWhenCatalogHasNoNumericLimit()
    {
        var catalogUri = new Uri("https://models.dev/api.json?test=context-window-suffix");
        const string json = """
            {
              "openai": {
                "id": "openai",
                "api": "https://api.openai.com/v1",
                "models": {
                  "gpt-4o-mini": {
                    "id": "gpt-4o-mini",
                    "name": "GPT-4o mini"
                  }
                }
              }
            }
            """;
        using var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });
        using var client = new HttpClient(handler);
        var enrichment = new AiModelCatalogEnrichmentService(client, catalogUri: catalogUri);

        IReadOnlyList<AiProviderModel> result = await enrichment.EnrichAsync(
            new AiProviderProfile { BaseUrl = "https://api.openai.com/v1" },
            [new AiProviderModel
            {
                Id = "gpt-4o-mini-128k",
                DisplayName = "gpt-4o-mini-128k",
                GroupName = "OpenAI"
            }]);

        AiProviderModel model = Assert.Single(result);
        Assert.Equal("GPT-4o mini", model.DisplayName);
        Assert.Equal(128000, model.ContextLimit);
    }

    [Fact]
    public void ParseModelsResponse_ReadsExplicitReasoningDisableAndDialectMetadata()
    {
        const string json = """
            {
              "data": [
                {
                  "id": "openai-reasoning",
                  "reasoning": {
                    "supported": true,
                    "can_disable": true,
                    "dialect": "openaiReasoningEffort",
                    "effort": ["low", "high"]
                  }
                },
                {
                  "id": "unknown-disable",
                  "reasoning": { "supported": true }
                },
                {
                  "id": "toggle-disable",
                  "reasoning": true,
                  "reasoning_options": [{ "type": "toggle" }]
                },
                {
                  "id": "effort-does-not-disable",
                  "reasoning": true,
                  "reasoning_options": [{ "type": "effort", "values": ["low", "high"] }]
                }
              ]
            }
            """;

        IReadOnlyList<AiProviderModel> models = AiModelDiscoveryService.ParseModelsResponse(json);
        AiProviderModel explicitModel = Assert.Single(models, model => model.Id == "openai-reasoning");
        Assert.True(explicitModel.ReasoningCanDisable);
        Assert.Equal("openaiReasoningEffort", explicitModel.ReasoningDialect);

        AiProviderModel unknown = Assert.Single(models, model => model.Id == "unknown-disable");
        Assert.Null(unknown.ReasoningCanDisable);
        Assert.Null(unknown.ReasoningDialect);

        AiProviderModel toggle = Assert.Single(models, model => model.Id == "toggle-disable");
        Assert.True(toggle.ReasoningCanDisable);
        Assert.True(toggle.ReasoningSupport);

        AiProviderModel effortOnly = Assert.Single(models, model => model.Id == "effort-does-not-disable");
        Assert.False(effortOnly.ReasoningCanDisable);
        Assert.Equal(new[] { "low", "high" }, effortOnly.ReasoningLevels);
    }

    [Fact]
    public async Task CatalogEnrichment_PreservesExplicitReasoningOffMetadata()
    {
        var catalogUri = new Uri("https://models.dev/api.json?test=reasoning-off-capability");
        const string catalogJson = """
            {
              "openai": {
                "id": "openai",
                "api": "https://api.openai.com/v1",
                "models": {
                  "reasoning-model": {
                    "id": "reasoning-model",
                    "name": "Reasoning model",
                    "reasoning": {
                      "supported": true,
                      "can_disable": true,
                      "dialect": "openaiReasoningEffort",
                      "effort": ["low", "high"]
                    }
                  }
                }
              }
            }
            """;
        using var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(catalogJson, Encoding.UTF8, "application/json")
        });
        using var client = new HttpClient(handler);
        var enrichment = new AiModelCatalogEnrichmentService(client, catalogUri: catalogUri);

        AiProviderModel result = Assert.Single(await enrichment.EnrichAsync(
            new AiProviderProfile { BaseUrl = "https://api.openai.com/v1" },
            [new AiProviderModel { Id = "reasoning-model", ReasoningSupport = true }]));

        Assert.True(result.ReasoningSupport);
        Assert.True(result.ReasoningCanDisable);
        Assert.Equal(new[] { "low", "high" }, result.ReasoningLevels);
        Assert.Equal("openaiReasoningEffort", result.ReasoningDialect);
    }

    [Fact]
    public async Task CatalogEnrichment_ParsesTopLevelEffortAndInfersNoneAsDisableCapability()
    {
        var catalogUri = new Uri("https://models.dev/api.json?test=top-level-effort-levels");
        const string catalogJson = """
            {
              "openai": {
                "id": "openai",
                "api": "https://api.openai.com/v1",
                "models": {
                  "reasoning-model": {
                    "id": "reasoning-model",
                    "name": "Reasoning model",
                    "reasoning": true,
                    "effort": {
                      "supported_levels": ["none", "low", "high"],
                      "default_level": "high"
                    }
                  },
                  "nested-reasoning-model": {
                    "id": "nested-reasoning-model",
                    "name": "Nested reasoning model",
                    "reasoning": {
                      "effort": {
                        "supported_levels": ["none", "low", "high"],
                        "default_level": "high"
                      }
                    }
                  },
                  "toggle-reasoning-model": {
                    "id": "toggle-reasoning-model",
                    "name": "Toggle reasoning model",
                    "reasoning": true,
                    "reasoning_options": [
                      { "type": "toggle" }
                    ]
                  },
                  "effort-without-none-model": {
                    "id": "effort-without-none-model",
                    "name": "Effort-only reasoning model",
                    "reasoning": true,
                    "reasoning_options": [
                      { "type": "effort", "values": ["low", "high"] }
                    ]
                  }
                }
              }
            }
            """;
        using var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(catalogJson, Encoding.UTF8, "application/json")
        });
        using var client = new HttpClient(handler);
        var enrichment = new AiModelCatalogEnrichmentService(client, catalogUri: catalogUri);

        AiProviderModel result = Assert.Single(await enrichment.EnrichAsync(
            new AiProviderProfile { BaseUrl = "https://api.openai.com/v1" },
            [new AiProviderModel { Id = "reasoning-model" }]));

        Assert.True(result.ReasoningSupport);
        Assert.True(result.ReasoningCanDisable);
        Assert.Equal(new[] { "low", "high" }, result.ReasoningLevels);

        AiProviderModel nested = Assert.Single(await enrichment.EnrichAsync(
            new AiProviderProfile { BaseUrl = "https://api.openai.com/v1" },
            [new AiProviderModel { Id = "nested-reasoning-model" }]));
        Assert.True(nested.ReasoningSupport);
        Assert.True(nested.ReasoningCanDisable);
        Assert.Equal(new[] { "low", "high" }, nested.ReasoningLevels);
        Assert.Equal("openaiReasoningEffort", nested.ReasoningDialect);

        IReadOnlyList<AiProviderModel> inferred = await enrichment.EnrichAsync(
            new AiProviderProfile { BaseUrl = "https://api.openai.com/v1" },
            [
                new AiProviderModel { Id = "toggle-reasoning-model" },
                new AiProviderModel { Id = "effort-without-none-model" }
            ]);
        AiProviderModel toggle = Assert.Single(inferred, model => model.Id == "toggle-reasoning-model");
        Assert.True(toggle.ReasoningSupport);
        Assert.True(toggle.ReasoningCanDisable);

        AiProviderModel effortOnly = Assert.Single(inferred, model => model.Id == "effort-without-none-model");
        Assert.True(effortOnly.ReasoningSupport);
        Assert.False(effortOnly.ReasoningCanDisable);
        Assert.Equal(new[] { "low", "high" }, effortOnly.ReasoningLevels);
    }

    [Fact]
    public async Task CatalogEnrichment_PreservesExplicitlyEmptyEffortLevelsLikeKelivo()
    {
        var catalogUri = new Uri("https://models.dev/api.json?test=explicit-empty-effort-levels");
        const string catalogJson = """
            {
              "openai": {
                "id": "openai",
                "api": "https://api.openai.com/v1",
                "models": {
                  "gpt-5.6": {
                    "id": "gpt-5.6",
                    "name": "GPT 5.6",
                    "reasoning": true,
                    "reasoning_options": [
                      { "type": "effort", "values": [] }
                    ]
                  }
                }
              }
            }
            """;
        using var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(catalogJson, Encoding.UTF8, "application/json")
        });
        using var client = new HttpClient(handler);
        var enrichment = new AiModelCatalogEnrichmentService(client, catalogUri: catalogUri);

        AiProviderModel model = Assert.Single(await enrichment.EnrichAsync(
            new AiProviderProfile { BaseUrl = "https://api.openai.com/v1" },
            [new AiProviderModel { Id = "gpt-5.6" }]));

        Assert.True(model.ReasoningSupport);
        Assert.False(model.ReasoningCanDisable);
        Assert.True(model.HasExplicitReasoningLevels);
        Assert.Empty(model.ReasoningLevels);
    }

    [Fact]
    public async Task CatalogEnrichment_AppliesVerifiedDeepSeekReasoningDefaultsWhenCatalogUnavailable()
    {
        var catalogUri = new Uri("https://models.dev/api.json?test=deepseek-reasoning-defaults");
        using var handler = new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var client = new HttpClient(handler);
        var enrichment = new AiModelCatalogEnrichmentService(client, catalogUri: catalogUri);

        IReadOnlyList<AiProviderModel> models = await enrichment.EnrichAsync(
            new AiProviderProfile { BaseUrl = "https://api.deepseek.com/v1" },
            [
                new AiProviderModel { Id = "deepseek-flash" },
                new AiProviderModel { Id = "deepseek-v4-pro" },
                new AiProviderModel { Id = "deepseek-reasoner" },
                new AiProviderModel { Id = "other-model" }
            ]);

        AiProviderModel flash = Assert.Single(models, model => model.Id == "deepseek-flash");
        Assert.True(flash.ReasoningSupport);
        Assert.True(flash.ReasoningCanDisable);
        Assert.Equal("deepseekThinking", flash.ReasoningDialect);
        Assert.Equal(new[] { "low", "high", "max" }, flash.ReasoningLevels);

        AiProviderModel v4 = Assert.Single(models, model => model.Id == "deepseek-v4-pro");
        Assert.True(v4.ReasoningSupport);
        Assert.True(v4.ReasoningCanDisable);
        Assert.Equal("deepseekThinking", v4.ReasoningDialect);

        AiProviderModel legacy = Assert.Single(models, model => model.Id == "deepseek-reasoner");
        Assert.Null(legacy.ReasoningCanDisable);
        Assert.Null(legacy.ReasoningDialect);
        Assert.Empty(legacy.ReasoningLevels);

        AiProviderModel other = Assert.Single(models, model => model.Id == "other-model");
        Assert.Null(other.ReasoningSupport);
        Assert.Null(other.ReasoningCanDisable);
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "EraTransModelCatalogTests",
            Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
