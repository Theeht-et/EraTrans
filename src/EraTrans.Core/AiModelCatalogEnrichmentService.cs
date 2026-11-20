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
        Timeout = TimeSpan.FromSeconds(60)
    };
    private static readonly SemaphoreSlim CacheGate = new(1, 1);
    private static readonly Dictionary<string, CatalogCacheEntry> Cache = new(StringComparer.Ordinal);

    private readonly HttpClient _httpClient;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Uri _catalogUri;
    private readonly string? _catalogCacheFilePath;

    private static readonly IReadOnlyDictionary<string, string[]> KnownProviderIdsByHost =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["api.openai.com"] = ["openai"],
            ["api.deepseek.com"] = ["deepseek"],
            ["dashscope.aliyuncs.com"] = ["alibaba", "alibaba-cn"],
            ["dashscope-intl.aliyuncs.com"] = ["alibaba"],
            ["open.bigmodel.cn"] = ["zhipuai", "zai"],
            ["api.moonshot.cn"] = ["moonshotai"],
            ["api.moonshot.ai"] = ["moonshotai"],
            ["ark.cn-beijing.volces.com"] = ["volcengine"],
            ["api.siliconflow.cn"] = ["siliconflow"],
            ["openrouter.ai"] = ["openrouter"],
            ["api.x.ai"] = ["xai"]
        };

    private static readonly IReadOnlyDictionary<string, string> CatalogVendorPrefixToProviderId =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["openai"] = "openai",
            ["anthropic"] = "anthropic",
            ["google"] = "google",
            ["deepseek"] = "deepseek",
            ["moonshotai"] = "moonshotai",
            ["qwen"] = "alibaba",
            ["x-ai"] = "xai",
            ["z-ai"] = "zai",
            ["mistralai"] = "mistral",
            ["meta-llama"] = "meta"
        };

    public AiModelCatalogEnrichmentService(
        HttpClient? httpClient = null,
        Func<DateTimeOffset>? utcNow = null,
        Uri? catalogUri = null,
        string? catalogCacheFilePath = null)
    {
        _httpClient = httpClient ?? SharedHttpClient;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _catalogUri = catalogUri ?? new Uri(CatalogUrl);
        _catalogCacheFilePath = catalogCacheFilePath ??
            (httpClient is null && catalogUri is null
                ? EraTransPaths.AiModelCatalogCacheFilePath
                : null);
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
            return models.Select(ApplyKnownReasoningHint).ToArray();
        }

        IReadOnlyList<string> providerIds = ResolveProviderIds(profile.BaseUrl, catalog)
            .Where(catalog.Providers.ContainsKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var globalMatches = new Dictionary<string, CatalogModel>(StringComparer.OrdinalIgnoreCase);
        var ambiguousModelIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (CatalogProvider provider in catalog.Providers.Values)
        {
            foreach (CatalogModel model in provider.Models.Values)
            {
                if (ambiguousModelIds.Contains(model.Id))
                    continue;

                if (globalMatches.Remove(model.Id))
                    ambiguousModelIds.Add(model.Id);
                else
                    globalMatches[model.Id] = model;
            }
        }

        return models.Select(model =>
        {
            CatalogModel? metadata = null;
            IReadOnlyList<string> candidates = GetProviderCandidates(providerIds, model.Id);
            foreach (string providerId in candidates)
            {
                if (catalog.Providers.TryGetValue(providerId, out CatalogProvider? provider) &&
                    TryFindModel(provider, model.Id, out metadata))
                    break;
            }

            if (metadata is null)
            {
                globalMatches.TryGetValue(model.Id, out metadata);
                if (metadata is null)
                {
                    int slash = model.Id.IndexOf('/');
                    if (slash > 0)
                        globalMatches.TryGetValue(model.Id[(slash + 1)..], out metadata);
                }
            }

            AiProviderModel enriched = metadata is null ? model : ApplyMetadata(model, metadata);
            return ApplyKnownReasoningHint(enriched);
        }).ToArray();
    }

    private async Task<CatalogSnapshot?> GetCatalogAsync(CancellationToken cancellationToken)
    {
        string cacheKey = _catalogUri.AbsoluteUri;
        await CacheGate.WaitAsync(cancellationToken);
        try
        {
            CatalogSnapshot? staleCatalog = null;
            if (Cache.TryGetValue(cacheKey, out CatalogCacheEntry? cached))
            {
                staleCatalog = cached.Catalog;
                if (_utcNow() < cached.ExpiresAt)
                    return cached.Catalog;
            }

            if (staleCatalog is null && !string.IsNullOrWhiteSpace(_catalogCacheFilePath))
            {
                try
                {
                    if (File.Exists(_catalogCacheFilePath))
                    {
                        string cachedJson = await File.ReadAllTextAsync(
                            _catalogCacheFilePath, cancellationToken);
                        staleCatalog = ParseCatalog(cachedJson);
                        Cache[cacheKey] = new CatalogCacheEntry(
                            staleCatalog, DateTimeOffset.MinValue);
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // A local cache read timeout is not important enough to block model selection.
                }
                catch (Exception ex) when (
                    ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
                {
                    // A corrupt/missing cache must not prevent a fresh catalog request.
                }
            }

            try
            {
                string json = await _httpClient.GetStringAsync(_catalogUri, cancellationToken);
                CatalogSnapshot loaded = ParseCatalog(json);
                Cache[cacheKey] = new CatalogCacheEntry(loaded, _utcNow().AddHours(24));
                SaveCatalogCache(json);
                return loaded;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return staleCatalog;
            }
            catch (HttpRequestException)
            {
                return staleCatalog;
            }
            catch (JsonException)
            {
                return staleCatalog;
            }
            catch (InvalidDataException)
            {
                return staleCatalog;
            }
        }
        finally
        {
            CacheGate.Release();
        }
    }

    private void SaveCatalogCache(string json)
    {
        if (string.IsNullOrWhiteSpace(_catalogCacheFilePath))
            return;

        string? directory = Path.GetDirectoryName(_catalogCacheFilePath);
        if (string.IsNullOrWhiteSpace(directory))
            return;

        string temporaryPath = _catalogCacheFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(temporaryPath, json, new System.Text.UTF8Encoding(false));
            File.Move(temporaryPath, _catalogCacheFilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The in-memory catalog is still usable when the local cache cannot be written.
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch (IOException)
            {
                // Best-effort cleanup of a temporary metadata cache file.
            }
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
                    SupportsTools = ReadBoolean(row, "tool_call") || ReadBoolean(row, "tool"),
                    SupportsReasoning = ReadBoolean(row, "reasoning") || ReadBoolean(row, "rsn"),
                    ReasoningSupport = ReadNullableBoolean(row, "reasoning")
                        ?? ReadNullableBoolean(row, "rsn"),
                    SupportsStructuredOutput = ReadBoolean(row, "structured_output") || ReadBoolean(row, "so"),
                    SupportsAttachments = ReadBoolean(row, "attachment"),
                    SupportsTemperature = ReadBoolean(row, "temperature"),
                    OpenWeights = ReadBoolean(row, "open_weights"),
                    Knowledge = ReadString(row, "knowledge"),
                    ReleaseDate = ReadString(row, "release_date"),
                    LastUpdated = ReadString(row, "last_updated"),
                    IsEmbedding = id.Contains("embedding", StringComparison.OrdinalIgnoreCase) ||
                                  id.Contains("embed-", StringComparison.OrdinalIgnoreCase),
                    InputModalities = ReadNestedStringArray(row, "modalities", "input", "in"),
                    OutputModalities = ReadNestedStringArray(row, "modalities", "output", "out"),
                    ContextLimit = ReadNestedInt(row, "limit", "context"),
                    InputLimit = ReadNestedInt(row, "limit", "input"),
                    OutputLimit = ReadNestedInt(row, "limit", "output"),
                    InputCostPerMillion = ReadNestedDouble(row, "cost", "input"),
                    OutputCostPerMillion = ReadNestedDouble(row, "cost", "output"),
                    ReasoningCostPerMillion = ReadNestedDouble(row, "cost", "reasoning"),
                    CacheReadCostPerMillion = ReadNestedDouble(row, "cost", "cache_read"),
                    CacheWriteCostPerMillion = ReadNestedDouble(row, "cost", "cache_write"),
                    InputAudioCostPerMillion = ReadNestedDouble(row, "cost", "input_audio"),
                    OutputAudioCostPerMillion = ReadNestedDouble(row, "cost", "output_audio")
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
            host = baseUri.Host;

        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(host))
        {
            if (KnownProviderIdsByHost.TryGetValue(host, out string[]? knownIds))
            {
                foreach (string id in knownIds)
                    matched.Add(id);
            }

            foreach (CatalogProvider provider in catalog.Providers.Values)
            {
                if (Uri.TryCreate(provider.Api, UriKind.Absolute, out Uri? providerUri) &&
                    string.Equals(providerUri.Host, host, StringComparison.OrdinalIgnoreCase))
                    matched.Add(provider.Id);
            }
        }

        return matched
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool TryFindModel(
        CatalogProvider provider,
        string requestedId,
        out CatalogModel? model)
    {
        if (provider.Models.TryGetValue(requestedId, out model))
            return true;

        int slash = requestedId.IndexOf('/');
        if (slash > 0 &&
            provider.Models.TryGetValue(requestedId[(slash + 1)..], out model))
            return true;

        string normalized = NormalizeModelId(requestedId);
        CatalogModel[] matches = provider.Models.Values
            .Where(candidate => string.Equals(
                NormalizeModelId(candidate.Id), normalized, StringComparison.Ordinal))
            .Take(2)
            .ToArray();

        if (matches.Length == 1)
        {
            model = matches[0];
            return true;
        }

        model = null;
        return false;
    }

    private static string NormalizeModelId(string value) =>
        new(value.Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());

    private static IReadOnlyList<string> GetProviderCandidates(
        IReadOnlyList<string> hostProviderIds,
        string modelId)
    {
        int slash = modelId.IndexOf('/');
        if (slash > 0 &&
            CatalogVendorPrefixToProviderId.TryGetValue(
                modelId[..slash], out string? mappedProvider))
        {
            return [mappedProvider, .. hostProviderIds.Where(id =>
                !string.Equals(id, mappedProvider, StringComparison.OrdinalIgnoreCase))];
        }

        return hostProviderIds;
    }

    private static AiProviderModel ApplyKnownReasoningHint(AiProviderModel model)
    {
        // Follow Kelivo's conservative model-id defaults only when neither the provider
        // nor the public catalog supplied an explicit true/false capability.
        if (model.ReasoningSupport is not null || model.SupportsReasoning)
            return model;

        string id = model.Id.Trim().ToLowerInvariant();
        int slash = id.LastIndexOf('/');
        if (slash >= 0)
            id = id[(slash + 1)..];

        bool supportsReasoning =
            id.StartsWith("o1", StringComparison.Ordinal) ||
            id.StartsWith("o3", StringComparison.Ordinal) ||
            id.StartsWith("o4", StringComparison.Ordinal) ||
            id.StartsWith("gpt-5", StringComparison.Ordinal) ||
            id.StartsWith("gpt-6", StringComparison.Ordinal) ||
            id.StartsWith("deepseek-reasoner", StringComparison.Ordinal) ||
            id.StartsWith("deepseek-r1", StringComparison.Ordinal) ||
            id.StartsWith("deepseek-v3.1", StringComparison.Ordinal) ||
            id.StartsWith("deepseek-v3.2", StringComparison.Ordinal) ||
            id.StartsWith("deepseek-v4", StringComparison.Ordinal) ||
            id.StartsWith("qwq", StringComparison.Ordinal) ||
            id.StartsWith("qwen3", StringComparison.Ordinal) ||
            id.StartsWith("qwen-3", StringComparison.Ordinal) ||
            id.StartsWith("kimi-k2-thinking", StringComparison.Ordinal) ||
            id.StartsWith("kimi-k2.5", StringComparison.Ordinal) ||
            id.StartsWith("kimi-k2.6", StringComparison.Ordinal) ||
            id.StartsWith("kimi-k2.7", StringComparison.Ordinal) ||
            id.StartsWith("kimi-k3", StringComparison.Ordinal) ||
            id.StartsWith("grok-4", StringComparison.Ordinal) ||
            id.StartsWith("gemini-2.5", StringComparison.Ordinal) ||
            id.StartsWith("gemini-3", StringComparison.Ordinal) ||
            id.StartsWith("claude-3-7", StringComparison.Ordinal) ||
            id.StartsWith("claude-3.7", StringComparison.Ordinal) ||
            id.StartsWith("claude-sonnet-4", StringComparison.Ordinal) ||
            id.StartsWith("claude-opus-4", StringComparison.Ordinal) ||
            id.StartsWith("claude-haiku-4", StringComparison.Ordinal);

        if (supportsReasoning)
        {
            model.ReasoningSupport = true;
            model.SupportsReasoning = true;
        }

        return model;
    }

    private static AiProviderModel ApplyMetadata(AiProviderModel source, CatalogModel metadata)
    {
        var inputModalities = metadata.InputModalities.Count > 0
            ? metadata.InputModalities.ToList()
            : source.InputModalities.ToList();
        var outputModalities = metadata.OutputModalities.Count > 0
            ? metadata.OutputModalities.ToList()
            : source.OutputModalities.ToList();

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
            SupportsTools = metadata.SupportsTools || source.SupportsTools,
            SupportsReasoning = source.ReasoningSupport
                ?? metadata.ReasoningSupport
                ?? (source.SupportsReasoning || metadata.SupportsReasoning),
            ReasoningSupport = source.ReasoningSupport ?? metadata.ReasoningSupport,
            SupportsStructuredOutput = metadata.SupportsStructuredOutput || source.SupportsStructuredOutput,
            SupportsAttachments = metadata.SupportsAttachments || source.SupportsAttachments,
            SupportsTemperature = metadata.SupportsTemperature || source.SupportsTemperature,
            OpenWeights = metadata.OpenWeights || source.OpenWeights,
            Knowledge = metadata.Knowledge ?? source.Knowledge,
            ReleaseDate = metadata.ReleaseDate ?? source.ReleaseDate,
            LastUpdated = metadata.LastUpdated ?? source.LastUpdated,
            InputModalities = inputModalities,
            OutputModalities = outputModalities,
            ContextLimit = source.ContextLimit ?? metadata.ContextLimit,
            InputLimit = source.InputLimit ?? metadata.InputLimit,
            OutputLimit = source.OutputLimit ?? metadata.OutputLimit,
            InputCostPerMillion = metadata.InputCostPerMillion ?? source.InputCostPerMillion,
            OutputCostPerMillion = metadata.OutputCostPerMillion ?? source.OutputCostPerMillion,
            ReasoningCostPerMillion = metadata.ReasoningCostPerMillion ?? source.ReasoningCostPerMillion,
            CacheReadCostPerMillion = metadata.CacheReadCostPerMillion ?? source.CacheReadCostPerMillion,
            CacheWriteCostPerMillion = metadata.CacheWriteCostPerMillion ?? source.CacheWriteCostPerMillion,
            InputAudioCostPerMillion = metadata.InputAudioCostPerMillion ?? source.InputAudioCostPerMillion,
            OutputAudioCostPerMillion = metadata.OutputAudioCostPerMillion ?? source.OutputAudioCostPerMillion
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

    private static bool? ReadNullableBoolean(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out JsonElement value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

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

    private static List<string> ReadNestedStringArray(
        JsonElement element,
        string objectName,
        string propertyName,
        string legacyPropertyName)
    {
        if (TryGetProperty(element, objectName, out JsonElement nested) &&
            nested.ValueKind == JsonValueKind.Object)
        {
            return ReadStringArray(nested, propertyName);
        }

        return ReadStringArray(element, legacyPropertyName);
    }

    private static double? ReadNestedDouble(JsonElement element, string objectName, string propertyName)
    {
        if (!TryGetProperty(element, objectName, out JsonElement nested) ||
            nested.ValueKind != JsonValueKind.Object ||
            !TryGetProperty(nested, propertyName, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out double result))
        {
            return null;
        }

        return result;
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
        public bool? ReasoningSupport { get; init; }
        public bool SupportsStructuredOutput { get; init; }
        public bool SupportsAttachments { get; init; }
        public bool SupportsTemperature { get; init; }
        public bool OpenWeights { get; init; }
        public string? Knowledge { get; init; }
        public string? ReleaseDate { get; init; }
        public string? LastUpdated { get; init; }
        public bool IsEmbedding { get; init; }
        public List<string> InputModalities { get; init; } = [];
        public List<string> OutputModalities { get; init; } = [];
        public int? ContextLimit { get; init; }
        public int? InputLimit { get; init; }
        public int? OutputLimit { get; init; }
        public double? InputCostPerMillion { get; init; }
        public double? OutputCostPerMillion { get; init; }
        public double? ReasoningCostPerMillion { get; init; }
        public double? CacheReadCostPerMillion { get; init; }
        public double? CacheWriteCostPerMillion { get; init; }
        public double? InputAudioCostPerMillion { get; init; }
        public double? OutputAudioCostPerMillion { get; init; }
    }
}
