using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EraTrans.Core;

public sealed record DictionaryExtractionResult(
    IReadOnlyList<string> GeneratedFiles,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string>? UpdatedContextFiles = null,
    IReadOnlyList<string>? UpdatedGroupingFiles = null);

/// <summary>
/// Runs the legacy dictionary parsers against a read-only game directory.
/// Every generated JSON file is written under the separate output directory.
/// </summary>
public sealed class DictionaryExtractor
{
    public DictionaryExtractionResult Extract(
        string gameDirectory,
        string outputDirectory,
        string? referenceGameDirectory = null,
        EraParserOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        string gameRoot = Path.GetFullPath(gameDirectory);
        string outputRoot = Path.GetFullPath(outputDirectory);
        if (!Directory.Exists(gameRoot))
        {
            throw new DirectoryNotFoundException($"Game directory does not exist: {gameRoot}");
        }

        EnsureOutputIsOutsideDirectory(gameRoot, outputRoot);

        string? referenceRoot = null;
        if (!string.IsNullOrWhiteSpace(referenceGameDirectory))
        {
            referenceRoot = Path.GetFullPath(referenceGameDirectory);
            if (!Directory.Exists(referenceRoot))
            {
                throw new DirectoryNotFoundException($"Reference game directory does not exist: {referenceRoot}");
            }
            EnsureOutputIsOutsideDirectory(referenceRoot, outputRoot);
        }

        Directory.CreateDirectory(outputRoot);
        var warnings = new ConcurrentBag<string>();
        var generatedFiles = new ConcurrentBag<string>();
        var updatedContextFiles = new ConcurrentBag<string>();
        var updatedGroupingFiles = new ConcurrentBag<string>();

        lock (LegacyParserRuntime.SyncRoot)
        {
            LegacyParserRuntime.Initialize(options ?? new EraParserOptions());

            var scannedFiles = GameDirectoryScanner.Scan(gameRoot);
            if (!Directory.Exists(Path.Combine(gameRoot, "XML")))
            {
                scannedFiles = scannedFiles.Where(file => file.Kind != GameFileKind.Xml).ToArray();
            }

            var erdFiles = scannedFiles
                .Where(file => file.Kind == GameFileKind.Erd)
                .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(file => file.RelativePath, StringComparer.Ordinal)
                .ToArray();

            if (erdFiles.Length > 0)
            {
                string erdDictionaryPath = Path.Combine(outputRoot, "CSV", "ERD字典.json");
                if (File.Exists(erdDictionaryPath))
                {
                    warnings.Add($"字典文件已存在，跳过以保护已有译文：{Path.GetRelativePath(outputRoot, erdDictionaryPath)}");
                }
                else
                {
                    PZJson.WriteErdFileNameDictionary(erdDictionaryPath, erdFiles);
                    generatedFiles.Add(erdDictionaryPath);
                }
            }

            GameFileKind[] legacyProcessingOrder =
            [
                GameFileKind.Csv,
                GameFileKind.Erb,
                GameFileKind.Erd,
                GameFileKind.ResourceCsv,
                GameFileKind.Xml
            ];

            foreach (var kind in legacyProcessingOrder)
            {
                Parallel.ForEach(scannedFiles
                    .Where(candidate => candidate.Kind == kind)
                    .OrderBy(candidate => candidate.RelativePath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(candidate => candidate.RelativePath, StringComparer.Ordinal),
                    file =>
                    {
                    string targetFile = ResolveOutputPath(outputRoot, file.RelativePath);
                    string dictionaryOutputPath = kind == GameFileKind.Xml
                        ? targetFile + ".json"
                        : Path.ChangeExtension(targetFile, ".json");

                    bool dictionaryAlreadyExists = File.Exists(dictionaryOutputPath);
                    if (dictionaryAlreadyExists)
                    {
                        warnings.Add(
                            $"字典文件已存在，保留原有词条数据，仅尝试补充缺失上下文：{Path.GetRelativePath(outputRoot, dictionaryOutputPath)}");
                    }

                    string parserTargetFile = targetFile;
                    string? contextRefreshDirectory = null;
                    if (dictionaryAlreadyExists)
                    {
                        // Re-parse into an isolated temporary directory. Only missing context
                        // fields will be merged back; existing translations and custom JSON
                        // properties are never replaced.
                        contextRefreshDirectory = Path.Combine(
                            Path.GetTempPath(),
                            $"EraTransContext-{Guid.NewGuid():N}");
                        parserTargetFile = Path.GetFullPath(
                            Path.Combine(contextRefreshDirectory, file.RelativePath));
                    }

                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(parserTargetFile)!);
                        string? referenceFile = referenceRoot is null
                            ? null
                            : ResolveOutputPath(referenceRoot, file.RelativePath);

                        bool hasReference = referenceFile is not null && File.Exists(referenceFile);
                        if (referenceRoot is not null && !hasReference)
                        {
                            warnings.Add($"未找到参考文件：{referenceFile}");
                        }

                        string candidateDictionaryPath;
                        switch (kind)
                        {
                            case GameFileKind.Csv:
                            case GameFileKind.Erd:
                            {
                                var parser = new CSVParser();
                                parser.ParseFile(file.FullPath);
                                if (hasReference)
                                {
                                    var referenceParser = new CSVParser();
                                    referenceParser.ParseFile(referenceFile!);
                                    parser.WriteJson(parserTargetFile, file.RelativePath, referenceParser.GetList());
                                }
                                else
                                {
                                    parser.WriteJson(parserTargetFile, file.RelativePath);
                                }
                                candidateDictionaryPath = Path.ChangeExtension(parserTargetFile, ".json");
                                break;
                            }
                            case GameFileKind.Erb:
                            {
                                var parser = new ERBParser();
                                parser.ParseFile(file.FullPath);
                                if (hasReference)
                                {
                                    var referenceParser = new ERBParser();
                                    referenceParser.ParseFile(referenceFile!);
                                    parser.WriteJson(parserTargetFile, file.RelativePath, referenceParser.GetListTuple());
                                }
                                else
                                {
                                    parser.WriteJson(parserTargetFile, file.RelativePath);
                                }
                                candidateDictionaryPath = Path.ChangeExtension(parserTargetFile, ".json");

                                // The legacy dictionary remains unchanged. Its text keys are
                                // linked to structural ERB metadata in a separate sidecar.
                                if (File.Exists(candidateDictionaryPath))
                                {
                                    var groupingService = new ErbTranslationGroupingService();
                                    if (groupingService.TryWriteOrUpdate(
                                            outputRoot,
                                            dictionaryOutputPath,
                                            candidateDictionaryPath,
                                            file.FullPath,
                                            file.RelativePath,
                                            parser.GetTranslationGroupingEntries(file.RelativePath),
                                            parser.GetTranslationControlMarkers(),
                                            out string sidecarPath,
                                            out string? groupingWarning))
                                    {
                                        updatedGroupingFiles.Add(sidecarPath);
                                    }
                                    else if (!string.IsNullOrWhiteSpace(groupingWarning))
                                    {
                                        warnings.Add(groupingWarning);
                                    }
                                }
                                break;
                            }
                            case GameFileKind.ResourceCsv:
                            {
                                var parser = new RESParser();
                                parser.ParseFile(file.FullPath);
                                if (hasReference)
                                {
                                    var referenceParser = new RESParser();
                                    referenceParser.ParseFile(referenceFile!);
                                    parser.WriteJson(parserTargetFile, file.RelativePath, referenceParser.GetList());
                                }
                                else
                                {
                                    parser.WriteJson(parserTargetFile, file.RelativePath);
                                }
                                candidateDictionaryPath = Path.ChangeExtension(parserTargetFile, ".json");
                                break;
                            }
                            case GameFileKind.Xml:
                            {
                                var parser = new XMLParser();
                                parser.ParseFile(file.FullPath);
                                if (hasReference)
                                {
                                    var referenceParser = new XMLParser();
                                    referenceParser.ParseFile(referenceFile!);
                                    parser.WriteJson(parserTargetFile, file.RelativePath, referenceParser.GetList());
                                }
                                else
                                {
                                    parser.WriteJson(parserTargetFile, file.RelativePath);
                                }
                                candidateDictionaryPath = parserTargetFile + ".json";
                                break;
                            }
                            default:
                                return;
                        }

                        if (dictionaryAlreadyExists)
                        {
                            try
                            {
                                if (File.Exists(candidateDictionaryPath) &&
                                    MergeMissingContexts(dictionaryOutputPath, candidateDictionaryPath))
                                {
                                    updatedContextFiles.Add(dictionaryOutputPath);
                                }
                            }
                            catch (Exception ex)
                            {
                                warnings.Add(
                                    $"无法补充现有字典的缺失上下文，原字典保持不变：{Path.GetRelativePath(outputRoot, dictionaryOutputPath)} - {ex.Message}");
                            }
                        }
                        else
                        {
                            AddIfCreated(candidateDictionaryPath, generatedFiles);
                        }
                    }
                    finally
                    {
                        if (contextRefreshDirectory is not null && Directory.Exists(contextRefreshDirectory))
                        {
                            try
                            {
                                Directory.Delete(contextRefreshDirectory, recursive: true);
                            }
                            catch
                            {
                                // Temporary context files are outside the user's output directory.
                            }
                        }
                    }
                    });
            }
        }

        return new DictionaryExtractionResult(
            generatedFiles.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(path => path, StringComparer.Ordinal)
                .ToArray(),
            warnings.OrderBy(warning => warning, StringComparer.Ordinal).ToArray(),
            updatedContextFiles
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            updatedGroupingFiles
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    private static bool MergeMissingContexts(string existingPath, string extractedPath)
    {
        byte[] originalBytes = File.ReadAllBytes(existingPath);
        byte[] originalHash = SHA256.HashData(originalBytes);
        bool hasUtf8Bom = originalBytes.Length >= 3 &&
                          originalBytes[0] == 0xEF &&
                          originalBytes[1] == 0xBB &&
                          originalBytes[2] == 0xBF;
        string existingText = new UTF8Encoding(false, true)
            .GetString(hasUtf8Bom ? originalBytes[3..] : originalBytes);

        JArray existingEntries;
        JArray extractedEntries;
        try
        {
            var loadSettings = new JsonLoadSettings
            {
                DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
                CommentHandling = CommentHandling.Ignore
            };
            existingEntries = JArray.Parse(existingText, loadSettings);
            extractedEntries = JArray.Parse(
                File.ReadAllText(extractedPath, new UTF8Encoding(false, true)),
                loadSettings);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException(
                $"无法补充字典上下文，JSON 文件无效：{existingPath}",
                ex);
        }

        var extractedByKey = extractedEntries.Children<JObject>()
            .Where(entry =>
                !string.IsNullOrWhiteSpace(entry["key"]?.ToString()) &&
                !string.IsNullOrWhiteSpace(entry["original"]?.ToString()) &&
                !string.IsNullOrWhiteSpace(entry["context"]?.ToString()))
            .GroupBy(entry => entry["key"]!.ToString(), StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);

        bool changed = false;
        foreach (JObject existingEntry in existingEntries.Children<JObject>())
        {
            if (!string.IsNullOrWhiteSpace(existingEntry["context"]?.ToString()))
            {
                continue;
            }

            string key = existingEntry["key"]?.ToString() ?? string.Empty;
            string matchKey = NormalizeContextMatchKey(key);
            string original = existingEntry["original"]?.ToString() ?? string.Empty;
            if (key.Length == 0 ||
                !extractedByKey.TryGetValue(matchKey, out JObject? extractedEntry) ||
                !string.Equals(extractedEntry["original"]?.ToString(), original, StringComparison.Ordinal))
            {
                continue;
            }

            JToken? context = extractedEntry["context"];
            if (context is null || string.IsNullOrWhiteSpace(context.ToString()))
            {
                continue;
            }

            existingEntry["context"] = context.DeepClone();
            changed = true;
        }

        if (!changed)
        {
            return false;
        }

        byte[] outputContent = new UTF8Encoding(false)
            .GetBytes(existingEntries.ToString(Formatting.Indented));
        if (hasUtf8Bom)
        {
            outputContent = [0xEF, 0xBB, 0xBF, .. outputContent];
        }

        string temporaryPath = Path.Combine(
            Path.GetDirectoryName(existingPath)!,
            $".{Path.GetFileName(existingPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       options: FileOptions.WriteThrough))
            {
                stream.Write(outputContent);
                stream.Flush(flushToDisk: true);
            }

            byte[] currentHash = SHA256.HashData(File.ReadAllBytes(existingPath));
            if (!CryptographicOperations.FixedTimeEquals(originalHash, currentHash))
            {
                throw new IOException(
                    $"字典在补充上下文时被其他程序修改，为避免覆盖外部更改已取消更新：{existingPath}");
            }

            try
            {
                File.Replace(temporaryPath, existingPath, destinationBackupFileName: null);
            }
            catch (PlatformNotSupportedException)
            {
                File.Move(temporaryPath, existingPath, overwrite: true);
            }

            return true;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string NormalizeContextMatchKey(string key)
    {
        if (key.StartsWith("变量", StringComparison.Ordinal) ||
            key.StartsWith("图像", StringComparison.Ordinal))
        {
            return key[2..];
        }

        return key;
    }

    private static void AddIfCreated(string path, ConcurrentBag<string> generatedFiles)
    {
        if (File.Exists(path))
        {
            generatedFiles.Add(path);
        }
    }

    private static string ResolveOutputPath(string outputRoot, string relativePath)
    {
        string fullPath = Path.GetFullPath(Path.Combine(outputRoot, relativePath));
        string rootWithSeparator = Path.EndsInDirectorySeparator(outputRoot)
            ? outputRoot
            : outputRoot + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Relative path escapes output directory: {relativePath}");
        }
        return fullPath;
    }

    private static void EnsureOutputIsOutsideDirectory(string gameRoot, string outputRoot)
    {
        string relative = Path.GetRelativePath(gameRoot, outputRoot);
        if (relative == "." ||
            (!Path.IsPathRooted(relative) &&
             relative != ".." &&
             !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Output directory must be outside every source directory.");
        }
    }
}
