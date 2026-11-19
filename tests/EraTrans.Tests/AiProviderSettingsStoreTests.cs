using EraTrans.Core;
using System.Runtime.Versioning;
using Xunit;

namespace EraTrans.Tests;

[SupportedOSPlatform("windows")]
public sealed class AiProviderSettingsMigrationTests
{
    [Fact]
    public void Store_LoadLegacyAppDataSettings_MigratesEncryptedApiKeyAndKeepsSource()
    {
        using var temporary = new TemporaryDirectory();
        string legacyPath = Path.Combine(temporary.Path, "AppData", "EraTrans", "ai-providers.json");
        string portablePath = Path.Combine(temporary.Path, "Software", "配置", "ai-providers.json");

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

        var legacyStore = new AiProviderSettingsStore(legacyPath);
        legacyStore.Save(originalSettings);
        byte[] originalLegacyFile = File.ReadAllBytes(legacyPath);

        var portableStore = new AiProviderSettingsStore(portablePath, legacyPath);
        AiProviderSettings loaded = portableStore.Load();

        Assert.Equal("provider-1", loaded.ActiveProviderId);
        Assert.Single(loaded.Providers);
        Assert.Equal("secret-test-api-key", loaded.Providers[0].ApiKey);
        Assert.True(File.Exists(portablePath));
        Assert.Equal(originalLegacyFile, File.ReadAllBytes(legacyPath));
        Assert.DoesNotContain("secret-test-api-key", File.ReadAllText(portablePath));
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
