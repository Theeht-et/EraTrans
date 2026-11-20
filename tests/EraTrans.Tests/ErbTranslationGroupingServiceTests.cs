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
            Path.Combine(output, "AI上下文", "ERB", "sample.groups.json"),
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

        ErbTranslationBatch batch = Assert.Single(service.BuildTranslationBatches(
            [document.Entries[0]],
            document.Entries.ToArray(),
            index));
        Assert.Empty(batch.SemanticContextGroups);
    }

    [Fact]
    public void BuildTranslationBatches_SendsAllPendingDialogueLinesTogetherWithoutDuplicatingThemAsContext()
    {
        using var temporary = new TemporaryDirectory();
        string dictionaryDirectory = Path.Combine(temporary.Path, "字典");
        string dictionaryPath = Path.Combine(dictionaryDirectory, "ERB", "dialogue.json");
        string candidatePath = Path.Combine(temporary.Path, "candidate.json");
        string gameDirectory = Path.Combine(temporary.Path, "Game");
        string sourcePath = WriteSource(gameDirectory, "ERB/dialogue.erb",
            "@DIALOGUE\\nPRINTFORML A句\\nPRINTFORML B句\\nPRINTFORML C句");
        ErbTranslationSourceEntry[] sourceEntries =
        [
            Source("文本ERB\\dialogue00000", "A句", 0, 2, "DIALOGUE", "flow:0", "", "", null),
            Source("文本ERB\\dialogue00001", "B句", 1, 3, "DIALOGUE", "flow:0", "", "", null),
            Source("文本ERB\\dialogue00002", "C句", 2, 4, "DIALOGUE", "flow:0", "", "", null)
        ];
        WriteDictionary(dictionaryPath, sourceEntries);
        WriteDictionary(candidatePath, sourceEntries);
        var service = new ErbTranslationGroupingService();
        Assert.True(service.TryWriteOrUpdate(dictionaryDirectory, dictionaryPath, candidatePath, sourcePath,
            ErbTranslationGroupingService.ComputeSourceFingerprint(sourcePath), "ERB/dialogue.erb",
            sourceEntries, [], out _, out string? warning), warning);
        DictionaryEditorDocument document = new DictionaryEditorService().Load(dictionaryPath);
        Assert.True(service.TryLoad(dictionaryDirectory, dictionaryPath, document.Entries.ToArray(), gameDirectory,
            out ErbTranslationGroupingIndex? index, out string? reason), reason);

        ErbTranslationBatch batch = Assert.Single(service.BuildTranslationBatches(
            document.Entries.ToArray(), document.Entries.ToArray(), index!));
        Assert.Equal(new[] { "A句", "B句", "C句" }, batch.Targets.Select(entry => entry.Original));
        var targetKeys = batch.Targets.Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain(batch.SemanticContextGroups.SelectMany(group => group.Entries),
            entry => targetKeys.Contains(entry.Key));
        Assert.Empty(batch.SemanticContextGroups);
    }

    [Fact]
    public void BuildTranslationBatches_DoesNotCountSharedTargetContextOncePerTarget()
    {
        using var temporary = new TemporaryDirectory();
        string dictionaryDirectory = Path.Combine(temporary.Path, "字典");
        string dictionaryPath = Path.Combine(dictionaryDirectory, "ERB/dialogue.json");
        string candidatePath = Path.Combine(temporary.Path, "candidate.json");
        string gameDirectory = Path.Combine(temporary.Path, "Game");

        string[] originals = Enumerable.Range(0, 5)
            .Select(index => new string((char)('A' + index), 1_000))
            .ToArray();
        string sourceContent = "@DIALOGUE\\n" +
                               string.Join("\\n", originals.Select(text => "PRINTFORML " + text));
        string sourcePath = WriteSource(gameDirectory, "ERB/dialogue.erb", sourceContent);
        ErbTranslationSourceEntry[] sourceEntries = originals
            .Select((original, sequence) => Source(
                $"文本ERB\\dialogue{sequence:D5}",
                original,
                sequence,
                sequence + 2,
                "DIALOGUE",
                "flow:0",
                "",
                "",
                null))
            .ToArray();

        WriteDictionary(dictionaryPath, sourceEntries);
        WriteDictionary(candidatePath, sourceEntries);
        var service = new ErbTranslationGroupingService();
        Assert.True(service.TryWriteOrUpdate(
            dictionaryDirectory,
            dictionaryPath,
            candidatePath,
            sourcePath,
            ErbTranslationGroupingService.ComputeSourceFingerprint(sourcePath),
            "ERB/dialogue.erb",
            sourceEntries,
            [],
            out _,
            out string? warning), warning);

        JArray dictionary = JArray.Parse(File.ReadAllText(dictionaryPath));
        string sharedContext = new('C', 2_000);
        foreach (JObject entry in dictionary.Cast<JObject>())
        {
            entry["context"] = sharedContext;
        }
        File.WriteAllText(dictionaryPath, dictionary.ToString(Formatting.Indented));

        DictionaryEditorDocument document = new DictionaryEditorService().Load(dictionaryPath);
        Assert.True(service.TryLoad(
            dictionaryDirectory,
            dictionaryPath,
            document.Entries.ToArray(),
            gameDirectory,
            out ErbTranslationGroupingIndex? index,
            out string? reason), reason);

        IReadOnlyList<ErbTranslationBatch> batches = service.BuildTranslationBatches(
            document.Entries.ToArray(),
            document.Entries.ToArray(),
            index!);

        ErbTranslationBatch batch = Assert.Single(batches);
        Assert.Equal(5, batch.Targets.Count);
        Assert.Equal(originals, batch.Targets.Select(target => target.Original));
    }

    [Fact]
    public void BuildTranslationBatches_KeepsNonTargetDialogueLinesAsReadOnlyContext()
    {
        using var temporary = new TemporaryDirectory();
        string dictionaryDirectory = Path.Combine(temporary.Path, "字典");
        string dictionaryPath = Path.Combine(dictionaryDirectory, "ERB", "dialogue.json");
        string candidatePath = Path.Combine(temporary.Path, "candidate.json");
        string gameDirectory = Path.Combine(temporary.Path, "Game");
        string sourcePath = WriteSource(gameDirectory, "ERB/dialogue.erb",
            "@DIALOGUE\\nPRINTFORML A句\\nPRINTFORML B句\\nPRINTFORML C句");
        ErbTranslationSourceEntry[] sourceEntries =
        [
            Source("文本ERB\\dialogue00000", "A句", 0, 2, "DIALOGUE", "flow:0", "", "", null),
            Source("文本ERB\\dialogue00001", "B句", 1, 3, "DIALOGUE", "flow:0", "", "", null),
            Source("文本ERB\\dialogue00002", "C句", 2, 4, "DIALOGUE", "flow:0", "", "", null)
        ];
        WriteDictionary(dictionaryPath, sourceEntries);
        WriteDictionary(candidatePath, sourceEntries);
        var service = new ErbTranslationGroupingService();
        Assert.True(service.TryWriteOrUpdate(dictionaryDirectory, dictionaryPath, candidatePath, sourcePath,
            ErbTranslationGroupingService.ComputeSourceFingerprint(sourcePath), "ERB/dialogue.erb",
            sourceEntries, [], out _, out string? warning), warning);
        DictionaryEditorDocument document = new DictionaryEditorService().Load(dictionaryPath);
        Assert.True(service.TryLoad(dictionaryDirectory, dictionaryPath, document.Entries.ToArray(), gameDirectory,
            out ErbTranslationGroupingIndex? index, out string? reason), reason);

        ErbTranslationBatch batch = Assert.Single(service.BuildTranslationBatches(
            [document.Entries[1]], document.Entries.ToArray(), index!));
        AiTranslationSemanticContextGroup context = Assert.Single(batch.SemanticContextGroups);
        Assert.Equal("same-group", context.Relationship);
        Assert.Equal(new[] { "A句", "C句" }, context.Entries.Select(entry => entry.Original));
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
    public void BuildTranslationBatches_ProvidesMenuPromptAndMutuallyExclusiveBranchesAsReadOnlyContext()
    {
        using var temporary = new TemporaryDirectory();
        string dictionaryDirectory = Path.Combine(temporary.Path, "字典");
        string dictionaryPath = Path.Combine(dictionaryDirectory, "ERB", "menu.json");
        string candidatePath = Path.Combine(temporary.Path, "candidate.json");
        string gameDirectory = Path.Combine(temporary.Path, "Game");
        string sourcePath = WriteSource(gameDirectory, "ERB/menu.erb", "@MENU\\nPRINTFORML 选择一个项目\\nINPUT\\nSELECTCASE RESULT\\nCASE 0\\nPRINTFORML 第一项\\nCASEELSE\\nPRINTFORML 第二项");

        var sourceEntries = new[]
        {
            Source("文本ERB\\menu00000", "选择一个项目", 0, 2, "MENU", "", "menu_0001", "MENU_PROMPT", null),
            Source("文本ERB\\menu00001", "第一项", 1, 7, "MENU", "menu_0001:SELECTCASE:CASE 0", "menu_0001", "CASE 0", null),
            Source("文本ERB\\menu00002", "第二项", 2, 10, "MENU", "menu_0001:SELECTCASE:CASEELSE", "menu_0001", "CASEELSE", null)
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
            [new ErbTranslationControlMarker(4, "MENU", "", "INPUT")],
            out _,
            out string? warning), warning);

        DictionaryEditorDocument document = new DictionaryEditorService().Load(dictionaryPath);
        Assert.True(service.TryLoad(
            dictionaryDirectory, dictionaryPath, document.Entries.ToArray(),
            gameDirectory,
            out ErbTranslationGroupingIndex? index, out string? reason), reason);

        ErbTranslationBatch batch = Assert.Single(service.BuildTranslationBatches(
            [document.Entries[1]],
            document.Entries.ToArray(),
            index!));

        Assert.Contains(batch.SemanticContextGroups, group =>
            group.Relationship == "menu-prompt" &&
            group.Entries.Any(entry => entry.Original == "选择一个项目"));
        Assert.Contains(batch.SemanticContextGroups, group =>
            group.Relationship == "alternative-branch" &&
            group.Entries.Any(entry => entry.Original == "第二项"));
        Assert.DoesNotContain(batch.Targets, target => target.Original == "第二项");
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
