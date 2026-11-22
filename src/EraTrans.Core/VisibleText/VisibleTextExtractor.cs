using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using EraTrans.Core.WriteBack;

namespace EraTrans.Core.VisibleText;

/// <summary>
/// Extracts every visible literal of an entire ERB tree into per-file dictionaries plus the
/// position sidecars used for write-back.
/// <para>
/// Dictionaries go to <c>&lt;dictionaryRoot&gt;/&lt;relative path&gt;.json</c> and sidecars to
/// <c>&lt;output root&gt;/AI上下文/&lt;relative path&gt;.spans.json</c>, matching
/// <see cref="SpanCatalog"/>. <c>&lt;output root&gt;</c> is the parent of a
/// <c>dictionaryRoot</c> whose last segment is <c>字典</c>, otherwise <c>dictionaryRoot</c> itself.
/// </para>
/// </summary>
public sealed class VisibleTextExtractor
{
    private const string SidecarDirectoryName = "AI上下文";
    private const string SidecarSuffix = ".spans.json";
    private const string DictionaryDirectoryName = "字典";
    private const string ReportFileName = "visibletext-report.json";

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// Scans <c>gameDirectory/sourceSubdirectory</c> recursively for <c>*.erb</c> files and
    /// writes one dictionary file per non-empty source file.
    /// </summary>
    public VisibleTextExtractionResult Extract(
        string gameDirectory,
        string dictionaryRoot,
        string sourceSubdirectory,
        EraParserOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(dictionaryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSubdirectory);
        ArgumentNullException.ThrowIfNull(options);

        string gameRoot = Path.GetFullPath(gameDirectory);
        if (!Directory.Exists(gameRoot))
        {
            throw new DirectoryNotFoundException($"Game directory does not exist: {gameRoot}");
        }

        string sourceRoot = Path.GetFullPath(Path.Combine(gameRoot, sourceSubdirectory));
        if (!Directory.Exists(sourceRoot))
        {
            throw new DirectoryNotFoundException($"Source directory does not exist: {sourceRoot}");
        }

        string dictionaryRootFull = Path.GetFullPath(dictionaryRoot);
        string sidecarRoot = ResolveSidecarRoot(dictionaryRootFull);
        IReadOnlyCollection<string> macros = LoadMacros(gameRoot);

        string[] files = Directory
            .GetFiles(sourceRoot, "*.erb", SearchOption.AllDirectories)
            .OrderBy(path => Path.GetRelativePath(gameRoot, path), StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => Path.GetRelativePath(gameRoot, path), StringComparer.Ordinal)
            .ToArray();

        var warnings = new List<string>();
        var generatedFiles = new List<string>();
        var diagnostics = new VisibleTextDiagnostics();
        int entryCount = 0;
        int preservedTranslations = 0;
        int resetTranslations = 0;
        int removedStaleFiles = 0;

        foreach (string file in files)
        {
            string relativePath = Path.GetRelativePath(gameRoot, file).Replace('\\', '/');
            try
            {
                string text = GameFileText.Read(file).Text;
                VisibleTextFileResult lexed = ErbVisibleTextLexer.ExtractFile(relativePath, text, macros);
                warnings.AddRange(lexed.Warnings);
                diagnostics.MergeFrom(lexed.Diagnostics);
                if (lexed.Entries.Count == 0)
                {
                    // The file no longer has visible text (or became unreadable to the lexer).
                    // Leaving last round's dictionary behind would show entries that do not exist
                    // any more, and their translations could still be applied by the fallback
                    // original-match path, so remove them.
                    if (DeleteIfExists(ResolveDictionaryPath(dictionaryRootFull, relativePath)))
                    {
                        removedStaleFiles++;
                    }

                    if (DeleteIfExists(ResolveSidecarPath(sidecarRoot, relativePath)))
                    {
                        removedStaleFiles++;
                    }

                    continue;
                }

                List<LineInfo> lineInfo = BuildLineInfo(text);
                string dictionaryPath = ResolveDictionaryPath(dictionaryRootFull, relativePath);
                DictionaryWriteStats stats = WriteDictionary(dictionaryPath, lexed.Entries, lineInfo, options.WriteContext);
                preservedTranslations += stats.Preserved;
                resetTranslations += stats.Reset;
                if (stats.Warning is not null)
                {
                    warnings.Add($"{relativePath}: {stats.Warning}");
                }

                generatedFiles.Add(dictionaryPath);

                string sidecarPath = ResolveSidecarPath(sidecarRoot, relativePath);
                if (WriteSidecar(sidecarPath, relativePath, text, lexed.Entries))
                {
                    generatedFiles.Add(sidecarPath);
                }

                string? groupingPath = VisibleTextGrouping.TryWrite(
                    dictionaryRootFull,
                    dictionaryPath,
                    file,
                    relativePath,
                    text,
                    lexed.Entries,
                    out string? groupingWarning);
                if (groupingPath is not null)
                {
                    generatedFiles.Add(groupingPath);
                }

                if (groupingWarning is not null)
                {
                    warnings.Add($"{relativePath}: {groupingWarning}");
                }

                entryCount += lexed.Entries.Count;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                warnings.Add($"{relativePath}: {ex.Message}");
            }
        }

        removedStaleFiles += RemoveOrphanDictionaries(gameRoot, dictionaryRootFull, sidecarRoot, sourceSubdirectory, warnings);

        if (!diagnostics.IsEmpty)
        {
            string reportPath = Path.Combine(sidecarRoot, SidecarDirectoryName, ReportFileName);
            WriteDiagnosticsReport(reportPath, diagnostics);
            generatedFiles.Add(reportPath);
            warnings.Add(BuildDiagnosticsSummary(diagnostics, reportPath));
        }

        if (removedStaleFiles > 0)
        {
            warnings.Add($"清理了 {removedStaleFiles} 个上一轮遗留、源文件已不存在的字典/定位文件。");
        }

        return new VisibleTextExtractionResult(generatedFiles, warnings, files.Length, entryCount)
        {
            PreservedTranslations = preservedTranslations,
            ResetTranslations = resetTranslations,
            Diagnostics = diagnostics
        };
    }

    /// <summary>Writes the coverage report as UTF-8 without BOM.</summary>
    private static void WriteDiagnosticsReport(string path, VisibleTextDiagnostics diagnostics)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new Utf8JsonWriter(stream, WriterOptions);
        writer.WriteStartObject();
        writer.WriteNumber("schema", 1);

        writer.WriteStartArray("unknownCommands");
        foreach ((string command, int lines) in diagnostics.UnknownCommands.OrderByDescending(pair => pair.Value))
        {
            writer.WriteStartObject();
            writer.WriteString("command", command);
            writer.WriteNumber("lines", lines);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WriteStartArray("unknownCommandSamples");
        foreach (string sample in diagnostics.UnknownCommandSamples)
        {
            writer.WriteStringValue(sample);
        }

        writer.WriteEndArray();

        writer.WriteStartArray("assignedAndPrintedVars");
        foreach ((string name, int lines) in diagnostics.AssignedAndPrintedVars.OrderByDescending(pair => pair.Value))
        {
            writer.WriteStartObject();
            writer.WriteString("name", name);
            writer.WriteNumber("lines", lines);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteNumber("assignedAndPrintedLines", diagnostics.AssignedAndPrintedLines);
        writer.WriteNumber("placeholderOnlyPrintLines", diagnostics.PlaceholderOnlyPrintLines);
        writer.WriteEndObject();
    }

    /// <summary>One-line warning so the user sees the report without opening the JSON.</summary>
    private static string BuildDiagnosticsSummary(VisibleTextDiagnostics diagnostics, string reportPath)
    {
        var parts = new List<string>();
        if (diagnostics.UnknownCommands.Count > 0)
        {
            int lines = diagnostics.UnknownCommands.Values.Sum();
            string top = string.Join(
                "、",
                diagnostics.UnknownCommands.OrderByDescending(pair => pair.Value).Take(3)
                    .Select(pair => $"{pair.Key} {pair.Value}"));
            parts.Add($"未知命令 {diagnostics.UnknownCommands.Count} 类 / {lines} 行（{top}）");
        }

        if (diagnostics.AssignedAndPrintedLines > 0)
        {
            string top = string.Join(
                "、",
                diagnostics.AssignedAndPrintedVars.OrderByDescending(pair => pair.Value).Take(3)
                    .Select(pair => $"{pair.Key} {pair.Value}"));
            parts.Add($"变量中转 {diagnostics.AssignedAndPrintedLines} 条未提取（{top}）");
        }

        if (diagnostics.PlaceholderOnlyPrintLines > 0)
        {
            parts.Add($"仅占位符打印 {diagnostics.PlaceholderOnlyPrintLines} 行");
        }

        return $"覆盖率自检：{string.Join("；", parts)}。详见 {Path.GetFileName(reportPath)}。";
    }

    /// <summary>
    /// Collects <c>#DEFINE</c> names from the game's <c>.erh</c> files. This mirrors the
    /// prototype and Emuera's ERB loader: <c>ERH</c>, <c>erb</c> and <c>ERB</c> at the game root.
    /// </summary>
    private static IReadOnlyCollection<string> LoadMacros(string gameRoot)
    {
        var macros = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string directoryName in new[] { "ERH", "erb", "ERB" })
        {
            string directory = Path.Combine(gameRoot, directoryName);
            if (!Directory.Exists(directory) || !seenDirectories.Add(Path.GetFullPath(directory)))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(directory, "*.erh", SearchOption.TopDirectoryOnly))
            {
                foreach (string rawLine in File.ReadLines(file))
                {
                    string? macro = ParseDefine(rawLine);
                    if (macro is not null)
                    {
                        macros.Add(macro);
                    }
                }
            }
        }

        return macros;
    }

    /// <summary>Parses <c>#DEFINE NAME</c> / <c>#DEFINE NAME(a,b)</c>; returns the macro name.</summary>
    private static string? ParseDefine(string rawLine)
    {
        string line = rawLine.Trim();
        if (line.Length < 7 || !line.StartsWith("#DEFINE", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        int index = 7;
        while (index < line.Length && (line[index] == ' ' || line[index] == '\t'))
        {
            index++;
        }

        int start = index;
        while (index < line.Length && line[index] != ' ' && line[index] != '\t' &&
               line[index] != ',' && line[index] != '(')
        {
            index++;
        }

        return index > start ? line.Substring(start, index - start) : null;
    }

    private static List<LineInfo> BuildLineInfo(string text)
    {
        List<(string Line, int Start)> lines = ErbVisibleTextLexer.SplitLines(text);
        var info = new List<LineInfo>(lines.Count);
        string currentFunction = string.Empty;

        foreach ((string line, _) in lines)
        {
            int p = 0;
            while (p < line.Length && (line[p] == ' ' || line[p] == '\t'))
            {
                p++;
            }

            if (p < line.Length && line[p] == '@')
            {
                currentFunction = ErbVisibleTextLexer.ParseFunctionName(line, p);
            }

            info.Add(new LineInfo(currentFunction, line));
        }

        return info;
    }

    /// <summary>Context shown next to a dictionary entry in the translation UI.</summary>
    private static string BuildContext(VisibleTextEntry entry, List<LineInfo> lineInfo)
    {
        if (entry.Line < 1 || entry.Line > lineInfo.Count)
        {
            return string.Empty;
        }

        LineInfo info = lineInfo[entry.Line - 1];
        string function = info.Function.Length == 0 ? "(unnamed)" : info.Function;
        return $"@{function} 行 {entry.Line}: {info.Line.TrimEnd()}";
    }

    private static DictionaryWriteStats WriteDictionary(
        string path,
        IReadOnlyList<VisibleTextEntry> entries,
        List<LineInfo> lineInfo,
        bool writeContext)
    {
        Dictionary<string, ExistingEntry>? existing = null;
        string? warning = null;
        if (File.Exists(path))
        {
            try
            {
                existing = LoadExistingDictionary(path);
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
            {
                warning = $"已有字典无法读取，按新建处理：{ex.Message}";
            }
        }

        int preserved = 0;
        int reset = 0;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using FileStream stream = File.Create(path);
        using var writer = new Utf8JsonWriter(stream, WriterOptions);
        writer.WriteStartArray();
        for (int i = 0; i < entries.Count; i++)
        {
            VisibleTextEntry entry = entries[i];
            string key = BuildKey(entry.RelativePath, i);
            string translation = string.Empty;
            int stage = 0;

            if (existing is not null && existing.TryGetValue(key, out ExistingEntry old))
            {
                if (string.Equals(old.Original, entry.Template, StringComparison.Ordinal))
                {
                    // Same position and same source text: keep whatever the user or the AI wrote.
                    if (old.Translation.Length > 0 || old.Stage != 0)
                    {
                        translation = old.Translation;
                        stage = old.Stage;
                        preserved++;
                    }
                }
                else if (old.Translation.Length > 0)
                {
                    // The source text at this position changed; the old translation no longer applies.
                    reset++;
                }
            }

            writer.WriteStartObject();
            writer.WriteString("key", key);
            writer.WriteString("original", entry.Template);
            writer.WriteString("translation", translation);
            writer.WriteNumber("stage", stage);
            writer.WriteString("context", writeContext ? BuildContext(entry, lineInfo) : string.Empty);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        return new DictionaryWriteStats(preserved, reset, warning);
    }

    private static Dictionary<string, ExistingEntry> LoadExistingDictionary(string path)
    {
        var map = new Dictionary<string, ExistingEntry>(StringComparer.Ordinal);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return map;
        }

        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("key", out JsonElement keyElement) ||
                keyElement.GetString() is not { Length: > 0 } key)
            {
                continue;
            }

            string original = item.TryGetProperty("original", out JsonElement originalElement) &&
                              originalElement.ValueKind == JsonValueKind.String
                ? originalElement.GetString() ?? string.Empty
                : string.Empty;
            string translation = item.TryGetProperty("translation", out JsonElement translationElement) &&
                                 translationElement.ValueKind == JsonValueKind.String
                ? translationElement.GetString() ?? string.Empty
                : string.Empty;
            int stage = item.TryGetProperty("stage", out JsonElement stageElement) &&
                        stageElement.TryGetInt32(out int parsedStage)
                ? parsedStage
                : 0;

            map[key] = new ExistingEntry(original, translation, stage);
        }

        return map;
    }

    private readonly record struct ExistingEntry(string Original, string Translation, int Stage);

    private readonly record struct DictionaryWriteStats(int Preserved, int Reset, string? Warning);

    /// <summary>
    /// Writes the position sidecar. The stored span covers the whole template, so
    /// <c>slice(span) == original</c> holds for every entry, including templates with
    /// placeholders (the replacement keeps the placeholders verbatim).
    /// </summary>
    private static bool WriteSidecar(
        string path,
        string relativePath,
        string sourceText,
        IReadOnlyList<VisibleTextEntry> entries)
    {
        var spans = new List<(string Key, VisibleTextSpan Span, int Line, string Command, string Kind)>();
        for (int i = 0; i < entries.Count; i++)
        {
            VisibleTextEntry entry = entries[i];
            if (entry.Template.Length == 0 || !entry.Span.IsValid || entry.Span.Length != entry.Template.Length)
            {
                continue;
            }

            spans.Add((BuildKey(entry.RelativePath, i), entry.Span, entry.Line, entry.Command, entry.Kind));
        }

        if (spans.Count == 0)
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using FileStream stream = File.Create(path);
        using var writer = new Utf8JsonWriter(stream, WriterOptions);
        writer.WriteStartObject();
        writer.WriteNumber("schema", 1);
        writer.WriteString("file", relativePath);
        writer.WriteString("fileHash", ComputeFileHash(sourceText));
        writer.WriteStartArray("spans");
        foreach ((string key, VisibleTextSpan span, int line, string command, string kind) in spans)
        {
            writer.WriteStartObject();
            writer.WriteString("key", key);
            writer.WriteNumber("start", span.Start);
            writer.WriteNumber("end", span.End);
            writer.WriteNumber("line", line);
            writer.WriteString("command", command);
            writer.WriteString("kind", kind);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        return true;
    }

    /// <summary>
    /// Fingerprint of the decoded source text; the write-back pass refuses to use locators whose
    /// source no longer matches, so a changed game file cannot be edited at stale offsets.
    /// </summary>
    internal static string ComputeFileHash(string text)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// <c>"文本" + game-relative path without extension, backslash separated + 5 digit ordinal</c>,
    /// the key format the legacy dictionaries already use.
    /// </summary>
    internal static string BuildKey(string relativePath, int sequence) =>
        "文本" + Path.ChangeExtension(relativePath, string.Empty).Replace('/', '\\') + sequence.ToString("D5");

    private static bool DeleteIfExists(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    /// <summary>
    /// Removes dictionaries (and their span sidecars) whose source file is gone. Only files below
    /// <paramref name="sourceSubdirectory"/> are considered, so dictionaries written by the legacy
    /// parser for CSV/ERD/XML live in the same directory unharmed.
    /// </summary>
    private static int RemoveOrphanDictionaries(
        string gameRoot,
        string dictionaryRoot,
        string sidecarRoot,
        string sourceSubdirectory,
        List<string> warnings)
    {
        string prefix = sourceSubdirectory.Replace('\\', '/').Trim('/') + "/";
        int removed = 0;
        foreach (string dictionaryPath in Directory.GetFiles(dictionaryRoot, "*.json", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(dictionaryRoot, dictionaryPath).Replace('\\', '/');
            if (!relative.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (relative.EndsWith(".spans.json", StringComparison.OrdinalIgnoreCase) ||
                relative.EndsWith(".groups.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string stem = relative[..^".json".Length];
            string absoluteStem = Path.Combine(gameRoot, stem.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(absoluteStem + ".erb") || File.Exists(absoluteStem + ".erh") ||
                File.Exists(absoluteStem + ".ERB") || File.Exists(absoluteStem + ".ERH"))
            {
                continue;
            }

            try
            {
                File.Delete(dictionaryPath);
                removed++;
                string sidecar = ResolveSidecarPath(sidecarRoot, stem + ".erb");
                if (DeleteIfExists(sidecar))
                {
                    removed++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"{relative}: 无法删除已失效的字典（{ex.Message}）。");
            }
        }

        return removed;
    }

    private static string ResolveDictionaryPath(string dictionaryRoot, string relativePath) =>
        Path.Combine(dictionaryRoot, Path.ChangeExtension(relativePath, ".json"));

    private static string ResolveSidecarPath(string sidecarRoot, string relativePath) =>
        Path.Combine(
            sidecarRoot,
            SidecarDirectoryName,
            StripExtension(relativePath) + SidecarSuffix);

    /// <summary>
    /// Removes the file extension including its dot. <see cref="Path.ChangeExtension(string, string)"/>
    /// keeps the dot when the replacement extension is empty, which would turn <c>x.erb</c> into
    /// <c>x..spans.json</c> for the sidecar name.
    /// </summary>
    private static string StripExtension(string relativePath)
    {
        string extension = Path.GetExtension(relativePath);
        return extension.Length == 0 ? relativePath : relativePath[..^extension.Length];
    }

    private static string ResolveSidecarRoot(string dictionaryRoot)
    {
        string root = Path.GetFullPath(dictionaryRoot);
        string outputRoot = string.Equals(Path.GetFileName(root), DictionaryDirectoryName, StringComparison.OrdinalIgnoreCase)
            ? Directory.GetParent(root)?.FullName ?? root
            : root;
        return outputRoot;
    }

    private readonly record struct LineInfo(string Function, string Line);
}
