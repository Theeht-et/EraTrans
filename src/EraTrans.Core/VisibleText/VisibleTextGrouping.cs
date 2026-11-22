namespace EraTrans.Core.VisibleText;

/// <summary>
/// Derives the ERB AI grouping sidecar (<c>AI上下文/*.groups.json</c>) for a dictionary
/// produced by <see cref="VisibleTextExtractor"/>.
/// <para>
/// The legacy extractor writes that sidecar itself, and the translation window planner only
/// translates entries that appear in it. Lexer-mode dictionaries therefore need the same
/// sidecar, built from the information the lexer already has: the function, branch and screen
/// of every visible entry. Screens (see <see cref="VisibleTextScreenMap"/>) are the packing
/// unit, so all text the player reads at once reaches the model together.
/// </para>
/// </summary>
public static class VisibleTextGrouping
{
    /// <summary>Commands that wait for the player, so the next text belongs to a new screen.</summary>
    private static readonly HashSet<string> InteractionCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "WAIT", "WAITKEY", "TWAIT", "INPUT", "INPUTS", "BINPUT", "CLEARLINE", "PRINTBUTTON",
        "PRINTBUTTONC", "PRINTBUTTONLC", "QUIT", "FORCE_QUIT"
    };

    private static readonly ErbTranslationGroupingService Service = new();

    /// <summary>Marker written into the sidecar so the planner packs by screen instead of by function.</summary>
    public const string ScreenPacking = "screen";

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

        Dictionary<int, ScreenLayout> layout = VisibleTextScreenMap.Build(sourceText);
        var sourceEntries = new List<ErbTranslationSourceEntry>(entries.Count);
        for (int index = 0; index < entries.Count; index++)
        {
            VisibleTextEntry entry = entries[index];
            ScreenLayout position = VisibleTextScreenMap.Resolve(layout, entry.Line);
            sourceEntries.Add(new ErbTranslationSourceEntry(
                Key: BuildKey(relativePath, index),
                Original: entry.Template,
                Sequence: index,
                ParsedLine: entry.Line,
                FunctionId: position.FunctionId,
                OutputType: MapOutputType(entry.Kind, entry.Command),
                BranchPath: position.BranchPath,
                ControlScopeId: position.ScreenId,
                BranchLabel: position.BranchLabel,
                CallTarget: null));
        }

        IReadOnlyList<ErbTranslationControlMarker> markers = BuildControlMarkers(layout, sourceText);
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
            out warning,
            packing: ScreenPacking);
        return written ? sidecarPath : null;
    }

    /// <summary>Dictionary keys must match what <see cref="VisibleTextExtractor"/> wrote.</summary>
    internal static string BuildKey(string relativePath, int index) =>
        VisibleTextExtractor.BuildKey(relativePath, index);

    private static IReadOnlyList<ErbTranslationControlMarker> BuildControlMarkers(
        Dictionary<int, ScreenLayout> layout,
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
                ScreenLayout position = VisibleTextScreenMap.Resolve(layout, lineNumber);
                markers.Add(new ErbTranslationControlMarker(
                    lineNumber,
                    position.FunctionId,
                    position.BranchPath,
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
        _ => "PRINT"
    };
}
