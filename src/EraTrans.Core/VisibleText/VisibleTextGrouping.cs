using EraTrans.Core.WriteBack;

namespace EraTrans.Core.VisibleText;

/// <summary>
/// Derives the ERB AI grouping sidecar (<c>AI上下文/*.groups.json</c>) for a dictionary
/// produced by <see cref="VisibleTextExtractor"/>.
/// <para>
/// The legacy extractor writes that sidecar itself, and <see cref="SequentialTranslationPlanner"/>
/// only translates entries that appear in it. Lexer-mode dictionaries therefore need the same
/// sidecar, built from the information the lexer already has: the enclosing ERB function of
/// every visible entry. Grouping into screens happens later; for now the function is the group
/// boundary, which is exactly what the planner packs windows by.
/// </para>
/// </summary>
public static class VisibleTextGrouping
{
    private static readonly ErbTranslationGroupingService Service = new();

    /// <summary>Commands that wait for the player, so they end the visible block before them.</summary>
    private static readonly HashSet<string> InteractionCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "WAIT", "WAITKEY", "TWAIT", "INPUT", "INPUTS", "BINPUT", "CLEARLINE", "PRINTBUTTON",
        "PRINTBUTTONC", "PRINTBUTTONLC", "QUIT", "FORCE_QUIT"
    };

    /// <summary>
    /// Writes <c>AI上下文/&lt;relative path&gt;.groups.json</c> for one dictionary file.
    /// Returns the sidecar path when it was written, otherwise <c>null</c> plus a warning.
    /// </summary>
    public static string? TryWrite(
        string dictionaryDirectory,
        string dictionaryFilePath,
        string sourceFilePath,
        string relativePath,
        string sourceText,
        IReadOnlyList<VisibleTextEntry> entries,
        out string? warning)
    {
        warning = null;
        if (entries.Count == 0)
        {
            return null;
        }

        Dictionary<int, string> functions = BuildFunctionMap(sourceText);
        var sourceEntries = new List<ErbTranslationSourceEntry>(entries.Count);
        for (int index = 0; index < entries.Count; index++)
        {
            VisibleTextEntry entry = entries[index];
            string functionId = ResolveFunction(functions, entry.Line);
            sourceEntries.Add(new ErbTranslationSourceEntry(
                Key: BuildKey(relativePath, index),
                Original: entry.Template,
                Sequence: index,
                ParsedLine: entry.Line,
                FunctionId: functionId,
                OutputType: MapOutputType(entry.Kind, entry.Command),
                BranchPath: string.Empty,
                ControlScopeId: string.Empty,
                BranchLabel: string.Empty,
                CallTarget: null));
        }

        IReadOnlyList<ErbTranslationControlMarker> markers = BuildControlMarkers(functions, sourceText);
        string fingerprint;
        try
        {
            fingerprint = ErbTranslationGroupingService.ComputeSourceFingerprint(sourceFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warning = $"未生成 ERB 分组文件，无法读取源文件：{sourceFilePath} - {ex.Message}";
            return null;
        }

        bool written = Service.TryWriteOrUpdate(
            dictionaryDirectory,
            dictionaryFilePath,
            dictionaryFilePath,
            sourceFilePath,
            fingerprint,
            relativePath,
            sourceEntries,
            markers,
            out string sidecarPath,
            out warning);
        return written ? sidecarPath : null;
    }

    /// <summary>Dictionary keys must match what <see cref="VisibleTextExtractor"/> wrote.</summary>
    internal static string BuildKey(string relativePath, int index) =>
        VisibleTextExtractor.BuildKey(relativePath, index);

    /// <summary>Maps 1-based line numbers to the ERB function (<c>@NAME</c>) that contains them.</summary>
    internal static Dictionary<int, string> BuildFunctionMap(string text)
    {
        var map = new Dictionary<int, string>();
        string current = "(global)";
        int lineNumber = 0;
        int start = 0;
        while (start <= text.Length)
        {
            int newline = text.IndexOf('\n', start);
            string line = newline < 0 ? text[start..] : text[start..newline];
            lineNumber++;
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith('@') && trimmed.Length > 1)
            {
                string name = ReadFunctionName(trimmed);
                if (name.Length > 0)
                {
                    current = name;
                }
            }

            map[lineNumber] = current;
            if (newline < 0)
            {
                break;
            }

            start = newline + 1;
        }

        return map;
    }

    private static string ReadFunctionName(string trimmedLine)
    {
        int index = 1;
        while (index < trimmedLine.Length &&
               !char.IsWhiteSpace(trimmedLine[index]) &&
               trimmedLine[index] != '(' &&
               trimmedLine[index] != ',' &&
               trimmedLine[index] != ';')
        {
            index++;
        }

        return trimmedLine[1..index];
    }

    private static string ResolveFunction(Dictionary<int, string> functions, int line) =>
        functions.TryGetValue(line, out string? functionId) ? functionId : "(global)";

    private static IReadOnlyList<ErbTranslationControlMarker> BuildControlMarkers(
        Dictionary<int, string> functions,
        string text)
    {
        var markers = new List<ErbTranslationControlMarker>();
        int lineNumber = 0;
        int start = 0;
        while (start <= text.Length)
        {
            int newline = text.IndexOf('\n', start);
            string line = newline < 0 ? text[start..] : text[start..newline];
            lineNumber++;
            string trimmed = line.TrimStart();
            string command = ReadCommand(trimmed);
            if (InteractionCommands.Contains(command))
            {
                markers.Add(new ErbTranslationControlMarker(
                    lineNumber,
                    ResolveFunction(functions, lineNumber),
                    string.Empty,
                    command.ToUpperInvariant()));
            }

            if (newline < 0)
            {
                break;
            }

            start = newline + 1;
        }

        return markers;
    }

    private static string ReadCommand(string trimmedLine)
    {
        int index = 0;
        while (index < trimmedLine.Length && !char.IsWhiteSpace(trimmedLine[index]))
        {
            index++;
        }

        return trimmedLine[..index];
    }

    /// <summary>
    /// Output types the grouping service recognises as visible text. <c>PRINT*</c> and the
    /// explicit names below are what <c>IsVisibleTextOutput</c> accepts; everything else would
    /// only be joined on the same line.
    /// </summary>
    private static string MapOutputType(string kind, string command) => kind switch
    {
        "data" => "DATA",
        "button" => "PRINTBUTTON",
        "html" => "HTML_PRINT",
        "custom" => command.Equals("REUSELASTLINE", StringComparison.OrdinalIgnoreCase)
            ? "REUSELASTLINE"
            : command.Equals("PUTFORM", StringComparison.OrdinalIgnoreCase)
                ? "PUTFORM"
                : "PRINT",
        "var" => "PRINT",
        _ => "PRINT"
    };
}
