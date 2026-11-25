using System.Text;
using System.Text.Json;
using EraTrans.Core;
using Xunit;

namespace EraTrans.Tests;

public sealed class VariableGlossaryServiceTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "EraTrans.GlossaryTests." + Guid.NewGuid().ToString("N"));

    public VariableGlossaryServiceTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Fact]
    public void SyncFromGameDirectory_ExtractsOnlyVisibleKanaEntriesAndPreservesExistingTranslations()
    {
        string gameDir = Path.Combine(_tempRoot, "TestGame");
        string csvDir = Path.Combine(gameDir, "CSV");
        string outputRoot = Path.Combine(_tempRoot, "Output");
        Directory.CreateDirectory(csvDir);

        File.WriteAllText(
            Path.Combine(csvDir, "Item.csv"),
            """
            ; comment
            0,ローター,100
            1,酒,200
            2,アナルビーズ,300 ; inline comment
            """,
            Encoding.UTF8);

        File.WriteAllText(
            Path.Combine(csvDir, "Talent.csv"),
            """
            0,処女
            1,恋慕
            2,キス未経験
            3,プライド高い
            """,
            Encoding.UTF8);

        File.WriteAllText(
            Path.Combine(csvDir, "Flag.csv"),
            """
            0,休憩フラグ
            1,システム設定
            """,
            Encoding.UTF8);

        File.WriteAllText(
            Path.Combine(csvDir, "Str.csv"),
            """
            0,トイレ
            """,
            Encoding.UTF8);

        EraTransOutputLayout layout = EraTransOutputLayout.Create(outputRoot, gameDir, EraExtractionMode.V2);
        var service = new VariableGlossaryService();

        VariableGlossaryDocument first = service.SyncFromGameDirectory(layout);

        Assert.Equal(4, first.Entries.Count);
        Assert.Contains(first.Entries, e => e.Table == "ITEM" && e.Original == "ローター");
        Assert.Contains(first.Entries, e => e.Table == "ITEM" && e.Original == "アナルビーズ");
        Assert.Contains(first.Entries, e => e.Table == "TALENT" && e.Original == "キス未経験");
        Assert.Contains(first.Entries, e => e.Table == "TALENT" && e.Original == "プライド高い");
        // Pure kanji, internal flags, and STR.csv must not be extracted.
        Assert.DoesNotContain(first.Entries, e => e.Original is "酒" or "処女" or "恋慕" or "休憩フラグ" or "トイレ");

        // User translates one term and saves.
        VariableGlossaryEntry[] edited = first.Entries
            .Select(e => e.Original == "ローター" ? e with { Translation = "跳蛋" } : e)
            .ToArray();
        service.Save(layout, first with { Entries = edited });

        // Re-syncing preserves existing translation and leaves original game CSV untouched.
        VariableGlossaryDocument second = service.SyncFromGameDirectory(layout);
        Assert.Equal("跳蛋", second.Entries.Single(e => e.Original == "ローター").Translation);
        Assert.True(File.Exists(VariableGlossaryService.GetGlossaryPath(layout)));
    }

    [Theory]
    [InlineData("CSV/Item.json", true)]
    [InlineData(@"CSV\Talent.json", true)]
    [InlineData("CSV/Flag.json", true)]
    [InlineData("CSV/Cflag.json", true)]
    [InlineData("CSV/Str.json", false)]
    [InlineData("CSV/Chara001.json", false)]
    [InlineData("ERB/SOURCE.json", false)]
    public void IsManagedVariableCsvDictionary_ShieldsOnlyVariableTables(string relativePath, bool expected)
    {
        Assert.Equal(expected, VariableGlossaryService.IsManagedVariableCsvDictionary(relativePath));
    }

    [Fact]
    public async Task TranslateWindowAsync_InjectsOnlyMatchedGlossaryTermsAlongsidePlaceholderMasking()
    {
        var glossary = new[]
        {
            new VariableGlossaryEntry("ITEM", "ローター", "跳蛋"),
            new VariableGlossaryEntry("TRAIN", "キス", "接吻"),
            new VariableGlossaryEntry("TALENT", "プライド高い", "自尊心强"),
            new VariableGlossaryEntry("ITEM", "オナホール", "")
        };

        var fakeClient = new RecordingClient("""{"1":"使用了跳蛋([[P0]])"}""");
        var service = new AiTranslationService(fakeClient);
        var profile = new AiProviderProfile
        {
            Id = "test",
            Name = "Test",
            BaseUrl = "https://example.invalid/v1",
            ApiKey = "sk-test",
            Model = "gpt-test"
        };
        var window = new TranslationWindow(
            "@USE_ITEM",
            1,
            [
                new WindowLine(1, "k1", "ローターを使った({SOURCE:恭順})", "@USE_ITEM", true, false, 0, false, null)
            ]);

        WindowTranslationOutcome outcome = await service.TranslateWindowAsync(
            profile,
            window,
            reasoning: null,
            glossary: glossary);

        using JsonDocument sent = JsonDocument.Parse(fakeClient.LastUserPrompt);
        JsonElement glossaryElement = sent.RootElement.GetProperty("glossary");
        Assert.Equal("跳蛋", glossaryElement.GetProperty("ローター").GetString());
        Assert.False(glossaryElement.TryGetProperty("キス", out _));
        Assert.False(glossaryElement.TryGetProperty("プライド高い", out _));
        Assert.False(glossaryElement.TryGetProperty("オナホール", out _));

        // Placeholder masking still protects {SOURCE:恭順}.
        Assert.DoesNotContain("{SOURCE:恭順}", fakeClient.LastUserPrompt, StringComparison.Ordinal);
        Assert.Equal("使用了跳蛋({SOURCE:恭順})", Assert.Single(outcome.Translations).Translation);
    }

    private sealed class RecordingClient(string response) : IAiChatCompletionClient
    {
        public string LastUserPrompt { get; private set; } = string.Empty;

        public Task<string> CompleteAsync(
            AiProviderProfile profile,
            string systemPrompt,
            string userPrompt,
            CancellationToken cancellationToken)
        {
            LastUserPrompt = userPrompt;
            return Task.FromResult(response);
        }
    }
}
