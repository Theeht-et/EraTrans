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
            ["api.anthropic.com"] = ["anthropic"],
            ["generativelanguage.googleapis.com"] = ["google"],
            ["api.x.ai"] = ["xai"],
            ["api.mistral.ai"] = ["mistral"],
            ["api.groq.com"] = ["groq"],
            ["api.cerebras.ai"] = ["cerebras"],
            ["api.cohere.com"] = ["cohere"],
            ["api.cohere.ai"] = ["cohere"],
            ["api.deepinfra.com"] = ["deepinfra"],
            ["api.together.xyz"] = ["togetherai"],
            ["api.perplexity.ai"] = ["perplexity"],
            ["aihubmix.com"] = ["aihubmix"],
            ["api.venice.ai"] = ["venice"],
            ["ai-gateway.vercel.sh"] = ["vercel"],
            ["gateway.ai.cloudflare.com"] = ["cloudflare-ai-gateway"],
            ["api.v0.dev"] = ["v0"],
            ["gitlab.com"] = ["gitlab"],
            ["cloud.gitlab.com"] = ["gitlab"],
            ["ai.salad.cloud"] = ["salad-cloud"],
            ["api.deepseek.com"] = ["deepseek"],
            ["dashscope.aliyuncs.com"] = ["alibaba", "alibaba-cn"],
            ["dashscope-intl.aliyuncs.com"] = ["alibaba"],
            ["open.bigmodel.cn"] = ["zhipuai", "zai"],
            ["api.moonshot.cn"] = ["moonshotai"],
            ["api.moonshot.ai"] = ["moonshotai"],
            ["ark.cn-beijing.volces.com"] = ["volcengine"],
            ["api.siliconflow.cn"] = ["siliconflow"],
            ["openrouter.ai"] = ["openrouter"]
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
                string normalizedId = NormalizeModelId(model.Id);
                if (ambiguousModelIds.Contains(normalizedId))
                    continue;

                if (globalMatches.Remove(normalizedId))
                    ambiguousModelIds.Add(normalizedId);
                else
                    globalMatches[normalizedId] = model;
            }
        }

        return models.Select(model =>
        {
            CatalogModel? metadata = null;
            (string lookupId, int? contextHint) = StripContextWindowSuffix(model.Id);
            IReadOnlyList<string> candidates = GetProviderCandidates(providerIds, lookupId);
            foreach (string providerId in candidates)
            {
                if (catalog.Providers.TryGetValue(providerId, out CatalogProvider? provider) &&
                    TryFindModel(provider, lookupId, out metadata))
                    break;
            }

            if (metadata is null)
            {
                globalMatches.TryGetValue(NormalizeModelId(lookupId), out metadata);
                if (metadata is null)
                {
                    int slash = lookupId.IndexOf('/');
                    if (slash > 0)
                        globalMatches.TryGetValue(
                            NormalizeModelId(lookupId[(slash + 1)..]), out metadata);
                }
            }

            if (metadata is null && contextHint is null)
                return ApplyKnownReasoningHint(model);

            metadata ??= new CatalogModel { Id = lookupId, Name = model.DisplayName };
            AiProviderModel enriched = ApplyMetadata(model, metadata, contextHint);
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
                        DateTimeOffset cacheWrittenAt = new(
                            File.GetLastWriteTimeUtc(_catalogCacheFilePath),
                            TimeSpan.Zero);
                        DateTimeOffset expiresAt = cacheWrittenAt.AddHours(24);
                        Cache[cacheKey] = new CatalogCacheEntry(staleCatalog, expiresAt);
                        if (_utcNow() < expiresAt)
                            return staleCatalog;
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
                    ReasoningLevels = ReadCatalogReasoningLevels(row),
                    ReasoningSupport = ReadNullableBoolean(row, "reasoning")
                        ?? ReadNullableBoolean(row, "rsn")
                        ?? (HasCatalogReasoningOptions(row) ? true : null),
                    SupportsReasoning = ReadNullableBoolean(row, "reasoning")
                        ?? ReadNullableBoolean(row, "rsn")
                        ?? HasCatalogReasoningOptions(row),
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

    private static List<string> ReadCatalogReasoningLevels(JsonElement row)
    {
        var levels = new List<string>();
        foreach (string propertyName in new[] { "reasoning_options", "reasoningOptions", "ro" })
        {
            if (!TryGetProperty(row, propertyName, out JsonElement options) ||
                options.ValueKind != JsonValueKind.Array)
                continue;

            foreach (JsonElement option in options.EnumerateArray())
            {
                if (option.ValueKind != JsonValueKind.Object ||
                    !string.Equals(ReadString(option, "type"), "effort", StringComparison.OrdinalIgnoreCase) ||
                    !TryGetProperty(option, "values", out JsonElement values) ||
                    values.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (JsonElement value in values.EnumerateArray())
                {
                    if (value.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(value.GetString()))
                        levels.Add(value.GetString()!);
                }
            }
        }

        return levels
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool HasCatalogReasoningOptions(JsonElement row)
    {
        foreach (string propertyName in new[] { "reasoning_options", "reasoningOptions", "ro" })
        {
            if (!TryGetProperty(row, propertyName, out JsonElement options) ||
                options.ValueKind != JsonValueKind.Array)
                continue;

            foreach (JsonElement option in options.EnumerateArray())
            {
                if (option.ValueKind != JsonValueKind.Object)
                    continue;
                string? type = ReadString(option, "type");
                if (type is not null &&
                    (type.Equals("effort", StringComparison.OrdinalIgnoreCase) ||
                     type.Equals("budget_tokens", StringComparison.OrdinalIgnoreCase) ||
                     type.Equals("toggle", StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
        }

        return false;
    }

    private static (string ModelId, int? ContextWindow) StripContextWindowSuffix(string modelId)
    {
        string value = modelId.Trim();
        int slash = value.LastIndexOf('/');
        int prefixLength = slash + 1;
        int separator = value.LastIndexOfAny(['-', '_', '.']);
        if (separator < prefixLength || separator == value.Length - 1)
            return (value, null);

        string suffix = value[(separator + 1)..];
        if (suffix.Length < 2)
            return (value, null);

        char unit = char.ToLowerInvariant(suffix[^1]);
        if (unit is not ('k' or 'm'))
            return (value, null);

        if (!double.TryParse(
                suffix[..^1],
                System.Globalization.NumberStyles.AllowDecimalPoint,
                System.Globalization.CultureInfo.InvariantCulture,
                out double amount) ||
            amount <= 0)
            return (value, null);

        double tokens = amount * (unit == 'k' ? 1_000 : 1_000_000);
        if (tokens < 1_024 || tokens > 10_000_000 || tokens != Math.Truncate(tokens))
            return (value, null);

        return (value[..separator], (int)tokens);
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
        // Match Kelivo's order: explicit provider/catalog metadata is authoritative;
        // infer only well-known reasoning families. Everything else stays unknown.
        if (model.ReasoningSupport is not null || model.SupportsReasoning)
            return model;

        string id = model.Id.Trim().ToLowerInvariant();
        int slash = id.LastIndexOf('/');
        if (slash >= 0)
            id = id[(slash + 1)..];

        bool supportsReasoning =
            System.Text.RegularExpressions.Regex.IsMatch(
                id, @"(^|[/_:@])(?:o[134]|gpt-[56](?:[.-][a-z0-9]+)?)(?:$|[-._:@])") ||
            System.Text.RegularExpressions.Regex.IsMatch(
                id, @"(^|[/_:@])deepseek-(?:reasoner|r1|v3\.1|v3\.2|v4)(?:$|[-._:@])") ||
            System.Text.RegularExpressions.Regex.IsMatch(
                id, @"(^|[/_:@])(?:qwq|qwen-?3)(?:$|[-._:@])") ||
            System.Text.RegularExpressions.Regex.IsMatch(
                id, @"(^|[/_:@])kimi-k2-thinking(?:$|[-._:@])") ||
            System.Text.RegularExpressions.Regex.IsMatch(
                id, @"(^|[/_:@])kimi-k2\.[567](?:$|[-._:@])") ||
            System.Text.RegularExpressions.Regex.IsMatch(
                id, @"(^|[/_:@])kimi-k3(?:$|[-._:@])") ||
            System.Text.RegularExpressions.Regex.IsMatch(
                id, @"(^|[/_:@])grok-4\.[567](?:$|[-._:@])") ||
            System.Text.RegularExpressions.Regex.IsMatch(
                id, @"(^|[/_:@])gemini-(?:2\.5|3|4)(?:$|[-._:@])") ||
            System.Text.RegularExpressions.Regex.IsMatch(
                id, @"(^|[/_:@])claude-(?:(?:3[-.]7)|(?:opus|sonnet|haiku|fable|mythos)-[45])(?:$|[-._:@])") ||
            System.Text.RegularExpressions.Regex.IsMatch(
                id, @"(^|[/_:@])glm-(?:4[.-][567]|5)(?:$|[-._:@])") ||
            System.Text.RegularExpressions.Regex.IsMatch(
                id, @"(^|[/_:@])(?:minimax-m[23]|mimo-v2|step-3|intern-s1|laguna)(?:$|[-._:@])") ||
            id.Contains("doubao", StringComparison.Ordinal) &&
                (id.Contains("seed-", StringComparison.Ordinal) ||
                 id.Contains("thinking", StringComparison.Ordinal));

        if (supportsReasoning)
        {
            model.ReasoningSupport = true;
            model.SupportsReasoning = true;
        }

        return model;
    }

    private static AiProviderModel ApplyMetadata(
        AiProviderModel source,
        CatalogModel metadata,
        int? contextHint = null)
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
            ReasoningLevels = source.ReasoningLevels.Count > 0
                ? source.ReasoningLevels.ToList()
                : metadata.ReasoningLevels.ToList(),
            SupportsStructuredOutput = metadata.SupportsStructuredOutput || source.SupportsStructuredOutput,
            SupportsAttachments = metadata.SupportsAttachments || source.SupportsAttachments,
            SupportsTemperature = metadata.SupportsTemperature || source.SupportsTemperature,
            OpenWeights = metadata.OpenWeights || source.OpenWeights,
            Knowledge = metadata.Knowledge ?? source.Knowledge,
            ReleaseDate = metadata.ReleaseDate ?? source.ReleaseDate,
            LastUpdated = metadata.LastUpdated ?? source.LastUpdated,
            InputModalities = inputModalities,
            OutputModalities = outputModalities,
            ContextLimit = source.ContextLimit ?? metadata.ContextLimit ?? contextHint,
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
        public List<string> ReasoningLevels { get; init; } = [];
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
