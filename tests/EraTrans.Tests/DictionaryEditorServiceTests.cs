using EraTrans.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace EraTrans.Tests;

public sealed class DictionaryEditorServiceTests
{
    [Fact]
    public void ListDictionaryFiles_SortsNumericFileNamesNaturally()
    {
        string root = CreateTempDirectory();
        foreach (string name in new[] { "0.json", "10.json", "11.json", "12.json", "1.json", "2.json", "3.json" })
            Write(Path.Combine(root, name), "[]");

        IReadOnlyList<DictionaryEditorFile> files =
            new DictionaryEditorService().ListDictionaryFiles(root);

        Assert.Equal(
            new[] { "0.json", "1.json", "2.json", "3.json", "10.json", "11.json", "12.json" },
            files.Select(file => Path.GetFileName(file.RelativePath)));
    }

    [Fact]
    public void ListDictionaryFiles_ExcludesExtractorMarkerAndAiSidecarFiles()
    {
        string root = CreateTempDirectory();
        Write(Path.Combine(root, "extractor.json"), "{\"schema\":1,\"mode\":\"lexer\"}");
        Write(Path.Combine(root, "visibletext-report.json"), "{\"schema\":1}");
        Write(Path.Combine(root, "辅助数据", "ERB", "hello.spans.json"), "{}");
        Write(Path.Combine(root, "AI上下文", "ERB", "hello.spans.json"), "{}");
        Write(Path.Combine(root, "ERB", "hello.spans.json"), "{}");
        Write(Path.Combine(root, "ERB", "hello.groups.json"), "{}");
        Write(Path.Combine(root, "ERB", "hello.json"), "[]");
        Write(Path.Combine(root, "CSV", "items.json"), "[]");

        IReadOnlyList<DictionaryEditorFile> files =
            new DictionaryEditorService().ListDictionaryFiles(root);

        Assert.Equal(
            new[] { Path.Combine("CSV", "items.json"), Path.Combine("ERB", "hello.json") },
            files.Select(file => file.RelativePath).ToArray());
    }

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

        entry.SetEntryMetadata("正文", isScriptEntry: true);
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
        Assert.Null(saved["entryTypeLabel"]);
        Assert.Null(saved["validationWarning"]);
        Assert.False(document.IsDirty);
    }

    [Fact]
    public void TranslationPreValidator_DetectsScriptNewlinesAndPlaceholderMismatches()
    {
        Assert.Equal(string.Empty, TranslationPreValidator.Validate("おはよう%CALLNAME:MASTER%", "早上好%CALLNAME:MASTER%", isScriptTarget: true));
        Assert.Equal(string.Empty, TranslationPreValidator.Validate("成功率50%、失敗率50%", "成功率50%、失败率50%", isScriptTarget: true));
        Assert.Equal(string.Empty, TranslationPreValidator.Validate("多行CSV原文", "第一行\n第二行", isScriptTarget: false));
        Assert.Equal(string.Empty, TranslationPreValidator.Validate(@"\@ARG ? 彼 : 彼女\@だ", @"\@ARG ? 他 : 她\@呀", isScriptTarget: true));

        string newlineWarning = TranslationPreValidator.Validate("单行脚本", "第一行\n第二行", isScriptTarget: true);
        Assert.Contains("换行", newlineWarning, StringComparison.Ordinal);

        string missingWarning = TranslationPreValidator.Validate("体力{LOCAL} / %CALLNAME:MASTER%", "体力 / %CALLNAME:MASTER%", isScriptTarget: true);
        Assert.Contains("缺少占位符", missingWarning, StringComparison.Ordinal);
        Assert.Contains("{LOCAL}", missingWarning, StringComparison.Ordinal);

        string extraWarning = TranslationPreValidator.Validate("こんにちは", "你好%EXTRA%", isScriptTarget: true);
        Assert.Contains("多出占位符", extraWarning, StringComparison.Ordinal);
        Assert.Contains("%EXTRA%", extraWarning, StringComparison.Ordinal);

        string ternaryMissingWarning = TranslationPreValidator.Validate(@"\@ARG ? 彼 : 彼女\@だ", "是他", isScriptTarget: true);
        Assert.Contains("条件块数量不一致", ternaryMissingWarning, StringComparison.Ordinal);

        string ternaryUnclosedWarning = TranslationPreValidator.Validate(@"\@ARG ? 彼 : 彼女\@だ", @"\@ARG ? 他 : 她呀", isScriptTarget: true);
        Assert.Contains("未闭合", ternaryUnclosedWarning, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidationWarning_SkipsReadOnlyVariablesAndIgnoredEntries_AndUpdatesWithStage()
    {
        string root = CreateTempDirectory();
        string path = Path.Combine(root, "ERB", "validation.json");
        Write(path,
            """
            [
              {
                "key": "变量ERB\\TEST00001",
                "original": "体力%CALLNAME:MASTER%",
                "translation": "缺少占位符的只读变量",
                "stage": 1
              },
              {
                "key": "文本ERB\\TEST00002",
                "original": "おはよう%CALLNAME:MASTER%",
                "translation": "早上好",
                "stage": 1
              }
            ]
            """);

        var document = new DictionaryEditorService().Load(path);
        DictionaryEditorEntry variableEntry = document.Entries[0];
        DictionaryEditorEntry editableEntry = document.Entries[1];

        Assert.False(variableEntry.IsEditable);
        Assert.False(variableEntry.HasValidationWarning);
        Assert.Equal(string.Empty, variableEntry.ValidationWarning);

        variableEntry.SetEntryMetadata("正文", isScriptEntry: true);
        Assert.False(variableEntry.HasValidationWarning);

        Assert.True(editableEntry.HasValidationWarning);
        Assert.Equal(1, document.WarningEntryCount);
        Assert.Contains("告警 1", document.SummaryText, StringComparison.Ordinal);

        editableEntry.Stage = -1;
        Assert.False(editableEntry.HasValidationWarning);
        Assert.Equal(string.Empty, editableEntry.ValidationWarning);
        Assert.Equal(0, document.WarningEntryCount);
        Assert.DoesNotContain("告警", document.SummaryText, StringComparison.Ordinal);

        editableEntry.Stage = 1;
        Assert.True(editableEntry.HasValidationWarning);
        Assert.Contains("缺少占位符", editableEntry.ValidationWarning, StringComparison.Ordinal);
        Assert.Equal(1, document.WarningEntryCount);
        Assert.Contains("告警 1", document.SummaryText, StringComparison.Ordinal);
    }

    [Fact]
    public void TranslationPreValidator_DescribeEntryType_MapsKindsAndCommands()
    {
        Assert.Equal("悬停提示", TranslationPreValidator.DescribeEntryType("html_title", "HTML_PRINT"));
        Assert.Equal("界面", TranslationPreValidator.DescribeEntryType("html", "HTML_PRINT"));
        Assert.Equal("按钮", TranslationPreValidator.DescribeEntryType("button", "PRINTBUTTON"));
        Assert.Equal("数据块", TranslationPreValidator.DescribeEntryType("data", "DATAFORM"));
        Assert.Equal("报错", TranslationPreValidator.DescribeEntryType("custom", "THROW"));
        Assert.Equal("正文", TranslationPreValidator.DescribeEntryType("form", "PRINTFORML"));
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
