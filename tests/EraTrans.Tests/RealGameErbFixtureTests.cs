using EraTrans.Core;
using Xunit;

namespace EraTrans.Tests;

public sealed class RealGameErbFixtureTests
{
    [Fact]
    public void Extract_RealEraNasCorpus_CreatesLoadableSidecarsAndSemanticBatches()
    {
        string gameDirectory = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "RealGames",
            "eraNAS");
        Assert.True(Directory.Exists(gameDirectory), $"Real-game fixture directory is missing: {gameDirectory}");

        string[] sourceFiles = Directory.EnumerateFiles(
            gameDirectory,
            "*.ERB",
            SearchOption.AllDirectories).ToArray();
        Assert.True(sourceFiles.Length >= 5, $"Expected the curated eraNAS ERB corpus; found {sourceFiles.Length} files.");

        using var temporary = new TemporaryDirectory();
        EraTransOutputLayout layout = EraTransOutputLayout.Create(
            Path.Combine(temporary.Path, "Output"),
            gameDirectory);

        DictionaryExtractionResult result = new DictionaryExtractor().Extract(
            gameDirectory,
            layout.DictionaryDirectory,
            options: new EraParserOptions
            {
                HideEnglishText = false
            });

        string[] sidecars = (result.UpdatedGroupingFiles ?? Array.Empty<string>()).ToArray();
        Assert.True(
            sidecars.Length >= 3,
            $"Expected sidecars for several real-game scripts. Created: {sidecars.Length}. Warnings: {string.Join(" | ", result.Warnings)}");

        var groupingService = new ErbTranslationGroupingService();
        var dictionaryService = new DictionaryEditorService();
        var seenOriginals = new HashSet<string>(StringComparer.Ordinal);
        int loadedGroupCount = 0;
        int translatedBatchCount = 0;

        foreach (string sidecarPath in sidecars)
        {
            string relativeSidecar = Path.GetRelativePath(layout.AiContextDirectory, sidecarPath);
            const string suffix = ".groups.json";
            Assert.EndsWith(suffix, relativeSidecar, StringComparison.OrdinalIgnoreCase);
            string relativeDictionary = relativeSidecar[..^suffix.Length] + ".json";
            string dictionaryPath = Path.Combine(layout.DictionaryDirectory, relativeDictionary);

            DictionaryEditorDocument document = dictionaryService.Load(dictionaryPath);
            Assert.True(
                groupingService.TryLoad(
                    layout.DictionaryDirectory,
                    dictionaryPath,
                    document.Entries.ToArray(),
                    gameDirectory,
                    out ErbTranslationGroupingIndex? index,
                    out string? reason),
                reason);

            Assert.NotNull(index);
            loadedGroupCount += index!.Groups.Count;
            foreach (DictionaryEditorEntry entry in document.Entries)
            {
                if (entry.Key.StartsWith("文本", StringComparison.Ordinal))
                {
                    seenOriginals.Add(entry.Original);
                }
            }

            DictionaryEditorEntry[] pendingTargets = document.Entries
                .Where(entry =>
                    entry.IsEditable &&
                    entry.Stage == 0 &&
                    string.IsNullOrWhiteSpace(entry.Translation) &&
                    !string.IsNullOrWhiteSpace(entry.Original))
                .ToArray();
            Assert.NotEmpty(pendingTargets);

            IReadOnlyList<ErbTranslationBatch> batches = groupingService.BuildTranslationBatches(
                pendingTargets,
                document.Entries.ToArray(),
                index);
            Assert.NotEmpty(batches);
            translatedBatchCount += batches.Count;

            foreach (ErbTranslationBatch batch in batches)
            {
                HashSet<string> targetKeys = batch.Targets
                    .Select(entry => entry.Key)
                    .ToHashSet(StringComparer.Ordinal);
                Assert.DoesNotContain(
                    batch.SemanticContextGroups.SelectMany(group => group.Entries),
                    context => targetKeys.Contains(context.Key));
            }
        }

        Assert.True(loadedGroupCount >= sidecars.Length);
        Assert.True(translatedBatchCount >= sidecars.Length);
        Assert.Contains(seenOriginals, original => original.Contains("Prebuilt toilet", StringComparison.Ordinal));
        Assert.Contains(seenOriginals, original => original.Contains("A robber comes to your stall", StringComparison.Ordinal));
        Assert.Contains(seenOriginals, original => original.Contains("Scented Bags", StringComparison.Ordinal));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "EraTransRealGameFixtures",
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
