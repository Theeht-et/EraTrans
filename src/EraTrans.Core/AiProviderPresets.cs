namespace EraTrans.Core;

public sealed record AiProviderPreset(
    string Name,
    string BaseUrl,
    AiProviderProtocol Protocol);

public static class AiProviderPresets
{
    public static IReadOnlyList<AiProviderPreset> All { get; } =
    [
        new("OpenAI 官方", "https://api.openai.com/v1", AiProviderProtocol.OpenAiCompatible),
        new("DeepSeek 深度求索", "https://api.deepseek.com", AiProviderProtocol.OpenAiCompatible),
        new("通义千问（中国大陆）", "https://dashscope.aliyuncs.com/compatible-mode/v1", AiProviderProtocol.OpenAiCompatible),
        new("通义千问（国际站）", "https://dashscope-intl.aliyuncs.com/compatible-mode/v1", AiProviderProtocol.OpenAiCompatible),
        new("智谱 GLM", "https://open.bigmodel.cn/api/paas/v4", AiProviderProtocol.OpenAiCompatible),
        new("月之暗面 Kimi（中国大陆）", "https://api.moonshot.cn/v1", AiProviderProtocol.OpenAiCompatible),
        new("月之暗面 Kimi（国际站）", "https://api.moonshot.ai/v1", AiProviderProtocol.OpenAiCompatible),
        new("火山引擎方舟（北京）", "https://ark.cn-beijing.volces.com/api/v3", AiProviderProtocol.OpenAiCompatible),
        new("硅基流动（中国大陆）", "https://api.siliconflow.cn/v1", AiProviderProtocol.OpenAiCompatible),
        new("OpenRouter", "https://openrouter.ai/api/v1", AiProviderProtocol.OpenAiCompatible),
        new("Google Gemini（原生 API）", "https://generativelanguage.googleapis.com/v1beta", AiProviderProtocol.GoogleGemini),
        new("Anthropic Claude（原生 API）", "https://api.anthropic.com", AiProviderProtocol.AnthropicClaude)
    ];
}
