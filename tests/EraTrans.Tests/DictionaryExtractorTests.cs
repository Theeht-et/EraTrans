using EraTrans.Core;
using Newtonsoft.Json.Linq;
using System.Text;
using Xunit;

namespace EraTrans.Tests;

public sealed class DictionaryExtractorTests
{
    [Fact]
    public void Extract_UsesLegacyParsers_AndLeavesGameFilesUnchanged()
    {
        string root = CreateTempDirectory();
        string game = Path.Combine(root, "Game");
        string output = Path.Combine(root, "Output");
        Directory.CreateDirectory(Path.Combine(game, "CSV"));
        Directory.CreateDirectory(Path.Combine(game, "ERB"));
        Directory.CreateDirectory(Path.Combine(game, "resources"));
        Directory.CreateDirectory(Path.Combine(game, "XML"));

        Write(game, "CSV/items.csv", "ID,物品甲,123,物品乙\n");
        Write(game, "ERB/combined.erb", "PRINTFORM ERB主文本\n");
        Write(game, "ERB/combined.erh", "PRINTFORM ERH伴随文本\n");
        Write(game, "ERB/standalone.erh", "PRINTFORM 独立ERH文本\n");
        Write(game, "ERB/Names.erd", "ID,魔物名称\n");
        Write(game, "resources/sprites.csv", "素材名称,其他信息\n");
        Write(game, "XML/skills.xml", "<root><defname parentname=\"火系\"><tokenname>火球术</tokenname></defname></root>");

        var sourceFiles = Directory.GetFiles(game, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(game, path), File.ReadAllBytes);

        var result = new DictionaryExtractor().Extract(game, output);

        Assert.True(File.Exists(Path.Combine(output, "CSV", "items.json")));
        Assert.True(File.Exists(Path.Combine(output, "ERB", "combined.json")));
        Assert.True(File.Exists(Path.Combine(output, "ERB", "standalone.json")));
        Assert.True(File.Exists(Path.Combine(output, "ERB", "Names.json")));
        Assert.True(File.Exists(Path.Combine(output, "resources", "sprites.json")));
        Assert.True(File.Exists(Path.Combine(output, "XML", "skills.xml.json")));
        Assert.True(File.Exists(Path.Combine(output, "CSV", "ERD字典.json")));

        var combined = ReadOriginals(Path.Combine(output, "ERB", "combined.json"));
        Assert.Contains("ERH伴随文本", combined);
        Assert.Contains("ERB主文本", combined);

        var standalone = ReadOriginals(Path.Combine(output, "ERB", "standalone.json"));
        Assert.Equal(1, standalone.Count(value => value == "独立ERH文本"));

        var csv = JArray.Parse(File.ReadAllText(Path.Combine(output, "CSV", "items.json")));
        Assert.Equal(new[] { "物品甲", "物品乙" }, csv.Select(item => (string)item!["original"]!).ToArray());

        var xml = ReadOriginals(Path.Combine(output, "XML", "skills.xml.json"));
        Assert.Contains("火系", xml);
        Assert.Contains("火球术", xml);

        var erdNames = JArray.Parse(File.ReadAllText(Path.Combine(output, "CSV", "ERD字典.json")));
        Assert.Equal("文件名000", (string?)erdNames[0]?["key"]);
        Assert.Equal("Names", (string?)erdNames[0]?["original"]);

        foreach (var entry in sourceFiles)
        {
            Assert.Equal(entry.Value, File.ReadAllBytes(Path.Combine(game, entry.Key)));
        }

        Assert.NotEmpty(result.GeneratedFiles);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Extract_WithReferenceDirectory_PreservesLegacyReferenceFillBehavior()
    {
        string root = CreateTempDirectory();
        string game = Path.Combine(root, "Game");
        string reference = Path.Combine(root, "Reference");
        string output = Path.Combine(root, "Output");
        Write(game, "CSV/items.csv", "ID,原文项目\n");
        Write(reference, "CSV/items.csv", "ID,参考译文\n");

        new DictionaryExtractor().Extract(game, output, reference);

        var entries = JArray.Parse(File.ReadAllText(Path.Combine(output, "CSV", "items.json")));
        Assert.Equal("变量" + Path.ChangeExtension(Path.Combine("CSV", "items.csv"), "") + "00000", (string?)entries[0]?["key"]);
        Assert.Equal("原文项目", (string?)entries[0]?["original"]);
        Assert.Equal("参考译文", (string?)entries[0]?["translation"]);
        Assert.Equal(1, (int?)entries[0]?["stage"]);
    }

    [Fact]
    public void Extract_DoesNotOverwriteExistingTranslatedDictionaries_AndStillCreatesNewOnes()
    {
        string root = CreateTempDirectory();
        string game = Path.Combine(root, "Game");
        string output = Path.Combine(root, "Output");

        Write(game, "CSV/items.csv", "ID,物品甲\n");
        Write(game, "ERB/creature.erd", "ID,魔物名称\n");

        var extractor = new DictionaryExtractor();
        extractor.Extract(game, output);

        string itemDictionaryPath = Path.Combine(output, "CSV", "items.json");
        string erdNameDictionaryPath = Path.Combine(output, "CSV", "ERD字典.json");
        string erdContentDictionaryPath = Path.Combine(output, "ERB", "creature.json");

        var itemEntries = JArray.Parse(File.ReadAllText(itemDictionaryPath));
        itemEntries[0]!["translation"] = "已翻译物品";
        itemEntries[0]!["stage"] = 1;
        string translatedItems = itemEntries.ToString(Newtonsoft.Json.Formatting.Indented);
        File.WriteAllText(itemDictionaryPath, translatedItems);

        var erdEntries = JArray.Parse(File.ReadAllText(erdNameDictionaryPath));
        erdEntries[0]!["translation"] = "魔物数据";
        erdEntries[0]!["stage"] = 1;
        string translatedErdNames = erdEntries.ToString(Newtonsoft.Json.Formatting.Indented);
        File.WriteAllText(erdNameDictionaryPath, translatedErdNames);

        var erdContentEntries = JArray.Parse(File.ReadAllText(erdContentDictionaryPath));
        erdContentEntries[0]!["translation"] = "魔物名称译文";
        erdContentEntries[0]!["stage"] = 1;
        string translatedErdContent = erdContentEntries.ToString(Newtonsoft.Json.Formatting.Indented);
        File.WriteAllText(erdContentDictionaryPath, translatedErdContent);

        // Simulate a game update that introduces a new file.
        Write(game, "CSV/new.csv", "ID,新增条目\n");

        var result = extractor.Extract(game, output);

        Assert.Equal(translatedItems, File.ReadAllText(itemDictionaryPath));
        Assert.Equal(translatedErdNames, File.ReadAllText(erdNameDictionaryPath));
        Assert.Equal(translatedErdContent, File.ReadAllText(erdContentDictionaryPath));

        string newDictionaryPath = Path.Combine(output, "CSV", "new.json");
        Assert.True(File.Exists(newDictionaryPath));
        Assert.Contains(newDictionaryPath, result.GeneratedFiles);
        Assert.Single(result.GeneratedFiles);

        Assert.Contains(result.Warnings, warning => warning.Contains("CSV" + Path.DirectorySeparatorChar + "items.json", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, warning => warning.Contains("CSV" + Path.DirectorySeparatorChar + "ERD字典.json", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, warning => warning.Contains("ERB" + Path.DirectorySeparatorChar + "creature.json", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_RejectsOutputInsideGameDirectory()
    {
        string root = CreateTempDirectory();
        string game = Path.Combine(root, "Game");
        Directory.CreateDirectory(game);

        Assert.Throws<InvalidOperationException>(() =>
            new DictionaryExtractor().Extract(game, Path.Combine(game, "EraTransOutput")));
    }

    [Fact]
    public void Extract_RejectsOutputInsideReferenceDirectory()
    {
        string root = CreateTempDirectory();
        string game = Path.Combine(root, "Game");
        string reference = Path.Combine(root, "Reference");
        Directory.CreateDirectory(game);
        Directory.CreateDirectory(reference);

        Assert.Throws<InvalidOperationException>(() =>
            new DictionaryExtractor().Extract(game, reference, reference));
    }

    [Fact]
    public void Extract_UsesConfiguredShiftJisEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding shiftJis = Encoding.GetEncoding(932);

        string root = CreateTempDirectory();
        string game = Path.Combine(root, "Game");
        string output = Path.Combine(root, "Output");
        string csvPath = Path.Combine(game, "CSV", "items.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(csvPath)!);
        File.WriteAllText(csvPath, "ID,商品名\n", shiftJis);

        new DictionaryExtractor().Extract(
            game,
            output,
            options: new EraParserOptions { FileEncoding = shiftJis });

        var entries = JArray.Parse(File.ReadAllText(Path.Combine(output, "CSV", "items.json")));
        Assert.Equal("商品名", (string?)entries[0]?["original"]);
    }

    private static List<string> ReadOriginals(string path)
    {
        return JArray.Parse(File.ReadAllText(path))
            .Select(item => (string)item!["original"]!)
            .ToList();
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

public sealed class GameFileWorkspaceTests
{
    [Fact]
    public void CopyForModification_CopiesToOutput_AndPreservesOriginal()
    {
        string root = Path.Combine(Path.GetTempPath(), "EraTransWorkspaceTests", Guid.NewGuid().ToString("N"));
        string game = Path.Combine(root, "Game");
        string output = Path.Combine(root, "Output");
        string originalPath = Path.Combine(game, "ERB", "sample.erb");
        Directory.CreateDirectory(Path.GetDirectoryName(originalPath)!);
        File.WriteAllText(originalPath, "PRINTFORM 原始内容");

        string copyPath = GameFileWorkspace.CopyForModification(game, output, "ERB/sample.erb");
        File.WriteAllText(copyPath, "PRINTFORM 修改副本");

        Assert.Equal("PRINTFORM 原始内容", File.ReadAllText(originalPath));
        Assert.Equal("PRINTFORM 修改副本", File.ReadAllText(copyPath));
    }

    [Fact]
    public void CopyForModification_RejectsOutputInsideGameDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), "EraTransWorkspaceTests", Guid.NewGuid().ToString("N"));
        string game = Path.Combine(root, "Game");
        Directory.CreateDirectory(Path.Combine(game, "CSV"));
        File.WriteAllText(Path.Combine(game, "CSV", "items.csv"), "ID,内容");

        Assert.Throws<InvalidOperationException>(() =>
            GameFileWorkspace.CopyForModification(game, Path.Combine(game, "Output"), "CSV/items.csv"));
    }

    [Fact]
    public void CopyForModification_RejectsPathTraversal()
    {
        string root = Path.Combine(Path.GetTempPath(), "EraTransWorkspaceTests", Guid.NewGuid().ToString("N"));
        string game = Path.Combine(root, "Game");
        Directory.CreateDirectory(game);

        Assert.Throws<ArgumentException>(() =>
            GameFileWorkspace.CopyForModification(game, Path.Combine(root, "Output"), "../outside.txt"));
    }
}
