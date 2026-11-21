using EraTrans.Core;
using Xunit;

namespace EraTrans.Tests;

public sealed class AiProviderPresetsTests
{
    [Fact]
    public void Presets_OfferOnlyOpenAiCompatibleProvidersWithPlainNames()
    {
        string[] names = AiProviderPresets.All.Select(preset => preset.Name).ToArray();

        // Native Gemini / Claude protocols are not supported, so they must not
        // be offered as built-in services.
        Assert.DoesNotContain(names, name => name.Contains("Gemini", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("Claude", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains('（') || name.Contains('('));

        Assert.Equal(
            "https://api.deepseek.com",
            Assert.Single(AiProviderPresets.All, item => item.Name == "DeepSeek").BaseUrl);
    }
}
