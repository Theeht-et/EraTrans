using EraTrans.Core;
using Xunit;

namespace EraTrans.Tests;

public sealed class AiProviderPresetsTests
{
    [Fact]
    public void All_ContainsOnlyTheCuratedMainstreamProvidersWithoutParentheticalNotes()
    {
        string[] names = AiProviderPresets.All.Select(preset => preset.Name).ToArray();

        Assert.Equal(9, names.Length);
        Assert.Contains("OpenAI", names);
        Assert.Contains("DeepSeek", names);
        Assert.Contains("通义千问", names);
        Assert.Contains("智谱 GLM", names);
        Assert.Contains("Kimi", names);
        Assert.Contains("火山引擎方舟", names);
        Assert.Contains("硅基流动", names);
        Assert.Contains("OpenRouter", names);
        Assert.Contains("Grok", names);

        Assert.DoesNotContain(names, name => name.Contains('（') || name.Contains('('));
        Assert.DoesNotContain(names, name => name.Contains("Gemini", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("Claude", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DeepSeekPreset_UsesItsOpenAiCompatibleBaseUrl()
    {
        AiProviderPreset preset = Assert.Single(AiProviderPresets.All, item => item.Name == "DeepSeek");
        Assert.Equal("https://api.deepseek.com", preset.BaseUrl);
    }
}