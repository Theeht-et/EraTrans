using EraTrans.Core;
using EraTrans.Core.VisibleText;
using Newtonsoft.Json.Linq;
using Xunit;

namespace EraTrans.Tests;

public sealed class ExtractionModeTests
{
    [Fact]
    public void Extract_LegacyMode_IsTheDefault_AndRecordsTheMarker()
    {
        string root = CreateTempDirectory();
        string game = Path.Combine(root, "Game");
        string output = Path.Combine(root, "Output");
        Write(game, "ERB/sample.erb", string.Join(Environment.NewLine,
        [
            "@SAMPLE",
            "PRINTFORML 可翻译文本"
        ]));

        new DictionaryExtractor().Extract(game, output);

        JObject marker = JObject.Parse(File.ReadAllText(Path.Combine(output, "extractor.json")));
        Assert.Equal("legacy", (string?)marker["mode"]);
        Assert.True(File.Exists(Path.Combine(output, "ERB", "sample.json")));
    }

    [Fact]
    public void Extract_LexerMode_OnlyKeepsVisibleText_AndDropsNonPrintLines()
    {
        string root = CreateTempDirectory();
        string game = Path.Combine(root, "Game");
        string output = Path.Combine(root, "Output");
        Write(game, "ERB/sample.erb", string.Join(Environment.NewLine,
        [
            "@SAMPLE",
            "#DIM キャラ番号",
            "SPLIT 出会ったキャラ記録, \"、\"",
            "PRINTFORML こんにちは",
            "PRINTFORMW %CALLNAME:MASTER%さん"
        ]));

        new DictionaryExtractor().Extract(game, output, options: new EraParserOptions
        {
            ExtractionMode = ExtractionMode.Lexer
        });

        JObject marker = JObject.Parse(File.ReadAllText(Path.Combine(output, "extractor.json")));
        Assert.Equal("lexer", (string?)marker["mode"]);

        JArray entries = JArray.Parse(File.ReadAllText(Path.Combine(output, "ERB", "sample.json")));
        Assert.Equal(2, entries.Count);
        Assert.Equal("こんにちは", (string?)entries[0]!["original"]);
        Assert.Equal("%CALLNAME:MASTER%さん", (string?)entries[1]!["original"]);
        Assert.All(entries, entry =>
        {
            string[] properties = ((JObject)entry!).Properties().Select(p => p.Name).ToArray();
            Assert.Equal(["key", "original", "translation", "stage", "context"], properties);
            Assert.StartsWith("文本", (string?)entry["key"], StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Extract_LexerMode_ReExtractionKeepsTranslations_AndResetsChangedOnes()
    {
        string root = CreateTempDirectory();
        string game = Path.Combine(root, "Game");
        string output = Path.Combine(root, "Output");
        string relative = "ERB/sample.erb";
        Write(game, relative, string.Join(Environment.NewLine,
        [
            "@SAMPLE",
            "PRINTFORML こんにちは",
            "PRINTFORML さようなら"
        ]));

        var options = new EraParserOptions { ExtractionMode = ExtractionMode.Lexer };
        var extractor = new DictionaryExtractor();
        extractor.Extract(game, output, options: options);

        string dictionaryPath = Path.Combine(output, "ERB", "sample.json");
        JArray entries = JArray.Parse(File.ReadAllText(dictionaryPath));
        entries[0]!["translation"] = "你好";
        entries[0]!["stage"] = 1;
        entries[1]!["translation"] = "再见";
        entries[1]!["stage"] = 1;
        File.WriteAllText(dictionaryPath, entries.ToString());

        DictionaryExtractionResult unchanged = extractor.Extract(game, output, options: options);
        Assert.Contains(unchanged.Warnings, w => w.Contains("保留译文 2 条", StringComparison.Ordinal));

        JArray afterFirst = JArray.Parse(File.ReadAllText(dictionaryPath));
        Assert.Equal("你好", (string?)afterFirst[0]!["translation"]);
        Assert.Equal(1, (int)afterFirst[0]!["stage"]!);

        // Change the second source line: its translation must be dropped, the first one kept.
        Write(game, relative, string.Join(Environment.NewLine,
        [
            "@SAMPLE",
            "PRINTFORML こんにちは",
            "PRINTFORML おやすみなさい"
        ]));

        DictionaryExtractionResult changed = extractor.Extract(game, output, options: options);
        Assert.Contains(changed.Warnings, w => w.Contains("重置 1 条", StringComparison.Ordinal));

        JArray afterSecond = JArray.Parse(File.ReadAllText(dictionaryPath));
        Assert.Equal("你好", (string?)afterSecond[0]!["translation"]);
        Assert.Equal(string.Empty, (string?)afterSecond[1]!["translation"]);
        Assert.Equal(0, (int)afterSecond[1]!["stage"]!);
    }

    [Fact]
    public void Extract_EngineMode_IsNotImplementedYet()
    {
        string root = CreateTempDirectory();
        string game = Path.Combine(root, "Game");
        string output = Path.Combine(root, "Output");
        Write(game, "ERB/sample.erb", "@SAMPLE" + Environment.NewLine + "PRINTFORML 文本");

        Assert.Throws<NotSupportedException>(() => new DictionaryExtractor().Extract(
            game,
            output,
            options: new EraParserOptions { ExtractionMode = ExtractionMode.Engine }));
    }

    private static void Write(string root, string relativePath, string contents)
    {
        string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "EraTransTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
