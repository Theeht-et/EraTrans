using EraTrans.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace EraTrans.Tests;

/// <summary>
/// Runs the extractor against complete, unmodified Japanese ERB files from a real
/// game so the sidecar and dictionary contracts stay honest on real input.
/// </summary>
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
    public void Extract_CompleteJapaneseRealGameFiles_WritesUsableGroupingSidecars()
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
        Assert.Equal(ExpectedJapaneseText.Count, sidecars.Length);

        var groupingService = new ErbTranslationGroupingService();
        var dictionaryService = new DictionaryEditorService();
        int checkedFiles = 0;

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

            // Every entry must belong to exactly one group whose member list agrees
            // with the entry records; the window planner relies on this.
            var groupByEntry = index.Groups
                .SelectMany(group => group.EntryKeys.Select(key => (key, group)))
                .ToDictionary(pair => pair.key, pair => pair.group, StringComparer.Ordinal);
            Assert.Equal(index.Entries.Count, groupByEntry.Count);
            Assert.All(index.Entries, entry => Assert.True(groupByEntry.ContainsKey(entry.Key)));

            checkedFiles++;
        }

        Assert.Equal(ExpectedJapaneseText.Count, checkedFiles);
    }

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/');

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "EraTransFixtureTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
