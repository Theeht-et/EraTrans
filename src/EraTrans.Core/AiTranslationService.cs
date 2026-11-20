using System.Diagnostics;
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

public interface IAiChatCompletionClient
{
    Task<string> CompleteAsync(
        AiProviderProfile profile,
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken);
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
        TranslateCoreAsync(profile, requests, null, cancellationToken);

    public Task<IReadOnlyList<AiTranslationResult>> TranslateWithSemanticContextAsync(
        AiProviderProfile profile,
        IReadOnlyList<AiTranslationRequest> requests,
        IReadOnlyList<AiTranslationSemanticContextGroup> semanticContextGroups,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(semanticContextGroups);
        return TranslateCoreAsync(profile, requests, semanticContextGroups, cancellationToken);
    }

    private async Task<IReadOnlyList<AiTranslationResult>> TranslateCoreAsync(
        AiProviderProfile profile,
        IReadOnlyList<AiTranslationRequest> requests,
        IReadOnlyList<AiTranslationSemanticContextGroup>? semanticContextGroups,
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
        return await CompleteWithLogAsync(
            profile,
            systemPrompt,
            userPrompt,
            requestKind: "Translation",
            requestedEntryCount: requests.Count,
            response => ParseResponse(response, requests),
            cancellationToken);
    }


    private async Task<T> CompleteWithLogAsync<T>(
        AiProviderProfile profile,
        string systemPrompt,
        string userPrompt,
        string requestKind,
        int requestedEntryCount,
        Func<string, T> processResponse,
        CancellationToken cancellationToken)
    {
        AiTranslationLogEntry? log = null;
        string apiKey = profile.ApiKey;

        if (_logStore is not null)
        {
            AiProviderHttpRequestDetails requestDetails =
                AiProviderHttpRequestFactory.Create(profile, systemPrompt, userPrompt);
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

    public async Task TestConnectionAsync(
        AiProviderProfile profile,
        CancellationToken cancellationToken = default)
    {
        AiProviderProfileValidator.Validate(profile);
        await CompleteWithLogAsync(
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