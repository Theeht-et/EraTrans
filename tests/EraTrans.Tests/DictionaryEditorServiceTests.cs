using EraTrans.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace EraTrans.Tests;

public sealed class DictionaryEditorServiceTests
{
    [Fact]
    public void LoadAndSave_ChangesOnlyTranslationAndStage_PreservesUnknownFieldsAndContext()
    {
        string root = CreateTempDirectory();
        string path = Path.Combine(root, "ERB", "opening.json");
        Write(path,
            """
            [
              {
                "key": "文本ERB\\OPENING00001",
                "original": "原始文本",
                "translation": "",
                "stage": 0,
                "context": "第一行\n第二行",
                "custom": {
                  "keep": true,
                  "items": [1, "two"]
                }
              }
            ]
            """);

        var document = new DictionaryEditorService().Load(path);
        var entry = Assert.Single(document.Entries);

        Assert.Equal("文本ERB\\OPENING00001", entry.Key);
        Assert.Equal("原始文本", entry.Original);
        Assert.Equal("第一行\n第二行", entry.Context);
        Assert.True(entry.IsEditable);
        Assert.False(document.IsDirty);

        entry.Translation = "翻译结果";
        entry.Stage = 1;
        Assert.True(document.IsDirty);

        document.Save();

        JObject saved = Assert.IsType<JObject>(Assert.Single(JArray.Parse(File.ReadAllText(path))));
        Assert.Equal("文本ERB\\OPENING00001", (string?)saved["key"]);
        Assert.Equal("原始文本", (string?)saved["original"]);
        Assert.Equal("翻译结果", (string?)saved["translation"]);
        Assert.Equal(1, (int?)saved["stage"]);
        Assert.Equal("第一行\n第二行", (string?)saved["context"]);
        Assert.True((bool?)saved["custom"]?["keep"]);
        Assert.Equal(new JArray(1, "two").ToString(), saved["custom"]?["items"]?.ToString());
        Assert.False(document.IsDirty);
    }

    [Fact]
    public void DisplayKey_ShowsOnlyTrailingFiveDigitIndex_AndPreservesFullKey()
    {
        string root = CreateTempDirectory();
        string path = Path.Combine(root, "keys.json");
        Write(path,
            """
            [
              { "key": "文本ERB\\OPENING00001", "original": "甲", "translation": "", "stage": 0 },
              { "key": "CSV\\items00042", "original": "乙", "translation": "", "stage": 0 },
              { "key": "custom-key", "original": "丙", "translation": "", "stage": 0 }
            ]
            """);

        DictionaryEditorDocument document = new DictionaryEditorService().Load(path);

        Assert.Equal(new[] { "00001", "00042", "custom-key" },
            document.Entries.Select(entry => entry.DisplayKey));
        Assert.Equal("文本ERB\\OPENING00001", document.Entries[0].Key);
        Assert.Equal("CSV\\items00042", document.Entries[1].Key);
        Assert.False(document.IsDirty);
    }

    [Fact]
    public void Load_VariableEntriesAreReadOnly_AndRemainUnchanged()
    {
        string root = CreateTempDirectory();
        string path = Path.Combine(root, "CSV", "Abl.json");
        Write(path,
            """
            [
              {
                "key": "变量CSV\\\\Abl00001",
                "original": "BASE",
                "translation": "BASE_CN",
                "stage": 1,
                "extra": "preserve"
              }
            ]
            """);
        byte[] originalBytes = File.ReadAllBytes(path);

        var document = new DictionaryEditorService().Load(path);
        var entry = Assert.Single(document.Entries);

        Assert.True(entry.IsVariable);
        Assert.False(entry.IsEditable);
        Assert.Throws<InvalidOperationException>(() => entry.Translation = "修改");
        Assert.Throws<InvalidOperationException>(() => entry.Stage = 0);
        Assert.False(document.IsDirty);

        document.Save();
        Assert.Equal(originalBytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void Load_MissingStageDefaultsToZero_AndDoesNotAddStageUnlessChanged()
    {
        string root = CreateTempDirectory();
        string path = Path.Combine(root, "resources", "sprites.json");
        Write(path,
            """
            [
              {
                "key": "resources\\\\sprites00000",
                "original": "SpriteA",
                "translation": "",
                "context": "resource context"
              }
            ]
            """);

        var document = new DictionaryEditorService().Load(path);
        var entry = Assert.Single(document.Entries);

        Assert.Equal(0, entry.Stage);
        Assert.Equal("未翻译", entry.StageDescription);

        entry.Translation = "新名称";
        document.Save();

        JObject saved = Assert.IsType<JObject>(Assert.Single(JArray.Parse(File.ReadAllText(path))));
        Assert.Equal("新名称", (string?)saved["translation"]);
        Assert.Null(saved["stage"]);
        Assert.Equal("resource context", (string?)saved["context"]);
    }


    [Fact]
    public void LoadAndSave_PreservesLongMultilineTranslationExactly()
    {
        string root = CreateTempDirectory();
        string path = Path.Combine(root, "long-translation.json");
        Write(path,
            """
            [
              {
                "key": "文本ERB\\LONG00001",
                "original": "长文本原文",
                "translation": "",
                "stage": 0,
                "context": "上下文",
                "customField": "preserve"
              }
            ]
            """);

        var document = new DictionaryEditorService().Load(path);
        DictionaryEditorEntry entry = Assert.Single(document.Entries);
        string longTranslation = string.Join(
            Environment.NewLine + Environment.NewLine,
            Enumerable.Range(1, 240).Select(index =>
                $"第 {index} 段：這是一段用于验证长篇译文编辑和保存完整性的测试文本，必须保留所有字符、标点与换行。{new string('译', 80)}"));

        entry.Translation = longTranslation;
        document.Save();

        JObject saved = Assert.IsType<JObject>(Assert.Single(JArray.Parse(File.ReadAllText(path))));
        Assert.Equal(longTranslation, (string?)saved["translation"]);
        Assert.Equal("长文本原文", (string?)saved["original"]);
        Assert.Equal("preserve", (string?)saved["customField"]);
        Assert.False(document.IsDirty);
    }

    [Fact]
    public void Load_EntriesWithStandardStagesShareReadOnlyStageOptions()
    {
        string root = CreateTempDirectory();
        string path = Path.Combine(root, "items.json");
        Write(path,
            """
            [
              { "key": "文本A", "original": "A", "translation": "", "stage": 0 },
              { "key": "文本B", "original": "B", "translation": "", "stage": 0 },
              { "key": "文本C", "original": "C", "translation": "", "stage": 1 }
            ]
            """);

        var document = new DictionaryEditorService().Load(path);

        Assert.Same(document.Entries[0].StageOptions, document.Entries[1].StageOptions);
        Assert.Same(document.Entries[0].StageOptions, document.Entries[2].StageOptions);
    }

    [Fact]
    public void EditingTranslationDoesNotRecalculateSummary_ButChangingStageDoes()
    {
        string root = CreateTempDirectory();
        string path = Path.Combine(root, "items.json");
        Write(path,
            """
            [
              { "key": "文本A", "original": "A", "translation": "", "stage": 0 }
            ]
            """);

        var document = new DictionaryEditorService().Load(path);
        int summaryChanges = 0;
        document.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(DictionaryEditorDocument.SummaryText))
            {
                summaryChanges++;
            }
        };

        document.Entries[0].Translation = "译文";
        Assert.Equal(0, summaryChanges);

        document.Entries[0].Stage = 1;
        Assert.Equal(1, summaryChanges);
        Assert.Equal(1, document.TranslatedEntryCount);
    }

    [Fact]
    public void Save_RefusesToOverwriteFileChangedByAnotherProgram()
    {
        string root = CreateTempDirectory();
        string path = Path.Combine(root, "items.json");
        Write(path,
            """
            [
              {
                "key": "CSV\\\\items00000",
                "original": "原文",
                "translation": "",
                "stage": 0
              }
            ]
            """);

        var document = new DictionaryEditorService().Load(path);
        document.Entries[0].Translation = "本地编辑";

        const string externalContent =
            """
            [
              {
                "key": "CSV\\\\items00000",
                "original": "外部修改",
                "translation": "external",
                "stage": 1
              }
            ]
            """;
        File.WriteAllText(path, externalContent);

        Assert.Throws<IOException>(() => document.Save());
        Assert.Equal(externalContent, File.ReadAllText(path));
        Assert.True(document.IsDirty);
    }

    [Fact]
    public void ListDictionaryFiles_RecursivelyReturnsJsonFilesInStableOrder()
    {
        string root = CreateTempDirectory();
        Write(Path.Combine(root, "B", "z.json"), "[]");
        Write(Path.Combine(root, "A", "a.JSON"), "[]");
        Write(Path.Combine(root, "A", "ignore.txt"), "not a dictionary");

        var files = new DictionaryEditorService().ListDictionaryFiles(root);

        Assert.Equal(
            new[] { Path.Combine("A", "a.JSON"), Path.Combine("B", "z.json") },
            files.Select(file => file.RelativePath).ToArray());
    }

    [Fact]
    public void Load_InvalidJsonDoesNotModifyTheDictionaryFile()
    {
        string root = CreateTempDirectory();
        string path = Path.Combine(root, "bad.json");
        const string invalid = "[{ not valid json";
        File.WriteAllText(path, invalid);

        Assert.Throws<InvalidDataException>(() => new DictionaryEditorService().Load(path));
        Assert.Equal(invalid, File.ReadAllText(path));
    }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "EraTransDictionaryEditorTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
