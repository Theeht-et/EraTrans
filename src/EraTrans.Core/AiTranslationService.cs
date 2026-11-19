using System.ClientModel;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using OpenAI;
using OpenAI.Chat;

namespace EraTrans.Core;

public sealed record AiTranslationRequest(int Id, string Key, string Original, string Context);

public sealed record AiTranslationResult(int Id, string Translation);

public interface IAiChatCompletionClient
{
    Task<string> CompleteAsync(
        AiProviderProfile profile,
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken);
}

/// <summary>
/// Thin adapter over the official OpenAI .NET SDK. The SDK handles compatible
/// Chat Completions HTTP requests, authentication, retries, and cancellation.
/// </summary>
public sealed class OpenAiCompatibleChatCompletionClient : IAiChatCompletionClient
{
    public async Task<string> CompleteAsync(
        AiProviderProfile profile,
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken)
    {
        AiProviderProfileValidator.Validate(profile);
        var client = new ChatClient(
            model: profile.Model,
            credential: new ApiKeyCredential(profile.ApiKey),
            options: new OpenAIClientOptions
            {
                Endpoint = new Uri(profile.BaseUrl)
            });

        ChatCompletion completion = await client.CompleteChatAsync(
            [
                new SystemChatMessage(systemPrompt),
                new UserChatMessage(userPrompt)
            ],
            cancellationToken: cancellationToken);

        string response = string.Concat(
            completion.Content
                .Where(part => !string.IsNullOrWhiteSpace(part.Text))
                .Select(part => part.Text));

        if (string.IsNullOrWhiteSpace(response))
        {
            throw new InvalidDataException("模型没有返回可用的文本内容。");
        }

        return response;
    }
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

    private static readonly JsonSerializerOptions PromptJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly IAiChatCompletionClient _client;

    public AiTranslationService(IAiChatCompletionClient? client = null)
    {
        _client = client ?? new OpenAiCompatibleChatCompletionClient();
    }

    public async Task<IReadOnlyList<AiTranslationResult>> TranslateAsync(
        AiProviderProfile profile,
        IReadOnlyList<AiTranslationRequest> requests,
        CancellationToken cancellationToken = default)
    {
        AiProviderProfileValidator.Validate(profile);
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0)
            return [];

        if (requests.Select(item => item.Id).Distinct().Count() != requests.Count)
            throw new ArgumentException("翻译请求中的 id 不能重复。", nameof(requests));

        if (requests.Any(item => item.Key.StartsWith("变量", StringComparison.Ordinal)))
            throw new InvalidOperationException("变量类词条不能提交给 AI 翻译。");

        var payload = requests.Select(item => new
        {
            id = item.Id,
            key = item.Key,
            original = item.Original,
            context = item.Context
        });

        string userPrompt = JsonSerializer.Serialize(payload, PromptJsonOptions);
        string response = await _client.CompleteAsync(
            profile,
            SystemPrompt,
            userPrompt,
            cancellationToken);

        return ParseResponse(response, requests);
    }

    public async Task TestConnectionAsync(
        AiProviderProfile profile,
        CancellationToken cancellationToken = default)
    {
        AiProviderProfileValidator.Validate(profile);
        string response = await _client.CompleteAsync(
            profile,
            "你正在执行 API 连通性测试。请只回复 OK。",
            "请回复 OK。",
            cancellationToken);

        if (string.IsNullOrWhiteSpace(response))
            throw new InvalidDataException("模型连接成功，但没有返回文本。");
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