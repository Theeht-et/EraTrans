namespace EraTrans.Core;

/// <summary>A dictionary entry as seen by the window planner.</summary>
public sealed record WindowSourceLine(int Id, string Key, string Original, bool IsTarget);

/// <summary>One line inside a translation window.</summary>
public sealed record WindowLine(
    int Id,
    string Key,
    string Text,
    string FunctionId,
    bool Translate,
    bool Reference,
    int Join,
    bool InlineCondition,
    string? AltGroup,
    string? Command = null,
    string? Kind = null,
    string? SceneId = null,
    bool Menu = false);

/// <summary>A consecutive run of source lines translated in one request.</summary>
public sealed record TranslationWindow(
    string FunctionId,
    int Part,
    IReadOnlyList<WindowLine> Lines,
    string? SceneId = null,
    string? SceneType = null)
{
    public int CharacterCount => Lines.Sum(line => line.Text.Length + 24);

    public int TargetCount => Lines.Count(line => line.Translate);
}

public sealed record WindowPlan(
    IReadOnlyList<TranslationWindow> Windows,
    int FunctionCount,
    int SplitFunctionCount,
    int JoinedLineCount,
    int InlineConditionCount,
    int AlternativeLineCount)
{
    public int TargetLineCount => Windows.Sum(window => window.TargetCount);
}

/// <summary>
/// Turns the ordered dictionary of one ERB file into translation windows.
///
/// The unit of packing is the whole ERB function: a window boundary may only
/// fall between two functions, so a function is never cut in half just to fill a
/// window. Only a function that exceeds the limits on its own is split, and that
/// split prefers branch boundaries so a dialogue branch stays in one piece.
/// </summary>
public sealed class SequentialTranslationPlanner
{
    public const int DefaultMaxCharacters = 3000;
    public const int DefaultMaxLines = 60;
    public const int DefaultOverlapLines = 2;

    /// <summary>Commands that render without a line break, so the next line continues the same sentence.</summary>
    private static readonly HashSet<string> NoNewlineCommands =
        new(StringComparer.OrdinalIgnoreCase) { "PRINTFORM", "PRINTFORMS", "PRINTFORMC" };

    private const char IdeographicSpace = '\u3000';

    private const string SentenceEnd = "。！？!?…‥」』）)\uff3d";

    public WindowPlan Build(
        IReadOnlyList<WindowSourceLine> lines,
        ErbTranslationGroupingIndex index,
        int maxCharacters = DefaultMaxCharacters,
        int maxLines = DefaultMaxLines,
        int overlapLines = DefaultOverlapLines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLines);
        ArgumentOutOfRangeException.ThrowIfNegative(overlapLines);

        var links = index.Entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Key))
            .GroupBy(entry => entry.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var groupTypeByKey = new Dictionary<string, string>(StringComparer.Ordinal);
        var menuKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (ErbTranslationGroup group in index.Groups)
        {
            foreach (string entryKey in group.EntryKeys)
            {
                groupTypeByKey[entryKey] = group.Type;
                if (string.Equals(group.Type, "menu", StringComparison.Ordinal))
                    menuKeys.Add(entryKey);
            }
        }

        var sources = new List<SourceItem>();
        foreach (WindowSourceLine line in lines)
        {
            if (string.IsNullOrWhiteSpace(line.Original))
                continue;
            if (!links.TryGetValue(line.Key, out ErbTranslationGroupEntry? link))
                continue;
            sources.Add(new SourceItem(line, link));
        }

        if (sources.Count == 0)
            return new WindowPlan([], 0, 0, 0, 0, 0);

        bool screenPacking = string.Equals(index.Packing, "screen", StringComparison.OrdinalIgnoreCase);
        List<List<SourceItem>> runs = BuildFunctionRuns(sources, screenPacking);
        Dictionary<string, int> joinByKey = BuildJoinSignals(sources);
        Dictionary<string, string> altByKey = BuildAltGroups(runs);

        // 1. Split every function that cannot fit into a window on its own.
        var units = new List<Unit>();
        int splitFunctions = 0;
        foreach (List<SourceItem> run in runs)
        {
            List<List<SourceItem>> chunks = SplitRun(run, maxCharacters, maxLines);
            if (chunks.Count > 1)
                splitFunctions++;

            for (int chunkIndex = 0; chunkIndex < chunks.Count; chunkIndex++)
            {
                List<SourceItem> chunk = chunks[chunkIndex];
                var windowLines = chunk
                    .Select(item => CreateLine(item, joinByKey, altByKey, menuKeys))
                    .ToList();
                string? sceneId = SharedScope(chunk);
                string? sceneType = sceneId is not null &&
                                    groupTypeByKey.TryGetValue(chunk[0].Line.Key, out string? type)
                    ? type
                    : null;
                units.Add(new Unit(
                    run[0].Link.FunctionId,
                    chunkIndex + 1,
                    windowLines,
                    IsSplitPart: chunks.Count > 1,
                    SceneId: sceneId,
                    SceneType: sceneType));
            }
        }

        // 2. Pack whole functions into windows; split parts always own their window.
        var specs = new List<(string FunctionId, int Part, List<WindowLine> Lines, string? SceneId, string? SceneType)>();
        string? currentSceneId = null;
        string? currentSceneType = null;
        var currentLines = new List<WindowLine>();
        string currentFunctionId = "";
        int currentPart = 1;
        int currentCharacters = 0;

        void Flush()
        {
            if (currentLines.Count == 0)
                return;
            specs.Add((currentFunctionId, currentPart, currentLines.ToArray().ToList(), currentSceneId, currentSceneType));
            currentLines.Clear();
            currentCharacters = 0;
            currentFunctionId = "";
            currentPart = 1;
            currentSceneId = null;
            currentSceneType = null;
        }

        foreach (Unit unit in units)
        {
            int unitCharacters = unit.Lines.Sum(line => line.Text.Length + 24);
            bool fits = currentLines.Count > 0 &&
                        currentCharacters + unitCharacters <= maxCharacters &&
                        currentLines.Count + unit.Lines.Count <= maxLines;

            if (!fits)
                Flush();

            // A window keeps the scene label only while it holds a single packed unit.
            if (currentLines.Count == 0)
            {
                currentSceneId = unit.SceneId;
                currentSceneType = unit.SceneType;
            }
            else
            {
                currentSceneId = null;
                currentSceneType = null;
            }

            currentLines.AddRange(unit.Lines);
            currentCharacters += unitCharacters;
            if (currentFunctionId.Length == 0)
                currentFunctionId = unit.FunctionId;

            if (unit.IsSplitPart)
            {
                currentPart = unit.Part;
                Flush();
            }
        }

        Flush();

        // 3. Prepend the previous window's tail so a split function stays readable.
        var windows = new List<TranslationWindow>();
        for (int index2 = 0; index2 < specs.Count; index2++)
        {
            (string functionId, int part, List<WindowLine> linesOfWindow, string? sceneId, string? sceneType) = specs[index2];
            var finalLines = new List<WindowLine>(linesOfWindow);

            if (index2 > 0 && overlapLines > 0 && part > 1)
            {
                (string previousFunction, _, List<WindowLine> previousLines, _, _) = specs[index2 - 1];
                if (string.Equals(previousFunction, functionId, StringComparison.Ordinal))
                {
                    WindowLine[] tail = previousLines.TakeLast(overlapLines).ToArray();
                    for (int tailIndex = tail.Length - 1; tailIndex >= 0; tailIndex--)
                    {
                        finalLines.Insert(0, tail[tailIndex] with
                        {
                            Translate = false,
                            Reference = true,
                            Join = 0
                        });
                    }
                }
            }

            windows.Add(new TranslationWindow(functionId, part, finalLines, sceneId, sceneType));
        }

        WindowLine[] allLines = windows.SelectMany(window => window.Lines).ToArray();
        return new WindowPlan(
            windows,
            runs.Count,
            splitFunctions,
            allLines.Count(line => line.Join == 1),
            allLines.Count(line => line.InlineCondition),
            allLines.Count(line => line.AltGroup is not null));
    }

    private static List<List<SourceItem>> BuildFunctionRuns(List<SourceItem> sources, bool screenPacking)
    {
        var runs = new List<List<SourceItem>>();
        foreach (SourceItem item in sources)
        {
            bool startsRun = runs.Count == 0;
            if (!startsRun)
            {
                SourceItem previous = runs[^1][^1];
                startsRun = !string.Equals(previous.Link.FunctionId, item.Link.FunctionId, StringComparison.Ordinal);
                if (!startsRun && screenPacking && !string.IsNullOrWhiteSpace(item.Link.ControlScopeId))
                {
                    // One screen is one packing unit: text shown together stays together.
                    startsRun = !string.Equals(
                        previous.Link.ControlScopeId,
                        item.Link.ControlScopeId,
                        StringComparison.Ordinal);
                }
            }

            if (startsRun)
            {
                runs.Add([]);
            }

            runs[^1].Add(item);
        }

        return runs;
    }

    /// <summary>
    /// join = 1 when the previous command renders without a line break (the text is
    /// literally the same rendered line). join = 2 for a likely continuation: an
    /// ideographic-space indent, an unclosed quote, or a previous line with no
    /// sentence-final punctuation.
    /// </summary>
    private static bool IsNoNewlineCommand(string? command) =>
        !string.IsNullOrWhiteSpace(command) && NoNewlineCommands.Contains(command);

    private static string? SharedScope(List<SourceItem> chunk)
    {
        string? scope = null;
        foreach (SourceItem item in chunk)
        {
            if (string.IsNullOrWhiteSpace(item.Link.ControlScopeId))
                return null;
            if (scope is null)
                scope = item.Link.ControlScopeId;
            else if (!string.Equals(scope, item.Link.ControlScopeId, StringComparison.Ordinal))
                return null;
        }

        return scope;
    }

    private static Dictionary<string, int> BuildJoinSignals(List<SourceItem> sources)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int index = 0; index < sources.Count; index++)
        {
            SourceItem current = sources[index];
            result[current.Line.Key] = 0;

            if (index == 0)
                continue;

            SourceItem previous = sources[index - 1];
            if (!string.Equals(previous.Link.FunctionId, current.Link.FunctionId, StringComparison.Ordinal))
                continue;

            if (IsNoNewlineCommand(previous.Link.Command) ||
                IsNoNewlineCommand(previous.Link.OutputType))
            {
                result[current.Line.Key] = 1;
                continue;
            }

            if (current.Line.Original.StartsWith(IdeographicSpace) ||
                HasUnclosedQuote(previous.Line.Original) ||
                !EndsSentence(previous.Line.Original))
            {
                result[current.Line.Key] = 2;
            }
        }

        return result;
    }

    /// <summary>
    /// Lines that belong to sibling branches of the same switch are mutually
    /// exclusive: they must not be read as one continuous dialogue, but they should
    /// share terminology.
    /// </summary>
    private static Dictionary<string, string> BuildAltGroups(List<List<SourceItem>> runs)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        int nextId = 0;

        foreach (List<SourceItem> run in runs)
        {
            var scopes = run
                .Where(item => !string.IsNullOrWhiteSpace(item.Link.BranchLabel))
                .GroupBy(item =>
                    item.Link.ControlScopeId + "|" + ParentBranchPath(item.Link.BranchPath),
                    StringComparer.Ordinal);

            foreach (var scope in scopes)
            {
                string[] labels = scope
                    .Select(item => item.Link.BranchLabel)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (labels.Length < 2)
                    continue;

                string groupId = "alt_" + (++nextId).ToString("D3");
                foreach (SourceItem item in scope)
                    result[item.Line.Key] = groupId;
            }
        }

        return result;
    }

    private static string ParentBranchPath(string branchPath)
    {
        int separator = branchPath.LastIndexOf(':');
        return separator > 0 ? branchPath[..separator] : branchPath;
    }

    /// <summary>
    /// Cuts an oversized function. A cut is placed on the last branch boundary that
    /// keeps at least half of the accumulated chunk, so neither a dialogue line nor
    /// a whole branch is left dangling.
    /// </summary>
    private static List<List<SourceItem>> SplitRun(
        List<SourceItem> run,
        int maxCharacters,
        int maxLines)
    {
        var chunks = new List<List<SourceItem>>();
        var chunk = new List<SourceItem>();
        int characters = 0;
        int lastBoundary = -1;

        foreach (SourceItem item in run)
        {
            int cost = item.Line.Original.Length + 24;
            bool overflow = chunk.Count > 0 &&
                            (characters + cost > maxCharacters || chunk.Count >= maxLines);

            if (overflow)
            {
                int cut = lastBoundary >= chunk.Count / 2 ? lastBoundary : chunk.Count;
                chunks.Add(chunk.Take(cut).ToList());
                var rest = chunk.Skip(cut).ToList();
                chunk = rest;
                characters = rest.Sum(entry => entry.Line.Original.Length + 24);
                lastBoundary = -1;
            }

            bool startsBranch = chunk.Count > 0 &&
                                !string.Equals(
                                    chunk[^1].Link.BranchPath,
                                    item.Link.BranchPath,
                                    StringComparison.Ordinal);
            if (startsBranch)
                lastBoundary = chunk.Count;

            chunk.Add(item);
            characters += cost;
        }

        if (chunk.Count > 0)
            chunks.Add(chunk);

        return chunks;
    }

    private static WindowLine CreateLine(
        SourceItem item,
        IReadOnlyDictionary<string, int> joinByKey,
        IReadOnlyDictionary<string, string> altByKey,
        IReadOnlySet<string> menuKeys) =>
        new(
            item.Line.Id,
            item.Line.Key,
            item.Line.Original,
            item.Link.FunctionId,
            item.Line.IsTarget,
            Reference: false,
            joinByKey.TryGetValue(item.Line.Key, out int join) ? join : 0,
            item.Line.Original.Contains("\\@", StringComparison.Ordinal),
            altByKey.TryGetValue(item.Line.Key, out string? alt) ? alt : null,
            item.Link.Command,
            item.Link.Kind,
            string.IsNullOrWhiteSpace(item.Link.ControlScopeId) ? null : item.Link.ControlScopeId,
            menuKeys.Contains(item.Line.Key));

    private static bool HasUnclosedQuote(string text)
    {
        int open = 0;
        int close = 0;
        foreach (char character in text)
        {
            if (character == '「')
                open++;
            else if (character == '」')
                close++;
        }

        return open > close;
    }

    private static bool EndsSentence(string text)
    {
        for (int index = text.Length - 1; index >= 0; index--)
        {
            char character = text[index];
            if (char.IsWhiteSpace(character) || character == IdeographicSpace)
                continue;
            return SentenceEnd.IndexOf(character) >= 0;
        }

        return false;
    }

    private sealed record SourceItem(WindowSourceLine Line, ErbTranslationGroupEntry Link);

    private sealed record Unit(
        string FunctionId,
        int Part,
        List<WindowLine> Lines,
        bool IsSplitPart,
        string? SceneId,
        string? SceneType);
}
