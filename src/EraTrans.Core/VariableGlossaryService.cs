using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using EraTrans.Core.WriteBack;

namespace EraTrans.Core;

public sealed record VariableGlossaryEntry(
    string Table,
    string Original,
    string Translation);

public sealed record VariableGlossaryDocument(
    int Version,
    IReadOnlyList<VariableGlossaryEntry> Entries);

/// <summary>
/// Manages the workspace-level variable/terminology glossary stored under
/// <c>辅助数据/variable-glossary.json</c> (strictly separate from the 5-field main
/// dictionary JSON contract). Only player-visible CSV variable tables with kana
/// characters are extracted; internal flag tables and pure-kanji/ASCII entries
/// are skipped automatically.
/// </summary>
public sealed class VariableGlossaryService
{
    public const string GlossaryFileName = "variable-glossary.json";
    public const int CurrentVersion = 1;

    private static readonly Dictionary<string, string> VisibleVariableCsvTables =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["item"] = "ITEM",
            ["train"] = "TRAIN",
            ["talent"] = "TALENT",
            ["abl"] = "ABL",
            ["exp"] = "EXP",
            ["palam"] = "PALAM",
            ["base"] = "BASE",
            ["source"] = "SOURCE",
            ["mark"] = "MARK",
            ["ex"] = "EX",
            ["equip"] = "EQUIP",
            ["stain"] = "STAIN"
        };

    private static readonly HashSet<string> InternalFlagCsvTables =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "flag",
            "cflag",
            "tflag",
            "tequip",
            "cstr"
        };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true
    };

    static VariableGlossaryService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static string GetGlossaryPath(EraTransOutputLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return Path.Combine(layout.AiContextDirectory, GlossaryFileName);
    }

    public static bool ContainsKana(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (char ch in text)
        {
            if ((ch >= '\u3040' && ch <= '\u309F') ||
                (ch >= '\u30A0' && ch <= '\u30FF'))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true for dictionary files corresponding to CSV variable definition
    /// tables (both visible variable tables managed in the glossary center and
    /// internal flag tables) so the main text editor can shield them from accidental
    /// direct editing or bulk translation. Does NOT shield <c>CSV/Str.json</c> or
    /// character CSVs (<c>CSV/Chara*.json</c>).
    /// </summary>
    public static bool IsManagedVariableCsvDictionary(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        string normalized = relativePath.Replace('\\', '/').TrimStart('/');
        string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || !segments[0].Equals("CSV", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string stem = Path.GetFileNameWithoutExtension(segments[^1]);
        return VisibleVariableCsvTables.ContainsKey(stem) || InternalFlagCsvTables.Contains(stem);
    }

    public VariableGlossaryDocument Load(EraTransOutputLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        string path = GetGlossaryPath(layout);
        if (!File.Exists(path))
        {
            return new VariableGlossaryDocument(CurrentVersion, Array.Empty<VariableGlossaryEntry>());
        }

        string json = File.ReadAllText(path, Encoding.UTF8);
        VariableGlossaryDocument? loaded = JsonSerializer.Deserialize<VariableGlossaryDocument>(json, JsonOptions);
        if (loaded?.Entries is null)
        {
            return new VariableGlossaryDocument(CurrentVersion, Array.Empty<VariableGlossaryEntry>());
        }

        return new VariableGlossaryDocument(
            loaded.Version <= 0 ? CurrentVersion : loaded.Version,
            loaded.Entries
                .Where(e => !string.IsNullOrWhiteSpace(e.Table) && !string.IsNullOrWhiteSpace(e.Original))
                .Select(e => new VariableGlossaryEntry(
                    e.Table.Trim().ToUpperInvariant(),
                    e.Original.Trim(),
                    (e.Translation ?? string.Empty).Trim()))
                .ToArray());
    }

    public void Save(EraTransOutputLayout layout, VariableGlossaryDocument document)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(document);

        string path = GetGlossaryPath(layout);
        string json = JsonSerializer.Serialize(document, JsonOptions);
        AtomicFileWriter.WriteAllText(path, json);
    }

    /// <summary>
    /// Scans the read-only original game directory's <c>CSV/</c> folder for
    /// player-visible variable tables, keeps only kana-containing names, merges
    /// with any existing saved translations, and persists <c>variable-glossary.json</c>.
    /// </summary>
    public VariableGlossaryDocument SyncFromGameDirectory(
        EraTransOutputLayout layout,
        Encoding? fallbackEncoding = null)
    {
        ArgumentNullException.ThrowIfNull(layout);

        VariableGlossaryDocument existing = Load(layout);
        var existingByTableAndOriginal = new Dictionary<(string Table, string Original), string>();
        var existingByOriginal = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (VariableGlossaryEntry entry in existing.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Translation))
            {
                continue;
            }

            existingByTableAndOriginal[(entry.Table, entry.Original)] = entry.Translation;
            existingByOriginal.TryAdd(entry.Original, entry.Translation);
        }

        Encoding encoding = fallbackEncoding ?? Encoding.GetEncoding(932);
        var discovered = new Dictionary<(string Table, string Original), VariableGlossaryEntry>();

        string? csvDirectory = FindCsvDirectory(layout.GameDirectory);
        if (csvDirectory is not null)
        {
            foreach (string csvFile in Directory.EnumerateFiles(csvDirectory, "*", SearchOption.AllDirectories))
            {
                if (!Path.GetExtension(csvFile).Equals(".csv", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string stem = Path.GetFileNameWithoutExtension(csvFile);
                if (!VisibleVariableCsvTables.TryGetValue(stem, out string? tableName))
                {
                    continue;
                }

                string content = ReadCsvTextSafe(csvFile, encoding);
                foreach (string rawLine in content.Split(["\r\n", "\n", "\r"], StringSplitOptions.None))
                {
                    string line = rawLine.Trim();
                    if (line.Length == 0 || line.StartsWith(';'))
                    {
                        continue;
                    }

                    int commaIndex = line.IndexOf(',');
                    if (commaIndex <= 0)
                    {
                        continue;
                    }

                    string idPart = line[..commaIndex].Trim();
                    if (!int.TryParse(idPart, out _))
                    {
                        continue;
                    }

                    string rest = line[(commaIndex + 1)..];
                    int nextComma = rest.IndexOf(',');
                    string namePart = (nextComma >= 0 ? rest[..nextComma] : rest).Trim();
                    int commentIdx = namePart.IndexOf(';');
                    if (commentIdx >= 0)
                    {
                        namePart = namePart[..commentIdx].Trim();
                    }

                    if (namePart.Length == 0 || !ContainsKana(namePart))
                    {
                        continue;
                    }

                    var key = (tableName, namePart);
                    if (discovered.ContainsKey(key))
                    {
                        continue;
                    }

                    string translation =
                        existingByTableAndOriginal.TryGetValue(key, out string? exact)
                            ? exact
                            : existingByOriginal.TryGetValue(namePart, out string? byOrig)
                                ? byOrig
                                : string.Empty;

                    discovered[key] = new VariableGlossaryEntry(tableName, namePart, translation);
                }
            }
        }

        // Preserve any previously translated entries even if temporarily absent from a scan.
        foreach (VariableGlossaryEntry entry in existing.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Translation))
            {
                continue;
            }

            var key = (entry.Table, entry.Original);
            discovered.TryAdd(key, entry);
        }

        VariableGlossaryEntry[] ordered = discovered.Values
            .OrderBy(e => e.Table, StringComparer.Ordinal)
            .ThenBy(e => e.Original, StringComparer.Ordinal)
            .ToArray();

        var result = new VariableGlossaryDocument(CurrentVersion, ordered);
        Save(layout, result);
        return result;
    }

    /// <summary>
    /// Selects only glossary entries that have a non-empty translation and appear in
    /// at least one line of the current batch, keeping prompt token usage minimal.
    /// </summary>
    public static IReadOnlyDictionary<string, string> MatchForTexts(
        IEnumerable<string> texts,
        IReadOnlyList<VariableGlossaryEntry>? entries,
        int maxTerms = 40)
    {
        if (entries is null || entries.Count == 0 || maxTerms <= 0)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        string[] nonEmptyTexts = texts
            .Where(t => !string.IsNullOrEmpty(t))
            .ToArray();
        if (nonEmptyTexts.Length == 0)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var candidates = entries
            .Where(e => !string.IsNullOrWhiteSpace(e.Original) && !string.IsNullOrWhiteSpace(e.Translation))
            .GroupBy(e => e.Original, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderByDescending(e => e.Original.Length)
            .ThenBy(e => e.Original, StringComparer.Ordinal);

        var matched = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (VariableGlossaryEntry entry in candidates)
        {
            if (matched.Count >= maxTerms)
            {
                break;
            }

            foreach (string text in nonEmptyTexts)
            {
                if (text.Contains(entry.Original, StringComparison.Ordinal))
                {
                    matched[entry.Original] = entry.Translation;
                    break;
                }
            }
        }

        return matched;
    }

    private static string? FindCsvDirectory(string gameDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory) || !Directory.Exists(gameDirectory))
        {
            return null;
        }

        string direct = Path.Combine(gameDirectory, "CSV");
        if (Directory.Exists(direct))
        {
            return direct;
        }

        foreach (string subDir in Directory.EnumerateDirectories(gameDirectory))
        {
            if (Path.GetFileName(subDir).Equals("CSV", StringComparison.OrdinalIgnoreCase))
            {
                return subDir;
            }
        }

        return null;
    }

    private static string ReadCsvTextSafe(string path, Encoding fallbackEncoding)
    {
        try
        {
            return GameFileText.Read(path, fallbackEncoding).Text;
        }
        catch
        {
            byte[] raw = File.ReadAllBytes(path);
            try
            {
                return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(raw);
            }
            catch
            {
                return fallbackEncoding.GetString(raw);
            }
        }
    }
}
