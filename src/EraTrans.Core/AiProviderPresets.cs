namespace EraTrans.Core;

public sealed record AiProviderPreset(string Name, string BaseUrl);

public static class AiProviderPresets
{
    public static IReadOnlyList<AiProviderPreset> All { get; } =
    [
        new("OpenAI", "https://api.openai.com/v1"),
        new("DeepSeek", "https://api.deepseek.com"),
        new("通义千问", "https://dashscope.aliyuncs.com/compatible-mode/v1"),
        new("智谱 GLM", "https://open.bigmodel.cn/api/paas/v4"),
        new("Kimi（月之暗面）", "https://api.moonshot.cn/v1"),
        new("火山引擎方舟（豆包）", "https://ark.cn-beijing.volces.com/api/v3"),
        new("硅基流动", "https://api.siliconflow.cn/v1"),
        new("OpenRouter", "https://openrouter.ai/api/v1"),
        new("Grok（xAI）", "https://api.x.ai/v1")
    ];
}