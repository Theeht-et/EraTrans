using System.Text;
using EraTrans.Core;
using Xunit;

namespace EraTrans.Tests;

public sealed class DictionaryProgressServiceTests
{
    [Fact]
    public void ScanFile_CountsEditableTranslatedPendingIgnoredAndSkipsVariables()
    {
        string root = CreateTempDirectory();
        string path = Path.Combine(root, "ERB", "talk.json");
        WriteUtf8Bom(path,
            """
            [
              { "key": "变量ERB\\TALK00001", "original": "A", "translation": "", "stage": 0 },
              { "key": "文本ERB\\TALK00002", "original": "B", "translation": "已译", "stage": 1 },
              { "key": "文本ERB\\TALK00003", "original": "C", "translation": "", "stage": 0 },
              { "key": "文本ERB\\TALK00004", "original": "D", "translation": "", "stage": -1 },
              { "key": "文本ERB\\TALK00005", "original": "E", "translation": "" }
            ]
            """);

        var file = new DictionaryEditorFile(path, Path.Combine("ERB", "talk.json"));
        DictionaryFileProgress progress = DictionaryProgressScanner.ScanFile(file);

        Assert.False(progress.ReadFailed);
        Assert.Equal(5, progress.TotalEntries);
        Assert.Equal(4, progress.EditableCount);
        Assert.Equal(1, progress.TranslatedCount);
        Assert.Equal(2, progress.PendingCount);
        Assert.Equal(1, progress.IgnoredCount);
        Assert.Equal(3, progress.ActiveCount);
        Assert.Equal(DictionaryProgressStatus.InProgress, progress.Status);
        Assert.Equal("翻译中", progress.StatusLabel);
    }

    [Fact]
    public void ScanFile_TreatsAllTranslatedOrIgnoredAsCompleted_AndHandlesMalformedJson()
    {
        string root = CreateTempDirectory();
        string completedPath = Path.Combine(root, "done.json");
        WriteUtf8(completedPath,
            """
            [
              { "key": "文本A", "original": "A", "translation": "甲", "stage": 1 },
              { "key": "文本B", "original": "B", "translation": "", "stage": -1 }
            ]
            """);

        string badPath = Path.Combine(root, "bad.json");
        WriteUtf8(badPath, "[{ broken json");

        DictionaryFileProgress done = DictionaryProgressScanner.ScanFile(new DictionaryEditorFile(completedPath, "done.json"));
        Assert.Equal(DictionaryProgressStatus.Completed, done.Status);
        Assert.Equal(100.0, done.CompletionPercent);
        Assert.Equal("已完成", done.StatusLabel);

        DictionaryFileProgress bad = DictionaryProgressScanner.ScanFile(new DictionaryEditorFile(badPath, "bad.json"));
        Assert.True(bad.ReadFailed);
        Assert.Equal("读取失败", bad.StatusLabel);
    }

    [Fact]
    public void FromDocument_AndAggregates_ReflectLiveStageEdits()
    {
        string root = CreateTempDirectory();
        string pathA = Path.Combine(root, "ERB", "Sub", "a.json");
        string pathB = Path.Combine(root, "ERB", "Sub", "b.json");
        WriteUtf8(pathA,
            """
            [
              { "key": "文本A1", "original": "A1", "translation": "", "stage": 0 },
              { "key": "文本A2", "original": "A2", "translation": "", "stage": 0 }
            ]
            """);
        WriteUtf8(pathB,
            """
            [
              { "key": "文本B1", "original": "B1", "translation": "译B1", "stage": 1 }
            ]
            """);

        var fileA = new DictionaryEditorFile(pathA, Path.Combine("ERB", "Sub", "a.json"));
        var fileB = new DictionaryEditorFile(pathB, Path.Combine("ERB", "Sub", "b.json"));
        DictionaryEditorDocument docA = new DictionaryEditorService().Load(pathA);

        docA.Entries[0].Stage = 1;
        docA.Entries[1].Stage = -1;

        DictionaryFileProgress progA = DictionaryProgressScanner.FromDocument(fileA, docA);
        DictionaryFileProgress progB = DictionaryProgressScanner.ScanFile(fileB);

        Assert.Equal(DictionaryProgressStatus.Completed, progA.Status);
        Assert.Equal(1, progA.TranslatedCount);
        Assert.Equal(0, progA.PendingCount);
        Assert.Equal(1, progA.IgnoredCount);

        DictionaryWorkspaceProgress workspace = DictionaryProgressScanner.Aggregate([progA, progB]);
        Assert.Equal(2, workspace.TotalFiles);
        Assert.Equal(2, workspace.CompletedFiles);
        Assert.Equal(2, workspace.TotalTranslated);
        Assert.Equal(0, workspace.TotalPending);
        Assert.Equal(100.0, workspace.CompletionPercent);

        DictionaryFileTreeFolder tree = DictionaryFileTreeBuilder.Build([fileA, fileB]);
        var lookup = new Dictionary<string, DictionaryFileProgress>(StringComparer.OrdinalIgnoreCase)
        {
            [fileA.FullPath] = progA,
            [fileB.FullPath] = progB
        };
        DictionaryFolderProgress folderProgress = DictionaryProgressScanner.AggregateFolder(tree.Folders[0], lookup);
        Assert.Equal(2, folderProgress.TotalFiles);
        Assert.Equal(2, folderProgress.CompletedFiles);
        Assert.Equal(DictionaryProgressStatus.Completed, folderProgress.Status);
    }

    private static void WriteUtf8(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void WriteUtf8Bom(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "EraTransProgressTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
