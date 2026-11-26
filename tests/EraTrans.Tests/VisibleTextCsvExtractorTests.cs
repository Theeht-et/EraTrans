using System.Text;
using EraTrans.Core;
using EraTrans.Core.VisibleText;
using Newtonsoft.Json.Linq;
using Xunit;

namespace EraTrans.Tests;

public sealed class VisibleTextCsvExtractorTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "EraTrans.VisibleTextCsvTests." + Guid.NewGuid().ToString("N"));

    public VisibleTextCsvExtractorTests()
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
    public void LexerMode_ExtractsOnlyVisibleCsvFields_AndSkipsHighRiskNonErbFiles()
    {
        string game = Path.Combine(_tempRoot, "Game");
        string outputRoot = Path.Combine(_tempRoot, "Output");
        string dictRoot = Path.Combine(outputRoot, "字典");

        Write(game, "CSV/Chara/Chara1.csv", string.Join("\r\n",
        [
            "番号,1",
            "名前,博麗 霊夢",
            "呼び名,霊夢",
            "基礎,体力,2000",
            "能力,技巧,2",
            "素質,処女",
            "フラグ,初期位置,9",
            "CSTR,10,～楽園の巫女～（博麗神社）;コメント",
            "CSTR,51,!IS_ROUTE_IN(index)",
            "CSTR,52,4:0|7:0|10:0|12:14",
            "CSTR,種族,人間"
        ]));

        Write(game, "CSV/Str.csv", string.Join("\r\n",
        [
            "1,鳥居",
            "1700,愛撫(慰)",
            "11517,プレイヤーズスコア(東方虹龍洞),ゲームオーバーテーマ,1000,関連キャラに弾幕勝負を挑む"
        ]));

        Write(game, "CSV/GameBase.csv", string.Join("\r\n",
        [
            "コード,9224517",
            "バージョン,proto",
            "タイトル,era_inSchool (Emuera Edition)",
            "ウィンドウタイトル,eratohoK",
            "作者,作者名",
            "製作年,2015-2025",
            "追加情報,※テスト情報"
        ]));

        Write(game, "CSV/_Replace.csv", string.Join("\r\n",
        [
            "お金の単位 , 円",
            "単位の位置 , 後",
            "起動時簡略表示 , 人理修復中...",
            "時間切れ表示 , 時間切れー",
            "汚れの初期値 , 0/0/2/1/8/0/1"
        ]));

        // High-risk non-display files that MUST be skipped in V2 mode.
        Write(game, "CSV/_Rename.csv", "TALENT:0,処女\r\n");
        Write(game, "CSV/Talent.csv", "0,処女\r\n");
        Write(game, "CSV/VariableSize.csv", "FLAG,1000\r\n");
        Write(game, "resources/顔.csv", "顔絵_服_通常_1,1_顔.webp,0,0,180,180\r\n");
        Write(game, "ERB/ソース補正.ERD", "0,快楽\r\n1,欲情\r\n");

        var options = new EraParserOptions { ExtractionMode = ExtractionMode.Lexer };
        new DictionaryExtractor().Extract(game, dictRoot, options: options);

        // High-risk files must NOT generate dictionaries.
        Assert.False(File.Exists(Path.Combine(dictRoot, "CSV", "_Rename.json")));
        Assert.False(File.Exists(Path.Combine(dictRoot, "CSV", "Talent.json")));
        Assert.False(File.Exists(Path.Combine(dictRoot, "CSV", "VariableSize.json")));
        Assert.False(File.Exists(Path.Combine(dictRoot, "CSV", "ERD字典.json")));
        Assert.False(File.Exists(Path.Combine(dictRoot, "resources", "顔.json")));
        Assert.False(File.Exists(Path.Combine(dictRoot, "ERB", "ソース補正.json")));

        // Chara1.csv: only 名前, 呼び名, and Japanese CSTR values (NOT 素質/能力/基礎/CSTR key/code).
        JArray charaEntries = JArray.Parse(File.ReadAllText(Path.Combine(dictRoot, "CSV", "Chara", "Chara1.json")));
        Assert.Equal(
            new[] { "博麗 霊夢", "霊夢", "～楽園の巫女～（博麗神社）", "人間" },
            charaEntries.Select(e => (string)e["original"]!).ToArray());
        Assert.All(charaEntries, entry =>
        {
            string[] props = ((JObject)entry).Properties().Select(p => p.Name).ToArray();
            Assert.Equal(["key", "original", "translation", "stage", "context"], props);
            Assert.StartsWith("文本", (string)entry["key"]!, StringComparison.Ordinal);
        });

        // Str.csv: preserves '(' and splits multi-comma compound rows without numeric columns.
        JArray strEntries = JArray.Parse(File.ReadAllText(Path.Combine(dictRoot, "CSV", "Str.json")));
        Assert.Equal(
            new[]
            {
                "鳥居",
                "愛撫(慰)",
                "プレイヤーズスコア(東方虹龍洞)",
                "ゲームオーバーテーマ",
                "関連キャラに弾幕勝負を挑む"
            },
            strEntries.Select(e => (string)e["original"]!).ToArray());

        // GameBase.csv: preserves '(' and skips コード/バージョン/製作年.
        JArray gameBaseEntries = JArray.Parse(File.ReadAllText(Path.Combine(dictRoot, "CSV", "GameBase.json")));
        Assert.Equal(
            new[] { "era_inSchool (Emuera Edition)", "eratohoK", "作者名", "※テスト情報" },
            gameBaseEntries.Select(e => (string)e["original"]!).ToArray());

        // _Replace.csv: skips 単位の位置 and 汚れの初期値.
        JArray replaceEntries = JArray.Parse(File.ReadAllText(Path.Combine(dictRoot, "CSV", "_Replace.json")));
        Assert.Equal(
            new[] { "円", "人理修復中...", "時間切れー" },
            replaceEntries.Select(e => (string)e["original"]!).ToArray());

        // Exact position sidecars must exist under 辅助数据/.
        Assert.True(File.Exists(Path.Combine(outputRoot, "辅助数据", "CSV", "Chara", "Chara1.spans.json")));
        Assert.True(File.Exists(Path.Combine(outputRoot, "辅助数据", "CSV", "Str.spans.json")));
    }

    [Fact]
    public void LexerMode_CsvExactSpanWriteBack_PreservesCommentsColumnsAndParentheses()
    {
        string game = Path.Combine(_tempRoot, "GameApply");
        string outputRoot = Path.Combine(_tempRoot, "OutputApply");
        string dictRoot = Path.Combine(outputRoot, "字典");
        string copyRoot = Path.Combine(outputRoot, "游戏副本");

        Write(game, "CSV/Chara/Chara1.csv", string.Join("\r\n",
        [
            "番号,1",
            "名前,霊夢",
            "素質,処女",
            "CSTR,種族,人間\t;種族メモ"
        ]));
        Write(game, "CSV/Str.csv", "11517,プレイヤーズスコア(東方虹龍洞),ゲームオーバーテーマ,1000,弾幕勝負\r\n");

        var options = new EraParserOptions { ExtractionMode = ExtractionMode.Lexer };
        new DictionaryExtractor().Extract(game, dictRoot, options: options);

        // Translate Chara1.json
        string charaDictPath = Path.Combine(dictRoot, "CSV", "Chara", "Chara1.json");
        JArray charaEntries = JArray.Parse(File.ReadAllText(charaDictPath));
        charaEntries[0]["translation"] = "灵梦";
        charaEntries[0]["stage"] = 1;
        charaEntries[1]["translation"] = "人类";
        charaEntries[1]["stage"] = 1;
        File.WriteAllText(charaDictPath, charaEntries.ToString());

        // Translate Str.json
        string strDictPath = Path.Combine(dictRoot, "CSV", "Str.json");
        JArray strEntries = JArray.Parse(File.ReadAllText(strDictPath));
        strEntries[0]["translation"] = "玩家乐谱(东方虹龙洞)";
        strEntries[0]["stage"] = 1;
        strEntries[1]["translation"] = "游戏结束主题";
        strEntries[1]["stage"] = 1;
        strEntries[2]["translation"] = "弹幕决斗";
        strEntries[2]["stage"] = 1;
        File.WriteAllText(strDictPath, strEntries.ToString());

        TranslationApplyResult result = new TranslationApplier().Apply(game, dictRoot, copyRoot, options);

        Assert.Equal(5, result.SpanReplacedCount);
        Assert.Equal(0, result.FallbackReplacedCount);

        string updatedChara = File.ReadAllText(Path.Combine(copyRoot, "CSV", "Chara", "Chara1.csv"), Encoding.UTF8);
        Assert.Contains("名前,灵梦\r\n", updatedChara, StringComparison.Ordinal);
        Assert.Contains("素質,処女\r\n", updatedChara, StringComparison.Ordinal);
        Assert.Contains("CSTR,種族,人类\t;種族メモ", updatedChara, StringComparison.Ordinal);

        string updatedStr = File.ReadAllText(Path.Combine(copyRoot, "CSV", "Str.csv"), Encoding.UTF8);
        Assert.Equal("11517,玩家乐谱(东方虹龙洞),游戏结束主题,1000,弹幕决斗\r\n", updatedStr);
    }

    [Fact]
    public void LexerMode_CsvReExtraction_PreservesUnchangedTranslations()
    {
        string game = Path.Combine(_tempRoot, "GameReextract");
        string dictRoot = Path.Combine(_tempRoot, "OutputReextract", "字典");
        Write(game, "CSV/Str.csv", "1,鳥居\r\n2,境内\r\n");

        var options = new EraParserOptions { ExtractionMode = ExtractionMode.Lexer };
        var extractor = new DictionaryExtractor();
        extractor.Extract(game, dictRoot, options: options);

        string strDictPath = Path.Combine(dictRoot, "CSV", "Str.json");
        JArray entries = JArray.Parse(File.ReadAllText(strDictPath));
        entries[0]["translation"] = "鸟居";
        entries[0]["stage"] = 1;
        entries[1]["translation"] = "境内";
        entries[1]["stage"] = 1;
        File.WriteAllText(strDictPath, entries.ToString());

        Write(game, "CSV/Str.csv", "1,鳥居\r\n2,本殿\r\n");
        extractor.Extract(game, dictRoot, options: options);

        JArray updated = JArray.Parse(File.ReadAllText(strDictPath));
        Assert.Equal("鸟居", (string?)updated[0]["translation"]);
        Assert.Equal(1, (int)updated[0]["stage"]!);
        Assert.Equal(string.Empty, (string?)updated[1]["translation"]);
        Assert.Equal(0, (int)updated[1]["stage"]!);
    }

    private static void Write(string root, string relativePath, string contents)
    {
        string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }
}
