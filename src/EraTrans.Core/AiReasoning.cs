using System.Text.Json;
using System.Text.Json.Nodes;

namespace EraTrans.Core;

/// <summary>
/// Reasoning is a per-request switch, not a per-model capability matrix.
/// Auto leaves the provider default untouched; On and Off write exactly one
/// documented payload shape for the endpoint in use.
/// </summary>
public enum AiReasoningMode
{
    Auto = 0,
    On = 1,
    Off = 2
}

/// <summary>
/// The reasoning choice for one AI service. Empty JSON means "use the preset
/// for this endpoint"; a non-empty JSON replaces the preset for that side only.
/// </summary>
public sealed record AiReasoningOptions(
    AiReasoningMode Mode = AiReasoningMode.Auto,
    string? OnJson = null,
    string? OffJson = null)
{
    public static AiReasoningOptions Auto { get; } = new();
}

public static class AiReasoning
{
    /// <summary>Fields the reasoning fragment must never replace.</summary>
    private static readonly HashSet<string> ReservedKeys =
        new(StringComparer.OrdinalIgnoreCase) { "model", "messages", "stream", "stop" };

    public static bool IsActive(AiReasoningOptions? options) =>
        options is not null && options.Mode != AiReasoningMode.Auto;

    /// <summary>
    /// Writes the reasoning shape for the current mode into the Chat Completions
    /// request body. Auto never touches the body.
    /// </summary>
    public static void Apply(
        Dictionary<string, object?> payload,
        string baseUrl,
        AiReasoningOptions? options)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (!IsActive(options))
            return;

        JsonObject shape = ResolveShape(baseUrl, options!);
        foreach (KeyValuePair<string, JsonNode?> property in shape)
            payload[property.Key] = property.Value?.DeepClone();
    }

    /// <summary>
    /// Resolves the fragment for the current mode: a valid user-supplied JSON
    /// replaces the preset, an empty or invalid one falls back to the preset.
    /// </summary>
    public static JsonObject ResolveShape(string baseUrl, AiReasoningOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        string? custom = options.Mode == AiReasoningMode.Off ? options.OffJson : options.OnJson;
        if (!string.IsNullOrWhiteSpace(custom) &&
            TryParseShape(custom, out JsonObject? parsed, out _) &&
            parsed is not null)
        {
            return parsed;
        }

        return (JsonObject)JsonNode.Parse(PresetJson(baseUrl, options.Mode == AiReasoningMode.On))!;
    }

    /// <summary>
    /// The most likely documented shape for this endpoint. Unknown hosts use the
    /// OpenAI top-level field, which is what OpenAI-compatible gateways accept.
    /// </summary>
    public static string PresetJson(string baseUrl, bool enable)
    {
        (string on, string off) = PresetFor(ResolveHost(baseUrl));
        return enable ? on : off;
    }

    /// <summary>Short label shown next to the selector so the active preset is visible.</summary>
    public static string PresetLabel(string baseUrl) => ResolveHost(baseUrl) switch
    {
        var host when host.Contains("openrouter.ai", StringComparison.OrdinalIgnoreCase) =>
            "reasoning.enabled",
        var host when IsAliyun(host) => "enable_thinking",
        var host when host.Contains("siliconflow", StringComparison.OrdinalIgnoreCase) =>
            "enable_thinking",
        var host when IsThinkingType(host) => "thinking.type",
        _ => "reasoning_effort"
    };

    public static bool TryParseShape(string? json, out JsonObject? shape, out string? error)
    {
        shape = null;
        error = null;

        if (string.IsNullOrWhiteSpace(json))
            return true;

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            error = "JSON 无效：" + ex.Message;
            return false;
        }

        if (node is not JsonObject parsed)
        {
            error = "必须是 JSON 对象，例如 {\"reasoning_effort\":\"high\"}。";
            return false;
        }

        foreach (KeyValuePair<string, JsonNode?> property in parsed)
        {
            if (ReservedKeys.Contains(property.Key))
            {
                error = $"不能覆盖请求的保留字段：{property.Key}。";
                return false;
            }
        }

        shape = parsed;
        return true;
    }

    private static (string On, string Off) PresetFor(string host)
    {
        if (host.Contains("openrouter.ai", StringComparison.OrdinalIgnoreCase))
        {
            return (
                "{\"reasoning\":{\"enabled\":true}}",
                "{\"reasoning\":{\"enabled\":false}}");
        }

        if (IsAliyun(host) || host.Contains("siliconflow", StringComparison.OrdinalIgnoreCase))
        {
            return (
                "{\"enable_thinking\":true}",
                "{\"enable_thinking\":false}");
        }

        if (IsThinkingType(host))
        {
            return (
                "{\"thinking\":{\"type\":\"enabled\"}}",
                "{\"thinking\":{\"type\":\"disabled\"}}");
        }

        // OpenAI official, xAI and unknown OpenAI-compatible gateways.
        return (
            "{\"reasoning_effort\":\"high\"}",
            "{\"reasoning_effort\":\"none\"}");
    }

    private static bool IsAliyun(string host) =>
        host.Contains("dashscope", StringComparison.OrdinalIgnoreCase) ||
        host.Contains("aliyun", StringComparison.OrdinalIgnoreCase);

    private static bool IsThinkingType(string host) =>
        host.Contains("bigmodel", StringComparison.OrdinalIgnoreCase) ||
        host.Contains("z.ai", StringComparison.OrdinalIgnoreCase) ||
        host.Contains("volces.com", StringComparison.OrdinalIgnoreCase) ||
        host.Contains("deepseek", StringComparison.OrdinalIgnoreCase) ||
        host.Contains("moonshot", StringComparison.OrdinalIgnoreCase) ||
        host.Contains("kimi", StringComparison.OrdinalIgnoreCase);

    private static string ResolveHost(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            return "";

        try
        {
            return new Uri(AiProviderProfileValidator.NormalizeBaseUrl(baseUrl)).Host.ToLowerInvariant();
        }
        catch (Exception ex) when (
            ex is ArgumentException or InvalidOperationException or UriFormatException)
        {
            return "";
        }
    }
}
