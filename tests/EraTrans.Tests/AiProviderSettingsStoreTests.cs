using EraTrans.Core;
using System.Runtime.Versioning;
using Xunit;

namespace EraTrans.Tests;

[SupportedOSPlatform("windows")]
public sealed class AiProviderSettingsStoreTests
{
    [Fact]
    public void Store_SaveAndLoad_RoundTripsProviderSettingsAndEncryptsApiKey()
    {
        using var temporary = new TemporaryDirectory();
        string path = Path.Combine(temporary.Path, "Config", "ai-providers.json");
        var originalSettings = new AiProviderSettings
        {
            ActiveProviderId = "provider-1",
            Providers =
            [
                new AiProviderProfile
                {
                    Id = "provider-1",
                    Name = "Test provider",
                    BaseUrl = "https://api.example.com/v1",
                    Model = "test-model",
                    ApiKey = "secret-test-api-key"
                }
            ]
        };

        var store = new AiProviderSettingsStore(path);
        store.Save(originalSettings);
        AiProviderSettings loaded = store.Load();

        Assert.Equal("provider-1", loaded.ActiveProviderId);
        AiProviderProfile provider = Assert.Single(loaded.Providers);
        Assert.Equal("Test provider", provider.Name);
        Assert.Equal("https://api.example.com/v1", provider.BaseUrl);
        Assert.Equal("test-model", provider.Model);
        Assert.Equal("secret-test-api-key", provider.ApiKey);
        Assert.True(File.Exists(path));
        Assert.DoesNotContain("secret-test-api-key", File.ReadAllText(path));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "EraTransAiSettingsTests",
            Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
