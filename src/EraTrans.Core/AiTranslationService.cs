using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace EraTrans.Core;

public sealed record AiTranslationRequest(int Id, string Key, string Original, string Context);

public sealed record AiTranslationResult(int Id, string Translation);

public sealed record AiTranslationContextEntry(string Key, string Original);

public sealed record AiTranslationSemanticContextGroup(
    string GroupId,
    string GroupType,
    string FunctionId,
    string BranchPath,
    string Relationship,
    IReadOnlyList<AiTranslationContextEntry> Entries)
{
    public IReadOnlyList<int> TargetIds { get; init; } = [];
}

/// <summary>
/// Result of one window request. A partially answered window is a success: the
/// caller retries only the missing ids instead of resending the whole window.
/// </summary>
public sealed record WindowTranslationOutcome(
    IReadOnlyList<AiTranslationResult> Translations,
    IReadOnlyList<int> MissingIds,
    IReadOnlyList<int> UnknownIds)
{
    public bool IsComplete => MissingIds.Count == 0;
}

public interface IAiChatCompletionClient
{
    Task<string> CompleteAsync(
        AiProviderProfile profile,
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reasoning is optional: implementations that do not care about it keep
    /// working through this default forwarder.
    /// </summary>
    Task<string> CompleteAsync(
        AiProviderProfile profile,
        string systemPrompt,
        string userPrompt,
        AiReasoningOptions? reasoning,
        CancellationToken cancellationToken = default) =>
        CompleteAsync(profile, systemPrompt, userPrompt, cancellationToken);
}

/// <summary>
/// Generates translations in indexed JSON batches so every model result can be
/// mapped back to the exact dictionary entry without relying on response order.
/// </summary>
public sealed class AiTranslationService
{
    private const string SystemPrompt = """
        你是 ERA 游戏文本本地化翻译助手。请将提供的游戏文本翻译为简体中文。
        保留原文中的变量占位符、控制代码、转义序列、格式标记、数值和专有标识，不要翻译 key。
        结合 context 理解语境；没有上下文时按游戏对白或界面文本自然翻译。
        只返回 JSON 数组，每项必须包含原样的整数 id 和字符串 translation。
        不要添加 Markdown 代码围栏、解释、前言或数组之外的内容。不能遗漏或重复任何 id。
        """;

    private const string SemanticSystemPrompt = """
        你是 ERA 游戏文本本地化翻译助手。请将 targets 中指定的游戏文本翻译为简体中文。
        保留原文中的变量占位符、控制代码、转义序列、格式标记、数值和专有标识。
        entryContexts 中的 targetIds 指明对应上下文适用于哪些目标；没有对应项时不要自行补充上下文。
        同一请求中的 targets 应作为一个整体阅读和翻译；如果它们属于连续对白或同一段文本，应保持人物称呼、代词、语气、术语和前后句表达一致，不要把每条译文当作互不相关的孤立句子。
        semanticContext 是只读参考，不是翻译目标。每个上下文项的 targetIds 明确限定它适用的目标 id；只能用于这些目标，绝不能扩展到同一请求中的其他目标。same-group 表示同一文本片段，menu-prompt 表示菜单提示，alternative-branch 表示互斥的条件分支，只能作为场景参考，不能当成连续发生的对白。groupType 用于补充说明参考文本的类别。
        只能翻译 targets 中的词条，只返回 targets 对应的 id。
        只返回 JSON 数组，每项必须包含原样的整数 id 和字符串 translation。
        不要添加 Markdown 代码围栏、解释、前言或数组之外的内容。不能遗漏或重复任何 id。
        """;

    /// <summary>
    /// Prompt for one sequential window: the model sees a contiguous slice of the
    /// script in source order, so it can read the passage as a whole before
    /// translating line by line.
    /// </summary>
    private const string WindowSystemPrompt = """
        你是 ERA 游戏文本本地化翻译助手。请把 lines 中 do 为 1 的行翻译成简体中文。

        1. 只返回一个 JSON 对象，键是 id，值是译文，例如 {"12":"译文"}。不要代码块、不要解释、不要输出 do 为 0 的行。
        2. 相邻且 join 相同的行属于同一句话：先整段理解，再逐行输出。不要让半句各自成句，拼起来要通顺，不要重复主语。
        3. alt 相同的行是互斥分支：不要把它们的译文接成连续对话，但人名、称呼、术语要与本请求其他行保持一致。
        4. 保留原文的变量占位符（如 %CALLNAME:MASTER%）、行内条件（\@ 条件 ? A # B \@）、控制码、标点和换行结构；无法翻译时原样返回原文。

        part 大于 1 表示这是同一函数前一段的延续，人称和译法沿用它。
        同一请求内，同一人名、称呼和术语必须使用同一译法。
        """;

    private static readonly JsonSerializerOptions PromptJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly IAiChatCompletionClient _client;
    private readonly IAiTranslationLogStore? _logStore;

    public AiTranslationService(
        IAiChatCompletionClient? client = null,
        IAiTranslationLogStore? logStore = null)
    {
        _client = client ?? new OpenAiCompatibleChatCompletionClient();
        _logStore = logStore;
    }

    public Task<IReadOnlyList<AiTranslationResult>> TranslateAsync(
        AiProviderProfile profile,
        IReadOnlyList<AiTranslationRequest> requests,
        CancellationToken cancellationToken = default) =>
        TranslateAsync(profile, requests, reasoning: null, cancellationToken);

    public Task<IReadOnlyList<AiTranslationResult>> TranslateAsync(
        AiProviderProfile profile,
        IReadOnlyList<AiTranslationRequest> requests,
        AiReasoningOptions? reasoning,
        CancellationToken cancellationToken = default) =>
        TranslateCoreAsync(profile, requests, null, reasoning, cancellationToken);

    public Task<IReadOnlyList<AiTranslationResult>> TranslateWithSemanticContextAsync(
        AiProviderProfile profile,
        IReadOnlyList<AiTranslationRequest> requests,
        IReadOnlyList<AiTranslationSemanticContextGroup> semanticContextGroups,
        CancellationToken cancellationToken = default) =>
        TranslateWithSemanticContextAsync(
            profile, requests, semanticContextGroups, reasoning: null, cancellationToken);

    public Task<IReadOnlyList<AiTranslationResult>> TranslateWithSemanticContextAsync(
        AiProviderProfile profile,
        IReadOnlyList<AiTranslationRequest> requests,
        IReadOnlyList<AiTranslationSemanticContextGroup> semanticContextGroups,
        AiReasoningOptions? reasoning,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(semanticContextGroups);
        return TranslateCoreAsync(profile, requests, semanticContextGroups, reasoning, cancellationToken);
    }

    /// <summary>
    /// Translates one sequential window. Lines marked as reference are sent as
    /// context only; the answer is a flat id-to-text object.
    /// </summary>
    public async Task<WindowTranslationOutcome> TranslateWindowAsync(
        AiProviderProfile profile,
        TranslationWindow window,
        AiReasoningOptions? reasoning = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(window);
        AiProviderProfileValidator.Validate(profile);

        if (window.TargetCount == 0)
            return new WindowTranslationOutcome([], [], []);

        string userPrompt = BuildWindowPayload(window);
        return await CompleteWithReasoningFallbackAsync(
            profile,
            WindowSystemPrompt,
            userPrompt,
            requestKind: "Translation",
            requestedEntryCount: window.TargetCount,
            processResponse: response => ParseWindowResponse(response, window),
            reasoning,
            cancellationToken);
    }

    private async Task<IReadOnlyList<AiTranslationResult>> TranslateCoreAsync(
        AiProviderProfile profile,
        IReadOnlyList<AiTranslationRequest> requests,
        IReadOnlyList<AiTranslationSemanticContextGroup>? semanticContextGroups,
        AiReasoningOptions? reasoning,
        CancellationToken cancellationToken)
    {
        AiProviderProfileValidator.Validate(profile);
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0)
            return [];

        if (requests.Select(item => item.Id).Distinct().Count() != requests.Count)
            throw new ArgumentException("翻译请求中的 id 不能重复。", nameof(requests));

        if (requests.Any(item => item.Key.StartsWith("变量", StringComparison.Ordinal)))
            throw new InvalidOperationException("变量类词条不能提交给 AI 翻译。");

        object payload = semanticContextGroups is null
            ? requests.Select(item => new
            {
                id = item.Id,
                key = item.Key,
                original = item.Original,
                context = item.Context
            }).ToArray()
            : BuildSemanticPayload(requests, semanticContextGroups);

        string userPrompt = JsonSerializer.Serialize(payload, PromptJsonOptions);
        string systemPrompt = semanticContextGroups is null ? SystemPrompt : SemanticSystemPrompt;
        return await CompleteWithReasoningFallbackAsync(
            profile,
            systemPrompt,
            userPrompt,
            requestKind: "Translation",
            requestedEntryCount: requests.Count,
            processResponse: response => ParseResponse(response, requests),
            reasoning,
            cancellationToken);
    }

    /// <summary>
    /// A rejected reasoning field fails the entire request, while the identical
    /// request without reasoning usually succeeds. Retry once without it instead
    /// of asking the user to understand provider dialects. Each attempt keeps its
    /// own log entry, so the rejected request stays available for diagnosis.
    /// </summary>
    private async Task<T> CompleteWithReasoningFallbackAsync<T>(
        AiProviderProfile profile,
        string systemPrompt,
        string userPrompt,
        string requestKind,
        int requestedEntryCount,
        Func<string, T> processResponse,
        AiReasoningOptions? reasoning,
        CancellationToken cancellationToken)
    {
        // Callers that do not pass a choice (the dictionary editor relies on the
        // saved per-provider setting) still need the effective value here, or the
        // retry decision could never see that reasoning was active.
        reasoning ??= AiReasoningSettingsStore.ResolveForProvider(profile.Id);

        try
        {
            return await CompleteWithLogAsync(
                profile, systemPrompt, userPrompt, requestKind, requestedEntryCount,
                processResponse, reasoning, cancellationToken);
        }
        catch (Exception ex) when (AiReasoningFallback.IsRejection(ex, reasoning))
        {
            return await CompleteWithLogAsync(
                profile, systemPrompt, userPrompt, requestKind, requestedEntryCount,
                processResponse, AiReasoningOptions.Auto, cancellationToken);
        }
    }

    private async Task<T> CompleteWithLogAsync<T>(
        AiProviderProfile profile,
        string systemPrompt,
        string userPrompt,
        string requestKind,
        int requestedEntryCount,
        Func<string, T> processResponse,
        AiReasoningOptions? reasoning,
        CancellationToken cancellationToken)
    {
        AiTranslationLogEntry? log = null;
        string apiKey = profile.ApiKey.Trim();

        if (_logStore is not null)
        {
            AiProviderHttpRequestDetails requestDetails =
                AiProviderHttpRequestFactory.Create(profile, systemPrompt, userPrompt, reasoning);
            string endpoint = requestDetails.Endpoint;
            using JsonDocument requestDocument = JsonDocument.Parse(requestDetails.JsonBody);
            string requestJson = JsonSerializer.Serialize(
                requestDocument.RootElement,
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });

            log = new AiTranslationLogEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                StartedAtUtc = DateTimeOffset.UtcNow,
                RequestKind = requestKind,
                ProviderName = RedactSecret(profile.Name, apiKey),
                Protocol = "OpenAI-compatible",
                RequestMethod = requestDetails.Method,
                Endpoint = endpoint,
                Model = RedactSecret(profile.Model, apiKey),
                RequestedEntryCount = requestedEntryCount,
                SystemPrompt = RedactSecret(systemPrompt, apiKey),
                UserPrompt = RedactSecret(userPrompt, apiKey),
                RequestJson = RedactSecret(requestJson, apiKey),
                Status = AiTranslationLogStatus.InProgress
            };

            try
            {
                // Persist before sending so interrupted requests still have a durable record.
                _logStore.Save(log);
            }
            catch (Exception ex)
            {
                throw new IOException(
                    "无法保存 AI 请求记录，因此本次请求未发送。请检查 Config/ai-translation-logs 目录的写入权限和磁盘空间。",
                    ex);
            }
        }

        Stopwatch? stopwatch = log is null ? null : Stopwatch.StartNew();
        try
        {
            string response = await _client.CompleteAsync(
                profile,
                systemPrompt,
                userPrompt,
                reasoning,
                cancellationToken);

            if (log is not null)
                log.RawResponse = RedactSecret(response, apiKey);

            T result = processResponse(response);
            if (log is not null)
                log.Status = AiTranslationLogStatus.Succeeded;

            return result;
        }
        catch (OperationCanceledException ex)
        {
            if (log is not null)
            {
                log.Status = AiTranslationLogStatus.Cancelled;
                log.ErrorType = ex.GetType().FullName;
                log.ErrorMessage = RedactSecret(ex.Message, apiKey);
                log.ErrorDetails = RedactSecret(ex.ToString(), apiKey);
            }

            throw;
        }
        catch (Exception ex)
        {
            if (log is not null)
            {
                log.Status = AiTranslationLogStatus.Failed;
                log.ErrorType = ex.GetType().FullName;
                log.ErrorMessage = RedactSecret(ex.Message, apiKey);
                log.ErrorDetails = RedactSecret(ex.ToString(), apiKey);
            }

            throw;
        }
        finally
        {
            if (log is not null)
            {
                stopwatch!.Stop();
                log.CompletedAtUtc = DateTimeOffset.UtcNow;
                log.DurationMilliseconds = stopwatch.ElapsedMilliseconds;
                try
                {
                    _logStore!.Save(log);
                }
                catch
                {
                    // Do not turn a successful model reply into a translation failure.
                }
            }
        }
    }

    private static string RedactSecret(string value, string secret)
    {
        if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(secret))
            return value;

        return value.Replace(secret, "[已隐藏]", StringComparison.Ordinal);
    }

    /// <summary>
    /// One window is sent as a line-ordered list: the model reads the passage in
    /// reading order, and the flags tell it which lines to translate, which belong
    /// to the same sentence, and which are mutually exclusive branches.
    /// </summary>
    public static string BuildWindowPayload(TranslationWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        var lines = new List<Dictionary<string, object?>>(window.Lines.Count);
        foreach (WindowLine line in window.Lines)
        {
            var item = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["fn"] = line.FunctionId,
                ["id"] = line.Id,
                ["text"] = line.Text,
                ["do"] = line.Translate ? 1 : 0
            };

            if (line.Join > 0)
                item["join"] = line.Join;
            if (line.AltGroup is not null)
                item["alt"] = line.AltGroup;
            if (line.InlineCondition)
                item["cond"] = 1;
            if (line.Reference)
                item["ref"] = 1;

            lines.Add(item);
        }

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (window.Part > 1)
            payload["part"] = window.Part;
        payload["lines"] = lines;

        return JsonSerializer.Serialize(payload, PromptJsonOptions);
    }

    /// <summary>
    /// Parses the flat id-to-text answer. Missing ids are reported instead of
    /// failing the whole window, and the older array shape is still accepted so a
    /// model that ignores the instructions does not waste the request.
    /// </summary>
    public static WindowTranslationOutcome ParseWindowResponse(
        string response,
        TranslationWindow window)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(response);
        ArgumentNullException.ThrowIfNull(window);

        HashSet<int> expected = window.Lines
            .Where(line => line.Translate)
            .Select(line => line.Id)
            .ToHashSet();
        var parsed = new Dictionary<int, string>();

        string json = ExtractJsonRegion(response);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("模型返回的 JSON 无法解析：" + ex.Message, ex);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in root.EnumerateObject())
                {
                    if (!int.TryParse(property.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
                        continue;

                    string? text = ReadTranslationText(property.Value);
                    if (!string.IsNullOrWhiteSpace(text))
                        parsed[id] = text;
                }
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in root.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object ||
                        !item.TryGetProperty("id", out JsonElement idElement) ||
                        !idElement.TryGetInt32(out int id) ||
                        !item.TryGetProperty("translation", out JsonElement translation))
                    {
                        continue;
                    }

                    string? text = ReadTranslationText(translation);
                    if (!string.IsNullOrWhiteSpace(text))
                        parsed[id] = text;
                }
            }
            else
            {
                throw new InvalidDataException("模型返回内容必须是 JSON 对象或数组。");
            }
        }

        if (parsed.Count == 0)
            throw new InvalidDataException("模型没有返回任何可用的译文。");

        int[] unknown = parsed.Keys.Where(id => !expected.Contains(id)).Order().ToArray();
        int[] missing = expected.Where(id => !parsed.ContainsKey(id)).Order().ToArray();

        var translations = window.Lines
            .Where(line => line.Translate && parsed.ContainsKey(line.Id))
            .Select(line => new AiTranslationResult(line.Id, parsed[line.Id]))
            .ToArray();

        return new WindowTranslationOutcome(translations, missing, unknown);
    }

    private static string? ReadTranslationText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Array => string.Concat(value.EnumerateArray()
            .Where(part => part.ValueKind == JsonValueKind.String)
            .Select(part => part.GetString())),
        _ => null
    };

    /// <summary>Accepts answers wrapped in a code fence or surrounded by prose.</summary>
    private static string ExtractJsonRegion(string response)
    {
        string trimmed = response.Trim();
        int objectStart = trimmed.IndexOf('{');
        int arrayStart = trimmed.IndexOf('[');
        bool objectFirst = objectStart >= 0 && (arrayStart < 0 || objectStart < arrayStart);
        int start = objectFirst ? objectStart : arrayStart;
        int end = objectFirst ? trimmed.LastIndexOf('}') : trimmed.LastIndexOf(']');

        if (start < 0 || end < start)
            throw new InvalidDataException("模型返回内容中没有 JSON 内容。");

        return trimmed[start..(end + 1)];
    }

    private static Dictionary<string, object> BuildSemanticPayload(
        IReadOnlyList<AiTranslationRequest> requests,
        IReadOnlyList<AiTranslationSemanticContextGroup> semanticContextGroups)
    {
        var payload = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["targets"] = requests.Select(item => new
            {
                id = item.Id,
                original = item.Original
            }).ToArray()
        };

        var entryContexts = requests
            .Where(item =>
                !string.IsNullOrWhiteSpace(item.Context) &&
                !string.Equals(item.Context, item.Original, StringComparison.Ordinal))
            .GroupBy(item => item.Context, StringComparer.Ordinal)
            .Select(group => new
            {
                targetIds = group.Select(item => item.Id).ToArray(),
                text = group.Key
            })
            .ToArray();

        if (entryContexts.Length > 0)
        {
            payload["entryContexts"] = entryContexts;
        }

        var requestIds = requests.Select(request => request.Id).ToHashSet();
        foreach (AiTranslationSemanticContextGroup group in semanticContextGroups)
        {
            if (group.TargetIds.Count == 0 || group.TargetIds.Any(id => !requestIds.Contains(id)))
            {
                throw new ArgumentException(
                    "语义上下文必须明确关联到当前请求中的至少一个目标 id。",
                    nameof(semanticContextGroups));
            }
        }

        var semanticContexts = semanticContextGroups
            .Select(group => new
            {
                targetIds = group.TargetIds.Distinct().Order().ToArray(),
                type = group.Relationship,
                groupType = group.GroupType,
                texts = group.Entries
                    .Where(entry => !string.IsNullOrWhiteSpace(entry.Original))
                    .Select(entry => entry.Original)
                    .ToArray()
            })
            .Where(group => group.targetIds.Length > 0 && group.texts.Length > 0)
            .ToArray();

        if (semanticContexts.Length > 0)
        {
            payload["semanticContext"] = semanticContexts;
        }

        return payload;
    }

    public Task TestConnectionAsync(
        AiProviderProfile profile,
        CancellationToken cancellationToken = default) =>
        TestConnectionAsync(profile, reasoning: null, cancellationToken);

    public async Task TestConnectionAsync(
        AiProviderProfile profile,
        AiReasoningOptions? reasoning,
        CancellationToken cancellationToken = default)
    {
        AiProviderProfileValidator.Validate(profile);
        await CompleteWithReasoningFallbackAsync(
            profile,
            "你正在执行 API 连通性测试。请只回复 OK。",
            "请回复 OK。",
            requestKind: "ConnectionTest",
            requestedEntryCount: 0,
            response =>
            {
                if (string.IsNullOrWhiteSpace(response))
                    throw new InvalidDataException("模型连接成功，但没有返回文本。");

                return response;
            },
            reasoning,
            cancellationToken);
    }

    public static IReadOnlyList<AiTranslationResult> ParseResponse(
        string response,
        IReadOnlyList<AiTranslationRequest> requests)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(response);
        string trimmed = response.Trim();
        int firstArray = trimmed.IndexOf('[');
        int lastArray = trimmed.LastIndexOf(']');
        if (firstArray < 0 || lastArray < firstArray)
            throw new InvalidDataException("模型返回内容不是有效的 JSON 数组。");

        string json = trimmed[firstArray..(lastArray + 1)];
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("模型返回内容必须是 JSON 数组。");

        var expectedIds = requests.Select(item => item.Id).ToHashSet();
        var results = new Dictionary<int, string>();

        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("id", out JsonElement idElement) ||
                !idElement.TryGetInt32(out int id) ||
                !item.TryGetProperty("translation", out JsonElement translationElement) ||
                translationElement.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("模型返回的词条缺少有效的 id 或 translation 字段。");
            }

            string translation = translationElement.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(translation))
                throw new InvalidDataException($"模型返回的词条 {id} 译文为空。");
            if (!expectedIds.Contains(id))
                throw new InvalidDataException($"模型返回了未请求的词条 id：{id}。");
            if (!results.TryAdd(id, translation))
                throw new InvalidDataException($"模型重复返回了词条 id：{id}。");
        }

        if (results.Count != expectedIds.Count)
        {
            var missing = expectedIds.Except(results.Keys).Order().ToArray();
            throw new InvalidDataException(
                "模型未返回全部词条，缺少 id：" + string.Join(", ", missing));
        }

        return requests
            .Select(request => new AiTranslationResult(request.Id, results[request.Id]))
            .ToArray();
    }
}
