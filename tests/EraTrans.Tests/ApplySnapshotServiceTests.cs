using System.IO.Compression;
using System.Text;
using EraTrans.Core;
using EraTrans.Core.VisibleText;
using Xunit;

namespace EraTrans.Tests;

public sealed class ApplySnapshotServiceTests : IDisposable
{
    private readonly string _tempRoot;

    public ApplySnapshotServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "EraTrans.SnapshotTests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private (EraTransOutputLayout Layout, string GameDir, string OutputRoot) CreateWorkspace(
        ExtractionMode mode = ExtractionMode.Lexer)
    {
        string gameDir = Path.Combine(_tempRoot, "SampleGame");
        string outputRoot = Path.Combine(_tempRoot, "Output");
        Directory.CreateDirectory(Path.Combine(gameDir, "ERB"));
        Directory.CreateDirectory(Path.Combine(gameDir, "CSV"));
        Directory.CreateDirectory(Path.Combine(gameDir, "sav"));
        Directory.CreateDirectory(Path.Combine(gameDir, "resources"));

        File.WriteAllText(Path.Combine(gameDir, "ERB", "MAIN.ERB"), "PRINTL 原版日文\r\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        File.WriteAllText(Path.Combine(gameDir, "CSV", "Item.erd"), "0,原版道具\r\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        File.WriteAllText(Path.Combine(gameDir, "sav", "save00.sav"), "ORIGINAL_SAVE", Encoding.UTF8);
        File.WriteAllText(Path.Combine(gameDir, "emuera.config"), "WindowWidth:1280", Encoding.UTF8);
        File.WriteAllBytes(Path.Combine(gameDir, "resources", "cg.png"), [0x89, 0x50, 0x4E, 0x47]);
        File.WriteAllBytes(Path.Combine(gameDir, "emuera.exe"), [0x4D, 0x5A, 0x90, 0x00]);

        EraTransOutputLayout layout = EraTransOutputLayout.Create(outputRoot, gameDir, mode);
        Directory.CreateDirectory(layout.DictionaryDirectory);
        Directory.CreateDirectory(Path.Combine(layout.DictionaryDirectory, "ERB"));
        File.WriteAllText(
            Path.Combine(layout.DictionaryDirectory, "ERB", "MAIN.json"),
            """[{"key":"文本00001","original":"原版日文","translation":"第一版译文","stage":1,"context":""}]""",
            Encoding.UTF8);

        return (layout, gameDir, outputRoot);
    }

    private static void SeedGameCopy(EraTransOutputLayout layout, string erbText, string erdFileName, string saveText)
    {
        Directory.CreateDirectory(Path.Combine(layout.GameCopyDirectory, "ERB"));
        Directory.CreateDirectory(Path.Combine(layout.GameCopyDirectory, "CSV"));
        Directory.CreateDirectory(Path.Combine(layout.GameCopyDirectory, "sav"));
        Directory.CreateDirectory(Path.Combine(layout.GameCopyDirectory, "resources"));

        File.WriteAllText(Path.Combine(layout.GameCopyDirectory, "ERB", "MAIN.ERB"), erbText, Encoding.UTF8);
        File.WriteAllText(Path.Combine(layout.GameCopyDirectory, "CSV", erdFileName), "0,道具", Encoding.UTF8);
        File.WriteAllText(Path.Combine(layout.GameCopyDirectory, "sav", "save00.sav"), saveText, Encoding.UTF8);
        File.WriteAllText(Path.Combine(layout.GameCopyDirectory, "emuera.config"), "UserCustomWidth:1920", Encoding.UTF8);
        File.WriteAllBytes(Path.Combine(layout.GameCopyDirectory, "resources", "cg.png"), [0x89, 0x50, 0x4E, 0x47]);
        File.WriteAllBytes(Path.Combine(layout.GameCopyDirectory, "emuera.exe"), [0x4D, 0x5A, 0x90, 0x00]);
    }

    [Fact]
    public void CreatePostApplySnapshot_SkipsSavEmueraConfigAndMediaFiles()
    {
        (EraTransOutputLayout layout, _, _) = CreateWorkspace(ExtractionMode.Lexer);
        SeedGameCopy(layout, "PRINTL 副本第一版译文", "Item.erd", "PLAYER_SAVE_V1");

        var service = new ApplySnapshotService();
        var options = new EraParserOptions { ExtractionMode = ExtractionMode.Lexer };
        ApplySnapshotInfo snapshot = service.CreatePostApplySnapshot(layout, applyResult: null, options: options);

        Assert.True(snapshot.Manifest.HasGameCopyBackup);
        Assert.True(snapshot.Manifest.HasDictionaryBackup);
        Assert.Equal("V2", snapshot.Manifest.Mode);
        Assert.StartsWith(Path.Combine(layout.AiContextDirectory, ApplySnapshotService.SnapshotsFolderName), snapshot.DirectoryPath);

        string copyZip = Path.Combine(snapshot.DirectoryPath, ApplySnapshotService.CopySnapshotArchiveName);
        using ZipArchive archive = ZipFile.OpenRead(copyZip);
        string[] entries = archive.Entries.Select(e => e.FullName).ToArray();

        Assert.Contains("ERB/MAIN.ERB", entries);
        Assert.Contains("CSV/Item.erd", entries);
        Assert.DoesNotContain(entries, e => e.StartsWith("sav/", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(entries, e => e.Equals("emuera.config", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(entries, e => e.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(entries, e => e.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RestoreSnapshot_GameCopyOnly_CleansExtraErdAndPreservesSavAndCurrentDictionary()
    {
        (EraTransOutputLayout layout, _, _) = CreateWorkspace(ExtractionMode.Lexer);
        SeedGameCopy(layout, "PRINTL 第一版译文", "Item.erd", "PLAYER_SAVE_PROGRESS");

        var service = new ApplySnapshotService();
        var options = new EraParserOptions { ExtractionMode = ExtractionMode.Lexer };
        ApplySnapshotInfo snapshot1 = service.CreatePostApplySnapshot(layout, applyResult: null, options: options);

        // Simulate a 2nd apply round that modifies MAIN.ERB, renames Item.erd -> 道具.erd, and updates dict + save.
        string renamedErd = Path.Combine(layout.GameCopyDirectory, "CSV", "道具.erd");
        File.Delete(Path.Combine(layout.GameCopyDirectory, "CSV", "Item.erd"));
        File.WriteAllText(renamedErd, "0,第二版道具", Encoding.UTF8);
        File.WriteAllText(Path.Combine(layout.GameCopyDirectory, "ERB", "MAIN.ERB"), "PRINTL 第二版译文", Encoding.UTF8);
        File.WriteAllText(Path.Combine(layout.GameCopyDirectory, "sav", "save00.sav"), "PLAYER_SAVE_LATEST", Encoding.UTF8);

        string dictPath = Path.Combine(layout.DictionaryDirectory, "ERB", "MAIN.json");
        File.WriteAllText(
            dictPath,
            """[{"key":"文本00001","original":"原版日文","translation":"最新编辑中译文","stage":1,"context":""}]""",
            Encoding.UTF8);

        ApplyRollbackResult rollback = service.RestoreSnapshot(layout, snapshot1, restoreDictionary: false);

        Assert.Equal(1, rollback.RemovedRenamedFiles);
        Assert.Equal(2, rollback.RestoredGameFiles);
        Assert.Equal(0, rollback.RestoredDictionaryFiles);
        Assert.False(File.Exists(renamedErd));
        Assert.True(File.Exists(Path.Combine(layout.GameCopyDirectory, "CSV", "Item.erd")));
        Assert.Equal("PRINTL 第一版译文", File.ReadAllText(Path.Combine(layout.GameCopyDirectory, "ERB", "MAIN.ERB"), Encoding.UTF8));
        Assert.Equal("PLAYER_SAVE_LATEST", File.ReadAllText(Path.Combine(layout.GameCopyDirectory, "sav", "save00.sav"), Encoding.UTF8));
        Assert.Equal("UserCustomWidth:1920", File.ReadAllText(Path.Combine(layout.GameCopyDirectory, "emuera.config"), Encoding.UTF8));
        Assert.Contains("最新编辑中译文", File.ReadAllText(dictPath, Encoding.UTF8));
    }

    [Fact]
    public void RestoreSnapshot_WithDictionary_RestoresBothGameCopyAndDictionary()
    {
        (EraTransOutputLayout layout, _, _) = CreateWorkspace(ExtractionMode.Lexer);
        SeedGameCopy(layout, "PRINTL 第一版译文", "Item.erd", "PLAYER_SAVE_V1");

        var service = new ApplySnapshotService();
        ApplySnapshotInfo snapshot1 = service.CreatePostApplySnapshot(layout);

        string dictPath = Path.Combine(layout.DictionaryDirectory, "ERB", "MAIN.json");
        string extraDictPath = Path.Combine(layout.DictionaryDirectory, "ERB", "EXTRA.json");
        File.WriteAllText(
            dictPath,
            """[{"key":"文本00001","original":"原版日文","translation":"改坏的译文","stage":1,"context":""}]""",
            Encoding.UTF8);
        File.WriteAllText(extraDictPath, "[]", Encoding.UTF8);
        File.WriteAllText(Path.Combine(layout.GameCopyDirectory, "ERB", "MAIN.ERB"), "PRINTL 改坏的译文", Encoding.UTF8);

        ApplyRollbackResult rollback = service.RestoreSnapshot(layout, snapshot1, restoreDictionary: true);

        Assert.Equal(2, rollback.RestoredGameFiles);
        Assert.Equal(1, rollback.RestoredDictionaryFiles);
        Assert.Equal("PRINTL 第一版译文", File.ReadAllText(Path.Combine(layout.GameCopyDirectory, "ERB", "MAIN.ERB"), Encoding.UTF8));
        Assert.Contains("第一版译文", File.ReadAllText(dictPath, Encoding.UTF8));
        Assert.False(File.Exists(extraDictPath));
        Assert.Equal("PLAYER_SAVE_V1", File.ReadAllText(Path.Combine(layout.GameCopyDirectory, "sav", "save00.sav"), Encoding.UTF8));
    }

    [Fact]
    public void RestoreGameCopyToOriginal_RestoresTextCleansRenamedErdAndPreservesSavAndMedia()
    {
        (EraTransOutputLayout layout, string gameDir, _) = CreateWorkspace(ExtractionMode.Lexer);
        SeedGameCopy(layout, "PRINTL 汉化后的脚本", "汉化道具.erd", "PLAYER_SAVE_KEEP");

        var service = new ApplySnapshotService();
        RestoreOriginalCopyResult result = service.RestoreGameCopyToOriginal(layout);

        Assert.Equal(1, result.RemovedRenamedErdFiles);
        Assert.Equal(2, result.RestoredGameFiles);
        Assert.False(File.Exists(Path.Combine(layout.GameCopyDirectory, "CSV", "汉化道具.erd")));
        Assert.True(File.Exists(Path.Combine(layout.GameCopyDirectory, "CSV", "Item.erd")));
        Assert.Equal("PRINTL 原版日文\r\n", File.ReadAllText(Path.Combine(layout.GameCopyDirectory, "ERB", "MAIN.ERB"), Encoding.UTF8));
        Assert.Equal("PLAYER_SAVE_KEEP", File.ReadAllText(Path.Combine(layout.GameCopyDirectory, "sav", "save00.sav"), Encoding.UTF8));
        Assert.Equal("UserCustomWidth:1920", File.ReadAllText(Path.Combine(layout.GameCopyDirectory, "emuera.config"), Encoding.UTF8));
        Assert.True(File.Exists(Path.Combine(layout.GameCopyDirectory, "resources", "cg.png")));
        Assert.True(File.Exists(Path.Combine(layout.GameCopyDirectory, "emuera.exe")));
        Assert.Equal("ORIGINAL_SAVE", File.ReadAllText(Path.Combine(gameDir, "sav", "save00.sav"), Encoding.UTF8));
    }

    [Fact]
    public void CreatePostApplySnapshot_PrunesOldestBeyondRetentionLimit()
    {
        (EraTransOutputLayout layout, _, _) = CreateWorkspace(ExtractionMode.Lexer);
        SeedGameCopy(layout, "PRINTL 文本", "Item.erd", "SAVE");

        var service = new ApplySnapshotService();
        DateTimeOffset baseTime = new(2026, 11, 25, 10, 0, 0, TimeSpan.FromHours(8));

        var createdIds = new List<string>();
        for (int i = 0; i < 7; i++)
        {
            ApplySnapshotInfo snap = service.CreatePostApplySnapshot(
                layout,
                applyResult: null,
                options: null,
                maxSnapshots: 5,
                timestamp: baseTime.AddMinutes(i));
            createdIds.Add(snap.Manifest.Id);
        }

        IReadOnlyList<ApplySnapshotInfo> remaining = service.ListSnapshots(layout);
        Assert.Equal(5, remaining.Count);
        Assert.DoesNotContain(remaining, s => s.Manifest.Id == createdIds[0]);
        Assert.DoesNotContain(remaining, s => s.Manifest.Id == createdIds[1]);
        Assert.Equal(createdIds[6], remaining[0].Manifest.Id);
        Assert.Equal(createdIds[2], remaining[4].Manifest.Id);
    }

    [Fact]
    public void EndToEnd_TwoConsecutiveAppliesWithAllowUpdateExisting_RollbackAndRestoreOriginal()
    {
        (EraTransOutputLayout layout, string gameDir, _) = CreateWorkspace(ExtractionMode.Legacy);
        var options = new EraParserOptions
        {
            ExtractionMode = ExtractionMode.Legacy,
            EnableVariableAutoCorrection = false
        };

        string dictPath = Path.Combine(layout.DictionaryDirectory, "ERB", "MAIN.json");
        File.WriteAllText(
            dictPath,
            """[{"key":"文本00001","original":"原版日文","translation":"译文A","stage":1,"context":""}]""",
            Encoding.UTF8);

        var applier = new TranslationApplier();
        var snapshotService = new ApplySnapshotService();
        DateTimeOffset t0 = new(2026, 11, 25, 12, 0, 0, TimeSpan.FromHours(8));

        // 1. Round 1 Apply -> Snapshot #1 -> place simulated player save sav/save00.dat and custom config.
        TranslationApplyResult round1 = applier.Apply(
            layout.GameDirectory,
            layout.DictionaryDirectory,
            layout.GameCopyDirectory,
            options,
            ignoreEncodingIssueFiles: false,
            allowUpdateExisting: true);
        ApplySnapshotInfo snapshot1 = snapshotService.CreatePostApplySnapshot(
            layout,
            round1,
            options,
            timestamp: t0);

        string copyErbPath = Path.Combine(layout.GameCopyDirectory, "ERB", "MAIN.ERB");
        Assert.Contains("译文A", File.ReadAllText(copyErbPath, Encoding.UTF8));

        string playerSavePath = Path.Combine(layout.GameCopyDirectory, "sav", "save00.dat");
        File.WriteAllText(playerSavePath, "PLAYER_PROGRESS_100H", Encoding.UTF8);
        string copyConfigPath = Path.Combine(layout.GameCopyDirectory, "emuera.config");
        File.WriteAllText(copyConfigPath, "CustomFont:NotoSans", Encoding.UTF8);

        // 2. Edit dictionary to 译文B -> Round 2 Apply directly in-place (without deleting GameCopyDirectory) -> Snapshot #2.
        File.WriteAllText(
            dictPath,
            """[{"key":"文本00001","original":"原版日文","translation":"译文B","stage":1,"context":""}]""",
            Encoding.UTF8);

        TranslationApplyResult round2 = applier.Apply(
            layout.GameDirectory,
            layout.DictionaryDirectory,
            layout.GameCopyDirectory,
            options,
            ignoreEncodingIssueFiles: false,
            allowUpdateExisting: true);
        ApplySnapshotInfo snapshot2 = snapshotService.CreatePostApplySnapshot(
            layout,
            round2,
            options,
            timestamp: t0.AddMinutes(1));

        Assert.Contains("译文B", File.ReadAllText(copyErbPath, Encoding.UTF8));
        Assert.Equal("PLAYER_PROGRESS_100H", File.ReadAllText(playerSavePath, Encoding.UTF8));
        Assert.Equal("CustomFont:NotoSans", File.ReadAllText(copyConfigPath, Encoding.UTF8));
        Assert.Equal(2, snapshotService.ListSnapshots(layout).Count);
        Assert.Equal(snapshot2.Manifest.Id, snapshotService.ListSnapshots(layout)[0].Manifest.Id);

        // 3a. Rollback to Snapshot #1 (game copy only): copy reverts to 译文A, sav intact, dictionary remains 译文B.
        snapshotService.RestoreSnapshot(layout, snapshot1, restoreDictionary: false);
        Assert.Contains("译文A", File.ReadAllText(copyErbPath, Encoding.UTF8));
        Assert.DoesNotContain("译文B", File.ReadAllText(copyErbPath, Encoding.UTF8));
        Assert.Equal("PLAYER_PROGRESS_100H", File.ReadAllText(playerSavePath, Encoding.UTF8));
        Assert.Contains("译文B", File.ReadAllText(dictPath, Encoding.UTF8));

        // 3b. Rollback to Snapshot #1 (game copy + dictionary): both copy and dictionary align cleanly at 译文A.
        snapshotService.RestoreSnapshot(layout, snapshot1, restoreDictionary: true);
        Assert.Contains("译文A", File.ReadAllText(copyErbPath, Encoding.UTF8));
        Assert.Contains("译文A", File.ReadAllText(dictPath, Encoding.UTF8));
        Assert.DoesNotContain("译文B", File.ReadAllText(dictPath, Encoding.UTF8));
        Assert.Equal("PLAYER_PROGRESS_100H", File.ReadAllText(playerSavePath, Encoding.UTF8));

        // 4. Restore game copy to original Japanese: copy reverts to 原版日文, sav intact, original game untouched.
        snapshotService.RestoreGameCopyToOriginal(layout, options);
        Assert.Contains("原版日文", File.ReadAllText(copyErbPath, Encoding.UTF8));
        Assert.DoesNotContain("译文A", File.ReadAllText(copyErbPath, Encoding.UTF8));
        Assert.Equal("PLAYER_PROGRESS_100H", File.ReadAllText(playerSavePath, Encoding.UTF8));
        Assert.Equal("CustomFont:NotoSans", File.ReadAllText(copyConfigPath, Encoding.UTF8));
        Assert.Equal("ORIGINAL_SAVE", File.ReadAllText(Path.Combine(gameDir, "sav", "save00.sav"), Encoding.UTF8));
    }
}
