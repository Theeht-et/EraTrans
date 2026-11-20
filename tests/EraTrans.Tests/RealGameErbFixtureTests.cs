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
    public async Task Extract_CompleteJapaneseRealGameFiles_GeneratesMeaningfulSemanticContexts()
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
        DictionaryEditorEntry storyTarget = Assert.Single(
            storyDocument.Entries,
            entry => entry.Original.Contains("居住区が出来て", StringComparison.Ordinal));
        ErbTranslationBatch storyBatch = Assert.Single(groupingService.BuildTranslationBatches(
            [storyTarget],
            storyDocument.Entries.ToArray(),
            storyIndex));
        AiTranslationSemanticContextGroup storyContext = Assert.Single(
            storyBatch.SemanticContextGroups,
            group => group.Relationship == "same-group");
        Assert.Contains(storyContext.Entries, entry =>
            entry.Original.Contains("様々な職業", StringComparison.Ordinal));

        // Real menu: when one choice is the target, the other choice and the prompt
        // from the same screen must be present as read-only context.
        const string confessionPath = "ERB/一日開始時処理/起床時告白処理.ERB";
        DictionaryEditorDocument confessionDocument = documentsBySource[confessionPath];
        ErbTranslationGroupingIndex confessionIndex = indexesBySource[confessionPath];
        // This complete file contains both confession and proposal menus. The first
        // occurrence is the confession menu, whose prompt and alternate choice follow it.
        DictionaryEditorEntry choiceTarget = confessionDocument.Entries.First(entry =>
            entry.Original.Contains("想いに応える", StringComparison.Ordinal));
        Assert.Contains(confessionIndex.Groups, group =>
            group.Type == "menu" && group.EntryKeys.Contains(choiceTarget.Key, StringComparer.Ordinal));

        ErbTranslationBatch choiceBatch = Assert.Single(groupingService.BuildTranslationBatches(
            [choiceTarget],
            confessionDocument.Entries.ToArray(),
            confessionIndex));
        AiTranslationSemanticContextGroup choiceContext = Assert.Single(
            choiceBatch.SemanticContextGroups,
            group => group.Relationship == "same-group");
        Assert.Contains(choiceContext.Entries, entry =>
            entry.Original.Contains("少し考えさせて欲しいと言う", StringComparison.Ordinal));
        Assert.Contains(choiceContext.Entries, entry =>
            entry.Original.Contains("想いに、", StringComparison.Ordinal));

        // BINPUT dispatches into IF/ELSE rather than SELECTCASE. Its result
        // branches must remain read-only alternatives associated with this menu.
        AiTranslationSemanticContextGroup[] confessionAlternatives = choiceBatch.SemanticContextGroups
            .Where(group => group.Relationship == "alternative-branch")
            .ToArray();
        Assert.NotEmpty(confessionAlternatives);
        Assert.Contains(confessionAlternatives.SelectMany(group => group.Entries), entry =>
            entry.Original.Contains("笑顔を浮かべて喜んだ", StringComparison.Ordinal));
        Assert.Contains(confessionAlternatives.SelectMany(group => group.Entries), entry =>
            entry.Original.Contains("少し考えさせて欲しいと返す", StringComparison.Ordinal));

        // Verify the final serialized request, not only the in-memory batch:
        // the two menu choices and the IF/ELSE outcomes must reach the AI as
        // read-only semanticContext while only the selected choice is a target.
        var capturingClient = new CapturingChatClient(
            "[{\"id\": " + choiceTarget.Index + ", \"translation\": \"测试译文\"}]");
        var aiService = new AiTranslationService(capturingClient);
        await aiService.TranslateWithSemanticContextAsync(
            new AiProviderProfile
            {
                Name = "Fixture test",
                BaseUrl = "https://example.com/v1",
                Model = "fixture-test",
                ApiKey = "fixture-test-key"
            },
            [new AiTranslationRequest(choiceTarget.Index, choiceTarget.Key, choiceTarget.Original, choiceTarget.Context)],
            choiceBatch.SemanticContextGroups);

        using (System.Text.Json.JsonDocument sentRequest =
               System.Text.Json.JsonDocument.Parse(capturingClient.LastUserPrompt))
        {
            System.Text.Json.JsonElement root = sentRequest.RootElement;
            System.Text.Json.JsonElement sentTarget = Assert.Single(root.GetProperty("targets").EnumerateArray());
            Assert.Equal(choiceTarget.Original, sentTarget.GetProperty("original").GetString());
            Assert.Equal(choiceTarget.Index, sentTarget.GetProperty("id").GetInt32());

            System.Text.Json.JsonElement[] sentContexts = root.GetProperty("semanticContext")
                .EnumerateArray()
                .ToArray();
            Assert.Contains(sentContexts, context =>
                context.GetProperty("type").GetString() == "same-group" &&
                context.GetProperty("texts").EnumerateArray().Any(text =>
                    text.GetString()!.Contains("少し考えさせて欲しいと言う", StringComparison.Ordinal)));
            Assert.Contains(sentContexts, context =>
                context.GetProperty("type").GetString() == "alternative-branch" &&
                context.GetProperty("texts").EnumerateArray().Any(text =>
                    text.GetString()!.Contains("笑顔を浮かべて喜んだ", StringComparison.Ordinal)));
            Assert.DoesNotContain(sentContexts.SelectMany(context =>
                    context.GetProperty("texts").EnumerateArray())
                .Select(text => text.GetString()),
                text => string.Equals(text, choiceTarget.Original, StringComparison.Ordinal));
        }

        // INPUT menu: the prompt and every visible option on the screen are one menu.
        const string movementPath = "ERB/コマンド/COMF400_移動.ERB";
        DictionaryEditorDocument movementDocument = documentsBySource[movementPath];
        ErbTranslationGroupingIndex movementIndex = indexesBySource[movementPath];
        DictionaryEditorEntry movementPrompt = movementDocument.Entries.First(entry =>
            entry.Original.Contains("どうしますか？", StringComparison.Ordinal));
        ErbTranslationBatch movementBatch = Assert.Single(groupingService.BuildTranslationBatches(
            [movementPrompt], movementDocument.Entries.ToArray(), movementIndex));
        AiTranslationSemanticContextGroup movementContext = Assert.Single(
            movementBatch.SemanticContextGroups,
            group => group.Relationship == "same-group");
        Assert.Contains(movementContext.Entries, entry =>
            entry.Original.Contains("そのまま移動する", StringComparison.Ordinal));
        Assert.Contains(movementContext.Entries, entry =>
            entry.Original.Contains("立ち止まる", StringComparison.Ordinal));
        Assert.Contains(movementContext.Entries, entry =>
            entry.Original.Contains("移動が終わるまで立ち止まらない", StringComparison.Ordinal));

        // HTML menu: LOCALS fragments accumulated across loops/conditions and
        // emitted by HTML_PRINT must be one read-only context surface.
        const string foodMenuPath = "ERB/起床前メニュー関連処理/料理レシピ/食堂メニュー設定処理.ERB";
        DictionaryEditorDocument foodMenuDocument = documentsBySource[foodMenuPath];
        ErbTranslationGroupingIndex foodMenuIndex = indexesBySource[foodMenuPath];
        DictionaryEditorEntry foodMenuTarget = foodMenuDocument.Entries.First(entry =>
            entry.Original.Contains("現在のメニュー一覧", StringComparison.Ordinal));
        ErbTranslationBatch foodMenuBatch = Assert.Single(groupingService.BuildTranslationBatches(
            [foodMenuTarget], foodMenuDocument.Entries.ToArray(), foodMenuIndex));
        AiTranslationSemanticContextGroup foodMenuContext = Assert.Single(
            foodMenuBatch.SemanticContextGroups,
            group => group.Relationship == "same-group");
        Assert.Contains(foodMenuContext.Entries, entry =>
            entry.Original.Contains("ルピ収入目安", StringComparison.Ordinal));
        Assert.Contains(foodMenuContext.Entries, entry =>
            entry.Original.Contains("メニューのコンボ効果", StringComparison.Ordinal));
        Assert.Contains(foodMenuContext.Entries, entry =>
            entry.Original.Contains("システムの説明", StringComparison.Ordinal));
        Assert.DoesNotContain(foodMenuContext.Entries, entry =>
            entry.Original.Contains("増加ZP / 1000", StringComparison.Ordinal) ||
            entry.Original.Contains("属性数値文字色変換_HTML", StringComparison.Ordinal));

        // Item detail: LOCALS copied into the detail-text variable is also a
        // rendered HTML/text region, not a set of unrelated string fragments.
        const string herbPath = "ERB/探索/アイテムデータ/ITEM_薬草.ERB";
        DictionaryEditorDocument herbDocument = documentsBySource[herbPath];
        ErbTranslationGroupingIndex herbIndex = indexesBySource[herbPath];
        DictionaryEditorEntry herbEffectTarget = herbDocument.Entries.First(entry =>
            entry.Original.Contains("味方単体のＨＰを[１００]回復する。", StringComparison.Ordinal));
        ErbTranslationBatch herbBatch = Assert.Single(groupingService.BuildTranslationBatches(
            [herbEffectTarget], herbDocument.Entries.ToArray(), herbIndex));
        AiTranslationSemanticContextGroup herbContext = Assert.Single(
            herbBatch.SemanticContextGroups,
            group => group.Relationship == "same-group");
        Assert.Contains(herbContext.Entries, entry =>
            entry.Original.Contains("■薬草", StringComparison.Ordinal));
        Assert.Contains(herbContext.Entries, entry =>
            entry.Original.Contains("様々な傷に効く植物群", StringComparison.Ordinal));
        Assert.Contains(herbContext.Entries, entry =>
            entry.Original.Contains("【効果】", StringComparison.Ordinal));

        // Main-story passage: the selected line must retain the route problem,
        // proposed solution, and the following quest context.
        const string routePath = "ERB/シナリオ/メインシナリオ/メインシナリオ_航路開拓計画.ERB";
        DictionaryEditorDocument routeDocument = documentsBySource[routePath];
        ErbTranslationGroupingIndex routeIndex = indexesBySource[routePath];
        DictionaryEditorEntry routeTarget = routeDocument.Entries.First(entry =>
            entry.Original.Contains("島の立地が不便に過ぎる", StringComparison.Ordinal));
        ErbTranslationBatch routeBatch = Assert.Single(groupingService.BuildTranslationBatches(
            [routeTarget], routeDocument.Entries.ToArray(), routeIndex));
        AiTranslationSemanticContextGroup routeContext = Assert.Single(
            routeBatch.SemanticContextGroups,
            group => group.Relationship == "same-group");
        Assert.Contains(routeContext.Entries, entry =>
            entry.Original.Contains("主要な航路から外れて", StringComparison.Ordinal));
        Assert.Contains(routeContext.Entries, entry =>
            entry.Original.Contains("人食い鳥", StringComparison.Ordinal));
        Assert.Contains(routeContext.Entries, entry =>
            entry.Original.Contains("討伐", StringComparison.Ordinal));
    }

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/');

    private sealed class CapturingChatClient(string response) : IAiChatCompletionClient
    {
        public string LastUserPrompt { get; private set; } = "";

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
