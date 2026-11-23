using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using EraTrans.Core.VisibleText;
using EraTrans.Core.WriteBack;
using Newtonsoft.Json.Linq;

namespace EraTrans.Core;

public sealed record RenamedGameFile(string PreviousPath, string NewPath);

public sealed record TranslationApplyResult(
    IReadOnlyList<string> CopiedFiles,
    IReadOnlyList<string> ModifiedFiles,
    IReadOnlyList<RenamedGameFile> RenamedFiles,
    IReadOnlyList<string> Warnings)
{
    /// <summary>Entries written through exact position sidecars (no global search).</summary>
    public int SpanReplacedCount { get; init; }

    /// <summary>Entries written by whole-file source matching (legacy path).</summary>
    public int FallbackReplacedCount { get; init; }

    /// <summary>Translations skipped because the file encoding cannot represent them.</summary>
    public int SkippedByEncodingCount { get; init; }

    /// <summary>Script translations skipped because they contained a line break.</summary>
    public int SkippedByNewLineCount { get; init; }
}

/// <summary>
/// Applies translated dictionaries only to copies of game files in a separate workspace.
/// The game directory and dictionary directory are treated as read-only inputs.
/// </summary>
public sealed class TranslationApplier
{
    public TranslationApplyResult Apply(
        string gameDirectory,
        string dictionaryDirectory,
        string outputDirectory,
        EraParserOptions? options = null,
        SpanCatalog? spanCatalog = null,
        bool ignoreEncodingIssueFiles = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(dictionaryDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        string gameRoot = Path.GetFullPath(gameDirectory);
        string dictionaryRoot = Path.GetFullPath(dictionaryDirectory);
        string outputRoot = Path.GetFullPath(outputDirectory);

        if (!Directory.Exists(gameRoot))
        {
            throw new DirectoryNotFoundException($"Game directory does not exist: {gameRoot}");
        }

        if (!Directory.Exists(dictionaryRoot))
        {
            throw new DirectoryNotFoundException($"Dictionary directory does not exist: {dictionaryRoot}");
        }

        EnsureDirectoriesAreDisjoint(gameRoot, dictionaryRoot, "Game and dictionary directories must not overlap.");
        EnsureDirectoriesAreDisjoint(gameRoot, outputRoot, "Working-copy output must be outside the game directory.");
        EnsureDirectoriesAreDisjoint(dictionaryRoot, outputRoot, "Working-copy output must be outside the dictionary directory.");

        var parserOptions = options ?? new EraParserOptions();
        ArgumentNullException.ThrowIfNull(parserOptions.FileEncoding);
        ValidateExtensions(parserOptions.TargetFileExtensions);
        ValidateAutoReplaceReferenceFiles(parserOptions.AutoReplaceReferenceFiles);

        // Create a complete game workspace before applying any edits. The workspace
        // creator refuses an existing destination so repeated runs cannot overwrite user
        // changes or apply translations on top of an earlier translated copy.
        var skippedEncodingIssues = new List<GameFileEncodingIssue>();
        IReadOnlyList<string> copiedDestinationFiles =
            GameFileWorkspace.CopyDirectoryForModification(
                gameRoot,
                outputRoot,
                parserOptions,
                ignoreEncodingIssueFiles,
                skippedEncodingIssues);

        var warnings = new ConcurrentBag<string>();
        var ignoredSourcePaths = new HashSet<string>(
            skippedEncodingIssues.Select(issue => Path.GetFullPath(issue.FullPath)),
            StringComparer.OrdinalIgnoreCase);
        foreach (GameFileEncodingIssue issue in skippedEncodingIssues)
        {
            warnings.Add(
                $"已忽略编码异常文件（已原样复制到游戏副本，未转码且未应用译文）：{issue.RelativePath}（{issue.KindLabel}）");
        }
        var spanCatalogResolved = spanCatalog ?? SpanCatalog.LoadFromSidecar(dictionaryRoot, warnings.Add);
        var copiedBySource = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string destinationPath in copiedDestinationFiles)
        {
            string relativePath = Path.GetRelativePath(outputRoot, destinationPath).Replace('\\', '/');
            copiedBySource[relativePath] = destinationPath;
        }

        int spanReplacedCount = 0;
        int fallbackReplacedCount = 0;
        int skippedByEncodingCount = 0;
        int skippedByNewLineCount = 0;
        var modifiedFiles = new ConcurrentBag<string>();
        var renamedFiles = new ConcurrentBag<RenamedGameFile>();

        var operations = spanCatalogResolved.Entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.RelativePath))
            .Select(entry => new ApplyOperation
            {
                SourcePath = Path.GetFullPath(Path.Combine(gameRoot, entry.RelativePath)),
                DestinationPath = copiedBySource.GetValueOrDefault(entry.RelativePath),
                TranslatedText = entry.TranslatedText,
                SourceSpan = entry.SourceSpan,
                SpanSidecarPath = entry.SidecarPath,
                DictionarySourceLine = entry.DictionarySourceLine,
            })
            .Where(op => op.DestinationPath is not null)
            .ToArray();

        Parallel.ForEach(operations, operation =>
        {
            if (ignoredSourcePaths.Contains(Path.GetFullPath(operation.SourcePath)))
            {
                return;
            }

            try
            {
                if (!copiedBySource.ContainsKey(Path.GetRelativePath(gameRoot, operation.SourcePath).Replace('\\', '/')) ||
                    operation.DestinationPath is null)
                {
                    return;
                }

                bool applied = ApplyTranslationToFile(
                    operation,
                    parserOptions.FileEncoding,
                    warnings,
                    ref spanReplacedCount,
                    ref fallbackReplacedCount,
                    ref skippedByEncodingCount,
                    ref skippedByNewLineCount);

                if (applied)
                {
                    modifiedFiles.Add(operation.DestinationPath);
                }
            }
            catch (Exception ex)
            {
                warnings.Add(
                    $"应用译文时发生错误（已跳过此文件）：{Path.GetRelativePath(gameRoot, operation.SourcePath)} - {ex.Message}");
            }
        });

        // Handle ERD renames (from sidecar)
        var erdRenameEntries = spanCatalogResolved.Entries
            .Where(entry => entry.Kind == SpanCatalogEntryKind.ErdRename)
            .ToArray();

        foreach (var entry in erdRenameEntries)
        {
            if (string.IsNullOrWhiteSpace(entry.RelativePath))
                continue;

            string sourcePath = Path.GetFullPath(Path.Combine(gameRoot, entry.RelativePath));
            string destinationPath = copiedBySource.GetValueOrDefault(entry.RelativePath);
            if (destinationPath is null || !File.Exists(destinationPath))
                continue;

            try
            {
                string newName = entry.TranslatedText.Trim();
                if (string.IsNullOrWhiteSpace(newName))
                    continue;

                string newPath = Path.Combine(Path.GetDirectoryName(destinationPath)!, newName);
                if (File.Exists(newPath))
                {
                    warnings.Add($"ERD 重命名目标已存在，跳过：{entry.RelativePath} -> {newName}");
                    continue;
                }

                File.Move(destinationPath, newPath);
                copiedBySource[entry.RelativePath] = newPath;
                renamedFiles.Add(new RenamedGameFile(entry.RelativePath, newName));
            }
            catch (Exception ex)
            {
                warnings.Add($"ERD 重命名失败：{entry.RelativePath} - {ex.Message}");
            }
        }

        // Apply CSV/RES/XML dictionaries (legacy path)
        ApplyLegacyDictionaries(
            gameRoot,
            dictionaryRoot,
            outputRoot,
            parserOptions,
            copiedBySource,
            modifiedFiles,
            warnings,
            ignoredSourcePaths);

        var renamedPathMap = renamedFiles.ToDictionary(
            x => x.PreviousPath,
            x => x.NewPath,
            StringComparer.OrdinalIgnoreCase);

        return new TranslationApplyResult(
            copiedDestinationFiles,
            modifiedFiles.Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            renamedFiles.Distinct()
                .OrderBy(r => r.PreviousPath, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            warnings.Distinct()
                .OrderBy(w => w, StringComparer.OrdinalIgnoreCase)
                .ToArray())
        {
            SpanReplacedCount = spanReplacedCount,
            FallbackReplacedCount = fallbackReplacedCount,
            SkippedByEncodingCount = skippedByEncodingCount,
            SkippedByNewLineCount = skippedByNewLineCount
        };
    }

    private sealed class ApplyOperation
    {
        public string SourcePath;
        public string DestinationPath;
        public string TranslatedText;
        public (int Start, int Length)? SourceSpan;
        public string SpanSidecarPath;
        public int DictionarySourceLine;
    }

    private static bool ApplyTranslationToFile(
        ApplyOperation operation,
        Encoding fileEncoding,
        ConcurrentBag<string> warnings,
        ref int spanReplacedCount,
        ref int fallbackReplacedCount,
        ref int skippedByEncodingCount,
        ref int skippedByNewLineCount)
    {
        if (operation.DestinationPath is null || !File.Exists(operation.DestinationPath))
            return false;

        // If we have exact span info, write at that position.
        if (operation.SourceSpan.HasValue)
        {
            var (start, length) = operation.SourceSpan.Value;
            if (start < 0 || length <= 0)
                return false;

            byte[] fileBytes = File.ReadAllBytes(operation.DestinationPath);
            if (start + length > fileBytes.Length)
                return false;

            byte[] translatedBytes = fileEncoding.GetBytes(operation.TranslatedText);
            if (translatedBytes.Length > length)
            {
                if (!CanExpand(fileEncoding, fileBytes, start, length, translatedBytes.Length))
                {
                    skippedByEncodingCount++;
                    warnings.Add(
                        $"译文过长无法写入（编码 {fileEncoding.WebName} 无法在原位置扩展）：{Path.GetFileName(operation.DestinationPath)} 第 {operation.DictionarySourceLine} 行");
                    return false;
                }
            }

            // For UTF-8 with BOM, keep the BOM at the start.
            bool hasBom = fileBytes.Length >= 3 &&
                fileBytes[0] == 0xEF && fileBytes[1] == 0xBB && fileBytes[2] == 0xBF;
            int bomLength = hasBom ? 3 : 0;
            int adjustedStart = start + bomLength;
            int adjustedLength = length;

            // Ensure we don't overwrite the BOM.
            if (adjustedStart < bomLength)
            {
                adjustedStart = bomLength;
            }

            if (translatedBytes.Length > adjustedLength)
            {
                // Shift remaining bytes to make room.
                byte[] newBytes = new byte[fileBytes.Length + translatedBytes.Length - adjustedLength];
                Array.Copy(fileBytes, 0, newBytes, 0, adjustedStart);
                Array.Copy(translatedBytes, 0, newBytes, adjustedStart, translatedBytes.Length);
                Array.Copy(fileBytes, adjustedStart + adjustedLength, newBytes, adjustedStart + translatedBytes.Length, fileBytes.Length - (adjustedStart + adjustedLength));
                fileBytes = newBytes;
            }
            else
            {
                Array.Copy(translatedBytes, 0, fileBytes, adjustedStart, translatedBytes.Length);
                // Zero out any leftover bytes if translation is shorter.
                if (translatedBytes.Length < adjustedLength)
                {
                    Array.Clear(fileBytes, adjustedStart + translatedBytes.Length, adjustedLength - translatedBytes.Length);
                }
            }

            // Preserve BOM.
            if (hasBom)
            {
                fileBytes[0] = 0xEF;
                fileBytes[1] = 0xBB;
                fileBytes[2] = 0xBF;
            }

            File.WriteAllBytes(operation.DestinationPath, fileBytes);
            spanReplacedCount++;
            return true;
        }

        // Fallback: whole-file source match (legacy behavior for backward compatibility).
        string sourceText = GameFileText.Read(operation.SourcePath, fileEncoding).Text;
        if (!sourceText.Contains(operation.TranslatedText, StringComparison.Ordinal))
        {
            // The translated text doesn't exist in the source - can't apply via fallback.
            return false;
        }

        string destinationText = GameFileText.Read(operation.DestinationPath, fileEncoding).Text;
        int index = destinationText.IndexOf(operation.TranslatedText, StringComparison.Ordinal);
        if (index >= 0)
        {
            // Already applied.
            return false;
        }

        string originalText = sourceText;
        if (!destinationText.Contains(originalText, StringComparison.Ordinal))
        {
            // The original text was modified - can't apply via fallback.
            return false;
        }

        string newText = destinationText.Replace(originalText, operation.TranslatedText, StringComparison.Ordinal);
        if (newText == destinationText)
        {
            return false;
        }

        if (newText.Contains('\n') && !originalText.Contains('\n'))
        {
            skippedByNewLineCount++;
            warnings.Add(
                $"译文包含换行符但原文不含，已跳过（需精确位置替换）：{Path.GetFileName(operation.DestinationPath)} 第 {operation.DictionarySourceLine} 行");
            return false;
        }

        byte[] newBytes = fileEncoding.GetBytes(newText);
        byte[] originalBytes = File.ReadAllBytes(operation.DestinationPath);
        if (!CanExpand(fileEncoding, originalBytes, 0, originalBytes.Length, newBytes.Length))
        {
            skippedByEncodingCount++;
            warnings.Add(
                $"译文过长无法写入（编码 {fileEncoding.WebName} 无法在原位置扩展）：{Path.GetFileName(operation.DestinationPath)} 第 {operation.DictionarySourceLine} 行");
            return false;
        }

        File.WriteAllBytes(operation.DestinationPath, newBytes);
        fallbackReplacedCount++;
        return true;
    }

    private static bool CanExpand(Encoding encoding, byte[] currentBytes, int start, int length, int newLength)
    {
        if (encoding is UTF8Encoding)
            return true;

        // For legacy encodings (CP932, GBK, etc.), we can't safely expand.
        // The caller should use exact span replacement instead.
        return newLength <= length;
    }

    private static void ApplyLegacyDictionaries(
        string gameRoot,
        string dictionaryRoot,
        string outputRoot,
        EraParserOptions options,
        ConcurrentDictionary<string, string> copiedBySource,
        ConcurrentBag<string> modifiedFiles,
        ConcurrentBag<string> warnings,
        IReadOnlySet<string> ignoredSourcePaths)
    {
        string csvDictionaryRoot = Path.Combine(dictionaryRoot, "CSV");
        if (!Directory.Exists(csvDictionaryRoot))
            return;

        var dictionaryFiles = Directory.EnumerateFiles(csvDictionaryRoot, "*.json", SearchOption.TopDirectoryOnly).ToArray();
        if (dictionaryFiles.Length == 0)
            return;

        var scriptFiles = copiedBySource.Keys
            .Where(key => key.EndsWith(".erb", StringComparison.OrdinalIgnoreCase) ||
                          key.EndsWith(".erh", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Parallel.ForEach(scriptFiles, sourcePath =>
        {
            if (ignoredSourcePaths.Contains(Path.GetFullPath(Path.Combine(gameRoot, sourcePath))))
            {
                return;
            }

            string relativePath = Path.GetRelativePath(gameRoot, sourcePath);
            string destinationPath;
            if (!copiedBySource.TryGetValue(sourcePath, out destinationPath!) ||
                !File.Exists(destinationPath))
            {
                return;
            }

            string dictionaryPath = Path.Combine(csvDictionaryRoot, relativePath + ".json");
            if (!File.Exists(dictionaryPath))
            {
                dictionaryPath = Path.Combine(csvDictionaryRoot, Path.GetFileName(relativePath) + ".json");
                if (!File.Exists(dictionaryPath))
                    return;
            }

            try
            {
                string sourceText = GameFileText.Read(Path.Combine(gameRoot, relativePath), options.FileEncoding).Text;
                string destinationText = GameFileText.Read(destinationPath, options.FileEncoding).Text;

                var dictionary = JArray.Parse(File.ReadAllText(dictionaryPath, new UTF8Encoding(false, true)));
                bool fileModified = false;

                foreach (JObject entry in dictionary.Children<JObject>())
                {
                    string key = entry["key"]?.ToString() ?? string.Empty;
                    string original = entry["original"]?.ToString() ?? string.Empty;
                    string translated = entry["translated"]?.ToString() ?? string.Empty;

                    if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(original) || string.IsNullOrWhiteSpace(translated))
                        continue;

                    if (!sourceText.Contains(original, StringComparison.Ordinal))
                        continue;

                    if (!destinationText.Contains(original, StringComparison.Ordinal))
                        continue;

                    if (destinationText.Contains(translated, StringComparison.Ordinal))
                        continue;

                    string newText = destinationText.Replace(original, translated, StringComparison.Ordinal);
                    if (newText == destinationText)
                        continue;

                    if (newText.Contains('\n') && !original.Contains('\n'))
                    {
                        warnings.Add(
                            $"译文包含换行符但原文不含，已跳过：{Path.GetFileName(destinationPath)} Key={key}");
                        continue;
                    }

                    byte[] newBytes = options.FileEncoding.GetBytes(newText);
                    byte[] originalBytes = File.ReadAllBytes(destinationPath);
                    if (!CanExpand(options.FileEncoding, originalBytes, 0, originalBytes.Length, newBytes.Length))
                    {
                        warnings.Add(
                            $"译文过长无法写入（编码 {options.FileEncoding.WebName} 无法在原位置扩展）：{Path.GetFileName(destinationPath)} Key={key}");
                        continue;
                    }

                    File.WriteAllBytes(destinationPath, newBytes);
                    destinationText = newText;
                    fileModified = true;
                }

                if (fileModified)
                {
                    modifiedFiles.Add(destinationPath);
                }
            }
            catch (Exception ex)
            {
                warnings.Add(
                    $"应用 CSV/RES/XML 字典时发生错误（已跳过此文件）：{sourcePath} - {ex.Message}");
            }
        });
    }

    private static void EnsureDirectoriesAreDisjoint(string first, string second, string message)
    {
        if (IsSameOrInside(first, second) || IsSameOrInside(second, first))
        {
            throw new InvalidOperationException(message);
        }
    }

    private static bool IsSameOrInside(string root, string candidate)
    {
        string relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(candidate));
        return relative.Equals(".", StringComparison.Ordinal) ||
               (!Path.IsPathRooted(relative) &&
                !relative.Equals("..", StringComparison.Ordinal) &&
                !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    private static void ValidateExtensions(IReadOnlyList<string>? extensions)
    {
        if (extensions is null)
            return;

        foreach (string ext in extensions)
        {
            if (string.IsNullOrWhiteSpace(ext) || !ext.StartsWith(".", StringComparison.Ordinal))
                throw new ArgumentException($"无效的目标文件扩展名：{ext}", nameof(extensions));
        }
    }

    private static void ValidateAutoReplaceReferenceFiles(IReadOnlyList<string>? referenceFiles)
    {
        if (referenceFiles is null)
            return;

        foreach (string file in referenceFiles)
        {
            if (string.IsNullOrWhiteSpace(file))
                throw new ArgumentException("自动替换参考文件路径不能为空。");
        }
    }
}