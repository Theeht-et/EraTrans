using EraTrans.Core;
using Xunit;

namespace EraTrans.Tests;

public sealed class AiTranslationLogStoreTests
{
    [Fact]
    public void SaveAndLoad_UpdatesTheSameRecordAndReturnsNewestFirst()
    {
        using var temporary = new TemporaryDirectory();
        var store = new AiTranslationLogStore(temporary.Path);
        var first = new AiTranslationLogEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            ProviderName = "Test provider",
            Model = "test-model",
            Endpoint = "https://example.com/v1/chat/completions",
            SystemPrompt = "system",
            UserPrompt = "user",
            RequestJson = """{"model":"test-model"}""",
            Status = AiTranslationLogStatus.InProgress
        };
        var second = new AiTranslationLogEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            StartedAtUtc = DateTimeOffset.UtcNow,
            ProviderName = "Test provider",
            Model = "test-model-2",
            Status = AiTranslationLogStatus.Succeeded
        };

        store.Save(first);
        first.Status = AiTranslationLogStatus.Failed;
        first.RawResponse = "raw reply";
        first.ErrorMessage = "parse failure";
        first.CompletedAtUtc = DateTimeOffset.UtcNow;
        first.DurationMilliseconds = 123;
        store.Save(first);
        store.Save(second);

        IReadOnlyList<AiTranslationLogEntry> loaded = store.LoadAll();
        Assert.Equal(2, loaded.Count);
        Assert.Equal(second.Id, loaded[0].Id);

        AiTranslationLogEntry updated = loaded.Single(entry => entry.Id == first.Id);
        Assert.Equal(AiTranslationLogStatus.Failed, updated.Status);
        Assert.Equal("raw reply", updated.RawResponse);
        Assert.Equal("parse failure", updated.ErrorMessage);
        Assert.Equal(123, updated.DurationMilliseconds);
        string path = Directory.GetFiles(temporary.Path, "*.json")
            .Single(file => Path.GetFileName(file).Contains(first.Id, StringComparison.Ordinal));
        Assert.Contains("\"failed\"", File.ReadAllText(path));
    }

    [Fact]
    public void DeleteAll_RemovesSavedRecords()
    {
        using var temporary = new TemporaryDirectory();
        var store = new AiTranslationLogStore(temporary.Path);
        store.Save(new AiTranslationLogEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            StartedAtUtc = DateTimeOffset.UtcNow,
            ProviderName = "Test",
            Model = "model"
        });

        store.DeleteAll();

        Assert.Empty(store.LoadAll());
        Assert.Empty(Directory.GetFiles(temporary.Path));
    }

    [Fact]
    public void LoadAll_SkipsOneCorruptedFileWithoutLosingOtherRecords()
    {
        using var temporary = new TemporaryDirectory();
        var store = new AiTranslationLogStore(temporary.Path);
        store.Save(new AiTranslationLogEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            StartedAtUtc = DateTimeOffset.UtcNow,
            ProviderName = "Valid",
            Model = "model"
        });
        File.WriteAllText(Path.Combine(temporary.Path, "corrupted.json"), "{invalid");

        AiTranslationLogEntry loaded = Assert.Single(store.LoadAll());
        Assert.Equal("Valid", loaded.ProviderName);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "EraTransTranslationLogs",
            Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
