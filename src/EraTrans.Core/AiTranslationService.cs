using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EraTrans.Core;

public sealed record AiTranslationResult(int Id, string Translation);

/// <summary>
/// Result of one window request. A partially answered window is a success: the
/// caller retries only the missing ids instead of resending the whole window.
/// </summary>
public sealed record WindowTranslationOutcome(
    IReadOnlyList<AiTranslationResult> Translations,
    IReadOnlyList<int> MissingIds,
    IReadOnlyList<int> UnknownIds,
    IReadOnlyDictionary<int, string>? IssuesByLineId = null,
    string? RawResponse = null)
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

    /// <summary>
    /// Multi-turn conversation overload used for same-session targeted retries.
    /// Custom/test implementations that only implement the single-turn overload
    /// automatically fall back via this default forwarder.
    /// </summary>
    Task<string> CompleteAsync(
        AiProviderProfile profile,
        IReadOnlyList<AiChatMessage> messages,
        AiReasoningOptions? reasoning = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        string systemPrompt = messages.FirstOrDefault(m => string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase))?.Content ?? string.Empty;
        string userPrompt = messages.LastOrDefault(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))?.Content ?? string.Empty;
        return CompleteAsync(profile, systemPrompt, userPrompt, reasoning, cancellationToken);
    }
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

        1. 只返回一个 JSON 对象，键必须原样抄写对应行的 id（例如 {"12_a3":"译文"}），值是译文。不要代码块、不要解释、不要输出 do 为 0 的行；严格一对一映射，严禁合并、拆分、漏行或重新编号。
        2. 相邻且 join 相同的行会被游戏引擎直接拼接成同一句话：请先通读整句并按自然中文语序构思，再把译文按原槽位自然切分写回各自 id，切勿把多行合并到同一 id。
           示例：「[[P0]]は」+「触手の蠢き」+「に反応して震えている。」→ 分别译为「[[P0]]因」「触手的蠕动」「而产生反应，身体颤抖着。」（拼起来通顺且严格对应原槽位）。
        3. alt 相同的行是互斥分支：不要把它们的译文接成连续对话，但人名、称呼、术语要与本请求其他行保持一致。
        4. sid 相同的行属于同一屏；整窗只含一屏时还会给出 scene（id/type）。同一屏内的称呼、语气与术语必须一致；type 为 menu 时是同一菜单的并列选项，type 为 dialogue 时是连续对白。
        5. cmd/kind 说明该行的显示方式：button 是按钮文字（简短、以动作开头、不加句号），data 是数据块选项，html 是界面文本；print/form 是正文对白，按句翻译。menu 为 1 的行是同一菜单的选项，句式与长度要互相一致。
        6. 原文中的 [[P0]]、[[P1]] 等标记是程序保护的代码占位符，必须原样保留且出现次数与原文完全一致（不要翻译、删除、修改编号，也不要在后半句重复补写 [[P0]]，需要主语时请省略或用“他/她”）；保留行内条件（\@ [[P0]] ? A # B \@）、控制码、标点和换行结构；无法翻译时原样返回原文。
        7. 若请求中包含 glossary 字段（原词到固定译名的映射），翻译正文与界面时必须严格遵循其中指定的译名。

        part 大于 1 表示这是同一函数前一段的延续，人称和译法沿用它。
        同一请求内，同一人名、称呼和术语必须使用同一译法。
        """;

    private static readonly Regex PlaceholderTokenRegex = new(
        @"\[\[\s*[Pp](\d+)\s*\]\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex LegacyArrayEntryRegex = new(
        """\{\s*"id"\s*:\s*(?:"(?<id>[^"]+)"|(?<id>-?\d+))\s*,\s*"translation"\s*:\s*"(?<val>(?:\\.|[^"\\])*)"\s*\}""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private const string GlossarySystemPrompt = """
        你是 ERA 游戏术语本地化助手。请把给定列表中的日文游戏术语（道具、指令、素质、能力等名称）翻译成简洁、自然的简体中文译名。

        1. 只返回一个 JSON 对象，键是编号 id，值是简洁中文译名，例如 {"0":"跳蛋","1":"接吻"}。不要代码块、不要解释。
        2. 译名保持简短贴合 ERA 游戏常见用语，不加多余标点或解释。
        """;

    private static readonly JsonSerializerOptions PromptJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonSerializerOptions LogRequestJsonOptions = new()
    {
        WriteIndented = true,
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
    /// Continues the same conversation to ask the model to fix only the missing or
    /// validation-rejected lines, carrying the exact per-line failure reasons so the
    /// model can correct them in place without losing screen context.
    /// </summary>
    public async Task<WindowTranslationOutcome> RetryWindowInSessionAsync(
        AiProviderProfile profile,
        TranslationWindow originalWindow,
        WindowTranslationOutcome previousOutcome,
        AiReasoningOptions? reasoning = null,
        IReadOnlyList<VariableGlossaryEntry>? glossary = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(originalWindow);
        ArgumentNullException.ThrowIfNull(previousOutcome);
        AiProviderProfileValidator.Validate(profile);

        if (previousOutcome.MissingIds.Count == 0)
            return new WindowTranslationOutcome([], [], []);

        HashSet<int> missingSet = previousOutcome.MissingIds.ToHashSet();
        WindowLine[] retryLines = originalWindow.Lines
            .Where(line => line.Translate && missingSet.Contains(line.Id))
            .ToArray();
        if (retryLines.Length == 0)
            return new WindowTranslationOutcome([], [], []);

        TranslationWindow retryWindow = originalWindow with { Lines = retryLines };
        if (string.IsNullOrWhiteSpace(previousOutcome.RawResponse))
        {
            return await TranslateWindowAsync(profile, retryWindow, reasoning, glossary, cancellationToken);
        }

        IReadOnlyDictionary<string, string> matchedGlossary = VariableGlossaryService.MatchForTexts(
            originalWindow.Lines.Select(line => line.Text),
            glossary);

        MaskedTranslationWindow maskedOriginal = AiPlaceholderMasker.MaskWindow(originalWindow);
        string originalUserPrompt = BuildWindowPayload(maskedOriginal.MaskedWindow, matchedGlossary);

        WindowLine[] maskedRetryLines = maskedOriginal.MaskedWindow.Lines
            .Where(line => line.Translate && missingSet.Contains(line.Id))
            .ToArray();
        TranslationWindow maskedRetryWindow = maskedOriginal.MaskedWindow with { Lines = maskedRetryLines };

        string followUpPrompt = BuildFollowUpCorrectionPrompt(maskedRetryLines, previousOutcome.IssuesByLineId);
        AiChatMessage[] messages =
        [
            new AiChatMessage("system", WindowSystemPrompt),
            new AiChatMessage("user", originalUserPrompt),
            new AiChatMessage("assistant", previousOutcome.RawResponse),
            new AiChatMessage("user", followUpPrompt)
        ];

        return await CompleteWithReasoningFallbackAsync(
            profile,
            messages,
            requestKind: "TranslationRetry",
            requestedEntryCount: maskedRetryLines.Length,
            processResponse: response =>
            {
                WindowTranslationOutcome outcome = ParseWindowResponse(response, maskedRetryWindow);
                return AiPlaceholderMasker.RestoreOutcome(outcome, maskedOriginal.PlaceholdersByLineId);
            },
            reasoning,
            cancellationToken);
    }

    private static string BuildFollowUpCorrectionPrompt(
        IReadOnlyList<WindowLine> failedMaskedLines,
        IReadOnlyDictionary<int, string>? issuesByLineId)
    {
        var builder = new StringBuilder();
        builder.AppendLine("你上一轮回复中以下条目未通过校验或遗漏，请只重新翻译这几条，并只返回一个 JSON 对象（键必须原样抄写下面的 id，不要输出其他行、不要解释、不要代码块）：");
        foreach (WindowLine line in failedMaskedLines)
        {
            string tag = FormatLineId(line);
            string reason = issuesByLineId is not null && issuesByLineId.TryGetValue(line.Id, out string? detail)
                ? detail
                : "上一轮未返回可用译文";
            builder.Append("- id=\"")
                .Append(tag)
                .Append("\"：")
                .Append(reason)
                .Append("｜原文：")
                .AppendLine(line.Text);
        }

        return builder.ToString().TrimEnd();
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
    private Task<T> CompleteWithReasoningFallbackAsync<T>(
        AiProviderProfile profile,
        string systemPrompt,
        string userPrompt,
        string requestKind,
        int requestedEntryCount,
        Func<string, T> processResponse,
        AiReasoningOptions? reasoning,
        CancellationToken cancellationToken) =>
        CompleteWithReasoningFallbackAsync(
            profile,
            [
                new AiChatMessage("system", systemPrompt),
                new AiChatMessage("user", userPrompt)
            ],
            requestKind,
            requestedEntryCount,
            processResponse,
            reasoning,
            cancellationToken);

    private async Task<T> CompleteWithReasoningFallbackAsync<T>(
        AiProviderProfile profile,
        IReadOnlyList<AiChatMessage> messages,
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
                profile, messages, requestKind, requestedEntryCount,
                processResponse, reasoning, cancellationToken);
        }
        catch (Exception ex) when (AiReasoningFallback.IsRejection(ex, reasoning))
        {
            return await CompleteWithLogAsync(
                profile, messages, requestKind, requestedEntryCount,
                processResponse, AiReasoningOptions.Auto, cancellationToken);
        }
    }

    private async Task<T> CompleteWithLogAsync<T>(
        AiProviderProfile profile,
        IReadOnlyList<AiChatMessage> messages,
        string requestKind,
        int requestedEntryCount,
        Func<string, T> processResponse,
        AiReasoningOptions? reasoning,
        CancellationToken cancellationToken)
    {
        AiTranslationLogEntry? log = null;
        string apiKey = profile.ApiKey.Trim();
        string systemPrompt = messages.FirstOrDefault(m => string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase))?.Content ?? string.Empty;
        string userPrompt = messages.LastOrDefault(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))?.Content ?? string.Empty;

        if (_logStore is not null)
        {
            AiProviderHttpRequestDetails requestDetails =
                AiProviderHttpRequestFactory.Create(profile, messages, reasoning);
            string endpoint = requestDetails.Endpoint;
            using JsonDocument requestDocument = JsonDocument.Parse(requestDetails.JsonBody);
            string requestJson = JsonSerializer.Serialize(
                requestDocument.RootElement,
                LogRequestJsonOptions);

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
            string response = messages.Count == 2 &&
                string.Equals(messages[0].Role, "system", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(messages[1].Role, "user", StringComparison.OrdinalIgnoreCase)
                ? await _client.CompleteAsync(
                    profile,
                    systemPrompt,
                    userPrompt,
                    reasoning,
                    cancellationToken)
                : await _client.CompleteAsync(
                    profile,
                    messages,
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
                ["id"] = FormatLineId(line),
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
    /// Formats a deterministic checksummed id (e.g. <c>12_a3</c>) for a window line so
    /// models cannot silently renumber sequential integer ids after merging lines.
    /// </summary>
    public static string FormatLineId(WindowLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        uint hash = 2166136261u;
        hash = MixHash(hash, line.Id.ToString(CultureInfo.InvariantCulture));
        hash = MixHash(hash, "|");
        hash = MixHash(hash, line.Text ?? string.Empty);
        return $"{line.Id.ToString(CultureInfo.InvariantCulture)}_{(hash & 0xFFu):x2}";
    }

    private static uint MixHash(uint hash, string text)
    {
        foreach (char ch in text)
        {
            hash ^= ch;
            hash *= 16777619u;
        }
        return hash;
    }

    /// <summary>
    /// Parses the flat id-to-text answer (supporting checksummed ids such as <c>12_a3</c>
    /// as well as legacy integer ids). When the JSON is truncated or partially malformed,
    /// salvages every intact entry instead of discarding the entire batch. Also validates
    /// per-line placeholder consistency and guards against cascading id-shift corruption.
    /// </summary>
    public static WindowTranslationOutcome ParseWindowResponse(
        string response,
        TranslationWindow window)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(response);
        ArgumentNullException.ThrowIfNull(window);

        WindowLine[] targetLines = window.Lines.Where(line => line.Translate).ToArray();
        Dictionary<int, WindowLine> expectedById = targetLines.ToDictionary(line => line.Id);
        Dictionary<string, int> tagToId = new(StringComparer.OrdinalIgnoreCase);
        foreach (WindowLine line in targetLines)
        {
            tagToId[FormatLineId(line)] = line.Id;
        }

        List<(string RawKey, string Text)> rawEntries = ExtractRawEntries(response);

        var taggedParsed = new Dictionary<int, string>();
        var bareParsed = new Dictionary<int, string>();
        var unknownSet = new HashSet<int>();
        var issuesByLineId = new Dictionary<int, string>();
        bool usedTaggedKeys = false;

        foreach ((string rawKey, string text) in rawEntries)
        {
            if (string.IsNullOrWhiteSpace(text))
                continue;

            string key = rawKey.Trim();
            if (tagToId.TryGetValue(key, out int taggedId))
            {
                usedTaggedKeys = true;
                taggedParsed[taggedId] = text;
                continue;
            }

            int underscoreIndex = key.IndexOf('_');
            if (underscoreIndex > 0 &&
                int.TryParse(key.AsSpan(0, underscoreIndex), NumberStyles.Integer, CultureInfo.InvariantCulture, out int badTaggedId))
            {
                // Key had a checksum suffix that does not match the expected line tag.
                unknownSet.Add(badTaggedId);
                if (expectedById.TryGetValue(badTaggedId, out WindowLine? expectedLine))
                {
                    issuesByLineId[badTaggedId] =
                        $"行号校验后缀不匹配（模型返回 {key}，期望 {FormatLineId(expectedLine)}）";
                }
                continue;
            }

            if (int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int bareId))
            {
                if (expectedById.ContainsKey(bareId))
                    bareParsed[bareId] = text;
                else
                    unknownSet.Add(bareId);
            }
        }

        var merged = new Dictionary<int, string>(taggedParsed);
        if (!usedTaggedKeys)
        {
            foreach (KeyValuePair<int, string> pair in bareParsed)
            {
                merged[pair.Key] = pair.Value;
            }
        }
        else
        {
            foreach (KeyValuePair<int, string> pair in bareParsed)
            {
                if (!merged.ContainsKey(pair.Key) &&
                    expectedById.TryGetValue(pair.Key, out WindowLine? line) &&
                    ValidatePlaceholders(line.Text, pair.Value) is null)
                {
                    merged[pair.Key] = pair.Value;
                }
            }
        }

        if (merged.Count == 0)
        {
            throw new InvalidDataException(BuildDetailedRejectionMessage(
                "模型没有返回任何可用的译文。",
                targetLines,
                issuesByLineId));
        }

        // Validate exact placeholder counts per line and detect cascading id-shift corruption.
        var validTranslations = new List<AiTranslationResult>(merged.Count);
        var mismatchedLineIndices = new List<int>();
        var acceptedLineIndices = new List<int>();

        for (int i = 0; i < targetLines.Length; i++)
        {
            WindowLine line = targetLines[i];
            if (!merged.TryGetValue(line.Id, out string? translation))
                continue;

            string? placeholderIssue = ValidatePlaceholders(line.Text, translation);
            if (placeholderIssue is not null)
            {
                mismatchedLineIndices.Add(i);
                issuesByLineId[line.Id] = placeholderIssue;
                continue;
            }

            acceptedLineIndices.Add(i);
            validTranslations.Add(new AiTranslationResult(line.Id, translation));
        }

        // Only apply cascading suffix truncation when the model ignored checksummed tags
        // and used bare sequential integers (where merging lines causes silent id drift).
        // When checksummed tags matched, each line's identity is verified individually.
        if (!usedTaggedKeys && mismatchedLineIndices.Count >= 2)
        {
            int firstBadIndex = mismatchedLineIndices[0];
            int firstBadId = targetLines[firstBadIndex].Id;
            var filtered = new List<AiTranslationResult>(validTranslations.Count);
            for (int k = 0; k < validTranslations.Count; k++)
            {
                if (acceptedLineIndices[k] < firstBadIndex)
                {
                    filtered.Add(validTranslations[k]);
                }
                else
                {
                    int droppedId = validTranslations[k].Id;
                    issuesByLineId.TryAdd(droppedId, $"检测到纯数字行号疑似错位（自第 {firstBadId} 行起截断待补翻）");
                }
            }
            validTranslations = filtered;
        }

        if (validTranslations.Count == 0)
        {
            throw new InvalidDataException(BuildDetailedRejectionMessage(
                "模型没有返回任何可用的译文（译文占位符或行号校验未通过）。",
                targetLines,
                issuesByLineId));
        }

        HashSet<int> acceptedIds = validTranslations.Select(item => item.Id).ToHashSet();
        int[] unknown = unknownSet.Order().ToArray();
        int[] missing = targetLines
            .Where(line => !acceptedIds.Contains(line.Id))
            .Select(line => line.Id)
            .Order()
            .ToArray();

        foreach (int missingId in missing)
        {
            issuesByLineId.TryAdd(missingId, "模型未返回该行译文");
        }

        return new WindowTranslationOutcome(validTranslations, missing, unknown, issuesByLineId, response);
    }

    private static string BuildDetailedRejectionMessage(
        string headline,
        IReadOnlyList<WindowLine> targetLines,
        IReadOnlyDictionary<int, string> issuesByLineId)
    {
        if (issuesByLineId.Count == 0)
            return headline;

        var builder = new StringBuilder(headline);
        builder.AppendLine();
        builder.AppendLine("具体未通过明细：");

        int shown = 0;
        const int maxShown = 8;
        foreach (WindowLine line in targetLines)
        {
            if (!issuesByLineId.TryGetValue(line.Id, out string? reason))
                continue;

            if (shown < maxShown)
            {
                builder.Append("- ")
                    .Append(FormatLineId(line))
                    .Append("：")
                    .AppendLine(reason);
            }
            shown++;
        }

        if (shown > maxShown)
        {
            builder.Append("……其余 ")
                .Append(shown - maxShown)
                .Append(" 条省略。");
        }

        return builder.ToString().TrimEnd();
    }

    private static string? ValidatePlaceholders(string originalText, string translatedText)
    {
        Dictionary<int, int> expected = CountPlaceholderIndices(originalText);
        Dictionary<int, int> actual = CountPlaceholderIndices(translatedText);

        var problems = new List<string>();
        foreach (KeyValuePair<int, int> pair in expected.OrderBy(item => item.Key))
        {
            int actualCount = actual.GetValueOrDefault(pair.Key);
            if (actualCount < pair.Value)
            {
                problems.Add(actualCount == 0
                    ? $"缺少 [[P{pair.Key}]]"
                    : $"缺少 [[P{pair.Key}]]（原文 {pair.Value} 处，译文 {actualCount} 处）");
            }
        }

        foreach (KeyValuePair<int, int> pair in actual.OrderBy(item => item.Key))
        {
            int expectedCount = expected.GetValueOrDefault(pair.Key);
            if (pair.Value > expectedCount)
            {
                problems.Add(expectedCount == 0
                    ? $"多出原文不存在的 [[P{pair.Key}]]"
                    : $"多出 [[P{pair.Key}]]（原文 {expectedCount} 处，译文 {pair.Value} 处）");
            }
        }

        return problems.Count == 0 ? null : string.Join("；", problems);
    }

    private static Dictionary<int, int> CountPlaceholderIndices(string text)
    {
        var counts = new Dictionary<int, int>();
        if (string.IsNullOrEmpty(text))
            return counts;

        foreach (Match match in PlaceholderTokenRegex.Matches(text))
        {
            if (int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
            {
                counts[index] = counts.GetValueOrDefault(index) + 1;
            }
        }
        return counts;
    }

    private static List<(string RawKey, string Text)> ExtractRawEntries(string response)
    {
        Exception? parseFailure = null;
        string? json = null;
        try
        {
            json = ExtractJsonRegion(response);
        }
        catch (InvalidDataException ex)
        {
            parseFailure = ex;
        }

        if (json is not null)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;
                var entries = new List<(string RawKey, string Text)>();
                if (root.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty property in root.EnumerateObject())
                    {
                        string? text = ReadTranslationText(property.Value);
                        if (!string.IsNullOrWhiteSpace(text))
                            entries.Add((property.Name, text));
                    }
                    return entries;
                }

                if (root.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in root.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object ||
                            !item.TryGetProperty("id", out JsonElement idElement) ||
                            !item.TryGetProperty("translation", out JsonElement translation))
                        {
                            continue;
                        }

                        string? rawKey = idElement.ValueKind switch
                        {
                            JsonValueKind.Number when idElement.TryGetInt32(out int num) => num.ToString(CultureInfo.InvariantCulture),
                            JsonValueKind.String => idElement.GetString(),
                            _ => null
                        };
                        string? text = ReadTranslationText(translation);
                        if (!string.IsNullOrWhiteSpace(rawKey) && !string.IsNullOrWhiteSpace(text))
                            entries.Add((rawKey, text));
                    }
                    return entries;
                }

                throw new InvalidDataException("模型返回内容必须是 JSON 对象或数组。");
            }
            catch (JsonException ex)
            {
                parseFailure = new InvalidDataException("模型返回的 JSON 无法解析：" + ex.Message, ex);
            }
        }

        // Fallback: rescue intact entries from truncated or partially malformed JSON.
        List<(string RawKey, string Text)> rescued = RescueEntriesFromPartialJson(response);
        if (rescued.Count > 0)
            return rescued;

        throw parseFailure ?? new InvalidDataException("模型返回内容中没有 JSON 内容。");
    }

    private static List<(string RawKey, string Text)> RescueEntriesFromPartialJson(string response)
    {
        var results = new List<(string RawKey, string Text)>();
        int startObject = response.IndexOf('{');
        int startArray = response.IndexOf('[');
        if (startObject < 0 && startArray < 0)
            return results;

        // First scan flat "key": "value" pairs with a JSON string state machine.
        int i = startObject >= 0 ? startObject : 0;
        int length = response.Length;
        while (i < length)
        {
            int keyStart = response.IndexOf('"', i);
            if (keyStart < 0)
                break;

            if (!TryReadJsonString(response, keyStart, out string? key, out int afterKey))
            {
                i = keyStart + 1;
                continue;
            }

            int pos = SkipWhitespace(response, afterKey);
            if (pos >= length || response[pos] != ':')
            {
                i = afterKey;
                continue;
            }

            pos = SkipWhitespace(response, pos + 1);
            if (pos >= length || response[pos] != '"')
            {
                i = pos;
                continue;
            }

            if (!TryReadJsonString(response, pos, out string? value, out int afterValue))
            {
                // Truncated string at end of reply: stop scanning here.
                break;
            }

            int nextToken = SkipWhitespace(response, afterValue);
            bool cleanlyTerminated = nextToken < length && (response[nextToken] == ',' || response[nextToken] == '}');
            if (cleanlyTerminated && !string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
            {
                results.Add((key, value));
                i = nextToken + 1;
            }
            else
            {
                // Value was likely broken by an unescaped quote; advance past the key and keep salvaging later entries.
                i = afterKey;
            }
        }

        if (results.Count > 0)
            return results;

        // Fallback for truncated legacy array shape: [{"id":1,"translation":"..."}, ...]
        foreach (Match match in LegacyArrayEntryRegex.Matches(response))
        {
            string rawId = match.Groups["id"].Value;
            string rawVal = match.Groups["val"].Value;
            try
            {
                string? decoded = JsonSerializer.Deserialize<string>("\"" + rawVal + "\"");
                if (!string.IsNullOrWhiteSpace(rawId) && !string.IsNullOrWhiteSpace(decoded))
                    results.Add((rawId, decoded));
            }
            catch (JsonException)
            {
                // Skip malformed entry.
            }
        }

        return results;
    }

    private static int SkipWhitespace(string text, int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;
        return index;
    }

    private static bool TryReadJsonString(string text, int openQuoteIndex, out string? value, out int nextIndex)
    {
        value = null;
        nextIndex = openQuoteIndex + 1;
        if (openQuoteIndex >= text.Length || text[openQuoteIndex] != '"')
            return false;

        int i = openQuoteIndex + 1;
        while (i < text.Length)
        {
            char ch = text[i];
            if (ch == '\\')
            {
                if (i + 1 >= text.Length)
                    return false;
                i += 2;
                continue;
            }

            if (ch == '"')
            {
                string literal = text[openQuoteIndex..(i + 1)];
                try
                {
                    value = JsonSerializer.Deserialize<string>(literal);
                    nextIndex = i + 1;
                    return value is not null;
                }
                catch (JsonException)
                {
                    return false;
                }
            }

            i++;
        }

        return false;
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
