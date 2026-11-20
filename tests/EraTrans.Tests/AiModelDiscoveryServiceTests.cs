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
        Assert.Equal("OpenAI", gpt.GroupName);
        Assert.Equal("openai", gpt.OwnedBy);
        Assert.Equal(1720000000, gpt.Created);
        Assert.True(gpt.IsChatModel);

        AiProviderModel claude = Assert.Single(
            models,
            model => model.Id == "anthropic/claude-sonnet-4");
        Assert.Equal("Claude Sonnet 4", claude.DisplayName);
        Assert.Equal("anthropic", claude.GroupName);

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
