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
        AiTranslationSemanticContextGroup sameGroup = Assert.Single(batch.SemanticContextGroups);
        Assert.Equal("same-group", sameGroup.Relationship);
        Assert.Equal(new[] { "文本ERB\\sample00000" }, sameGroup.Entries.Select(entry => entry.Key));
    }

    [Fact]
    public void TryLoad_TranslationEditsDoNotInvalidateSidecar_ButOriginalChangesDo()
    {
        using var temporary = new TemporaryDirectory();
        string dictionaryDirectory = Path.Combine(temporary.Path, "字典");
        string dictionaryPath = Path.Combine(dictionaryDirectory, "ERB", "menu.json");
        string candidatePath = Path.Combine(temporary.Path, "candidate.json");
        var sourceEntries = new[]
        {
            Source("文本ERB\\menu00000", "菜单提示", 0, 1, "MENU", "", "", "", null)
        };
        WriteDictionary(dictionaryPath, sourceEntries);
        WriteDictionary(candidatePath, sourceEntries);

        var service = new ErbTranslationGroupingService();
        Assert.True(service.TryWriteOrUpdate(
            dictionaryDirectory, dictionaryPath, candidatePath, "ERB/menu.erb",
            sourceEntries, [], out _, out string? warning), warning);

        JArray translated = JArray.Parse(File.ReadAllText(dictionaryPath));
        translated[0]!["translation"] = "人工译文";
        translated[0]!["stage"] = 1;
        File.WriteAllText(dictionaryPath, translated.ToString(Formatting.Indented));

        DictionaryEditorDocument translatedDocument = new DictionaryEditorService().Load(dictionaryPath);
        Assert.True(service.TryLoad(
            dictionaryDirectory, dictionaryPath, translatedDocument.Entries.ToArray(),
            out _, out string? translatedReason), translatedReason);

        JArray changed = JArray.Parse(File.ReadAllText(dictionaryPath));
        changed[0]!["original"] = "修改后的菜单提示";
        File.WriteAllText(dictionaryPath, changed.ToString(Formatting.Indented));
        DictionaryEditorDocument changedDocument = new DictionaryEditorService().Load(dictionaryPath);

        Assert.False(service.TryLoad(
            dictionaryDirectory, dictionaryPath, changedDocument.Entries.ToArray(),
            out _, out string? staleReason));
        Assert.Contains("已发生变化", staleReason ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildTranslationBatches_ProvidesMenuPromptAndMutuallyExclusiveBranchesAsReadOnlyContext()
    {
        using var temporary = new TemporaryDirectory();
        string dictionaryDirectory = Path.Combine(temporary.Path, "字典");
        string dictionaryPath = Path.Combine(dictionaryDirectory, "ERB", "menu.json");
        string candidatePath = Path.Combine(temporary.Path, "candidate.json");

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
            "ERB/menu.erb",
            sourceEntries,
            [new ErbTranslationControlMarker(4, "MENU", "", "INPUT")],
            out _,
            out string? warning), warning);

        DictionaryEditorDocument document = new DictionaryEditorService().Load(dictionaryPath);
        Assert.True(service.TryLoad(
            dictionaryDirectory, dictionaryPath, document.Entries.ToArray(),
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
