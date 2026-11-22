using EraTrans.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace EraTrans.Tests;

public sealed class TranslationApplierTests
{
    [Fact]
    public void Apply_TranslatesOnlyWorkspaceCopy_AndUsesLongestOriginalFirst()
    {
        string root = CreateTempDirectory();
        string game = Path.Combine(root, "Game");
        string dictionaries = Path.Combine(root, "Dictionaries");
        string output = Path.Combine(root, "WorkingCopy");

        Write(game, "ERB/script.erb", "PRINTFORM 猫咪和猫\nPRINTFORM 未翻译");
        WriteJson(dictionaries, "ERB/script.json",
        [
            Entry("猫", "Cat", 1),
            Entry("猫咪", "Kitty", 1),
            Entry("未翻译", "不应替换", 0)
        ]);

        string originalPath = Path.Combine(game, "ERB", "script.erb");
        byte[] originalBytes = File.ReadAllBytes(originalPath);
        byte[] dictionaryBytes = File.ReadAllBytes(Path.Combine(dictionaries, "ERB", "script.json"));

        var result = new TranslationApplier().Apply(game, dictionaries, output);

        string copyPath = Path.Combine(output, "ERB", "script.erb");
        Assert.Equal("PRINTFORM Kitty和Cat\nPRINTFORM 未翻译", File.ReadAllText(copyPath));
        Assert.Equal(originalBytes, File.ReadAllBytes(originalPath));
        Assert.Equal(dictionaryBytes, File.ReadAllBytes(Path.Combine(dictionaries, "ERB", "script.json")));
        Assert.Contains(copyPath, result.CopiedFiles);
        Assert.Contains(copyPath, result.ModifiedFiles);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Apply_CopiesEveryGameFileAndEmptyDirectoryBeforeApplyingTranslations()
    {
        string root = CreateTempDirectory();
        string game = Path.Combine(root, "Game");
        string dictionaries = Path.Combine(root, "Dictionaries");
        string output = Path.Combine(root, "WorkingCopy");

        Write(game, "ERB/script.erb", "PRINTFORM 原文");
        Write(game, "Graphics/title.bin", "binary game resource");
        Write(game, "Unparsed/readme.txt", "must also be copied");
        Directory.CreateDirectory(Path.Combine(game, "EmptyFolder"));
        WriteJson(dictionaries, "ERB/script.json",
        [
            Entry("原文", "译文", 1)
        ]);

        byte[] originalBinary = File.ReadAllBytes(Path.Combine(game, "Graphics", "title.bin"));
        var result = new TranslationApplier().Apply(
            game,
            dictionaries,
            output,
            new EraParserOptions { EnableVariableAutoCorrection = false });

        Assert.Equal("PRINTFORM 译文", File.ReadAllText(Path.Combine(output, "ERB", "script.erb")));
        Assert.Equal(originalBinary, File.ReadAllBytes(Path.Combine(output, "Graphics", "title.bin")));
        Assert.Equal("must also be copied", File.ReadAllText(Path.Combine(output, "Unparsed", "readme.txt")));
        Assert.True(Directory.Exists(Path.Combine(output, "EmptyFolder")));
        Assert.Equal(3, result.CopiedFiles.Count);
        Assert.Equal("PRINTFORM 原文", File.ReadAllText(Path.Combine(game, "ERB", "script.erb")));
        Assert.Equal(originalBinary, File.ReadAllBytes(Path.Combine(game, "Graphics", "title.bin")));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Apply_UsesResourceCsvFirstColumnReplacement()
    {
        string root = CreateTempDirectory();
        string game = Path.Combine(root, "Game");
        string dictionaries = Path.Combine(root, "Dictionaries");
        string output = Path.Combine(root, "WorkingCopy");

        Write(game, "resources/sprites.csv", "SpriteA,32,64\n;SpriteB,comment\nSpriteB,16,16\n");
        WriteJson(dictionaries, "resources/sprites.json",
        [
            Entry("SpriteA", "龙图标", 1),
            Entry("SpriteB", "不应替换注释", 1)
        ]);

        var result = new TranslationApplier().Apply(game, dictionaries, output);

        string copy = Path.Combine(output, "resources", "sprites.csv");
        string expected = string.Join("\n",
        [
            "龙图标,32,64",
            ";SpriteB,comment",
            "不应替换注释,16,16",
            ""
        ]);
        Assert.Equal(expected, File.ReadAllText(copy));
        Assert.Equal("SpriteA,32,64\n;SpriteB,comment\nSpriteB,16,16\n",
            File.ReadAllText(Path.Combine(game, "resources", "sprites.csv")));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Apply_HandlesXmlDictionary_AndRenamesOnlyErdWorkspaceCopy()
    {
        string root = CreateTempDirectory();
        string game = Path.Combine(root, "Game");
        string dictionaries = Path.Combine(root, "Dictionaries");
        string output = Path.Combine(root, "WorkingCopy");

        Write(game, "XML/ui.xml", "<root>旧标题</root>");
        Write(game, "ERB/creature.erd", "ID,旧名称\n");
        Write(game, "ERB/other.erd", "ID,不应复制\n");
        WriteJson(dictionaries, "XML/ui.xml.json",
        [
            Entry("旧标题", "新标题", 1)
        ]);
        WriteJson(dictionaries, "ERB/creature.json",
        [
            Entry("旧名称", "新名称", 1)
        ]);
        WriteJson(dictionaries, "CSV/ERD字典.json",
        [
            Entry("creature", "creature_cn", 1),
            Entry("other", "", 0)
        ]);

        var result = new TranslationApplier().Apply(game, dictionaries, output);

        Assert.Equal("<root>新标题</root>", File.ReadAllText(Path.Combine(output, "XML", "ui.xml")));
        Assert.Equal("ID,新名称\n", File.ReadAllText(Path.Combine(output, "ERB", "creature_cn.erd")));
        Assert.False(File.Exists(Path.Combine(output, "ERB", "creature.erd")));
        Assert.True(File.Exists(Path.Combine(output, "ERB", "other.erd")));
        Assert.Equal("ID,不应复制\n", File.ReadAllText(Path.Combine(output, "ERB", "other.erd")));
        Assert.True(File.Exists(Path.Combine(game, "ERB", "creature.erd")));
        Assert.True(File.Exists(Path.Combine(game, "ERB", "other.erd")));
        Assert.Single(result.RenamedFiles);
        Assert.Contains(result.RenamedFiles[0].NewPath, result.CopiedFiles);
        Assert.Contains(Path.Combine(output, "ERB", "creature_cn.erd"), result.ModifiedFiles);
        Assert.DoesNotContain(Path.Combine(output, "ERB", "creature.erd"), result.ModifiedFiles);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Apply_AutoCorrectsEveryScriptTypeOnlyInWorkspace_AfterTranslationAndErdRename()
    {
        string root = CreateTempDirectory();
        string game = Path.Combine(root, "Game");
        string dictionaries = Path.Combine(root, "Dictionaries");
        string output = Path.Combine(root, "WorkingCopy");

        Write(game, "CSV/Abl.csv", "ID,参考原文\n");
        Write(game, "ERB/target.erb", "PRINTFORM 普通原文\nPRINTFORM 参考原文\n");
        Write(game, "ERB/untouched.erb", "PRINTFORM 参考原文\n");
        Write(game, "ERB/standalone.erh", "PRINTFORM 参考原文\n");
        Write(game, "ERB/creature.erd", "ID,参考原文\n");

        WriteJson(dictionaries, "CSV/Abl.json",
        [
            Entry("参考原文", "自动修正文本", 1),
            Entry("不应修正", "错误文本", 0)
        ]);
        WriteJson(dictionaries, "ERB/target.json",
        [
            Entry("普通原文", "逐文件译文", 1)
        ]);
        WriteJson(dictionaries, "CSV/ERD字典.json",
        [
            Entry("creature", "creature_cn", 1)
        ]);

        var originalFiles = Directory.EnumerateFiles(game, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(game, path), File.ReadAllBytes);

        var result = new TranslationApplier().Apply(
            game,
            dictionaries,
            output,
            new EraParserOptions { EnableVariableAutoCorrection = true });

        Assert.Equal(
            "PRINTFORM 逐文件译文\nPRINTFORM 自动修正文本\n",
            File.ReadAllText(Path.Combine(output, "ERB", "target.erb")));
        Assert.Equal(
            "PRINTFORM 自动修正文本\n",
            File.ReadAllText(Path.Combine(output, "ERB", "untouched.erb")));
        Assert.Equal(
            "PRINTFORM 自动修正文本\n",
            File.ReadAllText(Path.Combine(output, "ERB", "standalone.erh")));
        Assert.Equal(
            "ID,自动修正文本\n",
            File.ReadAllText(Path.Combine(output, "ERB", "creature_cn.erd")));

        Assert.DoesNotContain(Path.Combine(output, "ERB", "creature.erd"), result.CopiedFiles);
        Assert.Contains(Path.Combine(output, "ERB", "creature_cn.erd"), result.CopiedFiles);
        Assert.Equal(5, result.CopiedFiles.Count);
        Assert.Equal(5, result.ModifiedFiles.Count);

        foreach (var original in originalFiles)
        {
            Assert.Equal(original.Value, File.ReadAllBytes(Path.Combine(game, original.Key)));
        }
    }

    [Fact]
    public void Apply_AutoCorrectionCanBeDisabled_AndStillCopiesTheCompleteGame()
    {
        string root = CreateTempDirectory();
        string game = Path.Combine(root, "Game");
        string dictionaries = Path.Combine(root, "Dictionaries");
        string output = Path.Combine(root, "WorkingCopy");

        Write(game, "CSV/Abl.csv", "ID,参考原文\n");
        Write(game, "ERB/untouched.erb", "PRINTFORM 参考原文\n");
        WriteJson(dictionaries, "CSV/Abl.json",
        [
            Entry("参考原文", "自动修正文本", 1)
        ]);

        var result = new TranslationApplier().Apply(
            game,
            dictionaries,
            output,
            new EraParserOptions { EnableVariableAutoCorrection = false });

        Assert.True(File.Exists(Path.Combine(output, "CSV", "Abl.csv")));
        Assert.True(File.Exists(Path.Combine(output, "ERB", "untouched.erb")));
        Assert.Equal(
            "PRINTFORM 参考原文\n",
            File.ReadAllText(Path.Combine(output, "ERB", "untouched.erb")));
        Assert.Equal(
            "PRINTFORM 参考原文\n",
            File.ReadAllText(Path.Combine(game, "ERB", "untouched.erb")));
        Assert.Contains(result.CopiedFiles, path => path.EndsWith("untouched.erb", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Apply_AutoCorrectionDoesNotModifyPreExistingWorkspaceFiles()
    {
        string root = CreateTempDirectory();
        string game = Path.Combine(root, "Game");
        string dictionaries = Path.Combine(root, "Dictionaries");
        string output = Path.Combine(root, "WorkingCopy");

        Write(game, "CSV/Abl.csv", "ID,参考原文\n");
        Write(game, "ERB/untouched.erb", "PRINTFORM 参考原文\n");
        Write(output, "ERB/untouched.erb", "用户已有副本内容");

        WriteJson(dictionaries, "CSV/Abl.json",
        [
            Entry("参考原文", "自动修正文本", 1)
        ]);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            new TranslationApplier().Apply(
                game,
                dictionaries,
                output,
                new EraParserOptions { EnableVariableAutoCorrection = true }));

        Assert.Contains("已存在", exception.Message, StringComparison.Ordinal);
        Assert.Equal("用户已有副本内容", File.ReadAllText(Path.Combine(output, "ERB", "untouched.erb")));
        Assert.Equal(
            "PRINTFORM 参考原文\n",
            File.ReadAllText(Path.Combine(game, "ERB", "untouched.erb")));
    }

    [Fact]
    public void Apply_RejectsOverlappingGameDictionaryAndOutputDirectories()
    {
        string root = CreateTempDirectory();
        string game = Path.Combine(root, "Game");
        string dictionaries = Path.Combine(game, "Dictionaries");
        string output = Path.Combine(root, "WorkingCopy");
        Directory.CreateDirectory(dictionaries);

        Assert.Throws<InvalidOperationException>(() =>
            new TranslationApplier().Apply(game, dictionaries, output));
    }

    [Fact]
    public void Apply_DoesNotOverwriteExistingWorkspaceFile()
    {
        string root = CreateTempDirectory();
        string game = Path.Combine(root, "Game");
        string dictionaries = Path.Combine(root, "Dictionaries");
        string output = Path.Combine(root, "WorkingCopy");

        Write(game, "ERB/script.erb", "PRINTFORM 原文");
        Write(output, "ERB/script.erb", "用户已有内容");
        WriteJson(dictionaries, "ERB/script.json",
        [
            Entry("原文", "译文", 1)
        ]);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            new TranslationApplier().Apply(game, dictionaries, output));

        Assert.Contains("已存在", exception.Message, StringComparison.Ordinal);
        Assert.Equal("用户已有内容", File.ReadAllText(Path.Combine(output, "ERB", "script.erb")));
        Assert.Equal("PRINTFORM 原文", File.ReadAllText(Path.Combine(game, "ERB", "script.erb")));
    }

    [Fact]
    public void Apply_RejectsUnsafeErdTranslationFileName()
    {
        string root = CreateTempDirectory();
        string game = Path.Combine(root, "Game");
        string dictionaries = Path.Combine(root, "Dictionaries");
        string output = Path.Combine(root, "WorkingCopy");

        Write(game, "ERB/creature.erd", "ID,名称\n");
        WriteJson(dictionaries, "CSV/ERD字典.json",
        [
            Entry("creature", "../outside", 1)
        ]);

        var result = new TranslationApplier().Apply(game, dictionaries, output);

        Assert.True(File.Exists(Path.Combine(game, "ERB", "creature.erd")));
        Assert.False(File.Exists(Path.Combine(root, "outside.erd")));
        Assert.Contains(result.Warnings, warning => warning.Contains("不是安全的文件名", StringComparison.Ordinal));
    }

    private static JObject Entry(string original, string translation, int stage) => new()
    {
        ["key"] = "test",
        ["original"] = original,
        ["translation"] = translation,
        ["stage"] = stage
    };

    private static void WriteJson(string root, string relativePath, IEnumerable<JObject> entries)
    {
        string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, new JArray(entries).ToString());
    }

    private static void Write(string root, string relativePath, string contents)
    {
        string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "EraTransApplyTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}