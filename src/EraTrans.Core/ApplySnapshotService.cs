using System.Globalization;
using System.IO.Compression;
using Newtonsoft.Json;

namespace EraTrans.Core;

public sealed record ApplySnapshotRenamedFile(
    string PreviousRelativePath,
    string NewRelativePath);

public sealed record ApplySnapshotManifest(
    string Id,
    DateTimeOffset CreatedAt,
    string Mode,
    bool HasGameCopyBackup,
    bool HasDictionaryBackup,
    int BackedUpGameFileCount,
    int BackedUpDictionaryFileCount,
    IReadOnlyList<string> ModifiedFiles,
    IReadOnlyList<ApplySnapshotRenamedFile> RenamedFiles,
    bool Completed);

public sealed record ApplySnapshotInfo(
    string DirectoryPath,
    ApplySnapshotManifest Manifest);

public sealed record ApplyRollbackResult(
    int RestoredGameFiles,
    int RemovedRenamedFiles,
    int RestoredDictionaryFiles);

public sealed record RestoreOriginalCopyResult(
    int RestoredGameFiles,
    int RemovedRenamedErdFiles);

/// <summary>
/// Manages lightweight pre-apply snapshots and rollback/restore operations for
/// the working game copy and dictionary under <see cref="EraTransOutputLayout.AiContextDirectory"/>.
/// The original game directory is strictly read-only, and save data (<c>sav/</c>)
/// plus <c>emuera.config</c> are never touched.
/// </summary>
public sealed class ApplySnapshotService
{
    public const string SnapshotsFolderName = "apply-snapshots";
    public const string CopyBeforeArchiveName = "copy-before.zip";
    public const string DictionaryArchiveName = "dictionary-snapshot.zip";
    public const string ManifestFileName = "manifest.json";
    public const int DefaultMaxSnapshots = 5;

    private static readonly string[] BuiltInTextExtensions =
    [
        ".erb",
        ".erh",
        ".csv",
        ".erd",
        ".xml",
        ".res"
    ];

    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        Formatting = Formatting.Indented
    };

    public string GetSnapshotsRoot(EraTransOutputLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return Path.Combine(layout.AiContextDirectory, SnapshotsFolderName);
    }

    public ApplySnapshotInfo CreatePreApplySnapshot(
        EraTransOutputLayout layout,
        EraParserOptions? options = null,
        int maxSnapshots = DefaultMaxSnapshots,
        DateTimeOffset? timestamp = null,
        bool pruneOldSnapshots = true)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (maxSnapshots <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSnapshots), "Snapshot retention count must be positive.");
        }

        DateTimeOffset createdAt = timestamp ?? DateTimeOffset.Now;
        string snapshotsRoot = GetSnapshotsRoot(layout);
        Directory.CreateDirectory(snapshotsRoot);

        string snapshotId = AllocateUniqueSnapshotId(snapshotsRoot, createdAt);
        string snapshotDir = Path.Combine(snapshotsRoot, snapshotId);
        Directory.CreateDirectory(snapshotDir);

        HashSet<string> textExtensions = BuildTextExtensionSet(options);

        int backedUpGameFiles = 0;
        bool hasGameCopyBackup = false;
        if (Directory.Exists(layout.GameCopyDirectory))
        {
            string copyZipPath = Path.Combine(snapshotDir, CopyBeforeArchiveName);
            backedUpGameFiles = PackGameCopyTextFiles(layout.GameCopyDirectory, copyZipPath, textExtensions);
            if (backedUpGameFiles > 0)
            {
                hasGameCopyBackup = true;
            }
            else if (File.Exists(copyZipPath))
            {
                File.Delete(copyZipPath);
            }
        }

        int backedUpDictionaryFiles = 0;
        bool hasDictionaryBackup = false;
        if (Directory.Exists(layout.DictionaryDirectory))
        {
            string dictZipPath = Path.Combine(snapshotDir, DictionaryArchiveName);
            backedUpDictionaryFiles = PackDictionaryJsonFiles(layout.DictionaryDirectory, dictZipPath);
            if (backedUpDictionaryFiles > 0)
            {
                hasDictionaryBackup = true;
            }
            else if (File.Exists(dictZipPath))
            {
                File.Delete(dictZipPath);
            }
        }

        string mode = options is null
            ? InferModeFromLayout(layout)
            : EraTransOutputLayout.GetVersionFolderName(options.ExtractionMode);

        var manifest = new ApplySnapshotManifest(
            Id: snapshotId,
            CreatedAt: createdAt,
            Mode: mode,
            HasGameCopyBackup: hasGameCopyBackup,
            HasDictionaryBackup: hasDictionaryBackup,
            BackedUpGameFileCount: backedUpGameFiles,
            BackedUpDictionaryFileCount: backedUpDictionaryFiles,
            ModifiedFiles: Array.Empty<string>(),
            RenamedFiles: Array.Empty<ApplySnapshotRenamedFile>(),
            Completed: false);

        WriteManifest(snapshotDir, manifest);
        if (pruneOldSnapshots)
        {
            PruneOldSnapshots(layout, maxSnapshots);
        }

        return new ApplySnapshotInfo(snapshotDir, manifest);
    }

    public ApplySnapshotInfo RecordApplyResult(
        ApplySnapshotInfo snapshot,
        EraTransOutputLayout layout,
        TranslationApplyResult applyResult)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(applyResult);

        string gameCopyRoot = Path.GetFullPath(layout.GameCopyDirectory);

        string[] modifiedFiles = applyResult.ModifiedFiles
            .Select(path => ToRelativeUnixPath(gameCopyRoot, path))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        ApplySnapshotRenamedFile[] renamedFiles = applyResult.RenamedFiles
            .Select(item => new ApplySnapshotRenamedFile(
                ToRelativeUnixPath(gameCopyRoot, item.PreviousPath),
                ToRelativeUnixPath(gameCopyRoot, item.NewPath)))
            .Where(item => !string.IsNullOrWhiteSpace(item.NewRelativePath))
            .OrderBy(item => item.NewRelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        ApplySnapshotManifest updated = snapshot.Manifest with
        {
            ModifiedFiles = modifiedFiles,
            RenamedFiles = renamedFiles,
            Completed = true
        };

        WriteManifest(snapshot.DirectoryPath, updated);
        return snapshot with { Manifest = updated };
    }

    public void DeleteSnapshot(ApplySnapshotInfo? snapshot)
    {
        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.DirectoryPath))
        {
            return;
        }

        if (Directory.Exists(snapshot.DirectoryPath))
        {
            Directory.Delete(snapshot.DirectoryPath, recursive: true);
        }
    }

    public IReadOnlyList<ApplySnapshotInfo> ListSnapshots(EraTransOutputLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        string snapshotsRoot = GetSnapshotsRoot(layout);
        if (!Directory.Exists(snapshotsRoot))
        {
            return Array.Empty<ApplySnapshotInfo>();
        }

        var list = new List<ApplySnapshotInfo>();
        foreach (string dir in Directory.EnumerateDirectories(snapshotsRoot))
        {
            string manifestPath = Path.Combine(dir, ManifestFileName);
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            try
            {
                string json = File.ReadAllText(manifestPath);
                ApplySnapshotManifest? manifest = JsonConvert.DeserializeObject<ApplySnapshotManifest>(json);
                if (manifest is not null)
                {
                    list.Add(new ApplySnapshotInfo(dir, manifest));
                }
            }
            catch
            {
                // Ignore corrupted snapshot manifests so other snapshots remain usable.
            }
        }

        return list
            .OrderByDescending(item => item.Manifest.CreatedAt)
            .ThenByDescending(item => item.Manifest.Id, StringComparer.Ordinal)
            .ToArray();
    }

    public int PruneOldSnapshots(EraTransOutputLayout layout, int maxSnapshots = DefaultMaxSnapshots)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (maxSnapshots <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSnapshots), "Snapshot retention count must be positive.");
        }

        IReadOnlyList<ApplySnapshotInfo> snapshots = ListSnapshots(layout);
        if (snapshots.Count <= maxSnapshots)
        {
            return 0;
        }

        int deleted = 0;
        foreach (ApplySnapshotInfo oldSnapshot in snapshots.Skip(maxSnapshots))
        {
            if (Directory.Exists(oldSnapshot.DirectoryPath))
            {
                Directory.Delete(oldSnapshot.DirectoryPath, recursive: true);
                deleted++;
            }
        }

        return deleted;
    }

    /// <summary>
    /// Restores the game working copy to the state captured before the selected snapshot
    /// (cleaning up ERD renames introduced in the selected or later snapshots), and
    /// optionally rolls back the dictionary directory as well.
    /// </summary>
    public ApplyRollbackResult RestoreSnapshot(
        EraTransOutputLayout layout,
        ApplySnapshotInfo snapshot,
        bool restoreDictionary = false)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(snapshot);

        string gameCopyRoot = Path.GetFullPath(layout.GameCopyDirectory);
        int removedRenamedFiles = 0;
        int restoredGameFiles = 0;
        int restoredDictionaryFiles = 0;

        if (Directory.Exists(gameCopyRoot))
        {
            IReadOnlyList<ApplySnapshotInfo> allSnapshots = ListSnapshots(layout);
            var renamedToRemove = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (ApplySnapshotInfo candidate in allSnapshots)
            {
                bool isTargetOrNewer =
                    candidate.Manifest.CreatedAt > snapshot.Manifest.CreatedAt ||
                    (candidate.Manifest.CreatedAt == snapshot.Manifest.CreatedAt &&
                     string.CompareOrdinal(candidate.Manifest.Id, snapshot.Manifest.Id) >= 0) ||
                    string.Equals(candidate.Manifest.Id, snapshot.Manifest.Id, StringComparison.Ordinal);

                if (!isTargetOrNewer)
                {
                    continue;
                }

                foreach (ApplySnapshotRenamedFile renamed in candidate.Manifest.RenamedFiles)
                {
                    if (!string.IsNullOrWhiteSpace(renamed.NewRelativePath))
                    {
                        renamedToRemove.Add(renamed.NewRelativePath);
                    }
                }
            }

            foreach (string relativePath in renamedToRemove)
            {
                if (IsProtectedRelativePath(relativePath))
                {
                    continue;
                }

                string fullPath = ResolveSafePath(gameCopyRoot, relativePath);
                if (File.Exists(fullPath))
                {
                    ClearReadOnlyAttribute(fullPath);
                    File.Delete(fullPath);
                    removedRenamedFiles++;
                }
            }
        }

        string copyZipPath = Path.Combine(snapshot.DirectoryPath, CopyBeforeArchiveName);
        if (File.Exists(copyZipPath))
        {
            Directory.CreateDirectory(gameCopyRoot);
            using ZipArchive archive = ZipFile.OpenRead(copyZipPath);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name))
                {
                    continue;
                }

                string normalized = NormalizeRelativePath(entry.FullName);
                if (IsProtectedRelativePath(normalized))
                {
                    continue;
                }

                string destinationPath = ResolveSafePath(gameCopyRoot, normalized);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                if (File.Exists(destinationPath))
                {
                    ClearReadOnlyAttribute(destinationPath);
                }

                entry.ExtractToFile(destinationPath, overwrite: true);
                restoredGameFiles++;
            }
        }

        if (restoreDictionary)
        {
            string dictZipPath = Path.Combine(snapshot.DirectoryPath, DictionaryArchiveName);
            if (!File.Exists(dictZipPath))
            {
                throw new FileNotFoundException("该快照不包含字典备份，无法回退字典。", dictZipPath);
            }

            string dictRoot = Path.GetFullPath(layout.DictionaryDirectory);
            Directory.CreateDirectory(dictRoot);

            var snapshotJsonRelatives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (ZipArchive archive = ZipFile.OpenRead(dictZipPath))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name) ||
                        !entry.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string normalized = NormalizeRelativePath(entry.FullName);
                    snapshotJsonRelatives.Add(normalized);
                    string destinationPath = ResolveSafePath(dictRoot, normalized);
                    Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                    if (File.Exists(destinationPath))
                    {
                        ClearReadOnlyAttribute(destinationPath);
                    }

                    entry.ExtractToFile(destinationPath, overwrite: true);
                    restoredDictionaryFiles++;
                }
            }

            // Remove any extra JSON dictionaries created after the snapshot so the dictionary state matches.
            foreach (string existingJson in Directory.EnumerateFiles(dictRoot, "*.json", SearchOption.AllDirectories))
            {
                string relative = NormalizeRelativePath(Path.GetRelativePath(dictRoot, existingJson));
                if (!snapshotJsonRelatives.Contains(relative))
                {
                    ClearReadOnlyAttribute(existingJson);
                    File.Delete(existingJson);
                }
            }
        }

        return new ApplyRollbackResult(restoredGameFiles, removedRenamedFiles, restoredDictionaryFiles);
    }

    /// <summary>
    /// Restores the game working copy's text/script files directly from the read-only
    /// original game directory and removes translated/renamed <c>.erd</c> files,
    /// leaving media files, <c>sav/</c>, and <c>emuera.config</c> untouched.
    /// </summary>
    public RestoreOriginalCopyResult RestoreGameCopyToOriginal(
        EraTransOutputLayout layout,
        EraParserOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(layout);

        string gameRoot = Path.GetFullPath(layout.GameDirectory);
        string gameCopyRoot = Path.GetFullPath(layout.GameCopyDirectory);

        if (!Directory.Exists(gameRoot))
        {
            throw new DirectoryNotFoundException($"原游戏目录不存在：{gameRoot}");
        }

        if (!Directory.Exists(gameCopyRoot))
        {
            throw new DirectoryNotFoundException($"游戏副本目录不存在：{gameCopyRoot}");
        }

        HashSet<string> textExtensions = BuildTextExtensionSet(options);
        int removedRenamedErdFiles = 0;
        int restoredGameFiles = 0;

        // 1. Remove any .erd files in GameCopyDirectory that do not exist in the original game directory.
        foreach (string copyFile in Directory.EnumerateFiles(gameCopyRoot, "*", SearchOption.AllDirectories))
        {
            string relative = NormalizeRelativePath(Path.GetRelativePath(gameCopyRoot, copyFile));
            if (IsProtectedRelativePath(relative))
            {
                continue;
            }

            if (!Path.GetExtension(copyFile).Equals(".erd", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string originalCandidate = ResolveSafePath(gameRoot, relative);
            if (!File.Exists(originalCandidate))
            {
                ClearReadOnlyAttribute(copyFile);
                File.Delete(copyFile);
                removedRenamedErdFiles++;
            }
        }

        // 2. Copy all text/script files from the read-only original game directory into GameCopyDirectory.
        foreach (string originalFile in Directory.EnumerateFiles(gameRoot, "*", SearchOption.AllDirectories))
        {
            string relative = NormalizeRelativePath(Path.GetRelativePath(gameRoot, originalFile));
            if (IsProtectedRelativePath(relative))
            {
                continue;
            }

            if (!textExtensions.Contains(Path.GetExtension(originalFile)))
            {
                continue;
            }

            string destinationPath = ResolveSafePath(gameCopyRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            if (File.Exists(destinationPath))
            {
                ClearReadOnlyAttribute(destinationPath);
            }

            File.Copy(originalFile, destinationPath, overwrite: true);
            ClearReadOnlyAttribute(destinationPath);
            File.SetLastWriteTimeUtc(destinationPath, File.GetLastWriteTimeUtc(originalFile));
            restoredGameFiles++;
        }

        return new RestoreOriginalCopyResult(restoredGameFiles, removedRenamedErdFiles);
    }

    public static HashSet<string> BuildTextExtensionSet(EraParserOptions? options)
    {
        var extensions = new HashSet<string>(BuiltInTextExtensions, StringComparer.OrdinalIgnoreCase);
        if (options?.TargetFileExtensions is null)
        {
            return extensions;
        }

        foreach (string raw in options.TargetFileExtensions)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            string trimmed = raw.Trim();
            if (!trimmed.StartsWith('.'))
            {
                trimmed = "." + trimmed;
            }

            extensions.Add(trimmed);
        }

        return extensions;
    }

    public static bool IsProtectedRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return true;
        }

        string normalized = NormalizeRelativePath(relativePath);
        string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return true;
        }

        if (segments[0].Equals("sav", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string fileName = segments[^1];
        return fileName.Equals("emuera.config", StringComparison.OrdinalIgnoreCase);
    }

    private static int PackGameCopyTextFiles(
        string gameCopyDirectory,
        string zipPath,
        IReadOnlySet<string> textExtensions)
    {
        string gameCopyRoot = Path.GetFullPath(gameCopyDirectory);
        int count = 0;

        using ZipArchive archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (string file in Directory.EnumerateFiles(gameCopyRoot, "*", SearchOption.AllDirectories))
        {
            string relative = NormalizeRelativePath(Path.GetRelativePath(gameCopyRoot, file));
            if (IsProtectedRelativePath(relative))
            {
                continue;
            }

            if (!textExtensions.Contains(Path.GetExtension(file)))
            {
                continue;
            }

            archive.CreateEntryFromFile(file, relative, CompressionLevel.Fastest);
            count++;
        }

        return count;
    }

    private static int PackDictionaryJsonFiles(string dictionaryDirectory, string zipPath)
    {
        string dictRoot = Path.GetFullPath(dictionaryDirectory);
        int count = 0;

        using ZipArchive archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (string file in Directory.EnumerateFiles(dictRoot, "*.json", SearchOption.AllDirectories))
        {
            string relative = NormalizeRelativePath(Path.GetRelativePath(dictRoot, file));
            archive.CreateEntryFromFile(file, relative, CompressionLevel.Fastest);
            count++;
        }

        return count;
    }

    private static string AllocateUniqueSnapshotId(string snapshotsRoot, DateTimeOffset createdAt)
    {
        string baseId = createdAt.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        string candidate = baseId;
        int suffix = 1;
        while (Directory.Exists(Path.Combine(snapshotsRoot, candidate)))
        {
            candidate = $"{baseId}-{suffix++}";
        }

        return candidate;
    }

    private static string InferModeFromLayout(EraTransOutputLayout layout)
    {
        string? versionFolder = Directory.GetParent(layout.AiContextDirectory)?.Name;
        return string.Equals(versionFolder, "V2", StringComparison.OrdinalIgnoreCase) ? "V2" : "V1";
    }

    private static void WriteManifest(string snapshotDir, ApplySnapshotManifest manifest)
    {
        string manifestPath = Path.Combine(snapshotDir, ManifestFileName);
        string json = JsonConvert.SerializeObject(manifest, JsonSettings);
        File.WriteAllText(manifestPath, json);
    }

    private static string ToRelativeUnixPath(string rootDirectory, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        string relative = Path.IsPathRooted(path)
            ? Path.GetRelativePath(rootDirectory, Path.GetFullPath(path))
            : path;

        return NormalizeRelativePath(relative);
    }

    private static string NormalizeRelativePath(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    private static string ResolveSafePath(string rootDirectory, string relativePath)
    {
        string fullRoot = Path.GetFullPath(rootDirectory);
        string osRelative = relativePath.Replace('/', Path.DirectorySeparatorChar);
        string fullCandidate = Path.GetFullPath(Path.Combine(fullRoot, osRelative));

        string relativeCheck = Path.GetRelativePath(fullRoot, fullCandidate);
        if (Path.IsPathRooted(relativeCheck) ||
            relativeCheck.Equals("..", StringComparison.Ordinal) ||
            relativeCheck.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"检测到非法相对路径，已中止操作：{relativePath}");
        }

        return fullCandidate;
    }

    private static void ClearReadOnlyAttribute(string filePath)
    {
        FileAttributes attrs = File.GetAttributes(filePath);
        if ((attrs & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(filePath, attrs & ~FileAttributes.ReadOnly);
        }
    }
}
