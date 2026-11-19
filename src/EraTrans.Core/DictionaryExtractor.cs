using System.Collections.Concurrent;

namespace EraTrans.Core;

public sealed record DictionaryExtractionResult(
    IReadOnlyList<string> GeneratedFiles,
    IReadOnlyList<string> Warnings);

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

                    // Never replace an existing dictionary: it may already contain user
                    // translations. Re-extraction creates only dictionaries that do not exist.
                    if (File.Exists(dictionaryOutputPath))
                    {
                        warnings.Add(
                            $"字典文件已存在，跳过以保护已有译文：{Path.GetRelativePath(outputRoot, dictionaryOutputPath)}");
                        return;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
                    string? referenceFile = referenceRoot is null
                        ? null
                        : ResolveOutputPath(referenceRoot, file.RelativePath);

                    bool hasReference = referenceFile is not null && File.Exists(referenceFile);
                    if (referenceRoot is not null && !hasReference)
                    {
                        warnings.Add($"未找到参考文件：{referenceFile}");
                    }

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
                                parser.WriteJson(targetFile, file.RelativePath, referenceParser.GetList());
                            }
                            else
                            {
                                parser.WriteJson(targetFile, file.RelativePath);
                            }
                            AddIfCreated(Path.ChangeExtension(targetFile, ".json"), generatedFiles);
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
                                parser.WriteJson(targetFile, file.RelativePath, referenceParser.GetListTuple());
                            }
                            else
                            {
                                parser.WriteJson(targetFile, file.RelativePath);
                            }
                            AddIfCreated(Path.ChangeExtension(targetFile, ".json"), generatedFiles);
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
                                parser.WriteJson(targetFile, file.RelativePath, referenceParser.GetList());
                            }
                            else
                            {
                                parser.WriteJson(targetFile, file.RelativePath);
                            }
                            AddIfCreated(Path.ChangeExtension(targetFile, ".json"), generatedFiles);
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
                                parser.WriteJson(targetFile, file.RelativePath, referenceParser.GetList());
                            }
                            else
                            {
                                parser.WriteJson(targetFile, file.RelativePath);
                            }
                            AddIfCreated(targetFile + ".json", generatedFiles);
                            break;
                        }
                    }
                    });
            }
        }

        return new DictionaryExtractionResult(
            generatedFiles.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(path => path, StringComparer.Ordinal)
                .ToArray(),
            warnings.OrderBy(warning => warning, StringComparer.Ordinal).ToArray());
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
