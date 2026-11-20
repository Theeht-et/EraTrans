using EraTrans.Core;
using Xunit;

namespace EraTrans.Tests;

public sealed class AiProviderPresetsTests
{
    [Fact]
    public void All_ContainsCommonDomesticAndNativeInternationalProviders()
    {
        Assert.Contains(AiProviderPresets.All, preset => preset.Name.Contains("OpenAI", StringComparison.Ordinal));
        Assert.Contains(AiProviderPresets.All, preset => preset.Name.Contains("DeepSeek", StringComparison.Ordinal));
        Assert.Contains(AiProviderPresets.All, preset => preset.Name.Contains("通义千问", StringComparison.Ordinal));
        Assert.Contains(AiProviderPresets.All, preset => preset.Name.Contains("智谱", StringComparison.Ordinal));
        Assert.Contains(AiProviderPresets.All, preset => preset.Name.Contains("Kimi", StringComparison.Ordinal));
        Assert.Contains(AiProviderPresets.All, preset => preset.Name.Contains("火山引擎", StringComparison.Ordinal));
        Assert.Contains(AiProviderPresets.All, preset => preset.Name.Contains("硅基流动", StringComparison.Ordinal));

        Assert.Contains(AiProviderPresets.All, preset =>
            preset.Protocol == AiProviderProtocol.GoogleGemini);
        Assert.Contains(AiProviderPresets.All, preset =>
            preset.Protocol == AiProviderProtocol.AnthropicClaude);
    }

    [Fact]
    public void DeepSeekPreset_UsesCurrentOpenAiCompatibleBaseUrl()
    {
        AiProviderPreset preset = Assert.Single(
            AiProviderPresets.All,
            item => item.Name.StartsWith("DeepSeek", StringComparison.Ordinal));

        Assert.Equal("https://api.deepseek.com", preset.BaseUrl);
        Assert.Equal(AiProviderProtocol.OpenAiCompatible, preset.Protocol);
    }
}
