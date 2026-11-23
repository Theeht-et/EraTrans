using EraTrans.Core;
using Newtonsoft.Json.Linq;
using System.Text;
using Xunit;
using EraTrans.Core.WriteBack;

namespace EraTrans.Tests;

public sealed class DictionaryExtractorTests
{
    [Fact]
    public void Extract_DefaultOptionsSuppressVariableEntriesButKeepTranslatableText()
    {
        string root = CreateTempDirectory();
        string game = Path.Combine(root, "Game");
        string output = Path.Combine(root, "Output");
        Write(game, "ERB/sample.erb", string.Join(Environment.NewLine,
        [
            "@SAMPLE",
            "CALL 日本語関数(ARG:0)",
            "PRINTFORML 可翻译文本"
        ]));

        new DictionaryExtractor().Extract(game, output);

        JArray entries = JArray.Parse(File.ReadAllText(Path.Combine(output, "ERB", "sample.json")));
        Assert.Contains(entries, entry => ((string?)entry!["key"])?.StartsWith("文本", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(entries, entry => ((string?)entry!["key"])?.StartsWith("变量", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Extract_GameNamedOutputStoresDictionaryAndGroupingSidecarUnderSameGameFolder()
    {
        string root = CreateTempDirectory();
        string game = Path.Combine(root, "Games", "ExampleGame");
        string output = Path.Combine(root, "Translations");
        Write(game, "ERB/menu.erb", string.Join(Environment.NewLine,
        [
            "@MENU",
            "PRINTFORML 选择一个项目",
            "INPUT",
            "SELECTCASE RESULT",
            "CASE 0",
            "PRINTFORML 第一项"
        ]));

        EraTransOutputLayout layout = EraTransOutputLayout.Create(output, game);
        DictionaryExtractionResult result =
            new DictionaryExtractor().Extract(game, layout.DictionaryDirectory);

        string dictionaryPath = Path.Combine(layout.DictionaryDirectory, "ERB", "menu.json");
        string sidecarPath = Path.Combine(layout.AiContextDirectory, "ERB", "menu.groups.json");
        Assert.True(File.Exists(dictionaryPath));
        Assert.True(File.Exists(sidecarPath));
        Assert.Contains(sidecarPath, result.UpdatedGroupingFiles ?? Array.Empty<string>());
        Assert.False(Directory.Exists(Path.Combine(output, "字典")));
        Assert.False(Directory.Exists(Path.Combine(output, "AI上下文")));
    }

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

        Write(game, "CSV/items.csv", "ID,物品甲,123,物品乙\n; 注释行应被跳过\n");
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
        Assert.All(csv, entry => Assert.Contains(
            "ID,物品甲,123,物品乙",
            (string?)entry!["context"] ?? string.Empty));

        var xmlEntries = JArray.Parse(File.ReadAllText(Path.Combine(output, "XML", "skills.xml.json")));
        Assert.Contains(xmlEntries, entry => (string?)entry!["original"] == "火系" &&
            ((string?)entry["context"])?.Contains("<defname", StringComparison.Ordinal) == true);
        Assert.Contains(xmlEntries, entry => (string?)entry!["original"] == "火球术" &&
            ((string?)entry["context"])?.Contains("<defname", StringComparison.Ordinal) == true);

        var resourceEntries = JArray.Parse(File.ReadAllText(Path.Combine(output, "resources", "sprites.json")));
        Assert.Contains(resourceEntries, entry =>
            ((string?)entry!["context"])?.Contains("素材名称,其他信息", StringComparison.Ordinal) == true);

        var erbEntries = JArray.Parse(File.ReadAllText(Path.Combine(output, "ERB", "combined.json")));
        Assert.Contains(erbEntries, entry => (string?)entry!["original"] == "ERB主文本" &&
            ((string?)entry["context"])?.Contains("PRINTFORM ERB主文本", StringComparison.Ordinal) == true);

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
    public void Extract_FiltersDynamicCallFormNames_AndPreservesStaticJapaneseNamesAndArguments()
    {
        string root = CreateTempDirectory();
        string game = Path.Combine(root, "Game");
        string output = Path.Combine(root, "Output");
        Write(game, "ERB/functionNames.erb", string.Join(Environment.NewLine,
        new[]
        {
            "CALLFORM KOJO_{NO:TARGET}_{SELECTCOM}(ARG:0, \"花括号动态名称参数文本\")",
            "CALLFORM %HOGES%(ARG:0, \"百分号动态名称参数文本\")",
            "CALLFORM KOJO_{NO,3}_{SELECTCOM}(ARG:0)",
            "CALLFORM KOJO_{NO,3}_{SELECTCOM}, GETNUM(ARG:0), \"逗号分隔动态名称参数文本\"",
            "CALLFORM \\@NO == 1 ? 関数甲 : 関数乙\\@(ARG:0, \"条件表达式动态名称参数文本\")",
            "CALLFORM KOJO_{NO:TARGET}_{SELECTCOM}",
            "CALLFORM %HOGES%",
            "CALLFORM %HOGES,3%",
            "CALLFORM \\@NO == 1 ? 関数甲 : 関数乙\\@",
            "CALLFORM 日本語関数(ARG:0, \"静态日文函数参数文本\")",
            "CALLFORM 日本語単引数関数(ARG:0)",
            "CALLFORM ASCII_FUNCTION(ARG:0, \"静态英文函数参数文本\")",
            "TRYCALLFORM KOJO_{NO:TARGET}_{SELECTCOM}(ARG:0, \"TRYCALLFORM参数文本\")",
            "TRYCCALLFORM KOJO_{NO:TARGET}_{SELECTCOM}(ARG:0, \"TRYCCALLFORM参数文本\")",
            "CALL 日本語CALL(ARG:0, \"普通CALL参数文本\")",
            "CALL X_{index}(ARG:0, \"普通CALL动态函数参数文本\")",
            "CALL X_{index}(ARG:0)",
            "CALL X_{index}, GETNUM(ARG:0), \"普通CALL逗号参数文本\"",
            "CALL X_{index}",
            "CALL ASCII_CALL(ARG:0, \"普通CALL英文函数参数文本\")"
        }));

        new DictionaryExtractor().Extract(
            game,
            output,
            options: new EraParserOptions { HideVariableEntries = false });

        JArray entries = JArray.Parse(File.ReadAllText(Path.Combine(output, "ERB", "functionNames.json")));
        string[] originals = entries
            .Select(entry => (string?)entry!["original"] ?? string.Empty)
            .ToArray();

        Assert.Contains("日本語関数", originals);
        Assert.Contains("日本語CALL", originals);
        Assert.Contains("日本語単引数関数", originals);
        Assert.DoesNotContain("ASCII_FUNCTION", originals);
        Assert.DoesNotContain("ASCII_CALL", originals);
        Assert.DoesNotContain(originals, original =>
            original.Contains("KOJO_", StringComparison.Ordinal) ||
            original.Contains("HOGES", StringComparison.Ordinal) ||
            original.Contains("関数甲", StringComparison.Ordinal) ||
            original.Contains("関数乙", StringComparison.Ordinal) ||
            original.Contains("SELECTCOM", StringComparison.Ordinal) ||
            original.Contains("TARGET", StringComparison.Ordinal) ||
            original.Contains("index", StringComparison.Ordinal) ||
            original == "NO");

        Assert.Contains(originals, original => original.Contains("花括号动态名称参数文本", StringComparison.Ordinal));
        Assert.Contains(originals, original => original.Contains("百分号动态名称参数文本", StringComparison.Ordinal));
        Assert.Contains(originals, original => original.Contains("逗号分隔动态名称参数文本", StringComparison.Ordinal));
        Assert.Contains(originals, original => original.Contains("条件表达式动态名称参数文本", StringComparison.Ordinal));
        Assert.Contains(originals, original => original.Contains("TRYCALLFORM参数文本", StringComparison.Ordinal));
        Assert.Contains(originals, original => original.Contains("TRYCCALLFORM参数文本", StringComparison.Ordinal));
        Assert.Contains(originals, original => original.Contains("普通CALL动态函数参数文本", StringComparison.Ordinal));
        Assert.Contains(originals, original => original.Contains("普通CALL逗号参数文本", StringComparison.Ordinal));
        Assert.Contains(originals, original => original.Contains("普通CALL英文函数参数文本", StringComparison.Ordinal));
        Assert.Contains(originals, original => original.Contains("静态日文函数参数文本", StringComparison.Ordinal));
        Assert.Contains(originals, original => original.Contains("普通CALL参数文本", StringComparison.Ordinal));
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
    public void Extract_BackfillsContextForLegacyReferenceKeysWithoutChangingReferenceTranslation()
    {
        string root = CreateTempDirectory();
        string game = Path.Combine(root, "Game");
        string reference = Path.Combine(root, "Reference");
        string output = Path.Combine(root, "Output");
        Write(game, "CSV/items.csv", "ID,原文项目\n");
        Write(reference, "CSV/items.csv", "ID,参考译文\n");

        var extractor = new DictionaryExtractor();
        extractor.Extract(game, output, reference);

        string dictionaryPath = Path.Combine(output, "CSV", "items.json");
        JArray entries = JArray.Parse(File.ReadAllText(dictionaryPath));
        JObject entry = Assert.IsType<JObject>(Assert.Single(entries));
        Assert.StartsWith("变量", (string?)entry["key"]);
        entry.Remove("context");
        File.WriteAllText(dictionaryPath, entries.ToString(Newtonsoft.Json.Formatting.Indented));

        DictionaryExtractionResult result = extractor.Extract(game, output);

        JObject updated = Assert.IsType<JObject>(Assert.Single(JArray.Parse(File.ReadAllText(dictionaryPath))));
        Assert.Equal("原文项目", (string?)updated["original"]);
        Assert.Equal("参考译文", (string?)updated["translation"]);
        Assert.Equal(1, (int?)updated["stage"]);
        Assert.Contains("ID,原文项目", (string?)updated["context"] ?? string.Empty);
        Assert.Contains(dictionaryPath, result.UpdatedContextFiles ?? Array.Empty<string>());
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
    public void Extract_BackfillsOnlyMissingContext_AndPreservesExistingTranslationsAndFields()
    {
        string root = CreateTempDirectory();
        string game = Path.Combine(root, "Game");
        string output = Path.Combine(root, "Output");
        Write(game, "CSV/items.csv", "ID,物品甲,物品乙\n");

        var extractor = new DictionaryExtractor();
        extractor.Extract(game, output);

        string dictionaryPath = Path.Combine(output, "CSV", "items.json");
        JArray entries = JArray.Parse(File.ReadAllText(dictionaryPath));
        Assert.Equal(2, entries.Count);
        JObject item = Assert.IsType<JObject>(entries[0]);
        JObject itemWithExistingContext = Assert.IsType<JObject>(entries[1]);
        item.Remove("context");
        item["translation"] = "人工译文";
        item["stage"] = 1;
        item["customField"] = "must remain";
        itemWithExistingContext["context"] = "用户已有的上下文";
        File.WriteAllText(dictionaryPath, entries.ToString(Newtonsoft.Json.Formatting.Indented));

        DictionaryExtractionResult result = extractor.Extract(game, output);

        JArray updatedEntries = JArray.Parse(File.ReadAllText(dictionaryPath));
        JObject updated = Assert.IsType<JObject>(updatedEntries[0]);
        JObject unchangedContext = Assert.IsType<JObject>(updatedEntries[1]);
        Assert.Equal("人工译文", (string?)updated["translation"]);
        Assert.Equal(1, (int?)updated["stage"]);
        Assert.Equal("must remain", (string?)updated["customField"]);
        Assert.Contains("ID,物品甲,物品乙", (string?)updated["context"] ?? string.Empty);
        Assert.Equal("用户已有的上下文", (string?)unchangedContext["context"]);
        Assert.Contains(dictionaryPath, result.UpdatedContextFiles ?? Array.Empty<string>());
        Assert.Empty(result.GeneratedFiles);
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
    public void Extract_RejectsAmbiguousEncodingBeforeCreatingDictionaryOutput()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string root = CreateTempDirectory();
        string game = Path.Combine(root, "Game");
        string output = Path.Combine(root, "Output");
        string source = Path.Combine(game, "ERB", "ambiguous.erb");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        byte[] gbkBytes = Encoding.GetEncoding(936).GetBytes("学习");
        File.WriteAllBytes(source, gbkBytes);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            new DictionaryExtractor().Extract(
                game,
                output,
                options: new EraParserOptions { FileEncoding = Encoding.GetEncoding(936) }));

        Assert.Contains("ERB/ambiguous.erb", error.Message);
        Assert.Contains("歧义", error.Message);
        Assert.False(Directory.Exists(output));
        Assert.Equal(gbkBytes, File.ReadAllBytes(source));
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


    [Fact]
    public void Extract_ErbNullableBoundariesPreserveDeclarationAndCompoundAssignmentEntries()
    {
        string root = CreateTempDirectory();
        string game = Path.Combine(root, "Game");
        string output = Path.Combine(root, "Output");
        Write(game, "ERB/nullable-boundaries.erb", string.Join(Environment.NewLine,
        [
            "@関数名,引数名=定義パラメータ",
            "#DIM 変数名 = 10",
            "RESULTS:0 += \"追加テキスト\""
        ]));

        new DictionaryExtractor().Extract(
            game,
            output,
            options: new EraParserOptions
            {
                HideVariableEntries = false,
                HideEnglishText = false
            });

        JArray entries = JArray.Parse(File.ReadAllText(
            Path.Combine(output, "ERB", "nullable-boundaries.json")));

        string[] originals = entries
            .Select(entry => (string?)entry!["original"] ?? string.Empty)
            .ToArray();

        Assert.Contains("関数名", originals);
        Assert.Contains("引数名", originals);
        Assert.Contains("変数名", originals);
        Assert.Contains(originals, original =>
            original.Contains("定義パラメータ", StringComparison.Ordinal));
        Assert.Contains(originals, original =>
            original.Contains("追加テキスト", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_ErbGrouping_SeparatesIfElseBranchesAndControlFlowSegments()
    {
        string root = CreateTempDirectory();
        string game = Path.Combine(root, "Game");
        string output = Path.Combine(root, "Output");
        Write(game, "ERB/flow.erb", string.Join(Environment.NewLine,
        [
            "@FLOW",
            "IF FLAG:1",
            "PRINTFORML 条件成立时的文本",
            "ELSE",
            "PRINTFORML 条件不成立时的文本",
            "ENDIF",
            "PRINTFORML 控制转移前的文本",
            "GOTO LABEL_END",
            "PRINTFORML 控制转移后的文本",
            "@LABEL_END",
            "PRINTFORML 标签函数中的文本"
        ]));

        new DictionaryExtractor().Extract(game, output);

        JObject sidecar = JObject.Parse(File.ReadAllText(
            Path.Combine(output, "AI上下文", "ERB", "flow.groups.json")));
        JArray entries = Assert.IsType<JArray>(sidecar["entries"]);

        string GroupFor(string original) =>
            entries.Children<JObject>()
                .Single(entry => (string?)entry["original"] == original)["groupId"]!.Value<string>()!;

        Assert.NotEqual(
            GroupFor("条件成立时的文本"),
            GroupFor("条件不成立时的文本"));
        Assert.NotEqual(
            GroupFor("控制转移前的文本"),
            GroupFor("控制转移后的文本"));
        Assert.NotEqual(
            GroupFor("控制转移后的文本"),
            GroupFor("标签函数中的文本"));
    }

    [Fact]
    public void Extract_CanIgnoreAmbiguousAndInvalidFiles_AndContinueExtractingValidFiles()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        System.Text.Encoding gbk = System.Text.Encoding.GetEncoding(936);

        string root = CreateTempDirectory();
        string game = Path.Combine(root, "Game");
        string output = Path.Combine(root, "Output");
        Directory.CreateDirectory(Path.Combine(game, "ERB"));

        File.WriteAllBytes(
            Path.Combine(game, "ERB", "valid.erb"),
            gbk.GetBytes("@MAIN\r\nPRINTL 你好世界\r\n"));
        File.WriteAllBytes(
            Path.Combine(game, "ERB", "ambiguous.erb"),
            gbk.GetBytes("学习"));
        File.WriteAllBytes(
            Path.Combine(game, "ERB", "invalid.erb"),
            [0xFF, 0xFE, 0x00]);

        var options = new EraParserOptions { FileEncoding = gbk };
        var extractor = new DictionaryExtractor();

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            extractor.Extract(game, output, options: options));
        Assert.True(GameFileEncodingPreflightException.TryExtract(error, out GameFileEncodingPreflightException? preflight));
        Assert.Equal(2, preflight!.Issues.Count);
        Assert.False(Directory.Exists(output));

        DictionaryExtractionResult result = extractor.Extract(
            game,
            output,
            options: options,
            ignoreEncodingIssueFiles: true);

        Assert.True(File.Exists(Path.Combine(output, "ERB", "valid.json")));
        Assert.False(File.Exists(Path.Combine(output, "ERB", "ambiguous.json")));
        Assert.False(File.Exists(Path.Combine(output, "ERB", "invalid.json")));
        Assert.Contains(result.Warnings, warning => warning.Contains("ERB/ambiguous.erb", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, warning => warning.Contains("ERB/invalid.erb", StringComparison.Ordinal));
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
        File.WriteAllText(path, contents, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
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
