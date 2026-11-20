using EraTrans.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace EraTrans.Tests;

public sealed class RealGameErbFixtureTests
{
    private static readonly IReadOnlyDictionary<string, string> ExpectedJapaneseText =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [@"ERB\コマンド\COMF400_移動.ERB"] = "どうしますか？",
            [@"ERB\シナリオ\サブイベント\サブイベント_商業区建設解禁.ERB"] = "お店が足りない、ですか～",
            [@"ERB\シナリオ\メインシナリオ\メインシナリオ_航路開拓計画.ERB"] = "しかし……如何せん、島の立地が不便に過ぎる。",
            [@"ERB\一日開始時処理\起床時告白処理.ERB"] = "少し考えさせて欲しいと言う",
            [@"ERB\起床前メニュー関連処理\料理レシピ\食堂メニュー設定処理.ERB"] = "現在のメニュー一覧",
            [@"ERB\探索\アイテムデータ\ITEM_薬草.ERB"] = "味方単体のＨＰを[１００]回復する。"
        };

    [Fact]
    public void Extract_CompleteJapaneseRealGameFiles_GeneratesMeaningfulSemanticContexts()
    {
        string gameDirectory = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "RealGames",
            "erablue_resort");
        Assert.True(Directory.Exists(gameDirectory), $"Japanese real-game fixture directory is missing: {gameDirectory}");

        string[] actualSourcePaths = Directory.EnumerateFiles(
                gameDirectory,
                "*.ERB",
                SearchOption.AllDirectories)
            .Select(path => NormalizePath(Path.GetRelativePath(gameDirectory, path)))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        string[] expectedSourcePaths = ExpectedJapaneseText.Keys
            .Select(NormalizePath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expectedSourcePaths, actualSourcePaths);

        using var temporary = new TemporaryDirectory();
        EraTransOutputLayout layout = EraTransOutputLayout.Create(
            Path.Combine(temporary.Path, "Output"),
            gameDirectory);

        DictionaryExtractionResult extraction = new DictionaryExtractor().Extract(
            gameDirectory,
            layout.DictionaryDirectory,
            options: new EraParserOptions
            {
                HideEnglishText = false
            });

        string[] sidecars = (extraction.UpdatedGroupingFiles ?? Array.Empty<string>()).ToArray();
        Assert.Equal(
            ExpectedJapaneseText.Count,
            sidecars.Length);

        var groupingService = new ErbTranslationGroupingService();
        var dictionaryService = new DictionaryEditorService();
        var documentsBySource = new Dictionary<string, DictionaryEditorDocument>(StringComparer.Ordinal);
        var indexesBySource = new Dictionary<string, ErbTranslationGroupingIndex>(StringComparer.Ordinal);

        foreach (string sidecarPath in sidecars)
        {
            string relativeSidecar = NormalizePath(
                Path.GetRelativePath(layout.AiContextDirectory, sidecarPath));
            const string suffix = ".groups.json";
            Assert.EndsWith(suffix, relativeSidecar, StringComparison.OrdinalIgnoreCase);

            string relativeDictionary = relativeSidecar[..^suffix.Length] + ".json";
            string sourceRelativePath = relativeDictionary[..^".json".Length] + ".ERB";
            Assert.Contains(expectedSourcePaths, path =>
                string.Equals(path, sourceRelativePath, StringComparison.Ordinal));

            string dictionaryPath = Path.Combine(
                layout.DictionaryDirectory,
                relativeDictionary.Replace('/', Path.DirectorySeparatorChar));
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

            JArray dictionaryJson = JArray.Parse(File.ReadAllText(dictionaryPath));
            Assert.All(dictionaryJson, entry =>
            {
                Assert.NotNull(entry["key"]);
                Assert.NotNull(entry["original"]);
                Assert.NotNull(entry["translation"]);
                Assert.NotNull(entry["stage"]);
                Assert.NotNull(entry["context"]);
                Assert.Equal(5, ((JObject)entry!).Properties().Count());
            });

            DictionaryEditorEntry[] textEntries = document.Entries
                .Where(entry => entry.Key.StartsWith("文本", StringComparison.Ordinal))
                .ToArray();
            Assert.NotEmpty(textEntries);
            Assert.Equal(textEntries.Length, index!.Entries.Count);

            string expectedText = ExpectedJapaneseText.Single(pair =>
                string.Equals(NormalizePath(pair.Key), sourceRelativePath, StringComparison.Ordinal)).Value;
            Assert.Contains(textEntries, entry =>
                entry.Original.Contains(expectedText, StringComparison.Ordinal));

            documentsBySource.Add(sourceRelativePath, document);
            indexesBySource.Add(sourceRelativePath, index);

            DictionaryEditorEntry[] pendingTargets = textEntries
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

            string[] batchedTargetKeys = batches
                .SelectMany(batch => batch.Targets)
                .Select(entry => entry.Key)
                .ToArray();
            string[] expectedTargetKeys = pendingTargets
                .Select(entry => entry.Key)
                .ToArray();
            Assert.Equal(
                expectedTargetKeys.OrderBy(key => key, StringComparer.Ordinal),
                batchedTargetKeys.OrderBy(key => key, StringComparer.Ordinal));
            Assert.Equal(batchedTargetKeys.Length, batchedTargetKeys.Distinct(StringComparer.Ordinal).Count());

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

        Assert.Equal(ExpectedJapaneseText.Count, documentsBySource.Count);
        Assert.Equal(ExpectedJapaneseText.Count, indexesBySource.Count);

        // Real continuous narrative: a selected story line must receive neighboring
        // lines from the same output passage as read-only AI context.
        const string storyPath = "ERB/シナリオ/サブイベント/サブイベント_商業区建設解禁.ERB";
        DictionaryEditorDocument storyDocument = documentsBySource[storyPath];
        ErbTranslationGroupingIndex storyIndex = indexesBySource[storyPath];
        DictionaryEditorEntry storyTarget = Assert.Single(storyDocument.Entries.Where(entry =>
            entry.Original.Contains("居住区が出来て", StringComparison.Ordinal)));
        ErbTranslationBatch storyBatch = Assert.Single(groupingService.BuildTranslationBatches(
            [storyTarget],
            storyDocument.Entries.ToArray(),
            storyIndex));
        AiTranslationSemanticContextGroup storyContext = Assert.Single(
            storyBatch.SemanticContextGroups.Where(group => group.Relationship == "same-group"));
        Assert.Contains(storyContext.Entries, entry =>
            entry.Original.Contains("様々な職業", StringComparison.Ordinal));

        // Real menu: when one choice is the target, the other choice and the prompt
        // from the same screen must be present as read-only context.
        const string confessionPath = "ERB/一日開始時処理/起床時告白処理.ERB";
        DictionaryEditorDocument confessionDocument = documentsBySource[confessionPath];
        ErbTranslationGroupingIndex confessionIndex = indexesBySource[confessionPath];
        DictionaryEditorEntry choiceTarget = Assert.Single(confessionDocument.Entries.Where(entry =>
            entry.Original.Contains("想いに応える", StringComparison.Ordinal)));
        Assert.Contains(confessionIndex.Groups, group =>
            group.Type == "menu" && group.EntryKeys.Contains(choiceTarget.Key, StringComparer.Ordinal));

        ErbTranslationBatch choiceBatch = Assert.Single(groupingService.BuildTranslationBatches(
            [choiceTarget],
            confessionDocument.Entries.ToArray(),
            confessionIndex));
        AiTranslationSemanticContextGroup choiceContext = Assert.Single(
            choiceBatch.SemanticContextGroups.Where(group => group.Relationship == "same-group"));
        Assert.Contains(choiceContext.Entries, entry =>
            entry.Original.Contains("少し考えさせて欲しいと言う", StringComparison.Ordinal));
        Assert.Contains(choiceContext.Entries, entry =>
            entry.Original.Contains("想いに、", StringComparison.Ordinal));
    }

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/');

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "EraTransJapaneseRealGameFixtures",
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
