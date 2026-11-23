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
        SpanCatalog? spanCatalog = null)
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
        IReadOnlyList<string> copiedDestinationFiles =
            GameFileWorkspace.CopyDirectoryForModification(gameRoot, outputRoot, parserOptions);

        var warnings = new ConcurrentBag<string>();
        var spanCatalogResolved = spanCatalog ?? SpanCatalog.LoadFromSidecar(dictionaryRoot, warnings.Add);
        var copiedBySource = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string destinationPath in copiedDestinationFiles)
        {
            string relativePath = Path.GetRelativePath(outputRoot, destinationPath);
            string sourcePath = ResolveWithinRoot(gameRoot, relativePath);
            copiedBySource[sourcePath] = destinationPath;
        }

        var modifiedFiles = new ConcurrentBag<string>();
        var operations = BuildOperations(gameRoot, dictionaryRoot, outputRoot, parserOptions, spanCatalogResolved, warnings);
        int spanReplaced = 0;
        int skippedByEncoding = 0;
        int skippedByNewLine = 0;
        int fallbackReplaced = 0;

        Parallel.ForEach(operations, operation =>
        {
            try
            {
                if (!copiedBySource.ContainsKey(operation.SourcePath) ||
                    !File.Exists(operation.DestinationPath))
                {
                    throw new IOException("完整游戏副本中缺少待处理文件。");
                }

                GameFileText file = GameFileText.Read(operation.DestinationPath);
                string content = file.Text;
                string translated = content;
                int fallbackCount = 0;
                List<SpanEdit>? acceptedEdits = null;

                IReadOnlyList<SpanSeed>? seeds = operation.SpanSeeds;
                if (seeds is not null &&
                    operation.SpanFileHash is { } expectedHash &&
                    !string.Equals(
                        VisibleTextExtractor.ComputeFileHash(content),
                        expectedHash,
                        StringComparison.Ordinal))
                {
                    warnings.Add(
                        $"按位置写回：{operation.RelativePath} - 源文件在提取之后被修改，定位信息已失效，本次改用原文匹配。");
                    seeds = null;
                }

                if (seeds is not null)
                {
                    string newLine = DetectNewLine(content);
                    var edits = new List<SpanEdit>(seeds.Count);
                    foreach (SpanSeed seed in seeds)
                    {
                        string replacement = NormalizeReplacement(seed.Translation, newLine);
                        if (IsScriptTarget(operation.SourcePath) && replacement.IndexOfAny(['\r', '\n']) >= 0)
                        {
                            Interlocked.Increment(ref skippedByNewLine);
                            warnings.Add(
                                $"按位置写回：{operation.RelativePath} - 译文含换行，脚本会变成两条语句，已跳过：{seed.Key}");
                            continue;
                        }

                        if (!IsLossless(file, replacement))
                        {
                            Interlocked.Increment(ref skippedByEncoding);
                            warnings.Add(
                                $"按位置写回：{operation.RelativePath} - 译文含「{file.Encoding.WebName}」无法表示的字符，已跳过：{seed.Key}");
                            continue;
                        }

                        edits.Add(new SpanEdit(seed.Key, seed.Span, seed.Original, replacement));
                    }

                    acceptedEdits = new List<SpanEdit>(edits.Count);
                    SpanApplyResult spanResult = SpanApplier.Apply(content, edits, acceptedEdits);
                    translated = spanResult.Text;
                    Interlocked.Add(ref spanReplaced, spanResult.AppliedCount);
                    foreach (string warning in spanResult.Warnings)
                    {
                        warnings.Add($"按位置写回：{operation.RelativePath} - {warning}");
                    }
                }
                else if (operation.IsResourceCsv)
                {
                    translated = TranslateResourceConfig(content, operation.ResourceReplacements!);
                }
                else if (operation.ReplacementPlan is not null)
                {
                    translated = RegexReplace(content, operation.ReplacementPlan, out fallbackCount);
                }

                if (fallbackCount > 0)
                {
                    Interlocked.Add(ref fallbackReplaced, fallbackCount);
                }

                if (acceptedEdits is { Count: > 0 })
                {
                    // Splice the replacement bytes into the original file so untouched text keeps
                    // its exact bytes (Shift-JIS duplicate mappings would otherwise be rewritten).
                    if (!file.TryWriteSpliced(operation.DestinationPath, acceptedEdits, out string? spliceWarning))
                    {
                        if (spliceWarning is not null)
                        {
                            warnings.Add($"按位置写回：{operation.RelativePath} - {spliceWarning}");
                        }

                        file.Text = translated;
                        file.Write(operation.DestinationPath);
                    }
                    else if (spliceWarning is not null)
                    {
                        warnings.Add($"按位置写回：{operation.RelativePath} - {spliceWarning}");
                    }

                    modifiedFiles.Add(operation.DestinationPath);
                }
                else if (!string.Equals(translated, content, StringComparison.Ordinal))
                {
                    if (!IsLossless(file, translated))
                    {
                        warnings.Add(
                            $"处理副本：{operation.RelativePath} - 译文含「{file.Encoding.WebName}」无法表示的字符，游戏中可能显示为「?」。");
                    }

                    file.Text = translated;
                    file.Write(operation.DestinationPath);
                    modifiedFiles.Add(operation.DestinationPath);
                }

            }
            catch (Exception ex)
            {
                warnings.Add($"处理副本失败：{operation.RelativePath} - {ex.Message}");
            }
        });

        var renamedFiles = RenameErdCopies(
            gameRoot,
            dictionaryRoot,
            outputRoot,
            copiedBySource,
            warnings);

        // The legacy autoReplace step runs after normal translation and ERD renaming.
        // Every original game file is already present in the complete workspace, so this
        // pass only edits its corresponding copy.
        if (parserOptions.EnableVariableAutoCorrection)
        {
            ApplyVariableAutoCorrection(
                gameRoot,
                dictionaryRoot,
                outputRoot,
                parserOptions,
                copiedBySource,
                modifiedFiles,
                warnings);
        }

        var renamedPathMap = renamedFiles.ToDictionary(
            item => item.PreviousPath,
            item => item.NewPath,
            StringComparer.OrdinalIgnoreCase);

        return new TranslationApplyResult(
            copiedBySource.Values
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            modifiedFiles
                .Select(path => renamedPathMap.TryGetValue(path, out string? renamedPath) ? renamedPath : path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            renamedFiles
                .OrderBy(item => item.NewPath, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            warnings
                .OrderBy(warning => warning, StringComparer.Ordinal)
                .ToArray())
        {
            SpanReplacedCount = spanReplaced,
            FallbackReplacedCount = fallbackReplaced,
            SkippedByEncodingCount = skippedByEncoding,
            SkippedByNewLineCount = skippedByNewLine
        };
    }

    private static List<FileApplyOperation> BuildOperations(
        string gameRoot,
        string dictionaryRoot,
        string outputRoot,
        EraParserOptions options,
        SpanCatalog spanCatalog,
        ConcurrentBag<string> warnings)
    {
        var operations = new List<FileApplyOperation>();
        var reservedDestinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var dictionaryFiles = Directory.EnumerateFiles(dictionaryRoot, "*.json", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.Ordinal);

        foreach (string dictionaryFile in dictionaryFiles)
        {
            string relativeDictionaryPath = Path.GetRelativePath(dictionaryRoot, dictionaryFile);
            string normalizedRelativePath = relativeDictionaryPath.Replace('\\', '/');

            // The ERD name dictionary is consumed by the rename pass, not applied to a
            // same-named game script.
            if (normalizedRelativePath.Equals("CSV/ERD字典.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // AI sidecars (grouping/locate files) share the output tree but are not dictionaries.
            if (normalizedRelativePath.StartsWith("AI上下文/", StringComparison.OrdinalIgnoreCase) ||
                normalizedRelativePath.EndsWith(".spans.json", StringComparison.OrdinalIgnoreCase) ||
                normalizedRelativePath.EndsWith(".groups.json", StringComparison.OrdinalIgnoreCase) ||
                normalizedRelativePath.Equals("extractor.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bool isXmlDictionary = normalizedRelativePath.EndsWith(".xml.json", StringComparison.OrdinalIgnoreCase);
            bool isResourceCsv = normalizedRelativePath.StartsWith("resources", StringComparison.OrdinalIgnoreCase);

            JArray jsonArray;
            try
            {
                jsonArray = JArray.Parse(File.ReadAllText(dictionaryFile));
            }
            catch (Exception ex)
            {
                warnings.Add($"字典 JSON 无法解析：{relativeDictionaryPath} - {ex.Message}");
                continue;
            }

            IReadOnlyDictionary<string, string>? resourceReplacements = null;
            RegexReplacePlan? replacementPlan = null;

            try
            {
                if (isResourceCsv)
                {
                    resourceReplacements = BuildResourceReplacements(jsonArray);
                }
                else
                {
                    replacementPlan = BuildRegexReplacePlan(jsonArray);
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"字典替换规则无效：{relativeDictionaryPath} - {ex.Message}");
                continue;
            }

            var targetFiles = ResolveTargetFiles(
                gameRoot,
                relativeDictionaryPath,
                isXmlDictionary,
                options.TargetFileExtensions);

            if (!isResourceCsv && targetFiles.Count > 1)
            {
                // A dictionary named ERB/foo.json can match foo.erb and foo.erh. When locators
                // exist for exactly one of them, rewriting the other would apply .erb offsets to
                // .erh text and fail on every entry, so keep only the exact target.
                var withLocators = targetFiles
                    .Where(path => spanCatalog.ContainsGamePath(
                        Path.GetRelativePath(gameRoot, path).Replace('\\', '/')))
                    .ToList();
                if (withLocators.Count > 0)
                {
                    targetFiles = withLocators;
                }
            }

            if (targetFiles.Count == 0)
            {
                if (!normalizedRelativePath.Contains("修正字典", StringComparison.OrdinalIgnoreCase))
                {
                    warnings.Add($"找不到字典对应的游戏文件：{relativeDictionaryPath}");
                }
                continue;
            }

            foreach (string sourcePath in targetFiles)
            {
                string relativeGamePath = Path.GetRelativePath(gameRoot, sourcePath);
                string destinationPath = ResolveWithinRoot(outputRoot, relativeGamePath);

                if (!reservedDestinations.Add(destinationPath))
                {
                    warnings.Add($"多个字典映射到同一游戏文件，已跳过重复项：{relativeGamePath}");
                    continue;
                }

                List<SpanSeed>? spanSeeds = null;
                int fallbackEntryCount = 0;
                string? spanFileHash = null;

                if (!isResourceCsv && spanCatalog.TryGetSpans(relativeGamePath, out IReadOnlyDictionary<string, TextSpan> spansByKey))
                {
                    // The file has an extraction sidecar: only exact-position edits are allowed,
                    // otherwise a global replace could hit the same text in unrelated lines.
                    spanSeeds = BuildSpanSeeds(jsonArray, spansByKey, relativeGamePath, warnings);
                    if (spanCatalog.TryGetFileHash(relativeGamePath, out string recordedHash))
                    {
                        spanFileHash = recordedHash;
                    }
                }
                else
                {
                    fallbackEntryCount = jsonArray.Children<JObject>().Count(entry => GetStage(entry) > 0);
                }

                operations.Add(new FileApplyOperation(
                    relativeGamePath,
                    sourcePath,
                    destinationPath,
                    isResourceCsv,
                    replacementPlan,
                    resourceReplacements,
                    spanSeeds,
                    fallbackEntryCount,
                    spanFileHash));
            }
        }

        return operations;
    }

    private static List<SpanSeed> BuildSpanSeeds(
        JArray jsonArray,
        IReadOnlyDictionary<string, TextSpan> spansByKey,
        string relativeGamePath,
        ConcurrentBag<string> warnings)
    {
        var seeds = new List<SpanSeed>();
        int unlocated = 0;

        foreach (JObject entry in jsonArray.Children<JObject>())
        {
            if (GetStage(entry) <= 0)
            {
                continue;
            }

            string? key = entry["key"]?.ToString();
            string translation = entry["translation"]?.ToString() ?? string.Empty;
            if (key is null || !spansByKey.TryGetValue(key, out TextSpan span))
            {
                unlocated++;
                continue;
            }

            if (translation.Length == 0)
            {
                continue;
            }

            seeds.Add(new SpanSeed(key, span, entry["original"]?.ToString() ?? string.Empty, translation));
        }

        if (unlocated > 0)
        {
            warnings.Add($"按位置写回：{relativeGamePath} 有 {unlocated} 条已翻译词条缺少定位信息，本次未写入");
        }

        return seeds;
    }

    private static string DetectNewLine(string content)
    {
        return content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
    }

    private static bool IsScriptTarget(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Equals(".erb", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".erh", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLossless(GameFileText file, string text)
    {
        // Always round-trip: even UTF-8 can silently turn an unpaired surrogate into U+FFFD,
        // and the player would see a replacement glyph instead of the translation.
        byte[] bytes = file.Encoding.GetBytes(text);
        return string.Equals(file.Encoding.GetString(bytes), text, StringComparison.Ordinal);
    }

    private static string NormalizeReplacement(string translation, string newLine)
    {
        string text = translation
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Replace("<Tab>", "\t");
        return newLine == "\n" ? text : text.Replace("\n", newLine);
    }

    private static List<string> ResolveTargetFiles(
        string gameRoot,
        string relativeDictionaryPath,
        bool isXmlDictionary,
        IReadOnlyList<string> targetExtensions)
    {
        var candidates = new List<string>();

        if (isXmlDictionary)
        {
            string relativeXmlPath = relativeDictionaryPath[..^".json".Length];
            string xmlPath = ResolveWithinRoot(gameRoot, relativeXmlPath);
            if (File.Exists(xmlPath))
            {
                candidates.Add(xmlPath);
            }
            return candidates;
        }

        string dictionaryBasePath = ResolveWithinRoot(gameRoot, relativeDictionaryPath);
        foreach (string extension in targetExtensions)
        {
            string candidate = Path.GetFullPath(Path.ChangeExtension(dictionaryBasePath, extension));
            if (IsSameOrInside(gameRoot, candidate) && File.Exists(candidate) &&
                !candidates.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(candidate);
            }
        }

        return candidates;
    }

    private static IReadOnlyDictionary<string, string> BuildResourceReplacements(JArray jsonArray)
    {
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JObject item in jsonArray.Children<JObject>())
        {
            if (GetStage(item) <= 0)
            {
                continue;
            }

            string original = item["original"]?.ToString() ?? string.Empty;
            string translation = item["translation"]?.ToString() ?? string.Empty;

            // The legacy implementation uses ToDictionary and treats duplicate original
            // resource names as an invalid dictionary. Preserve that failure behavior.
            if (!replacements.TryAdd(original, translation))
            {
                throw new InvalidOperationException($"资源字典包含重复原文：{original}");
            }
        }

        return replacements;
    }

    private static RegexReplacePlan? BuildRegexReplacePlan(JArray jsonArray)
    {
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        var orderedKeys = new List<string>();

        var entries = jsonArray.Children<JObject>()
            .Where(item => GetStage(item) > 0)
            .OrderByDescending(item => item["original"]?.ToString().Length ?? 0);

        foreach (JObject entry in entries)
        {
            string key = NormalizeDictionaryText(entry["original"]?.ToString());
            string value = NormalizeDictionaryText(entry["translation"]?.ToString());

            if (string.IsNullOrEmpty(key) || replacements.ContainsKey(key))
            {
                continue;
            }

            replacements.Add(key, value);
            orderedKeys.Add(key);
        }

        if (orderedKeys.Count == 0)
        {
            return null;
        }

        // Longest originals are placed first, matching the legacy replacement order.
        string pattern = string.Join("|", orderedKeys.Select(Regex.Escape));
        return new RegexReplacePlan(new Regex(pattern), replacements);
    }

    private static string RegexReplace(string gameContent, RegexReplacePlan? plan, out int replacedCount)
    {
        replacedCount = 0;
        if (plan is null)
        {
            return gameContent;
        }

        string newLine = DetectNewLine(gameContent);
        int count = 0;
        string result = plan.Pattern.Replace(gameContent, match =>
        {
            if (!plan.Replacements.TryGetValue(match.Value, out string? value))
            {
                return match.Value;
            }

            count++;
            return NormalizeReplacement(value, newLine);
        });
        replacedCount = count;
        return result;
    }

    private static string TranslateResourceConfig(
        string gameContent,
        IReadOnlyDictionary<string, string> replacements)
    {
        // This deliberately follows the legacy RES-config line splitting and matching
        // behavior so existing dictionaries keep their expected results. Line endings are
        // now preserved instead of being rewritten to the host platform's newline.
        string newLine = DetectNewLine(gameContent);
        string[] lines = newLine == "\r\n"
            ? gameContent.Split("\r\n", StringSplitOptions.None)
            : gameContent.Split('\n');

        var replacedLines = new List<string>(lines.Length);
        foreach (string line in lines)
        {
            string translatedLine = line;
            int commaIndex = line.IndexOf(',');
            if (!line.StartsWith(";", StringComparison.Ordinal) && commaIndex > -1)
            {
                string key = line[..commaIndex].Trim();
                if (replacements.TryGetValue(key, out string? translation))
                {
                    translatedLine = translation + line[commaIndex..];
                }
            }
            replacedLines.Add(translatedLine);
        }

        return string.Join(newLine, replacedLines);
    }

    private static void ApplyVariableAutoCorrection(
        string gameRoot,
        string dictionaryRoot,
        string outputRoot,
        EraParserOptions options,
        ConcurrentDictionary<string, string> copiedBySource,
        ConcurrentBag<string> modifiedFiles,
        ConcurrentBag<string> warnings)
    {
        string csvDictionaryRoot = Path.Combine(dictionaryRoot, "CSV");
        if (!Directory.Exists(csvDictionaryRoot))
        {
            return;
        }

        string[] availableDictionaries = Directory.EnumerateFiles(
                csvDictionaryRoot,
                "*",
                SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToArray();

        var selectedDictionaries = new List<string>();
        foreach (string referenceName in options.AutoReplaceReferenceFiles)
        {
            string? match = availableDictionaries.FirstOrDefault(path =>
                Path.GetFileNameWithoutExtension(path).Equals(
                    referenceName,
                    StringComparison.OrdinalIgnoreCase));

            if (match is not null)
            {
                selectedDictionaries.Add(match);
            }
        }

        if (selectedDictionaries.Count == 0)
        {
            return;
        }

        var combinedEntries = new JArray();
        foreach (string dictionaryPath in selectedDictionaries)
        {
            try
            {
                var dictionary = JArray.Parse(File.ReadAllText(dictionaryPath));
                foreach (JToken entry in dictionary)
                {
                    combinedEntries.Add(entry.DeepClone());
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"自动变量修正参考字典无法解析：{Path.GetFileName(dictionaryPath)} - {ex.Message}");
            }
        }

        var replacementPlan = BuildRegexReplacePlan(combinedEntries);
        if (replacementPlan is null)
        {
            return;
        }

        string[] scriptFiles = Directory.EnumerateFiles(gameRoot, "*", SearchOption.AllDirectories)
            .Where(path =>
            {
                string extension = Path.GetExtension(path);
                return extension.Equals(".erb", StringComparison.OrdinalIgnoreCase) ||
                       extension.Equals(".erh", StringComparison.OrdinalIgnoreCase) ||
                       extension.Equals(".erd", StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Parallel.ForEach(scriptFiles, sourcePath =>
        {
            string relativePath = Path.GetRelativePath(gameRoot, sourcePath);
            string destinationPath;

            if (copiedBySource.TryGetValue(sourcePath, out string? existingCopy))
            {
                destinationPath = existingCopy ??
                    throw new InvalidOperationException("工作副本映射无效。");
            }
            else
            {
                destinationPath = ResolveWithinRoot(outputRoot, relativePath);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                    File.Copy(sourcePath, destinationPath, overwrite: false);
                    copiedBySource[sourcePath] = destinationPath;
                }
                catch (Exception ex)
                {
                    warnings.Add($"复制自动修正副本失败：{relativePath} - {ex.Message}");
                    return;
                }
            }

            try
            {
                GameFileText file = GameFileText.Read(destinationPath);
                string corrected = RegexReplace(file.Text, replacementPlan, out _);
                if (!string.Equals(corrected, file.Text, StringComparison.Ordinal))
                {
                    file.Text = corrected;
                    file.Write(destinationPath);
                    modifiedFiles.Add(destinationPath);
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"自动变量修正副本失败：{relativePath} - {ex.Message}");
            }
        });
    }

    private static void ValidateAutoReplaceReferenceFiles(IReadOnlyList<string> referenceFiles)
    {
        ArgumentNullException.ThrowIfNull(referenceFiles);
        if (referenceFiles.Any(name =>
                string.IsNullOrWhiteSpace(name) ||
                Path.IsPathRooted(name) ||
                !Path.GetFileName(name).Equals(name, StringComparison.Ordinal) ||
                name.Contains('/') ||
                name.IndexOf((char)92) >= 0 ||
                name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                "Auto-replace reference entries must be simple file names without a path or .json extension.",
                nameof(referenceFiles));
        }
    }

    private static List<RenamedGameFile> RenameErdCopies(
        string gameRoot,
        string dictionaryRoot,
        string outputRoot,
        ConcurrentDictionary<string, string> copiedBySource,
        ConcurrentBag<string> warnings)
    {
        var renamed = new List<RenamedGameFile>();
        string erdDictionaryPath = Path.Combine(dictionaryRoot, "CSV", "ERD字典.json");
        if (!File.Exists(erdDictionaryPath))
        {
            return renamed;
        }

        JArray entries;
        try
        {
            entries = JArray.Parse(File.ReadAllText(erdDictionaryPath));
        }
        catch (Exception ex)
        {
            warnings.Add($"ERD 文件名字典无法解析：{ex.Message}");
            return renamed;
        }

        var erdSourceGroups = Directory.EnumerateFiles(gameRoot, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path).Equals(".erd", StringComparison.OrdinalIgnoreCase))
            .GroupBy(path => Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        foreach (JObject entry in entries.Children<JObject>())
        {
            string original = entry["original"]?.ToString() ?? string.Empty;
            string translation = entry["translation"]?.ToString() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(original) ||
                string.IsNullOrWhiteSpace(translation) ||
                original.Equals(translation, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!IsSafeErdFileName(translation))
            {
                warnings.Add($"ERD 译名不是安全的文件名，已跳过：{original} -> {translation}");
                continue;
            }

            if (!erdSourceGroups.TryGetValue(original, out string[]? sourceMatches))
            {
                warnings.Add($"找不到待重命名的 ERD 文件：{original}.erd");
                continue;
            }

            if (sourceMatches.Length != 1)
            {
                warnings.Add($"存在多个同名 ERD 文件，无法确定重命名目标：{original}.erd");
                continue;
            }

            string sourcePath = sourceMatches[0];
            if (!copiedBySource.TryGetValue(sourcePath, out string? copiedPath))
            {
                string relativePath = Path.GetRelativePath(gameRoot, sourcePath);
                copiedPath = ResolveWithinRoot(outputRoot, relativePath);

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(copiedPath)!);
                    File.Copy(sourcePath, copiedPath, overwrite: false);
                    copiedBySource[sourcePath] = copiedPath;
                }
                catch (Exception ex)
                {
                    warnings.Add($"复制 ERD 文件失败：{relativePath} - {ex.Message}");
                    continue;
                }
            }

            string destinationPath = Path.Combine(
                Path.GetDirectoryName(copiedPath)!,
                translation + ".erd");

            if (File.Exists(destinationPath))
            {
                warnings.Add($"ERD 目标文件已存在，未覆盖：{destinationPath}");
                continue;
            }

            try
            {
                File.Move(copiedPath, destinationPath);
                copiedBySource[sourcePath] = destinationPath;
                renamed.Add(new RenamedGameFile(copiedPath, destinationPath));
            }
            catch (Exception ex)
            {
                warnings.Add($"重命名 ERD 副本失败：{original}.erd - {ex.Message}");
            }
        }

        return renamed;
    }

    private static bool IsSafeErdFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            Path.IsPathRooted(fileName) ||
            !Path.GetFileName(fileName).Equals(fileName, StringComparison.Ordinal) ||
            fileName.EndsWith(".", StringComparison.Ordinal) ||
            fileName.EndsWith(" ", StringComparison.Ordinal))
        {
            return false;
        }

        const string invalidWindowsCharacters = "<>:\"/\\|?*";
        return !fileName.Any(character =>
            char.IsControl(character) || invalidWindowsCharacters.Contains(character));
    }

    private static int GetStage(JObject entry)
    {
        return int.TryParse(
            entry["stage"]?.ToString(),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int stage)
            ? stage
            : 0;
    }

    private static string NormalizeDictionaryText(string? content)
    {
        return (content ?? string.Empty)
            .Replace("\r\n", "\n")
            .Replace("\r", "\n")
            .Replace("\\n", "\n")
            .Replace("\n\n", "\n")
            .Replace("<Tab>", "\t");
    }

    private static string ResolveWithinRoot(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException($"Absolute path is not allowed: {relativePath}");
        }

        string fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!IsSameOrInside(root, fullPath) || Path.GetFullPath(root).Equals(fullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Path escapes its root directory: {relativePath}");
        }

        return fullPath;
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

    private static void ValidateExtensions(IReadOnlyList<string> extensions)
    {
        ArgumentNullException.ThrowIfNull(extensions);
        if (extensions.Count == 0 || extensions.Any(extension =>
                string.IsNullOrWhiteSpace(extension) ||
                !extension.StartsWith(".", StringComparison.Ordinal) ||
                extension.Contains(Path.DirectorySeparatorChar) ||
                extension.Contains(Path.AltDirectorySeparatorChar)))
        {
            throw new ArgumentException("Target file extensions must be file extensions such as .csv or .erb.", nameof(extensions));
        }
    }

    private sealed record FileApplyOperation(
        string RelativePath,
        string SourcePath,
        string DestinationPath,
        bool IsResourceCsv,
        RegexReplacePlan? ReplacementPlan,
        IReadOnlyDictionary<string, string>? ResourceReplacements,
        IReadOnlyList<SpanSeed>? SpanSeeds,
        int FallbackEntryCount,
        string? SpanFileHash);

    private sealed record SpanSeed(string Key, TextSpan Span, string Original, string Translation);

    private sealed record RegexReplacePlan(
        Regex Pattern,
        IReadOnlyDictionary<string, string> Replacements);
}