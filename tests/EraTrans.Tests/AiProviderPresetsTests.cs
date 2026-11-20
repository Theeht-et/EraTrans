using EraTrans.Core;
using Xunit;

namespace EraTrans.Tests;

public sealed class AiProviderPresetsTests
{
    [Fact]
    public void All_ContainsOnlyTheCuratedMainstreamProviders()
    {
        string[] names = AiProviderPresets.All.Select(preset => preset.Name).ToArray();

        Assert.Equal(9, names.Length);
        Assert.Contains(names, name => name == "OpenAI");
        Assert.Contains(names, name => name == "DeepSeek");
        Assert.Contains(names, name => name.Contains("通义千问", StringComparison.Ordinal));
        Assert.Contains(names, name => name.Contains("智谱 GLM", StringComparison.Ordinal));
        Assert.Contains(names, name => name.Contains("Kimi", StringComparison.Ordinal));
        Assert.Contains(names, name => name.Contains("火山引擎方舟", StringComparison.Ordinal));
        Assert.Contains(names, name => name.Contains("硅基流动", StringComparison.Ordinal));
        Assert.Contains(names, name => name == "OpenRouter");
        Assert.Contains(names, name => name.Contains("Grok", StringComparison.Ordinal));

        Assert.DoesNotContain(names, name => name.Contains("Gemini", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("Claude", StringComparison.OrdinalIgnoreCase));
        Assert.All(AiProviderPresets.All, preset =>
        {
            Assert.False(string.IsNullOrWhiteSpace(preset.Name));
            Assert.StartsWith("https://", preset.BaseUrl);
        });
    }

    [Fact]
    public void DeepSeekPreset_UsesItsOpenAiCompatibleBaseUrl()
    {
        AiProviderPreset preset = Assert.Single(AiProviderPresets.All, item => item.Name == "DeepSeek");
        Assert.Equal("https://api.deepseek.com", preset.BaseUrl);
    }
}