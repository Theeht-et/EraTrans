using EraTrans.Core;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Xunit;

namespace EraTrans.Tests;

public sealed class AiModelDiscoveryServiceTests
{
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
            Assert.Equal("https://api.example.com/v1/models", request.RequestUri?.ToString());
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
        Assert.Equal("上下文：128K", model.ContextLimitLabel);
        Assert.True(model.ReasoningSupport == true);
        Assert.Contains("输入 $2.5/1M", model.MetadataSummary);
        Assert.Contains("输出 $10/1M", model.MetadataSummary);
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
}
