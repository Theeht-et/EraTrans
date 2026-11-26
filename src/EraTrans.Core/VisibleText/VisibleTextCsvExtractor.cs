using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using EraTrans.Core.WriteBack;

namespace EraTrans.Core.VisibleText;

public sealed record VisibleTextCsvEntry(
    string RelativePath,
    int Line,
    string Kind,
    VisibleTextSpan Span,
    string Original,
    string ContextLine);

public sealed record VisibleTextCsvExtractionResult(
    IReadOnlyList<string> GeneratedFiles,
    IReadOnlyList<string> Warnings,
    int FileCount,
    int EntryCount,
    int PreservedTranslations,
    int ResetTranslations);

/// <summary>
/// V2 visible-text extractor for player-facing CSV files (<c>Chara*.csv</c>, <c>Str.csv</c>,
/// <c>GameBase.csv</c>, and <c>_Replace.csv</c>). Produces strict 5-field main dictionaries
/// plus exact-position <c>.spans.json</c> sidecars so write-back uses <see cref="SpanApplier"/>
/// without whole-file regex replacement or touching internal variable/logic columns.
/// </summary>
public sealed class VisibleTextCsvExtractor
{
    private const string DictionaryDirectoryName = "字典";
    private const string SidecarDirectoryName = "辅助数据";
    private const string SidecarSuffix = ".spans.json";

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly HashSet<string> CharaTwoColumnDisplayKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "名前", "呼び名", "あだ名", "ニックネーム", "マスター名",
        "NAME", "CALLNAME", "NICKNAME", "MASTERNAME"
    };

    private static readonly HashSet<string> CharaThreeColumnDisplayKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "CSTR"
    };

    private static readonly HashSet<string> GameBaseDisplayKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "タイトル", "ウィンドウタイトル", "作者", "追加情報", "弄ってみた人",
        "TITLE", "AUTHOR", "INFO"
    };

    private static readonly HashSet<string> ReplaceDisplayKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "起動時簡略表示",
        "お金の単位",
        "時間切れ表示",
        "時間経過表示",
        "販売アイテム数表示",
        "DRAWLINE文字",
        "BAR文字1",
        "BAR文字2",
        "システムメニュー0",
        "システムメニュー1"
    };

    private enum CsvCategory
    {
        None,
        Chara,
        Str,
        GameBase,
        Replace
    }

    public VisibleTextCsvExtractionResult Extract(
        string gameDirectory,
        string dictionaryRoot,
        IReadOnlyList<GameFile> csvFiles,
        EraParserOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(dictionaryRoot);
        ArgumentNullException.ThrowIfNull(csvFiles);

        string dictionaryRootFull = Path.GetFullPath(dictionaryRoot);
        string sidecarRoot = ResolveSidecarRoot(dictionaryRootFull);
        EraParserOptions effectiveOptions = options ?? new EraParserOptions();

        var generatedFiles = new List<string>();
        var warnings = new List<string>();
        int fileCount = 0;
        int entryCount = 0;
        int preservedTranslations = 0;
        int resetTranslations = 0;

        foreach (GameFile file in csvFiles
                     .OrderBy(candidate => candidate.RelativePath, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(candidate => candidate.RelativePath, StringComparer.Ordinal))
        {
            CsvCategory category = ClassifyCsvFile(file.FullPath);
            if (category == CsvCategory.None)
            {
                continue;
            }

            string relativePath = file.RelativePath.Replace('\\', '/');
            try
            {
                string sourceText = GameFileText.Read(file.FullPath, effectiveOptions.FileEncoding).Text;
                List<VisibleTextCsvEntry> entries = ExtractFromContent(
                    relativePath,
                    category,
                    sourceText,
                    effectiveOptions.SkipNonJapaneseEntries);

                if (entries.Count == 0)
                {
                    continue;
                }

                string dictionaryPath = ResolveDictionaryPath(dictionaryRootFull, relativePath);
                DictionaryWriteStats stats = WriteDictionary(
                    dictionaryPath,
                    entries,
                    effectiveOptions.WriteContext);

                preservedTranslations += stats.Preserved;
                resetTranslations += stats.Reset;
                if (stats.Warning is not null)
                {
                    warnings.Add($"{relativePath}: {stats.Warning}");
                }

                generatedFiles.Add(dictionaryPath);

                string sidecarPath = ResolveSidecarPath(sidecarRoot, relativePath);
                if (WriteSidecar(sidecarPath, relativePath, sourceText, entries))
                {
                    generatedFiles.Add(sidecarPath);
                }

                fileCount++;
                entryCount += entries.Count;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                warnings.Add($"{relativePath}: {ex.Message}");
            }
        }

        return new VisibleTextCsvExtractionResult(
            generatedFiles,
            warnings,
            fileCount,
            entryCount,
            preservedTranslations,
            resetTranslations);
    }

    internal static List<VisibleTextCsvEntry> ExtractFromContent(
        string relativePath,
        string filePathOrName,
        string sourceText,
        bool skipNonJapaneseEntries)
    {
        CsvCategory category = ClassifyCsvFile(filePathOrName);
        if (category == CsvCategory.None)
        {
            return [];
        }

        return ExtractFromContent(relativePath, category, sourceText, skipNonJapaneseEntries);
    }

    private static List<VisibleTextCsvEntry> ExtractFromContent(
        string relativePath,
        CsvCategory category,
        string sourceText,
        bool skipNonJapaneseEntries)
    {
        var entries = new List<VisibleTextCsvEntry>();
        int pos = 0;
        int lineNumber = 1;
        int length = sourceText.Length;

        while (pos < length)
        {
            int newline = sourceText.IndexOf('\n', pos);
            int lineEnd;
            int nextPos;
            if (newline < 0)
            {
                lineEnd = length > pos && sourceText[length - 1] == '\r' ? length - 1 : length;
                nextPos = length;
            }
            else
            {
                lineEnd = newline > pos && sourceText[newline - 1] == '\r' ? newline - 1 : newline;
                nextPos = newline + 1;
            }

            string line = sourceText[pos..lineEnd];
            ExtractLineEntries(
                relativePath,
                category,
                line,
                pos,
                lineNumber,
                skipNonJapaneseEntries,
                entries);

            pos = nextPos;
            lineNumber++;
        }

        return entries;
    }

    private static void ExtractLineEntries(
        string relativePath,
        CsvCategory category,
        string line,
        int lineOffset,
        int lineNumber,
        bool skipNonJapaneseEntries,
        List<VisibleTextCsvEntry> entries)
    {
        ReadOnlySpan<char> trimmedStart = line.AsSpan().TrimStart([' ', '\t']);
        if (trimmedStart.IsEmpty || trimmedStart[0] == ';')
        {
            return;
        }

        List<CsvColumn> columns = SplitColumnsWithSpans(line, lineOffset);
        if (columns.Count < 2)
        {
            return;
        }

        string key = columns[0].Value;
        switch (category)
        {
            case CsvCategory.Chara:
                if (CharaTwoColumnDisplayKeys.Contains(key))
                {
                    TryAddEntry(relativePath, lineNumber, $"CHARA:{key}", columns[1], line, skipNonJapaneseEntries, requireJapanese: true, entries);
                }
                else if (CharaThreeColumnDisplayKeys.Contains(key) && columns.Count >= 3)
                {
                    // Skip column 1 (the CSTR key/index); only extract column 2 (the display value).
                    TryAddEntry(relativePath, lineNumber, $"CHARA:CSTR[{columns[1].Value}]", columns[2], line, skipNonJapaneseEntries, requireJapanese: true, entries);
                }
                break;

            case CsvCategory.Str:
                if (!int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int strIndex) || strIndex < 0)
                {
                    return;
                }

                // Str.csv may store either a single value (col 1) or comma-separated fields
                // split later in ERB (e.g. eraTW music entries). Extract each non-numeric text segment
                // as an independent span so numeric columns and comma delimiters stay intact.
                for (int colIndex = 1; colIndex < columns.Count; colIndex++)
                {
                    CsvColumn column = columns[colIndex];
                    if (column.Value.Length == 0 ||
                        int.TryParse(column.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                    {
                        continue;
                    }

                    string kind = columns.Count == 2 ? $"STR:{key}" : $"STR:{key}:{colIndex}";
                    TryAddEntry(relativePath, lineNumber, kind, column, line, skipNonJapaneseEntries, requireJapanese: true, entries);
                }
                break;

            case CsvCategory.GameBase:
                if (GameBaseDisplayKeys.Contains(key))
                {
                    TryAddEntry(relativePath, lineNumber, $"GAMEBASE:{key}", columns[1], line, skipNonJapaneseEntries, requireJapanese: false, entries);
                }
                break;

            case CsvCategory.Replace:
                if (ReplaceDisplayKeys.Contains(key))
                {
                    TryAddEntry(relativePath, lineNumber, $"REPLACE:{key}", columns[1], line, skipNonJapaneseEntries, requireJapanese: false, entries);
                }
                break;
        }
    }

    private static void TryAddEntry(
        string relativePath,
        int lineNumber,
        string kind,
        CsvColumn column,
        string line,
        bool skipNonJapaneseEntries,
        bool requireJapanese,
        List<VisibleTextCsvEntry> entries)
    {
        if (column.Value.Length == 0)
        {
            return;
        }

        if (requireJapanese && skipNonJapaneseEntries && !ContainsJapanese(column.Value))
        {
            return;
        }

        entries.Add(new VisibleTextCsvEntry(
            relativePath,
            lineNumber,
            kind,
            new VisibleTextSpan(column.Start, column.End),
            column.Value,
            line.TrimEnd()));
    }

    /// <summary>
    /// Splits a CSV line up to the first <c>;</c> comment marker, preserving exact character offsets
    /// for each trimmed column value. Deliberately does NOT truncate at <c>(</c>.
    /// </summary>
    private static List<CsvColumn> SplitColumnsWithSpans(string line, int lineOffset)
    {
        int semicolonIndex = line.IndexOf(';');
        int activeEnd = semicolonIndex >= 0 ? semicolonIndex : line.Length;
        var columns = new List<CsvColumn>(4);

        int start = 0;
        while (true)
        {
            int commaIndex = line.IndexOf(',', start, activeEnd - start);
            int rawEnd = commaIndex >= 0 ? commaIndex : activeEnd;

            int valStart = start;
            while (valStart < rawEnd && (line[valStart] == ' ' || line[valStart] == '\t'))
            {
                valStart++;
            }

            int valEnd = rawEnd;
            while (valEnd > valStart && (line[valEnd - 1] == ' ' || line[valEnd - 1] == '\t'))
            {
                valEnd--;
            }

            string value = line[valStart..valEnd];
            columns.Add(new CsvColumn(value, lineOffset + valStart, lineOffset + valEnd));

            if (commaIndex < 0)
            {
                break;
            }

            start = commaIndex + 1;
        }

        return columns;
    }

    private static CsvCategory ClassifyCsvFile(string filePath)
    {
        string stem = Path.GetFileNameWithoutExtension(filePath);
        if (string.IsNullOrWhiteSpace(stem))
        {
            return CsvCategory.None;
        }

        if (stem.StartsWith("Chara", StringComparison.OrdinalIgnoreCase))
        {
            return CsvCategory.Chara;
        }

        if (stem.Equals("Str", StringComparison.OrdinalIgnoreCase))
        {
            return CsvCategory.Str;
        }

        if (stem.Equals("GameBase", StringComparison.OrdinalIgnoreCase))
        {
            return CsvCategory.GameBase;
        }

        if (stem.Equals("_Replace", StringComparison.OrdinalIgnoreCase))
        {
            return CsvCategory.Replace;
        }

        return CsvCategory.None;
    }

    private static bool ContainsJapanese(string text)
    {
        foreach (char c in text)
        {
            if ((c >= '\u3040' && c <= '\u30FF') ||
                (c >= '\u4E00' && c <= '\u9FFF'))
            {
                return true;
            }
        }

        return false;
    }

    private static DictionaryWriteStats WriteDictionary(
        string path,
        IReadOnlyList<VisibleTextCsvEntry> entries,
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
            catch (Exception ex) when (ex is IOException or JsonException)
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
            VisibleTextCsvEntry entry = entries[i];
            string key = BuildKey(entry.RelativePath, i);
            string translation = string.Empty;
            int stage = 0;

            if (existing is not null && existing.TryGetValue(key, out ExistingEntry old))
            {
                if (string.Equals(old.Original, entry.Original, StringComparison.Ordinal))
                {
                    if (old.Translation.Length > 0 || old.Stage != 0)
                    {
                        translation = old.Translation;
                        stage = old.Stage;
                        preserved++;
                    }
                }
                else if (old.Translation.Length > 0)
                {
                    reset++;
                }
            }

            writer.WriteStartObject();
            writer.WriteString("key", key);
            writer.WriteString("original", entry.Original);
            writer.WriteString("translation", translation);
            writer.WriteNumber("stage", stage);
            writer.WriteString("context", writeContext ? $"行 {entry.Line} ({entry.Kind}): {entry.ContextLine}" : string.Empty);
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

    private static bool WriteSidecar(
        string path,
        string relativePath,
        string sourceText,
        IReadOnlyList<VisibleTextCsvEntry> entries)
    {
        var spans = new List<(string Key, VisibleTextCsvEntry Entry)>(entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            VisibleTextCsvEntry entry = entries[i];
            if (entry.Original.Length == 0 || !entry.Span.IsValid || entry.Span.Length != entry.Original.Length)
            {
                continue;
            }

            spans.Add((BuildKey(entry.RelativePath, i), entry));
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
        writer.WriteString("fileHash", VisibleTextExtractor.ComputeFileHash(sourceText));
        writer.WriteStartArray("spans");
        foreach ((string key, VisibleTextCsvEntry entry) in spans)
        {
            writer.WriteStartObject();
            writer.WriteString("key", key);
            writer.WriteNumber("start", entry.Span.Start);
            writer.WriteNumber("end", entry.Span.End);
            writer.WriteNumber("line", entry.Line);
            writer.WriteString("command", "CSV");
            writer.WriteString("kind", entry.Kind);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        return true;
    }

    private static string BuildKey(string relativePath, int ordinal)
    {
        string stem = StripExtension(relativePath).Replace('/', '\\');
        return $"文本{stem}{ordinal:D5}";
    }

    private static string ResolveDictionaryPath(string dictionaryRoot, string relativePath) =>
        Path.Combine(dictionaryRoot, Path.ChangeExtension(relativePath, ".json"));

    private static string ResolveSidecarPath(string sidecarRoot, string relativePath) =>
        Path.Combine(
            sidecarRoot,
            SidecarDirectoryName,
            StripExtension(relativePath) + SidecarSuffix);

    private static string StripExtension(string relativePath)
    {
        string extension = Path.GetExtension(relativePath);
        return extension.Length == 0 ? relativePath : relativePath[..^extension.Length];
    }

    private static string ResolveSidecarRoot(string dictionaryRoot)
    {
        string root = Path.GetFullPath(dictionaryRoot);
        return string.Equals(Path.GetFileName(root), DictionaryDirectoryName, StringComparison.OrdinalIgnoreCase)
            ? Directory.GetParent(root)?.FullName ?? root
            : root;
    }

    private readonly record struct CsvColumn(string Value, int Start, int End);
    private readonly record struct ExistingEntry(string Original, string Translation, int Stage);
    private readonly record struct DictionaryWriteStats(int Preserved, int Reset, string? Warning);
}
