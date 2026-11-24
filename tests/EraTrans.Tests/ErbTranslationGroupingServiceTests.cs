using EraTrans.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace EraTrans.Tests;

public sealed class ErbTranslationGroupingServiceTests
{
    [Fact]
    public void TryWriteOrUpdate_StoresSeparateSidecar_AndKeepsRepeatedOccurrencesSeparate()
    {
        using var temporary = new TemporaryDirectory();
        string output = temporary.Path;
        string dictionaryDirectory = Path.Combine(output, "字典");
        string dictionaryPath = Path.Combine(dictionaryDirectory, "ERB", "sample.json");
        string candidatePath = Path.Combine(temporary.Path, "candidate.json");
        string gameDirectory = Path.Combine(temporary.Path, "Game");
        string sourcePath = WriteSource(gameDirectory, "ERB/sample.erb", "@FIRST\\nPRINTFORML 重复文本\\n@SECOND\\nPRINTFORML 重复文本");

        var sourceEntries = new[]
        {
            Source("文本ERB\\sample00000", "重复文本", 0, 2, "FIRST", "", "", "", null),
            Source("文本ERB\\sample00001", "重复文本", 1, 20, "SECOND", "", "", "", null)
        };
        WriteDictionary(dictionaryPath, sourceEntries);
        WriteDictionary(candidatePath, sourceEntries);

        var service = new ErbTranslationGroupingService();
        Assert.True(service.TryWriteOrUpdate(
            dictionaryDirectory,
            dictionaryPath,
            candidatePath,
            sourcePath,
            ErbTranslationGroupingService.ComputeSourceFingerprint(sourcePath),
            "ERB/sample.erb",
            sourceEntries,
            [],
            out string sidecarPath,
            out string? warning), warning);

        Assert.True(File.Exists(sidecarPath));
        Assert.Equal(
            Path.Combine(output, "辅助数据", "ERB", "sample.groups.json"),
            sidecarPath);

        DictionaryEditorDocument document = new DictionaryEditorService().Load(dictionaryPath);
        Assert.True(service.TryLoad(
            dictionaryDirectory,
            dictionaryPath,
            document.Entries.ToArray(),
            gameDirectory,
            out ErbTranslationGroupingIndex? index,
            out string? reason), reason);

        Assert.NotNull(index);
        Assert.Equal(2, index!.Entries.Count);
        Assert.Equal(2, index.Groups.Count);
        Assert.NotEqual(index.Entries[0].GroupId, index.Entries[1].GroupId);

    }

    [Fact]
    public void TryWriteOrUpdate_SplitsGroupsWhenControlScopeOrBranchLabelChanges()
    {
        using var temporary = new TemporaryDirectory();
        string dictionaryDirectory = Path.Combine(temporary.Path, "字典");
        string dictionaryPath = Path.Combine(dictionaryDirectory, "ERB", "menu.json");
        string candidatePath = Path.Combine(temporary.Path, "candidate.json");
        string gameDirectory = Path.Combine(temporary.Path, "Game");
        string sourcePath = WriteSource(gameDirectory, "ERB/menu.erb",
            "@MENU\\nPRINTFORML 菜单提示\\nPRINTFORML 第一项\\nPRINTFORML 第二项");

        ErbTranslationSourceEntry[] sourceEntries =
        [
            Source("文本ERB\\menu00000", "菜单提示", 0, 2, "MENU", "flow:0", "menu_0001", "MENU_PROMPT", null),
            Source("文本ERB\\menu00001", "第一项", 1, 3, "MENU", "flow:0", "menu_0001", "CASE 0", null),
            Source("文本ERB\\menu00002", "第二项", 2, 4, "MENU", "flow:0", "menu_0002", "CASE 0", null)
        ];
        WriteDictionary(dictionaryPath, sourceEntries);
        WriteDictionary(candidatePath, sourceEntries);

        var service = new ErbTranslationGroupingService();
        Assert.True(service.TryWriteOrUpdate(
            dictionaryDirectory,
            dictionaryPath,
            candidatePath,
            sourcePath,
            ErbTranslationGroupingService.ComputeSourceFingerprint(sourcePath),
            "ERB/menu.erb",
            sourceEntries,
            [],
            out _,
            out string? writeWarning), writeWarning);

        DictionaryEditorDocument document = new DictionaryEditorService().Load(dictionaryPath);
        Assert.True(service.TryLoad(
            dictionaryDirectory,
            dictionaryPath,
            document.Entries.ToArray(),
            gameDirectory,
            out ErbTranslationGroupingIndex? index,
            out string? loadReason), loadReason);

        Assert.NotNull(index);
        Assert.Equal(3, index!.Groups.Count);
        Assert.All(index.Groups, group => Assert.Single(group.EntryKeys));
        Assert.Equal(
            new[] { "MENU_PROMPT", "CASE 0", "CASE 0" },
            index.Groups.Select(group => group.BranchLabel));
    }

    [Fact]
    public void TryLoad_AcceptsErbSourceFileDirectlyUnderGameRoot()
    {
        using var temporary = new TemporaryDirectory();
        string dictionaryDirectory = Path.Combine(temporary.Path, "字典");
        string dictionaryPath = Path.Combine(dictionaryDirectory, "sample.json");
        string candidatePath = Path.Combine(temporary.Path, "candidate.json");
        string gameDirectory = Path.Combine(temporary.Path, "Game");
        string sourcePath = WriteSource(
            gameDirectory,
            "sample.erb",
            "@SAMPLE\\nPRINTFORML 根目录中的对白");

        ErbTranslationSourceEntry[] sourceEntries =
        [
            Source("文本sample00000", "根目录中的对白", 0, 2, "SAMPLE", "", "", "", null)
        ];
        WriteDictionary(dictionaryPath, sourceEntries);
        WriteDictionary(candidatePath, sourceEntries);

        var service = new ErbTranslationGroupingService();
        Assert.True(service.TryWriteOrUpdate(
            dictionaryDirectory,
            dictionaryPath,
            candidatePath,
            sourcePath,
            ErbTranslationGroupingService.ComputeSourceFingerprint(sourcePath),
            "sample.erb",
            sourceEntries,
            [],
            out _,
            out string? writeWarning), writeWarning);

        DictionaryEditorDocument document = new DictionaryEditorService().Load(dictionaryPath);
        Assert.True(service.TryLoad(
            dictionaryDirectory,
            dictionaryPath,
            document.Entries.ToArray(),
            gameDirectory,
            out ErbTranslationGroupingIndex? index,
            out string? loadReason), loadReason);

        ErbTranslationGroup group = Assert.Single(index!.Groups);
        Assert.Equal(new[] { "文本sample00000" }, group.EntryKeys);
    }

    [Fact]
    public void TryLoad_TranslationEditsDoNotInvalidateSidecar_ButOriginalChangesDo()
    {
        using var temporary = new TemporaryDirectory();
        string dictionaryDirectory = Path.Combine(temporary.Path, "字典");
        string dictionaryPath = Path.Combine(dictionaryDirectory, "ERB", "menu.json");
        string candidatePath = Path.Combine(temporary.Path, "candidate.json");
        string gameDirectory = Path.Combine(temporary.Path, "Game");
        string sourcePath = WriteSource(gameDirectory, "ERB/menu.erb", "@MENU\\nPRINTFORML 菜单提示");
        var sourceEntries = new[]
        {
            Source("文本ERB\\menu00000", "菜单提示", 0, 1, "MENU", "", "", "", null)
        };
        WriteDictionary(dictionaryPath, sourceEntries);
        WriteDictionary(candidatePath, sourceEntries);

        var service = new ErbTranslationGroupingService();
        Assert.True(service.TryWriteOrUpdate(
            dictionaryDirectory, dictionaryPath, candidatePath, sourcePath,
            ErbTranslationGroupingService.ComputeSourceFingerprint(sourcePath), "ERB/menu.erb",
            sourceEntries, [], out _, out string? warning), warning);

        JArray translated = JArray.Parse(File.ReadAllText(dictionaryPath));
        translated[0]!["translation"] = "人工译文";
        translated[0]!["stage"] = 1;
        File.WriteAllText(dictionaryPath, translated.ToString(Formatting.Indented));

        DictionaryEditorDocument translatedDocument = new DictionaryEditorService().Load(dictionaryPath);
        Assert.True(service.TryLoad(
            dictionaryDirectory, dictionaryPath, translatedDocument.Entries.ToArray(),
            gameDirectory,
            out _, out string? translatedReason), translatedReason);

        JArray changed = JArray.Parse(File.ReadAllText(dictionaryPath));
        changed[0]!["original"] = "修改后的菜单提示";
        File.WriteAllText(dictionaryPath, changed.ToString(Formatting.Indented));
        DictionaryEditorDocument changedDocument = new DictionaryEditorService().Load(dictionaryPath);

        Assert.False(service.TryLoad(
            dictionaryDirectory, dictionaryPath, changedDocument.Entries.ToArray(),
            gameDirectory,
            out _, out string? staleReason));
        Assert.Contains("已发生变化", staleReason ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void TryLoad_RejectsSidecarWithMissingRequiredSourceMetadata()
    {
        using var temporary = new TemporaryDirectory();
        string output = temporary.Path;
        string dictionaryDirectory = Path.Combine(output, "字典");
        string dictionaryPath = Path.Combine(dictionaryDirectory, "ERB", "sample.json");
        string candidatePath = Path.Combine(output, "candidate.json");
        string gameDirectory = Path.Combine(output, "Game");
        string sourcePath = WriteSource(gameDirectory, "ERB/sample.erb", "@SAMPLE\\nPRINTFORML 文本");

        ErbTranslationSourceEntry[] sourceEntries =
        [
            Source("文本ERB\\sample00000", "文本", 0, 2, "SAMPLE", "", "", "", null)
        ];
        WriteDictionary(dictionaryPath, sourceEntries);
        WriteDictionary(candidatePath, sourceEntries);

        var service = new ErbTranslationGroupingService();
        Assert.True(service.TryWriteOrUpdate(
            dictionaryDirectory,
            dictionaryPath,
            candidatePath,
            sourcePath,
            ErbTranslationGroupingService.ComputeSourceFingerprint(sourcePath),
            "ERB/sample.erb",
            sourceEntries,
            [],
            out string sidecarPath,
            out string? warning), warning);

        JObject sidecar = JObject.Parse(File.ReadAllText(sidecarPath));
        sidecar["sourceFile"] = null;
        File.WriteAllText(sidecarPath, sidecar.ToString(Formatting.Indented));

        DictionaryEditorDocument document = new DictionaryEditorService().Load(dictionaryPath);
        Assert.False(service.TryLoad(
            dictionaryDirectory,
            dictionaryPath,
            document.Entries.ToArray(),
            gameDirectory,
            out _,
            out string? reason));
        Assert.Contains("缺少必要", reason ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void TryLoad_RejectsChangedErbSourceOrSameNameErhCompanion()
    {
        using var temporary = new TemporaryDirectory();
        string dictionaryDirectory = Path.Combine(temporary.Path, "字典");
        string dictionaryPath = Path.Combine(dictionaryDirectory, "ERB", "menu.json");
        string candidatePath = Path.Combine(temporary.Path, "candidate.json");
        string gameDirectory = Path.Combine(temporary.Path, "Game");
        string sourcePath = WriteSource(gameDirectory, "ERB/menu.erb", "@MENU\\nPRINTFORML 菜单提示");
        string companionPath = WriteSource(gameDirectory, "ERB/menu.erh", "#DIM 菜单变量");

        var sourceEntries = new[]
        {
            Source("文本ERB\\menu00000", "菜单提示", 0, 1, "MENU", "", "", "", null)
        };
        WriteDictionary(dictionaryPath, sourceEntries);
        WriteDictionary(candidatePath, sourceEntries);

        var service = new ErbTranslationGroupingService();
        Assert.True(service.TryWriteOrUpdate(
            dictionaryDirectory,
            dictionaryPath,
            candidatePath,
            sourcePath,
            ErbTranslationGroupingService.ComputeSourceFingerprint(sourcePath),
            "ERB/menu.erb",
            sourceEntries,
            [],
            out _,
            out string? warning), warning);

        File.WriteAllText(companionPath, "#DIM 菜单变量\\n#DIM 新增变量");
        DictionaryEditorDocument document = new DictionaryEditorService().Load(dictionaryPath);

        Assert.False(service.TryLoad(
            dictionaryDirectory,
            dictionaryPath,
            document.Entries.ToArray(),
            gameDirectory,
            out _,
            out string? reason));
        Assert.Contains("ERB 源文件或其同名 ERH 文件已发生变化", reason ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void TryWriteOrUpdate_GroupsKnownHtmlDisplayCallsWithAdjacentPrintedText()
    {
        using var temporary = new TemporaryDirectory();
        string dictionaryDirectory = Path.Combine(temporary.Path, "字典");
        string dictionaryPath = Path.Combine(dictionaryDirectory, "ERB", "html.json");
        string candidatePath = Path.Combine(temporary.Path, "candidate.json");
        string gameDirectory = Path.Combine(temporary.Path, "Game");
        string sourcePath = WriteSource(gameDirectory, "ERB/html.erb", "@HTML_MENU\\nPRINTFORML 第一段\\nCALL HTMLPRINTL, \"第二段\"\\nPRINTFORML 第三段");

        ErbTranslationSourceEntry[] sourceEntries =
        [
            Source("文本ERB\\html00000", "第一段", 0, 2, "HTML_MENU", "flow:0", "", "", null),
            new(
                "文本ERB\\html00001",
                "第二段",
                1,
                3,
                "HTML_MENU",
                "CALL",
                "flow:0",
                "",
                "",
                "HTMLPRINTL"),
            Source("文本ERB\\html00002", "第三段", 2, 4, "HTML_MENU", "flow:0", "", "", null)
        ];
        WriteDictionary(dictionaryPath, sourceEntries);
        WriteDictionary(candidatePath, sourceEntries);

        var service = new ErbTranslationGroupingService();
        Assert.True(service.TryWriteOrUpdate(
            dictionaryDirectory,
            dictionaryPath,
            candidatePath,
            sourcePath,
            ErbTranslationGroupingService.ComputeSourceFingerprint(sourcePath),
            "ERB/html.erb",
            sourceEntries,
            [],
            out _,
            out string? warning), warning);

        DictionaryEditorDocument document = new DictionaryEditorService().Load(dictionaryPath);
        Assert.True(service.TryLoad(
            dictionaryDirectory,
            dictionaryPath,
            document.Entries.ToArray(),
            gameDirectory,
            out ErbTranslationGroupingIndex? index,
            out string? reason), reason);

        Assert.Single(index!.Groups);
        Assert.Equal(
            new[] { "文本ERB\\html00000", "文本ERB\\html00001", "文本ERB\\html00002" },
            Assert.Single(index.Groups).EntryKeys);
    }

    [Fact]
    public void TryWriteOrUpdate_WritesSchemaVersionTwo()
    {
        using var temporary = new TemporaryDirectory();
        string dictionaryDirectory = Path.Combine(temporary.Path, "字典");
        string dictionaryPath = Path.Combine(dictionaryDirectory, "ERB", "sample.json");
        string candidatePath = Path.Combine(temporary.Path, "candidate.json");
        string gameDirectory = Path.Combine(temporary.Path, "Game");
        string sourcePath = WriteSource(gameDirectory, "ERB/sample.erb", "@SAMPLE\\nPRINTFORML 文本");
        var sourceEntries = new[]
        {
            Source("文本ERB\\sample00000", "文本", 0, 1, "SAMPLE", "", "", "", null)
        };
        WriteDictionary(dictionaryPath, sourceEntries);
        WriteDictionary(candidatePath, sourceEntries);

        var service = new ErbTranslationGroupingService();
        Assert.True(service.TryWriteOrUpdate(
            dictionaryDirectory, dictionaryPath, candidatePath, sourcePath,
            ErbTranslationGroupingService.ComputeSourceFingerprint(sourcePath), "ERB/sample.erb",
            sourceEntries, [], out string sidecarPath, out string? warning, packing: "screen"), warning);

        JObject sidecar = JObject.Parse(File.ReadAllText(sidecarPath));
        Assert.Equal(2, sidecar["schemaVersion"]!.Value<int>());
        Assert.Equal("screen", sidecar["packing"]!.Value<string>());
    }

    [Fact]
    public void TryLoad_AcceptsVersionOneSidecarWithoutCommandKindOrPacking()
    {
        using var temporary = new TemporaryDirectory();
        string dictionaryDirectory = Path.Combine(temporary.Path, "字典");
        string dictionaryPath = Path.Combine(dictionaryDirectory, "ERB", "sample.json");
        string candidatePath = Path.Combine(temporary.Path, "candidate.json");
        string gameDirectory = Path.Combine(temporary.Path, "Game");
        string sourcePath = WriteSource(gameDirectory, "ERB/sample.erb", "@SAMPLE\\nPRINTFORML 文本");
        var sourceEntries = new[]
        {
            Source("文本ERB\\sample00000", "文本", 0, 1, "SAMPLE", "", "", "", null)
        };
        WriteDictionary(dictionaryPath, sourceEntries);
        WriteDictionary(candidatePath, sourceEntries);

        var service = new ErbTranslationGroupingService();
        Assert.True(service.TryWriteOrUpdate(
            dictionaryDirectory, dictionaryPath, candidatePath, sourcePath,
            ErbTranslationGroupingService.ComputeSourceFingerprint(sourcePath), "ERB/sample.erb",
            sourceEntries, [], out string sidecarPath, out string? warning, packing: "screen"), warning);

        // 模拟旧版（v1）sidecar：没有 packing，词条也没有 command/kind。
        JObject sidecar = JObject.Parse(File.ReadAllText(sidecarPath));
        sidecar["schemaVersion"] = 1;
        sidecar.Remove("packing");
        if (sidecar["entries"] is JArray entries)
        {
            foreach (JObject entry in entries.OfType<JObject>())
            {
                entry.Remove("command");
                entry.Remove("kind");
            }
        }
        File.WriteAllText(sidecarPath, sidecar.ToString(Formatting.Indented));

        DictionaryEditorDocument document = new DictionaryEditorService().Load(dictionaryPath);
        Assert.True(service.TryLoad(
            dictionaryDirectory, dictionaryPath, document.Entries.ToArray(), gameDirectory,
            out ErbTranslationGroupingIndex? index, out string? reason), reason);
        Assert.Equal(1, index!.SchemaVersion);
        Assert.Null(index.Packing);
        Assert.Single(index.Entries);
    }

    [Fact]
    public void TryLoad_RejectsUnsupportedSchemaVersion()
    {
        using var temporary = new TemporaryDirectory();
        string dictionaryDirectory = Path.Combine(temporary.Path, "字典");
        string dictionaryPath = Path.Combine(dictionaryDirectory, "ERB", "sample.json");
        string candidatePath = Path.Combine(temporary.Path, "candidate.json");
        string gameDirectory = Path.Combine(temporary.Path, "Game");
        string sourcePath = WriteSource(gameDirectory, "ERB/sample.erb", "@SAMPLE\\nPRINTFORML 文本");
        var sourceEntries = new[]
        {
            Source("文本ERB\\sample00000", "文本", 0, 1, "SAMPLE", "", "", "", null)
        };
        WriteDictionary(dictionaryPath, sourceEntries);
        WriteDictionary(candidatePath, sourceEntries);

        var service = new ErbTranslationGroupingService();
        Assert.True(service.TryWriteOrUpdate(
            dictionaryDirectory, dictionaryPath, candidatePath, sourcePath,
            ErbTranslationGroupingService.ComputeSourceFingerprint(sourcePath), "ERB/sample.erb",
            sourceEntries, [], out string sidecarPath, out string? warning), warning);

        JObject sidecar = JObject.Parse(File.ReadAllText(sidecarPath));
        sidecar["schemaVersion"] = 99;
        File.WriteAllText(sidecarPath, sidecar.ToString(Formatting.Indented));

        DictionaryEditorDocument document = new DictionaryEditorService().Load(dictionaryPath);
        Assert.False(service.TryLoad(
            dictionaryDirectory, dictionaryPath, document.Entries.ToArray(), gameDirectory,
            out _, out string? reason));
        Assert.Contains("版本不受支持", reason ?? string.Empty, StringComparison.Ordinal);
    }

    private static string WriteSource(string gameDirectory, string relativePath, string content)
    {
        string fullPath = Path.Combine(
            gameDirectory,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        return fullPath;
    }

    private static ErbTranslationSourceEntry Source(
        string key,
        string original,
        int sequence,
        int line,
        string function,
        string branchPath,
        string controlScope,
        string branchLabel,
        string? callTarget) =>
        new(
            key,
            original,
            sequence,
            line,
            function,
            "PRINTFORML",
            branchPath,
            controlScope,
            branchLabel,
            callTarget);

    private static void WriteDictionary(string path, IEnumerable<ErbTranslationSourceEntry> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var dictionary = new JArray(entries.Select(entry => new JObject
        {
            ["key"] = entry.Key,
            ["original"] = entry.Original,
            ["translation"] = "",
            ["stage"] = 0,
            ["context"] = "本地上下文"
        }));
        File.WriteAllText(path, dictionary.ToString(Formatting.Indented));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "EraTransGroupingTests",
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
