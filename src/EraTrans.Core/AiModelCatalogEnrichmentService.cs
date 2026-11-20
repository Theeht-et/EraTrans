using System.Net.Http;
using System.Text.Json;

namespace EraTrans.Core;

/// <summary>
/// Optionally enriches provider-returned model ids with public model specifications
/// from models.dev. Catalog data is advisory only: failures never block model discovery.
/// </summary>
public sealed class AiModelCatalogEnrichmentService
{
    private const string CatalogUrl = "https://models.dev/api.json";
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(8)
    };
    private static readonly SemaphoreSlim CacheGate = new(1, 1);
    private static readonly Dictionary<string, CatalogCacheEntry> Cache = new(StringComparer.Ordinal);

    private readonly HttpClient _httpClient;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Uri _catalogUri;

    public AiModelCatalogEnrichmentService(
        HttpClient? httpClient = null,
        Func<DateTimeOffset>? utcNow = null,
        Uri? catalogUri = null)
    {
        _httpClient = httpClient ?? SharedHttpClient;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _catalogUri = catalogUri ?? new Uri(CatalogUrl);
    }

    public async Task<IReadOnlyList<AiProviderModel>> EnrichAsync(
        AiProviderProfile profile,
        IReadOnlyList<AiProviderModel> models,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(models);
        if (models.Count == 0)
        {
            return models;
        }

        CatalogSnapshot? catalog = await GetCatalogAsync(cancellationToken);
        if (catalog is null)
        {
            return models;
        }

        IReadOnlyList<string> providerIds = ResolveProviderIds(profile.BaseUrl, catalog);
        var globalMatches = new Dictionary<string, CatalogModel>(StringComparer.OrdinalIgnoreCase);
        var ambiguousModelIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (providerIds.Count == 0)
        {
            foreach (CatalogProvider provider in catalog.Providers.Values)
            {
                foreach (CatalogModel model in provider.Models.Values)
                {
                    if (ambiguousModelIds.Contains(model.Id))
                    {
                        continue;
                    }

                    if (globalMatches.Remove(model.Id))
                    {
                        ambiguousModelIds.Add(model.Id);
                    }
                    else
                    {
                        globalMatches[model.Id] = model;
                    }
                }
            }
        }

        return models.Select(model =>
        {
            CatalogModel? metadata = null;
            foreach (string providerId in providerIds)
            {
                if (catalog.Providers.TryGetValue(providerId, out CatalogProvider? provider) &&
                    TryFindModel(provider, model.Id, out metadata))
                {
                    break;
                }
            }

            if (metadata is null && providerIds.Count == 0)
            {
                globalMatches.TryGetValue(model.Id, out metadata);
            }

            return metadata is null ? model : ApplyMetadata(model, metadata);
        }).ToArray();
    }

    private async Task<CatalogSnapshot?> GetCatalogAsync(CancellationToken cancellationToken)
    {
        string cacheKey = _catalogUri.AbsoluteUri;
        await CacheGate.WaitAsync(cancellationToken);
        try
        {
            if (Cache.TryGetValue(cacheKey, out CatalogCacheEntry? cached) &&
                _utcNow() < cached.ExpiresAt)
            {
                return cached.Catalog;
            }

            try
            {
                string json = await _httpClient.GetStringAsync(_catalogUri, cancellationToken);
                CatalogSnapshot loaded = ParseCatalog(json);
                Cache[cacheKey] = new CatalogCacheEntry(loaded, _utcNow().AddHours(12));
                return loaded;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // models.dev is optional metadata; provider model discovery must still work.
                return Cache.TryGetValue(cacheKey, out CatalogCacheEntry? stale)
                    ? stale.Catalog
                    : null;
            }
            catch (HttpRequestException)
            {
                return Cache.TryGetValue(cacheKey, out CatalogCacheEntry? stale)
                    ? stale.Catalog
                    : null;
            }
            catch (JsonException)
            {
                return Cache.TryGetValue(cacheKey, out CatalogCacheEntry? stale)
                    ? stale.Catalog
                    : null;
            }
            catch (InvalidDataException)
            {
                return Cache.TryGetValue(cacheKey, out CatalogCacheEntry? stale)
                    ? stale.Catalog
                    : null;
            }
        }
        finally
        {
            CacheGate.Release();
        }
    }

    private static CatalogSnapshot ParseCatalog(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("models.dev catalog root must be an object.");
        }

        var providers = new Dictionary<string, CatalogProvider>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty providerProperty in document.RootElement.EnumerateObject())
        {
            if (providerProperty.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            JsonElement providerJson = providerProperty.Value;
            string providerId = ReadString(providerJson, "id") ?? providerProperty.Name;
            string providerName = ReadString(providerJson, "name") ?? providerId;
            string? api = ReadString(providerJson, "api");
            if (!TryGetProperty(providerJson, "models", out JsonElement modelRows) ||
                modelRows.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var models = new Dictionary<string, CatalogModel>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty modelProperty in modelRows.EnumerateObject())
            {
                if (modelProperty.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                JsonElement row = modelProperty.Value;
                string id = ReadString(row, "id") ?? modelProperty.Name;
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                models[id] = new CatalogModel
                {
                    Id = id,
                    Name = ReadString(row, "name") ?? id,
                    Family = ReadString(row, "family"),
                    Status = ReadString(row, "status"),
                    SupportsTools = ReadBoolean(row, "tool"),
                    SupportsReasoning = ReadBoolean(row, "rsn"),
                    SupportsStructuredOutput = ReadBoolean(row, "so"),
                    IsEmbedding = id.Contains("embedding", StringComparison.OrdinalIgnoreCase) ||
                                  id.Contains("embed-", StringComparison.OrdinalIgnoreCase),
                    InputModalities = ReadStringArray(row, "in"),
                    OutputModalities = ReadStringArray(row, "out"),
                    ContextLimit = ReadNestedInt(row, "limit", "context"),
                    InputLimit = ReadNestedInt(row, "limit", "input"),
                    OutputLimit = ReadNestedInt(row, "limit", "output")
                };
            }

            providers[providerId] = new CatalogProvider
            {
                Id = providerId,
                Name = providerName,
                Api = api,
                Models = models
            };
        }

        return new CatalogSnapshot(providers);
    }

    private static IReadOnlyList<string> ResolveProviderIds(
        string baseUrl,
        CatalogSnapshot catalog)
    {
        string? host = null;
        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? baseUri))
        {
            host = baseUri.Host;
        }

        var matched = new List<string>();
        if (!string.IsNullOrWhiteSpace(host))
        {
            foreach (CatalogProvider provider in catalog.Providers.Values)
            {
                if (Uri.TryCreate(provider.Api, UriKind.Absolute, out Uri? providerUri) &&
                    string.Equals(providerUri.Host, host, StringComparison.OrdinalIgnoreCase))
                {
                    matched.Add(provider.Id);
                }
            }
        }

        return matched;
    }

    private static bool TryFindModel(
        CatalogProvider provider,
        string requestedId,
        out CatalogModel? model)
    {
        if (provider.Models.TryGetValue(requestedId, out model))
        {
            return true;
        }

        int slash = requestedId.IndexOf('/');
        if (slash > 0 &&
            provider.Models.TryGetValue(requestedId[(slash + 1)..], out model))
        {
            return true;
        }

        model = null;
        return false;
    }

    private static AiProviderModel ApplyMetadata(AiProviderModel source, CatalogModel metadata)
    {
        var inputModalities = metadata.InputModalities.ToList();
        var outputModalities = metadata.OutputModalities.ToList();
        return new AiProviderModel
        {
            Id = source.Id,
            DisplayName = string.IsNullOrWhiteSpace(source.DisplayName) ||
                          string.Equals(source.DisplayName, source.Id, StringComparison.Ordinal)
                ? metadata.Name
                : source.DisplayName,
            GroupName = source.GroupName,
            OwnedBy = source.OwnedBy,
            Created = source.Created,
            IsEmbedding = source.IsEmbedding || metadata.IsEmbedding,
            IsRerank = source.IsRerank,
            Family = metadata.Family,
            Status = metadata.Status,
            SupportsTools = metadata.SupportsTools,
            SupportsReasoning = metadata.SupportsReasoning,
            SupportsStructuredOutput = metadata.SupportsStructuredOutput,
            InputModalities = inputModalities,
            OutputModalities = outputModalities,
            ContextLimit = metadata.ContextLimit,
            InputLimit = metadata.InputLimit,
            OutputLimit = metadata.OutputLimit
        };
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? ReadString(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? text = value.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static bool ReadBoolean(JsonElement element, string name) =>
        TryGetProperty(element, name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.True;

    private static List<string> ReadStringArray(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToList();
    }

    private static int? ReadNestedInt(JsonElement element, string objectName, string propertyName)
    {
        if (!TryGetProperty(element, objectName, out JsonElement nested) ||
            nested.ValueKind != JsonValueKind.Object ||
            !TryGetProperty(nested, propertyName, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out int result))
        {
            return null;
        }

        return result;
    }

    private sealed record CatalogSnapshot(
        Dictionary<string, CatalogProvider> Providers);

    private sealed record CatalogCacheEntry(
        CatalogSnapshot Catalog,
        DateTimeOffset ExpiresAt);

    private sealed class CatalogProvider
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string? Api { get; init; }
        public Dictionary<string, CatalogModel> Models { get; init; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class CatalogModel
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string? Family { get; init; }
        public string? Status { get; init; }
        public bool SupportsTools { get; init; }
        public bool SupportsReasoning { get; init; }
        public bool SupportsStructuredOutput { get; init; }
        public bool IsEmbedding { get; init; }
        public List<string> InputModalities { get; init; } = [];
        public List<string> OutputModalities { get; init; } = [];
        public int? ContextLimit { get; init; }
        public int? InputLimit { get; init; }
        public int? OutputLimit { get; init; }
    }
}
