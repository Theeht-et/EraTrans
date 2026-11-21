using System.Text.RegularExpressions;

namespace EraTrans.Core;

/// <summary>
/// Resolves the reasoning wire dialect for EraTrans' OpenAI Chat Completions
/// transport. Native Responses, Anthropic Messages and Gemini transports are
/// intentionally not treated as Chat Completions dialects.
/// </summary>
public static class AiReasoningDialectSupport
{
    private const string OpenAiEffort = "openaiReasoningEffort";
    private const string OpenRouter = "openrouterReasoning";
    private const string DeepSeek = "deepseekThinking";
    private const string Qwen = "qwenEnableThinking";
    private const string ThinkingType = "thinkingType";
    private const string SiliconFlow = "siliconflowEnableThinking";
    private const string Kimi = "kimiThinking";
    private const string Intern = "internThinkingMode";
    private const string ChatTemplate = "chatTemplateKwargs";

    public static bool IsCompatible(string baseUrl, AiProviderModel model)
    {
        if (model.ReasoningSupport != true)
            return false;

        string? expected = ResolveDialect(baseUrl, model.Id);
        if (expected is null)
            return false;

        if (string.IsNullOrWhiteSpace(model.ReasoningDialect))
            return !expected.Equals(DeepSeek, StringComparison.OrdinalIgnoreCase);

        return expected.Equals(model.ReasoningDialect, StringComparison.OrdinalIgnoreCase);
    }

    public static void Apply(
        Dictionary<string, object?> payload,
        string baseUrl,
        AiProviderModel model,
        AiModelReasoningSettings setting)
    {
        if (!IsCompatible(baseUrl, model))
            return;

        string dialect = ResolveDialect(baseUrl, model.Id)!;
        string effort = setting.Effort.Trim().ToLowerInvariant();

        if (setting.Disabled)
        {
            if (model.ReasoningCanDisable != true)
                return;

            switch (dialect)
            {
                case OpenAiEffort:
                    payload["reasoning_effort"] = "none";
                    break;
                case OpenRouter:
                    payload["reasoning"] = new { enabled = false };
                    break;
                case DeepSeek:
                case ThinkingType:
                    payload["thinking"] = new { type = "disabled" };
                    break;
                case Qwen:
                    payload["enable_thinking"] = false;
                    break;
                case SiliconFlow:
                    payload["enable_thinking"] = false;
                    break;
                case Kimi:
                    payload["thinking"] = new { type = "disabled" };
                    break;
                case Intern:
                    payload["thinking_mode"] = false;
                    break;
                case ChatTemplate:
                    payload["chat_template_kwargs"] = new Dictionary<string, object?>
                    {
                        ["enable_thinking"] = false
                    };
                    break;
            }

            return;
        }

        bool advertisedEffort = model.ReasoningLevels.Any(level =>
            string.Equals(level, effort, StringComparison.OrdinalIgnoreCase));
        bool budgetEffort = model.ReasoningLevels.Count == 0 &&
            dialect is OpenRouter or Qwen or SiliconFlow;
        if (!setting.Enabled || (!advertisedEffort && !budgetEffort))
            return;

        switch (dialect)
        {
            case OpenAiEffort:
                payload["reasoning_effort"] = effort;
                break;
            case OpenRouter:
                if (model.ReasoningLevels.Count > 0)
                    payload["reasoning"] = new { effort };
                else
                    payload["reasoning"] = new { enabled = true, max_tokens = ResolveBudget(effort) };
                break;
            case DeepSeek:
                if (effort is "low" or "high" or "max")
                {
                    payload["thinking"] = new { type = "enabled" };
                    payload["reasoning_effort"] = effort;
                }
                break;
            case ThinkingType:
                payload["thinking"] = new { type = "enabled" };
                payload["reasoning_effort"] = effort;
                break;
            case Qwen:
                if (model.ReasoningCanDisable == true)
                    payload["enable_thinking"] = true;
                payload["thinking_budget"] = ResolveBudget(effort);
                break;
            case SiliconFlow:
                payload["thinking_budget"] = ResolveBudget(effort);
                break;
            case Kimi:
                payload["thinking"] = new { type = "enabled", effort };
                break;
        }
    }

    /// <summary>
    /// Applies only model-family defaults mirrored from Kelivo. Explicit false
    /// capability and explicit catalog metadata always take precedence.
    /// </summary>
    public static AiProviderModel ApplyKnownModelDefaults(
        AiProviderModel model,
        string baseUrl)
    {
        if (model.ReasoningSupport == false)
            return model;

        string id = NormalizeModelId(model.Id);
        string? dialect = ResolveDialect(baseUrl, model.Id);
        bool documentedDeepSeekModel =
            id.Equals("deepseek-flash", StringComparison.OrdinalIgnoreCase) ||
            id.Equals("deepseek-v4-pro", StringComparison.OrdinalIgnoreCase);
        bool unverifiedOfficialDeepSeek =
            dialect?.Equals(DeepSeek, StringComparison.OrdinalIgnoreCase) == true &&
            !documentedDeepSeekModel;

        if (TryGetModelDefaults(id, out string[] levels, out bool canDisable))
        {
            model.ReasoningSupport ??= true;
            model.SupportsReasoning = true;
            if (!unverifiedOfficialDeepSeek)
            {
                if (model.ReasoningLevels.Count == 0)
                    model.ReasoningLevels = [.. levels];
                model.ReasoningCanDisable ??= canDisable;
                if (model.ReasoningDialect is null && dialect is not null)
                    model.ReasoningDialect = dialect;
            }
        }
        else if (IsKnownReasoningFamily(id))
        {
            // Preserve Kelivo-style model-family capability recognition even when
            // the current provider has no known wire dialect for that model.
            model.ReasoningSupport ??= true;
            model.SupportsReasoning = true;
            bool documentedDeepSeekModel =
                id.Equals("deepseek-flash", StringComparison.OrdinalIgnoreCase) ||
                id.Equals("deepseek-v4-pro", StringComparison.OrdinalIgnoreCase);
            if (model.ReasoningDialect is null &&
                dialect is not null &&
                (!dialect.Equals(DeepSeek, StringComparison.OrdinalIgnoreCase) ||
                 documentedDeepSeekModel))
            {
                model.ReasoningDialect = dialect;
            }
        }
        else if ((model.ReasoningSupport == true || model.SupportsReasoning) &&
                 model.ReasoningDialect is null &&
                 dialect is not null &&
                 !dialect.Equals(DeepSeek, StringComparison.OrdinalIgnoreCase))
        {
            // Explicit capability metadata can use a known provider dialect.
            model.ReasoningDialect = dialect;
        }

        // DeepSeek's documented current model list is intentionally narrow.
        if (dialect == DeepSeek &&
            (id.Equals("deepseek-flash", StringComparison.OrdinalIgnoreCase) ||
             id.Equals("deepseek-v4-pro", StringComparison.OrdinalIgnoreCase)))
        {
            model.ReasoningSupport ??= true;
            model.SupportsReasoning = true;
            if (model.ReasoningLevels.Count == 0)
                model.ReasoningLevels = ["low", "high", "max"];
            model.ReasoningCanDisable ??= true;
            model.ReasoningDialect ??= DeepSeek;
        }

        return model;
    }

    public static string? ResolveDialect(string baseUrl, string modelId)
    {
        string host;
        try
        {
            host = new Uri(AiProviderProfileValidator.NormalizeBaseUrl(baseUrl)).Host;
        }
        catch (Exception ex) when (
            ex is ArgumentException or InvalidOperationException or UriFormatException)
        {
            return null;
        }

        if (host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("api.x.ai", StringComparison.OrdinalIgnoreCase))
            return OpenAiEffort;

        if (host.Equals("openrouter.ai", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".openrouter.ai", StringComparison.OrdinalIgnoreCase))
            return OpenRouter;

        if (host.Equals("api.deepseek.com", StringComparison.OrdinalIgnoreCase))
            return DeepSeek;

        if (host.Contains("dashscope", StringComparison.OrdinalIgnoreCase) &&
            host.EndsWith(".aliyuncs.com", StringComparison.OrdinalIgnoreCase))
            return Qwen;

        if (host.Equals("open.bigmodel.cn", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("api.z.ai", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("ark.cn-beijing.volces.com", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("xiaomimimo", StringComparison.OrdinalIgnoreCase))
            return ThinkingType;

        if (host.Equals("api.siliconflow.cn", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("api.siliconflow.com", StringComparison.OrdinalIgnoreCase))
            return SiliconFlow;

        if (host.Equals("api.moonshot.cn", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("api.moonshot.ai", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("api.kimi.com", StringComparison.OrdinalIgnoreCase))
        {
            return IsKimiK3(modelId) ? OpenAiEffort : Kimi;
        }

        if (host.Equals("chat.intern-ai.org.cn", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("api.intern-ai.org.cn", StringComparison.OrdinalIgnoreCase))
            return Intern;

        if (host.Equals("poolside.ai", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".poolside.ai", StringComparison.OrdinalIgnoreCase))
            return ChatTemplate;

        return null;
    }

    private static bool IsKnownReasoningFamily(string id)
    {
        return Regex.IsMatch(id, @"^(?:o[134]|gpt-[56])(?:$|[-.])", RegexOptions.IgnoreCase) ||
               Regex.IsMatch(id, @"^deepseek-(?:reasoner|r1|v3\.1|v3\.2|v4)(?:$|[-._:@])", RegexOptions.IgnoreCase) ||
               Regex.IsMatch(id, @"^(?:qwq|qwen-?3)(?:$|[-.])", RegexOptions.IgnoreCase) ||
               Regex.IsMatch(id, @"^kimi-k2-thinking(?:$|[-._:@])", RegexOptions.IgnoreCase) ||
               Regex.IsMatch(id, @"^kimi-k2\.[567](?:$|[-._:@])", RegexOptions.IgnoreCase) ||
               Regex.IsMatch(id, @"^kimi-k3(?:$|[-._:@])", RegexOptions.IgnoreCase) ||
               Regex.IsMatch(id, @"^grok-4\.[567](?:$|[-._:@])", RegexOptions.IgnoreCase) ||
               Regex.IsMatch(id, @"^gemini-(?:2\.5|3|4)(?:$|[-._:@])", RegexOptions.IgnoreCase) ||
               Regex.IsMatch(id, @"^claude-(?:(?:3[-.]7)|(?:opus|sonnet|haiku|fable|mythos)-[45])(?:$|[-._:@])", RegexOptions.IgnoreCase) ||
               Regex.IsMatch(id, @"^glm-(?:4[.-][567]|5)(?:$|[-._:@])", RegexOptions.IgnoreCase) ||
               Regex.IsMatch(id, @"^(?:minimax-m[23]|step-3|intern-s1|laguna)(?:$|[-._:@])", RegexOptions.IgnoreCase) ||
               id.Contains("mimo-v2", StringComparison.OrdinalIgnoreCase) ||
               (id.Contains("doubao", StringComparison.OrdinalIgnoreCase) &&
                (id.Contains("seed-", StringComparison.OrdinalIgnoreCase) ||
                 id.Contains("thinking", StringComparison.OrdinalIgnoreCase)));
    }

    private static bool TryGetModelDefaults(
        string id,
        out string[] levels,
        out bool canDisable)
    {
        levels = [];
        canDisable = false;

        if (Regex.IsMatch(id, @"^deepseek-(?:reasoner|r1|v3\.1|v3\.2|v4)(?:$|[-._:@])", RegexOptions.IgnoreCase))
        {
            levels = ["low", "high", "max"];
            canDisable = true;
            return true;
        }

        if (Regex.IsMatch(id, @"^gpt-6(?:$|[-.])", RegexOptions.IgnoreCase))
        {
            levels = ["low", "medium", "high", "xhigh", "max"];
            return true;
        }

        if (Regex.IsMatch(id, @"^gpt-5\.6(?:-(?:sol|terra|luna))?(?:$|[.@])", RegexOptions.IgnoreCase))
        {
            levels = ["low", "medium", "high", "xhigh", "max"];
            canDisable = true;
            return true;
        }

        if (Regex.IsMatch(id, @"^gpt-5\.5-pro(?:$|[-.])", RegexOptions.IgnoreCase))
        {
            levels = ["medium", "high", "xhigh"];
            return true;
        }
        if (Regex.IsMatch(id, @"^gpt-5\.5-(?:codex|chat-latest)(?:$|[-.])", RegexOptions.IgnoreCase))
            return false;
        if (Regex.IsMatch(id, @"^gpt-5\.5(?:$|[-.])", RegexOptions.IgnoreCase))
        {
            levels = ["low", "medium", "high", "xhigh"];
            canDisable = true;
            return true;
        }

        if (Regex.IsMatch(id, @"^gpt-5\.4-pro(?:$|[-.])", RegexOptions.IgnoreCase))
        {
            levels = ["medium", "high", "xhigh"];
            return true;
        }
        if (Regex.IsMatch(id, @"^gpt-5\.4-(?:codex|chat-latest)(?:$|[-.])", RegexOptions.IgnoreCase))
            return false;
        if (Regex.IsMatch(id, @"^gpt-5\.4(?:$|[-.])", RegexOptions.IgnoreCase))
        {
            levels = ["low", "medium", "high", "xhigh"];
            canDisable = true;
            return true;
        }

        if (Regex.IsMatch(id, @"^gpt-5\.3-codex(?:$|[-.])", RegexOptions.IgnoreCase))
        {
            levels = ["low", "medium", "high", "xhigh"];
            return true;
        }
        if (Regex.IsMatch(id, @"^gpt-5\.3-chat-latest(?:$|[-.])", RegexOptions.IgnoreCase))
        {
            levels = ["low", "medium", "high", "xhigh"];
            canDisable = true;
            return true;
        }
        if (Regex.IsMatch(id, @"^gpt-5\.3(?:$|[-.])", RegexOptions.IgnoreCase))
            return false;

        if (Regex.IsMatch(id, @"^gpt-5\.2-pro(?:$|[-.])", RegexOptions.IgnoreCase))
        {
            levels = ["medium", "high", "xhigh"];
            return true;
        }
        if (Regex.IsMatch(id, @"^gpt-5\.2-codex(?:$|[-.])", RegexOptions.IgnoreCase))
        {
            levels = ["low", "medium", "high", "xhigh"];
            return true;
        }
        if (Regex.IsMatch(id, @"^gpt-5\.2-chat-latest(?:$|[-.])", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(id, @"^gpt-5\.2(?:$|[-.])", RegexOptions.IgnoreCase))
        {
            levels = ["low", "medium", "high", "xhigh"];
            canDisable = true;
            return true;
        }

        if (Regex.IsMatch(id, @"^gpt-5\.1-chat-latest(?:$|[-.])", RegexOptions.IgnoreCase))
        {
            levels = ["low", "medium", "high"];
            canDisable = true;
            return true;
        }
        if (Regex.IsMatch(id, @"^gpt-5\.1-codex-max(?:$|[-.])", RegexOptions.IgnoreCase))
        {
            levels = ["low", "medium", "high", "xhigh"];
            return true;
        }
        if (Regex.IsMatch(id, @"^gpt-5\.1-codex(?:$|[-.])", RegexOptions.IgnoreCase))
        {
            levels = ["low", "medium", "high"];
            return true;
        }
        if (Regex.IsMatch(id, @"^gpt-5\.1-pro(?:$|[-.])", RegexOptions.IgnoreCase))
            return false;
        if (Regex.IsMatch(id, @"^gpt-5\.1(?:$|[-.])", RegexOptions.IgnoreCase))
        {
            levels = ["low", "medium", "high"];
            canDisable = true;
            return true;
        }

        if (Regex.IsMatch(id, @"^gpt-5-pro(?:$|[-.])", RegexOptions.IgnoreCase))
        {
            levels = ["high"];
            return true;
        }
        if (Regex.IsMatch(id, @"^gpt-5-codex(?:$|[-.])", RegexOptions.IgnoreCase))
        {
            levels = ["low", "medium", "high"];
            return true;
        }
        if (Regex.IsMatch(id, @"^gpt-5-chat-latest(?:$|[-.])", RegexOptions.IgnoreCase))
            return false;
        if (Regex.IsMatch(id, @"^gpt-5(?:$|[-.])", RegexOptions.IgnoreCase))
        {
            levels = ["minimal", "low", "medium", "high"];
            return true;
        }

        if (Regex.IsMatch(id, @"^(?:o1|o3|o4)(?:$|[-.])", RegexOptions.IgnoreCase))
        {
            levels = ["low", "medium", "high"];
            return true;
        }

        if (Regex.IsMatch(id, @"^grok-4\.6(?:$|[-.])", RegexOptions.IgnoreCase))
        {
            levels = ["low", "medium", "high", "xhigh"];
            return true;
        }
        if (Regex.IsMatch(id, @"^grok-4\.5(?:$|[-.])", RegexOptions.IgnoreCase))
        {
            levels = ["low", "medium", "high"];
            return true;
        }

        if (Regex.IsMatch(id, @"^glm-5\.3(?:$|[-.])", RegexOptions.IgnoreCase))
        {
            levels = ["low", "high", "max"];
            return true;
        }
        if (Regex.IsMatch(id, @"^glm-5\.2(?:$|[-.])", RegexOptions.IgnoreCase))
        {
            levels = ["low", "medium", "high", "xhigh", "max"];
            canDisable = true;
            return true;
        }
        if (Regex.IsMatch(id, @"^glm-(?:4[-.]?[567]|5)(?:$|[-.])", RegexOptions.IgnoreCase))
        {
            canDisable = true;
            return true;
        }

        if (Regex.IsMatch(id, @"^(?:qwq|qwen-?3)(?:$|[-.])", RegexOptions.IgnoreCase))
        {
            canDisable = !IsDashScopeThinkingOnlyModel(id);
            return true;
        }

        if (Regex.IsMatch(id, @"^kimi-k3(?:$|[-.])", RegexOptions.IgnoreCase))
        {
            levels = ["low", "high", "max"];
            return true;
        }
        if (id.Contains("kimi-for-coding-highspeed", StringComparison.OrdinalIgnoreCase))
        {
            canDisable = true;
            return true;
        }
        if (id.Contains("kimi-for-coding", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(id, @"^k3(?:-256k)?$", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(id, @"^kimi-k2\.8(?:$|[-.])", RegexOptions.IgnoreCase))
        {
            levels = ["low", "high", "max"];
            canDisable = true;
            return true;
        }
        if (id.Contains("kimi-k2.5", StringComparison.OrdinalIgnoreCase) ||
            id.Contains("kimi-k2.6", StringComparison.OrdinalIgnoreCase))
        {
            canDisable = true;
            return true;
        }
        if (id.Contains("kimi-k2-thinking", StringComparison.OrdinalIgnoreCase) ||
            id.Contains("kimi-k2.7", StringComparison.OrdinalIgnoreCase))
            return true;

        if (Regex.IsMatch(id, @"^mimo-v2(?:$|[-.])", RegexOptions.IgnoreCase))
        {
            levels = ["low", "medium", "high"];
            canDisable = true;
            return true;
        }

        if (id.StartsWith("laguna-", StringComparison.OrdinalIgnoreCase))
        {
            canDisable = true;
            return true;
        }

        if (id.Contains("doubao", StringComparison.OrdinalIgnoreCase) &&
            (id.Contains("seed-", StringComparison.OrdinalIgnoreCase) ||
             id.Contains("thinking", StringComparison.OrdinalIgnoreCase)))
        {
            canDisable = true;
            return true;
        }

        return false;
    }

    private static bool IsDashScopeThinkingOnlyModel(string id)
    {
        if (id.Contains("qwen3.7-max-preview", StringComparison.OrdinalIgnoreCase) ||
            id.Contains("qwen3.7-max-2026-05-17", StringComparison.OrdinalIgnoreCase))
            return true;
        if (Regex.IsMatch(id, @"(^|[/_:@])qwq(?:$|[-.])", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(id, @"(^|[/_:@])deepseek-r1(?:$|[-.])", RegexOptions.IgnoreCase))
            return true;
        if (id.Contains("kimi-k2.7-code", StringComparison.OrdinalIgnoreCase) ||
            id.Contains("kimi-k2-thinking", StringComparison.OrdinalIgnoreCase))
            return true;
        if (Regex.IsMatch(id, @"(^|[/_:@])minimax-m2\.(?:1|5)(?:$|[-.])", RegexOptions.IgnoreCase))
            return true;
        return id.Contains("-thinking", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKimiK3(string modelId)
    {
        string id = NormalizeModelId(modelId);
        return Regex.IsMatch(id, @"^(?:kimi-k3|k3)(?:$|[-.:])", RegexOptions.IgnoreCase);
    }

    private static string NormalizeModelId(string modelId)
    {
        string id = modelId.Trim();
        int slash = id.LastIndexOf('/');
        if (slash >= 0)
            id = id[(slash + 1)..];

        int at = id.LastIndexOf('@');
        if (at > 0 && id[(at + 1)..].All(char.IsDigit))
            id = id[..at];

        return id.ToLowerInvariant();
    }

    private static int ResolveBudget(string effort) => effort switch
    {
        "minimal" => 512,
        "low" => 1024,
        "medium" => 4096,
        "high" => 8192,
        "xhigh" => 16000,
        "max" => 32000,
        _ => 4096
    };
}
