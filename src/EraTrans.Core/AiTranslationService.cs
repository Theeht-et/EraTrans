using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace EraTrans.Core;

public sealed record AiTranslationResult(int Id, string Translation);

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
/// Sends translation requests. Every request carries lines in reading order and
/// is answered with a flat id-to-text object.
/// </summary>
public sealed class AiTranslationService
{
    /// <summary>
    /// Prompt for one request: the model sees a contiguous slice of the script in
    /// source order, so it can read the passage as a whole before translating.
    /// </summary>
    private const string WindowSystemPrompt = """
        你是 ERA 游戏文本本地化翻译助手。请把 lines 中 do 为 1 的行翻译成简体中文。

        1. 只返回一个 JSON 对象，键是 id，值是译文，例如 {"12":"译文"}。不要代码块、不要解释、不要输出 do 为 0 的行。
        2. 相邻且 join 相同的行属于同一句话：先整段理解，再逐行输出。不要让半句各自成句，拼起来要通顺，不要重复主语。
        3. alt 相同的行是互斥分支：不要把它们的译文接成连续对话，但人名、称呼、术语要与本请求其他行保持一致。
        4. sid 相同的行属于同一屏；整窗只含一屏时还会给出 scene（id/type）。同一屏内的称呼、语气与术语必须一致；type 为 menu 时是同一菜单的并列选项，type 为 dialogue 时是连续对白。
        5. cmd/kind 说明该行的显示方式：button 是按钮文字（简短、以动作开头、不加句号），data 是数据块选项，html 是界面文本；print/form 是正文对白，按句翻译。menu 为 1 的行是同一菜单的选项，句式与长度要互相一致。
        6. 原文中的 [[P0]]、[[P1]] 等标记是程序保护的代码占位符，必须原样保留（不要翻译、删除或修改编号）；保留行内条件（\@ [[P0]] ? A # B \@）、控制码、标点和换行结构；无法翻译时原样返回原文。
        7. 若请求中包含 glossary 字段（原词到固定译名的映射），翻译正文与界面时必须严格遵循其中指定的译名。

        part 大于 1 表示这是同一函数前一段的延续，人称和译法沿用它。
        同一请求内，同一人名、称呼和术语必须使用同一译法。
        """;

    private const string GlossarySystemPrompt = """
        你是 ERA 游戏术语本地化助手。请把给定列表中的日文游戏术语（道具、指令、素质、能力等名称）翻译成简洁、自然的简体中文译名。

        1. 只返回一个 JSON 对象，键是编号 id，值是简洁中文译名，例如 {"0":"跳蛋","1":"接吻"}。不要代码块、不要解释。
        2. 译名保持简短贴合 ERA 游戏常见用语，不加多余标点或解释。
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

    /// <summary>
    /// Translates one window of lines. Lines marked as reference are sent as
    /// context only; the answer is a flat id-to-text object.
    /// </summary>
    public Task<WindowTranslationOutcome> TranslateWindowAsync(
        AiProviderProfile profile,
        TranslationWindow window,
        AiReasoningOptions? reasoning = null,
        CancellationToken cancellationToken = default) =>
        TranslateWindowAsync(profile, window, reasoning, glossary: null, cancellationToken);

    /// <summary>
    /// Translates one window of lines with optional on-demand variable glossary injection.
    /// </summary>
    public async Task<WindowTranslationOutcome> TranslateWindowAsync(
        AiProviderProfile profile,
        TranslationWindow window,
        AiReasoningOptions? reasoning,
        IReadOnlyList<VariableGlossaryEntry>? glossary,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(window);
        AiProviderProfileValidator.Validate(profile);

        if (window.Lines.Any(line =>
                line.Translate && line.Key.StartsWith("变量", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("变量类词条不能提交给 AI 翻译。");
        }

        if (window.TargetCount == 0)
            return new WindowTranslationOutcome([], [], []);

        // Match glossary against the unmasked original texts so terms appearing in
        // either prose or placeholders are captured for the current batch.
        IReadOnlyDictionary<string, string> matchedGlossary = VariableGlossaryService.MatchForTexts(
            window.Lines.Select(line => line.Text),
            glossary);

        MaskedTranslationWindow masked = AiPlaceholderMasker.MaskWindow(window);
        string userPrompt = BuildWindowPayload(masked.MaskedWindow, matchedGlossary);
        return await CompleteWithReasoningFallbackAsync(
            profile,
            WindowSystemPrompt,
            userPrompt,
            requestKind: "Translation",
            requestedEntryCount: window.TargetCount,
            processResponse: response =>
            {
                WindowTranslationOutcome outcome = ParseWindowResponse(response, masked.MaskedWindow);
                return AiPlaceholderMasker.RestoreOutcome(outcome, masked.PlaceholdersByLineId);
            },
            reasoning,
            cancellationToken);
    }

    /// <summary>
    /// Batch-translates variable glossary entries for the Glossary Center window.
    /// Returns a map from batch index (0..entries.Count-1) to translated Chinese term.
    /// </summary>
    public async Task<IReadOnlyDictionary<int, string>> TranslateGlossaryEntriesAsync(
        AiProviderProfile profile,
        IReadOnlyList<VariableGlossaryEntry> entries,
        AiReasoningOptions? reasoning = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        AiProviderProfileValidator.Validate(profile);

        if (entries.Count == 0)
            return new Dictionary<int, string>();

        var items = new List<Dictionary<string, object>>(entries.Count);
        for (int index = 0; index < entries.Count; index++)
        {
            items.Add(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["id"] = index,
                ["table"] = entries[index].Table,
                ["text"] = entries[index].Original
            });
        }

        string userPrompt = JsonSerializer.Serialize(
            new Dictionary<string, object>(StringComparer.Ordinal) { ["terms"] = items },
            PromptJsonOptions);

        return await CompleteWithReasoningFallbackAsync(
            profile,
            GlossarySystemPrompt,
            userPrompt,
            requestKind: "GlossaryTranslation",
            requestedEntryCount: entries.Count,
            processResponse: response => ParseGlossaryResponse(response, entries.Count),
            reasoning,
            cancellationToken);
    }

    private static IReadOnlyDictionary<int, string> ParseGlossaryResponse(string response, int count)
    {
        string json = ExtractJsonRegion(response);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("模型返回的术语 JSON 无法解析：" + ex.Message, ex);
        }

        var parsed = new Dictionary<int, string>();
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("模型返回的术语结果必须是 JSON 对象。");
            }

            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (!int.TryParse(property.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) ||
                    id < 0 || id >= count)
                {
                    continue;
                }

                string? text = ReadTranslationText(property.Value)?.Trim();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    parsed[id] = text;
                }
            }
        }

        if (parsed.Count == 0)
        {
            throw new InvalidDataException("模型没有返回任何可用的术语译名。");
        }

        return parsed;
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
    /// One request is sent as a line-ordered list: the model reads the passage in
    /// reading order, and the flags tell it which lines to translate, which belong
    /// to the same sentence, and which are mutually exclusive branches.
    /// </summary>
    public static string BuildWindowPayload(
        TranslationWindow window,
        IReadOnlyDictionary<string, string>? matchedGlossary = null)
    {
        ArgumentNullException.ThrowIfNull(window);

        var lines = new List<Dictionary<string, object?>>(window.Lines.Count);
        foreach (WindowLine line in window.Lines)
        {
            var item = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = line.Id,
                ["text"] = line.Text,
                ["do"] = line.Translate ? 1 : 0
            };

            if (!string.IsNullOrWhiteSpace(line.FunctionId))
                item["fn"] = line.FunctionId;
            if (line.Join > 0)
                item["join"] = line.Join;
            if (line.AltGroup is not null)
                item["alt"] = line.AltGroup;
            if (line.InlineCondition)
                item["cond"] = 1;
            if (line.Reference)
                item["ref"] = 1;
            if (!string.IsNullOrWhiteSpace(line.Command))
                item["cmd"] = line.Command;
            if (!string.IsNullOrWhiteSpace(line.Kind))
                item["kind"] = line.Kind;
            if (!string.IsNullOrWhiteSpace(line.SceneId))
                item["sid"] = line.SceneId;
            if (line.Menu)
                item["menu"] = 1;

            lines.Add(item);
        }

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (window.Part > 1)
            payload["part"] = window.Part;
        if (!string.IsNullOrWhiteSpace(window.SceneId))
        {
            var scene = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = window.SceneId
            };
            if (!string.IsNullOrWhiteSpace(window.SceneType))
                scene["type"] = window.SceneType;
            payload["scene"] = scene;
        }
        if (matchedGlossary is { Count: > 0 })
            payload["glossary"] = matchedGlossary;
        payload["lines"] = lines;

        return JsonSerializer.Serialize(payload, PromptJsonOptions);
    }

    /// <summary>
    /// Parses the flat id-to-text answer. Missing ids are reported instead of
    /// failing the whole request, and the older array shape is still accepted so a
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
}
