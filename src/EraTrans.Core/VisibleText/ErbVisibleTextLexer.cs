using System.Text;

namespace EraTrans.Core.VisibleText;

/// <summary>
/// Line-oriented lexer for Emuera ERB scripts that reports every literal the game can put on
/// screen: print/printform arguments, DATA/DATAFORM rows, buttons, HTML text nodes and
/// string variables that are assigned literal text and later printed.
/// <para>
/// The rules are a behavior-preserving port of the verified prototype
/// (<c>work/proto4/Program.cs</c>), including its Emuera preprocessor handling
/// (<c>[SKIPSTART]</c>, <c>[IF_DEBUG]</c>, <c>[IF_NDEBUG]</c>, <c>[IF 宏]</c>) and its
/// <c>LOCALS / RESULTS / TSTR / STR</c> data-flow tracking. Do not "simplify" it:
/// the entry counts on the two reference games were validated against the prototype.
/// </para>
/// <para>
/// The lexer never executes the game and never needs the Emuera engine. It works purely on
/// the already decoded text of one file, so encoding detection stays with
/// <see cref="EraTrans.Core.WriteBack.GameFileText"/>.
/// </para>
/// </summary>
public static class ErbVisibleTextLexer
{
    /// <summary>
    /// Print function codes registered in Emuera (<c>FunctionIdentifier.addPrintFunction</c>).
    /// Codes outside this set are not treated as visible output.
    /// </summary>
    private static readonly HashSet<string> PrintCodes = new(StringComparer.Ordinal)
    {
        "PRINT", "PRINTC", "PRINTCD", "PRINTCK", "PRINTD", "PRINTDL", "PRINTDW",
        "PRINTFORM", "PRINTFORMC", "PRINTFORMCD", "PRINTFORMCK", "PRINTFORMD", "PRINTFORMDL", "PRINTFORMDW",
        "PRINTFORMK", "PRINTFORMKL", "PRINTFORMKW", "PRINTFORML", "PRINTFORMLC", "PRINTFORMLCD", "PRINTFORMLCK",
        "PRINTFORMN", "PRINTFORMS", "PRINTFORMSD", "PRINTFORMSDL", "PRINTFORMSDW", "PRINTFORMSK", "PRINTFORMSKL",
        "PRINTFORMSKW", "PRINTFORMSL", "PRINTFORMSN", "PRINTFORMSW", "PRINTFORMW",
        "PRINTK", "PRINTKL", "PRINTKW", "PRINTL", "PRINTLC", "PRINTLCD", "PRINTLCK", "PRINTN",
        "PRINTS", "PRINTSD", "PRINTSDL", "PRINTSDW",
        "PRINTSINGLE", "PRINTSINGLED", "PRINTSINGLEFORM", "PRINTSINGLEFORMD", "PRINTSINGLEFORMK",
        "PRINTSINGLEFORMS", "PRINTSINGLEFORMSD", "PRINTSINGLEFORMSK", "PRINTSINGLEK", "PRINTSINGLES",
        "PRINTSINGLESD", "PRINTSINGLESK", "PRINTSINGLEV", "PRINTSINGLEVD", "PRINTSINGLEVK",
        "PRINTSK", "PRINTSKL", "PRINTSKW", "PRINTSL", "PRINTSN", "PRINTSW",
        "PRINTV", "PRINTVD", "PRINTVDL", "PRINTVDW", "PRINTVK", "PRINTVKL", "PRINTVKW", "PRINTVL",
        "PRINTVN", "PRINTVW", "PRINTW"
    };

    private static readonly HashSet<string> DataOpen = new(StringComparer.Ordinal)
    {
        "PRINTDATA", "PRINTDATAL", "PRINTDATAW", "PRINTDATAK", "PRINTDATAKL", "PRINTDATAKW",
        "PRINTDATAD", "PRINTDATADL", "PRINTDATADW", "DATALIST", "STRDATA"
    };

    private static readonly HashSet<string> DataClose = new(StringComparer.Ordinal)
    {
        "ENDDATA", "ENDLIST"
    };

    /// <summary>
    /// Extracts the visible text of one ERB file.
    /// </summary>
    /// <param name="relativePath">
    /// Game-relative path of the file with forward slashes, e.g.
    /// <c>ERB/コマンド/COMF400_移動.erb</c>. Used as the entry path and for nothing else.
    /// </param>
    /// <param name="text">Whole decoded file text (the value of <c>GameFileText.Text</c>).</param>
    /// <param name="macros">Names defined by <c>#DEFINE</c> anywhere in the game; used by <c>[IF 宏]</c>.</param>
    public static VisibleTextFileResult ExtractFile(
        string relativePath,
        string text,
        IReadOnlyCollection<string> macros,
        bool extractHtmlTitle = true,
        bool extractPrintDataEntries = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentNullException.ThrowIfNull(text);

        string normalizedRelativePath = relativePath.Replace('\\', '/');
        var macroSet = macros is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(macros, StringComparer.OrdinalIgnoreCase);

        var lines = SplitLines(text);
        var result = new List<VisibleTextEntry>();
        var warnings = new List<string>();
        var scope = new Dictionary<string, Assignment>(StringComparer.Ordinal);
        var pp = new PpState(macroSet);
        var diagnostics = new VisibleTextDiagnostics();
        var localLabels = CollectLabels(lines);
        var printedVars = new HashSet<string>(StringComparer.Ordinal);
        var literalAssignments = new List<(string Var, string Function, int Line, string Text)>();
        int dataDepth = 0;
        string functionName = string.Empty;

        for (int li = 0; li < lines.Count; li++)
        {
            string line = lines[li].Line;
            int lineStart = lines[li].Start;

            string trimmedLine = line.Trim();

            // Emuera line continuation (EraStreamReader): only a line that is exactly "{" opens the
            // block and only a line that is exactly "}" closes it. Anything else is ordinary text,
            // e.g. ASCII art that happens to end with a brace. The block is one logical line whose
            // parts are joined with a single space, so it is lexed as such and every emitted span is
            // then mapped back to the physical line it came from.
            if (string.Equals(trimmedLine, "{", StringComparison.Ordinal))
            {
                var joined = new StringBuilder();
                var segments = new List<(int MergedStart, int MergedEnd, int SourceStart)>();
                int blockLine = li;
                li++;
                bool closed = false;
                while (li < lines.Count)
                {
                    string inner = lines[li].Line;
                    if (string.Equals(inner.Trim(), "}", StringComparison.Ordinal))
                    {
                        closed = true;
                        break;
                    }

                    if (joined.Length > 0)
                    {
                        joined.Append(' ');
                    }

                    int mergedStart = joined.Length;
                    joined.Append(inner);
                    segments.Add((mergedStart, joined.Length, lines[li].Start));
                    li++;
                }

                if (closed && joined.Length > 0 && segments.Count > 0)
                {
                    VisibleTextFileResult nested = ExtractFile(relativePath, joined.ToString(), macros, extractHtmlTitle, extractPrintDataEntries);
                    warnings.AddRange(nested.Warnings);
                    diagnostics.MergeFrom(nested.Diagnostics);
                    foreach (VisibleTextEntry entry in nested.Entries)
                    {
                        foreach ((int mergedStart, int mergedEnd, int sourceStart) in segments)
                        {
                            int lo = Math.Max(entry.Span.Start, mergedStart);
                            int hi = Math.Min(entry.Span.End, mergedEnd);
                            if (hi <= lo)
                            {
                                continue;
                            }

                            int rawStart = sourceStart + (lo - mergedStart);
                            int rawEnd = sourceStart + (hi - mergedStart);
                            string raw = text.Substring(rawStart, rawEnd - rawStart);
                            if (!IsWorth(raw.Trim()))
                            {
                                continue;
                            }

                            int s = rawStart + (raw.Length - raw.TrimStart().Length);
                            int e = rawEnd - (raw.Length - raw.TrimEnd().Length);
                            string slice = text.Substring(s, e - s);
                            result.Add(new VisibleTextEntry(
                                normalizedRelativePath,
                                blockLine + 1,
                                entry.Command,
                                entry.Kind,
                                slice,
                                new[] { new VisibleTextSpan(s, e) },
                                new VisibleTextSpan(s, e)));
                        }
                    }
                }

                continue;
            }

            int p = 0;
            while (p < line.Length && char.IsWhiteSpace(line[p])) p++;
            if (p >= line.Length)
            {
                continue;
            }

            // Emuera preprocessor: [SKIPSTART]/[SKIPEND]/[IF_DEBUG]/[IF_NDEBUG]/[IF macro]/[ELSEIF]/[ELSE]/[ENDIF]
            if (IsPreprocessorLine(line, p))
            {
                int tokenEnd = p + 1;
                while (tokenEnd < line.Length && (char.IsLetterOrDigit(line[tokenEnd]) || line[tokenEnd] == '_')) tokenEnd++;
                string ppToken = line.Substring(p + 1, tokenEnd - p - 1);
                int argStart = tokenEnd;
                while (argStart < line.Length && char.IsWhiteSpace(line[argStart])) argStart++;
                int argEnd = argStart;
                while (argEnd < line.Length && line[argEnd] != ' ' && line[argEnd] != '\t' && line[argEnd] != ']') argEnd++;
                string ppArg = argEnd > argStart ? line.Substring(argStart, argEnd - argStart) : string.Empty;
                if (ppToken.Length > 0)
                {
                    pp.AddKeyWord(ppToken, ppArg);
                }

                continue;
            }

            if (pp.Disabled)
            {
                continue;
            }

            char first = line[p];
            if (first == ';' || first == '$' || first == '#')
            {
                continue;
            }

            if (first == '@')
            {
                scope.Clear();
                dataDepth = 0;
                functionName = ParseFunctionName(line, p);
                continue;
            }

            int tokenEnd2 = p;
            while (tokenEnd2 < line.Length && !IsSeparator(line[tokenEnd2])) tokenEnd2++;
            string token = line.Substring(p, tokenEnd2 - p).ToUpperInvariant();
            if (token.Length == 0)
            {
                continue;
            }

            if (DataClose.Contains(token))
            {
                if (dataDepth > 0)
                {
                    dataDepth--;
                }

                continue;
            }

            if (DataOpen.Contains(token))
            {
                dataDepth++;
                continue;
            }

            if (dataDepth > 0 && (token == "DATA" || token == "DATAFORM"))
            {
                if (extractPrintDataEntries)
                {
                    DraftEntry? dataEntry = BuildLineEntry(
                        li, lineStart, functionName, line, tokenEnd2,
                        token == "DATAFORM" ? "form" : "plain", token);
                    if (dataEntry is not null)
                    {
                        dataEntry.Kind = "data";
                        result.Add(Materialize(dataEntry, normalizedRelativePath));
                    }
                }

                continue;
            }

            // Assignment? VAR = ... / VAR += ...
            if (TryAssignment(token, line, tokenEnd2, out string varName, out string assignOp, out int rhsStart))
            {
                var assignment = new Assignment
                {
                    Var = varName,
                    Line = li + 1,
                    LineStart = lineStart,
                    FunctionName = functionName
                };

                if (assignOp == "=" && rhsStart < line.Length && line[rhsStart] == '%')
                {
                    int close = SkipPercent(line, rhsStart, line.Length);
                    string inner = line.Substring(rhsStart + 1, Math.Max(0, close - rhsStart - 2)).Trim();
                    if (RegVar(inner) is string alias && alias.Length > 0 && inner.IndexOf(',') < 0)
                    {
                        assignment.AliasVar = alias;
                        scope[varName] = assignment;
                        continue;
                    }
                }

                var (parts, template, span) = BuildParts(
                    line, rhsStart, line.Length, ContainsForm(line.Substring(rhsStart)) ? "form" : "plain");
                if (parts.Count > 0 && IsWorth(template))
                {
                    assignment.Parts = parts;
                    assignment.Span = span;
                    assignment.Template = template;
                    scope[varName] = assignment;
                }
                else
                {
                    assignment.Parts = null;
                    assignment.Template = null;
                    scope[varName] = assignment;
                }

                continue;
            }

            // Arbitrary variable assigned a Japanese literal (e.g. "CITY_NAME = 東京"). The lexer
            // cannot know whether it will be displayed, so it only feeds the coverage report:
            // when the same function later prints that variable, the text is currently missed.
            if (TryLiteralAssignment(token, line, tokenEnd2, out string literalVar, out int literalStart))
            {
                string literal = SliceArgument(line, literalStart);
                if (literal.Length >= 2 && literal[0] == '"' && literal[^1] == '"')
                {
                    literal = literal.Substring(1, literal.Length - 2);
                }

                if (literal.Length >= 2 &&
                    literal.IndexOf('%') < 0 &&
                    literal.IndexOf('(') < 0 &&
                    HasJapanese(literal))
                {
                    literalAssignments.Add((literalVar, functionName, li + 1, literal));
                }

                continue;
            }

            if (PrintCodes.Contains(token))
            {
                string cls = Classify(token);
                if (cls == "num")
                {
                    continue;
                }

                int argStart = tokenEnd2;
                if (argStart < line.Length && IsSeparator(line[argStart]) && line[argStart] != ',')
                {
                    argStart++;
                }

                if (cls == "form")
                {
                    AddFormEntries(
                        line, argStart, li, lineStart, functionName, token, normalizedRelativePath, result);
                }
                else
                {
                    int parentIndex = -1;
                    DraftEntry? entry = cls == "str"
                        ? BuildStrExprEntry(li, lineStart, functionName, line, tokenEnd2, token)
                        : BuildLineEntry(li, lineStart, functionName, line, tokenEnd2, "plain", token);
                    if (entry is not null)
                    {
                        entry.Kind = "print";
                        parentIndex = result.Count;
                        result.Add(Materialize(entry, normalizedRelativePath));
                    }

                    if (cls == "str")
                    {
                        AddConcatLiteralEntries(
                            line, argStart, line.Length, li, lineStart, functionName, token, normalizedRelativePath, result);
                    }

                    if (parentIndex >= 0 &&
                        result.Count > parentIndex + 1 &&
                        !CanKeepWholeArgumentTemplate(result[parentIndex].Template))
                    {
                        // The argument is a concatenation of several literals: the whole-argument
                        // template would contain quotes and '+' operators and could not be written
                        // back safely, so only the literal fragments extracted above remain.
                        result.RemoveAt(parentIndex);
                    }
                }

                AddVarRefs(line, scope, result, normalizedRelativePath);
                TrackPrintLine(line, functionName, diagnostics, printedVars);
                continue;
            }

            switch (token)
            {
                case "PRINTBUTTON":
                case "PRINTBUTTONC":
                case "PRINTBUTTONLC":
                {
                    int parentIndex = -1;
                    DraftEntry? entry = BuildButtonEntry(li, lineStart, functionName, line, tokenEnd2, token);
                    if (entry is not null)
                    {
                        parentIndex = result.Count;
                        result.Add(Materialize(entry, normalizedRelativePath));
                    }

                    int buttonStart = tokenEnd2;
                    while (buttonStart < line.Length && char.IsWhiteSpace(line[buttonStart]))
                    {
                        buttonStart++;
                    }

                    int buttonComma = FindTopComma(line, buttonStart, line.Length);
                    AddConcatLiteralEntries(
                        line,
                        buttonStart,
                        buttonComma < 0 ? line.Length : buttonComma,
                        li,
                        lineStart,
                        functionName,
                        token,
                        normalizedRelativePath,
                        result);

                    if (parentIndex >= 0 &&
                        result.Count > parentIndex + 1 &&
                        !CanKeepWholeArgumentTemplate(result[parentIndex].Template))
                    {
                        result.RemoveAt(parentIndex);
                    }

                    AddVarRefs(line, scope, result, normalizedRelativePath);
                    TrackPrintLine(line, functionName, diagnostics, printedVars);
                    break;
                }

                case "HTML_PRINT":
                case "HTML_PRINT_ISLAND":
                {
                    foreach (DraftEntry entry in BuildHtmlEntries(li, lineStart, functionName, line, tokenEnd2, token, extractHtmlTitle))
                    {
                        result.Add(Materialize(entry, normalizedRelativePath));
                    }

                    AddVarRefs(line, scope, result, normalizedRelativePath);
                    TrackPrintLine(line, functionName, diagnostics, printedVars);
                    break;
                }

                case "PRINTPLAIN":
                {
                    DraftEntry? entry = BuildLineEntry(li, lineStart, functionName, line, tokenEnd2, "plain", token);
                    if (entry is not null)
                    {
                        result.Add(Materialize(entry, normalizedRelativePath));
                    }

                    AddVarRefs(line, scope, result, normalizedRelativePath);
                    TrackPrintLine(line, functionName, diagnostics, printedVars);
                    break;
                }

                case "PRINTPLAINFORM":
                case "PUTFORM":
                {
                    DraftEntry? entry = BuildLineEntry(li, lineStart, functionName, line, tokenEnd2, "form", token);
                    if (entry is not null)
                    {
                        result.Add(Materialize(entry, normalizedRelativePath));
                    }

                    AddVarRefs(line, scope, result, normalizedRelativePath);
                    TrackPrintLine(line, functionName, diagnostics, printedVars);
                    break;
                }

                case "CUSTOMDRAWLINE":
                case "REUSELASTLINE":
                {
                    DraftEntry? entry = BuildLineEntry(li, lineStart, functionName, line, tokenEnd2, "plain", "custom");
                    if (entry is not null)
                    {
                        entry.Kind = "custom";
                        result.Add(Materialize(entry, normalizedRelativePath));
                    }

                    AddVarRefs(line, scope, result, normalizedRelativePath);
                    TrackPrintLine(line, functionName, diagnostics, printedVars);
                    break;
                }

                default:
                    TrackUnknownCommand(token, line, tokenEnd2, li + 1, normalizedRelativePath, diagnostics, localLabels);
                    break;
            }
        }

        foreach ((string var, string function, int _, string _) in literalAssignments)
        {
            if (!printedVars.Contains(function + "\u0000" + var))
            {
                continue;
            }

            diagnostics.AssignedAndPrintedLines++;
            diagnostics.AssignedAndPrintedVars[var] =
                diagnostics.AssignedAndPrintedVars.TryGetValue(var, out int assignedCount)
                    ? assignedCount + 1
                    : 1;
        }

        return new VisibleTextFileResult(normalizedRelativePath, result, warnings)
        {
            Diagnostics = diagnostics
        };
    }

    /// <summary>Mirror of Emuera <c>ErbLoader.PPState</c>.</summary>
    private sealed class PpState
    {
        private readonly HashSet<string> _macros;
        private bool _skip;
        private bool _done;
        private readonly Stack<bool> _disabledStack = new();
        private readonly Stack<bool> _doneStack = new();
        private readonly Stack<string> _ppMatch = new();

        public PpState(HashSet<string> macros)
        {
            _macros = macros;
        }

        public bool Disabled { get; private set; }

        public void AddKeyWord(string token, string token2)
        {
            switch (token.ToUpperInvariant())
            {
                case "SKIPSTART":
                    if (_skip)
                    {
                        break;
                    }

                    _ppMatch.Push("SKIPEND");
                    _disabledStack.Push(Disabled);
                    _doneStack.Push(_done);
                    _skip = true;
                    Disabled = true;
                    _done = false;
                    break;

                case "IF_DEBUG": // Program.DebugMode is false in analysis/headless mode
                    _ppMatch.Push("ELSEIF");
                    _disabledStack.Push(Disabled);
                    _doneStack.Push(_done);
                    Disabled = true;
                    _done = false;
                    break;

                case "IF_NDEBUG":
                    _ppMatch.Push("ELSEIF");
                    _disabledStack.Push(Disabled);
                    _doneStack.Push(_done);
                    Disabled = false;
                    _done = true;
                    break;

                case "IF":
                    _ppMatch.Push("ELSEIF");
                    _disabledStack.Push(Disabled);
                    _doneStack.Push(_done);
                    Disabled = token2.Length == 0 || !_macros.Contains(token2);
                    _done = !Disabled;
                    break;

                case "ELSEIF":
                    if (_ppMatch.Count == 0 || _ppMatch.Pop() != "ELSEIF")
                    {
                        break;
                    }

                    _ppMatch.Push("ELSEIF");
                    Disabled = _done || token2.Length == 0 || !_macros.Contains(token2);
                    _done |= !Disabled;
                    break;

                case "ELSE":
                    if (_ppMatch.Count == 0 || _ppMatch.Pop() != "ELSEIF")
                    {
                        break;
                    }

                    _ppMatch.Push("ENDIF");
                    Disabled = _done;
                    _done = true;
                    break;

                case "SKIPEND":
                    if (_ppMatch.Count == 0 || _ppMatch.Pop() != "SKIPEND")
                    {
                        break;
                    }

                    _skip = false;
                    Disabled = _disabledStack.Pop();
                    _done = _doneStack.Pop();
                    break;

                case "ENDIF":
                    if (_ppMatch.Count == 0)
                    {
                        break;
                    }

                    {
                        string match = _ppMatch.Pop();
                        if (match != "ENDIF" && match != "ELSEIF")
                        {
                            break;
                        }

                        Disabled = _disabledStack.Pop();
                        _done = _doneStack.Pop();
                    }
                    break;
            }

            if (_skip)
            {
                Disabled = true;
            }
        }
    }

    private sealed class Assignment
    {
        public string Var = string.Empty;
        public int Line;
        public int LineStart;
        public string FunctionName = string.Empty;
        public List<DraftPart>? Parts;
        public DraftPart Span;
        public string? Template;
        public string? AliasVar;
        public bool Emitted;
    }

    private sealed class DraftEntry
    {
        public int Line;
        public int LineStart;
        public string FunctionName = string.Empty;
        public string Command = string.Empty;
        public string Kind = string.Empty;
        public string Template = string.Empty;
        public List<DraftPart> Parts = new();
        public DraftPart Span;
    }

    /// <summary>A literal fragment with line-local offsets; converted to absolute offsets on materialization.</summary>
    private readonly record struct DraftPart(int Start, int End);

    private static VisibleTextEntry Materialize(DraftEntry draft, string relativePath)
    {
        var parts = new List<VisibleTextSpan>(draft.Parts.Count);
        foreach (DraftPart part in draft.Parts)
        {
            parts.Add(new VisibleTextSpan(draft.LineStart + part.Start, draft.LineStart + part.End));
        }

        DraftPart spanPart = draft.Span;
        if (spanPart.End <= spanPart.Start && draft.Parts.Count == 1)
        {
            spanPart = draft.Parts[0];
        }

        VisibleTextSpan span = spanPart.End > spanPart.Start
            ? new VisibleTextSpan(draft.LineStart + spanPart.Start, draft.LineStart + spanPart.End)
            : new VisibleTextSpan(draft.LineStart, draft.LineStart);

        return new VisibleTextEntry(
            relativePath,
            draft.Line,
            draft.Command,
            draft.Kind,
            draft.Template,
            parts,
            span);
    }

    /// <summary>
    /// Splits a file into lines and remembers the absolute offset every line starts at.
    /// <c>\r\n</c>, <c>\n</c> and a lone <c>\r</c> are all handled; the returned line text never
    /// contains the terminator, and the start offset always points at the first character of
    /// the line in the original file, which is what span calculation relies on.
    /// </summary>
    internal static List<(string Line, int Start)> SplitLines(string text)
    {
        var lines = new List<(string Line, int Start)>();
        int position = 0;
        while (true)
        {
            int newline = text.IndexOf('\n', position);
            int end = newline < 0 ? text.Length : newline;
            int contentEnd = end;
            if (contentEnd > position && text[contentEnd - 1] == '\r')
            {
                contentEnd--;
            }

            lines.Add((text.Substring(position, contentEnd - position), position));
            if (newline < 0)
            {
                break;
            }

            position = newline + 1;
        }

        return lines;
    }

    /// <summary>Function name of an <c>@...</c> line: everything after <c>@</c> up to <c>(</c> or <c>,</c>.</summary>
    internal static string ParseFunctionName(string line, int atIndex)
    {
        string rest = line.Substring(atIndex + 1).Trim();
        int separator = rest.IndexOfAny(['(', ',']);
        if (separator >= 0)
        {
            rest = rest.Substring(0, separator);
        }

        return rest.Trim();
    }

    private static bool IsPreprocessorLine(string line, int p) =>
        line[p] == '[' && !(p + 1 < line.Length && line[p + 1] == '[');

    private static string? RegVar(string inner)
    {
        if (string.IsNullOrEmpty(inner))
        {
            return null;
        }

        int j = 0;
        while (j < inner.Length && (char.IsLetterOrDigit(inner[j]) || inner[j] == '_' || inner[j] == ':'))
        {
            j++;
        }

        if (j == 0)
        {
            return null;
        }

        string value = inner.Substring(0, j);
        if (value.StartsWith("LOCALS", StringComparison.Ordinal) ||
            value == "RESULTS" ||
            value.StartsWith("RESULTS:", StringComparison.Ordinal) ||
            value.StartsWith("TSTR:", StringComparison.Ordinal) ||
            value.StartsWith("STR:", StringComparison.Ordinal))
        {
            return value;
        }

        return null;
    }

    /// <summary>
    /// Identifier prefix of a <c>%...%</c> reference, without the tracked-variable restriction
    /// (used by the coverage report to recognise every variable a print line displays).
    /// </summary>
    private static string? RegAnyVar(string inner)
    {
        if (string.IsNullOrEmpty(inner))
        {
            return null;
        }

        int j = 0;
        while (j < inner.Length && (char.IsLetterOrDigit(inner[j]) || inner[j] == '_' || inner[j] == ':'))
        {
            j++;
        }

        if (j == 0)
        {
            return null;
        }

        string value = inner.Substring(0, j);
        return char.IsLetter(value[0]) || value[0] == '_' ? value : null;
    }

    private static bool TryAssignment(
        string token,
        string line,
        int tokenEnd,
        out string varName,
        out string assignOp,
        out int rhsStart)
    {
        varName = string.Empty;
        assignOp = string.Empty;
        rhsStart = tokenEnd;

        if (!(token.StartsWith("LOCALS", StringComparison.Ordinal) ||
              token == "RESULTS" ||
              token.StartsWith("RESULTS:", StringComparison.Ordinal) ||
              token.StartsWith("TSTR:", StringComparison.Ordinal) ||
              token.StartsWith("STR:", StringComparison.Ordinal)))
        {
            return false;
        }

        int i = tokenEnd;
        while (i < line.Length && char.IsWhiteSpace(line[i]))
        {
            i++;
        }

        if (i < line.Length && line[i] == '+' && i + 1 < line.Length && line[i + 1] == '=')
        {
            assignOp = "+=";
            i += 2;
        }
        else if (i < line.Length && line[i] == '=' && (i + 1 >= line.Length || line[i + 1] != '='))
        {
            assignOp = "=";
            i += 1;
        }
        else
        {
            return false;
        }

        varName = token;
        rhsStart = i;
        return true;
    }

    private static int ArgumentStart(string line)
    {
        int p = 0;
        while (p < line.Length && char.IsWhiteSpace(line[p]))
        {
            p++;
        }

        int tokenEnd = p;
        while (tokenEnd < line.Length && !IsSeparator(line[tokenEnd]))
        {
            tokenEnd++;
        }

        if (tokenEnd < line.Length && IsSeparator(line[tokenEnd]) && line[tokenEnd] != ',')
        {
            tokenEnd++;
        }

        return tokenEnd;
    }

    private static void AddVarRefs(
        string line,
        Dictionary<string, Assignment> scope,
        List<VisibleTextEntry> result,
        string relativePath)
    {
        var refs = new List<string>();
        CollectRefs(line, ArgumentStart(line), line.Length, refs);

        foreach (string raw in refs)
        {
            string reference = raw;
            int comma = reference.IndexOf(',');
            if (comma >= 0)
            {
                reference = reference.Substring(0, comma);
            }

            reference = reference.Trim();
            string? name = RegVar(reference);
            if (name is null)
            {
                continue;
            }

            if (!scope.TryGetValue(name, out Assignment? assignment) ||
                assignment.Emitted ||
                assignment.Template is null)
            {
                continue;
            }

            assignment.Emitted = true;
            var draft = new DraftEntry
            {
                Line = assignment.Line,
                LineStart = assignment.LineStart,
                FunctionName = assignment.FunctionName,
                Command = assignment.Var + " (var)",
                Kind = "var",
                Template = assignment.Template,
                Span = assignment.Span,
                Parts = assignment.Parts is null ? new List<DraftPart>() : new List<DraftPart>(assignment.Parts)
            };
            result.Add(Materialize(draft, relativePath));
        }
    }

    private static void CollectRefs(string line, int start, int end, List<string> refs)
    {
        int i = start;
        while (i < end)
        {
            if (line[i] == '%')
            {
                int close = SkipPercent(line, i, end);
                refs.Add(line.Substring(i + 1, Math.Max(0, close - i - 2)));
                i = close;
            }
            else if (line[i] == '{')
            {
                i = SkipBrace(line, i, end);
            }
            else if (line[i] == '\\' && i + 1 < end && line[i + 1] == '@')
            {
                int close = FindYenAtClose(line, i, end);
                if (close <= i)
                {
                    break;
                }

                CollectRefs(line, i + 2, close, refs);
                i = close + 2;
            }
            else
            {
                i++;
            }
        }
    }

    /// <summary>
    /// <c>PRINTBUTTON "[" + LOCALS + "] 突撃", 100</c>: a quoted literal that is an operand of
    /// <c>+</c> is text the player really sees, but it is not the whole argument, so the
    /// "argument is a single string literal" paths miss it. Each such literal becomes an entry of
    /// its own whose span covers only the characters inside the quotes, so write-back edits the
    /// literal and leaves the surrounding expression (variables, function calls) untouched.
    /// </summary>
    private static void AddConcatLiteralEntries(
        string line,
        int start,
        int end,
        int lineIndex,
        int lineStart,
        string functionName,
        string command,
        string relativePath,
        List<VisibleTextEntry> result)
    {
        int i = start;
        while (i < end)
        {
            if (line[i] != '"')
            {
                i++;
                continue;
            }

            int close = FindQuote(line, i, end);
            if (close < 0)
            {
                return;
            }

            bool concatenated = PreviousNonSpace(line, i, start) == '+' || NextNonSpace(line, close + 1, end) == '+';
            if (concatenated && close - i - 1 >= 1)
            {
                string text = line.Substring(i + 1, close - i - 1);
                if (HasJapanese(text) && IsWorth(text))
                {
                    var span = new VisibleTextSpan(lineStart + i + 1, lineStart + close);
                    if (!HasPartSpan(result, lineIndex + 1, span))
                    {
                        result.Add(new VisibleTextEntry(
                            relativePath,
                            lineIndex + 1,
                            command,
                            "print",
                            text,
                            new List<VisibleTextSpan> { span },
                            span));
                    }
                }
            }

            i = close + 1;
        }
    }

    /// <summary>Same as <see cref="AddConcatLiteralEntries"/>, but only inside <c>%...%</c> regions.</summary>
    /// <summary>
    /// A form argument is the whole printable template, but a quoted literal inside a placeholder
    /// (<c>%GETNAME(1) + "所属"</c>) is still displayed. In that case the argument cannot be one
    /// template (the placeholder is an expression, not text), so the literal segments outside the
    /// placeholders become their own entries and the inner literals are added as fragments too.
    /// </summary>
    private static void AddFormEntries(
        string line,
        int argStart,
        int lineIndex,
        int lineStart,
        string functionName,
        string command,
        string relativePath,
        List<VisibleTextEntry> result)
    {
        DraftEntry? entry = BuildLineEntry(lineIndex, lineStart, functionName, line, argStart, "form", command);
        var fragments = new List<VisibleTextEntry>();
        AddConcatLiteralEntriesInForm(
            line, argStart, line.Length, lineIndex, lineStart, functionName, command, relativePath, fragments);

        if (fragments.Count == 0)
        {
            if (entry is not null)
            {
                result.Add(Materialize(entry, relativePath));
            }

            return;
        }

        var emitted = new List<VisibleTextEntry>(fragments.Count + 2);
        if (entry is not null)
        {
            foreach (DraftPart part in entry.Parts)
            {
                string slice = line.Substring(part.Start, part.End - part.Start);
                if (!IsWorth(slice))
                {
                    continue;
                }

                emitted.Add(Materialize(
                    new DraftEntry
                    {
                        Line = lineIndex + 1,
                        LineStart = lineStart,
                        FunctionName = functionName,
                        Command = command,
                        Kind = "form",
                        Template = slice,
                        Span = part,
                        Parts = { part }
                    },
                    relativePath));
            }
        }

        emitted.AddRange(fragments);
        emitted.Sort((left, right) => left.Span.Start.CompareTo(right.Span.Start));
        result.AddRange(emitted);
    }

    private static void AddConcatLiteralEntriesInForm(
        string line,
        int start,
        int end,
        int lineIndex,
        int lineStart,
        string functionName,
        string command,
        string relativePath,
        List<VisibleTextEntry> result)
    {
        int i = start;
        while (i < end)
        {
            if (line[i] != '%')
            {
                i++;
                continue;
            }

            int close = SkipPercent(line, i, end);
            AddConcatLiteralEntries(
                line,
                i + 1,
                Math.Min(close - 1, end),
                lineIndex,
                lineStart,
                functionName,
                command,
                relativePath,
                result);
            i = close;
        }
    }

    private static bool HasPartSpan(List<VisibleTextEntry> entries, int line, VisibleTextSpan span)
    {
        foreach (VisibleTextEntry entry in entries)
        {
            if (entry.Line != line)
            {
                continue;
            }

            foreach (VisibleTextSpan part in entry.Parts)
            {
                if (part.Start == span.Start && part.End == span.End)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static char PreviousNonSpace(string s, int index, int limit)
    {
        for (int i = index - 1; i >= limit; i--)
        {
            if (!char.IsWhiteSpace(s[i]))
            {
                return s[i];
            }
        }

        return '\0';
    }

    private static char NextNonSpace(string s, int index, int limit)
    {
        for (int i = index; i < limit; i++)
        {
            if (!char.IsWhiteSpace(s[i]))
            {
                return s[i];
            }
        }

        return '\0';
    }

    private static DraftEntry? BuildLineEntry(
        int lineIndex,
        int lineStart,
        string functionName,
        string line,
        int cmdEnd,
        string mode,
        string command)
    {
        int start = cmdEnd;
        if (start < line.Length && IsSeparator(line[start]) && line[start] != ',')
        {
            start++;
        }

        return BuildEntry(lineIndex, lineStart, functionName, line, start, line.Length, mode, command);
    }

    private static DraftEntry? BuildStrExprEntry(
        int lineIndex,
        int lineStart,
        string functionName,
        string line,
        int cmdEnd,
        string command)
    {
        int start = cmdEnd;
        while (start < line.Length && char.IsWhiteSpace(line[start]))
        {
            start++;
        }

        if (start >= line.Length)
        {
            return null;
        }

        if (line[start] == '"')
        {
            int close = FindQuote(line, start, line.Length);
            if (close < 0)
            {
                return null;
            }

            string text = line.Substring(start + 1, close - start - 1);
            if (!IsWorth(text))
            {
                return null;
            }

            return new DraftEntry
            {
                Line = lineIndex + 1,
                LineStart = lineStart,
                FunctionName = functionName,
                Command = command,
                Kind = "plain",
                Template = text,
                Span = new DraftPart(start + 1, close),
                Parts = { new DraftPart(start + 1, close) }
            };
        }

        if (line[start] == '@' && start + 1 < line.Length && line[start + 1] == '"')
        {
            return BuildEntry(
                lineIndex, lineStart, functionName, line, start + 2,
                FindQuoteForm(line, start + 1, line.Length), "form", command);
        }

        return null;
    }

    private static DraftEntry? BuildButtonEntry(
        int lineIndex,
        int lineStart,
        string functionName,
        string line,
        int cmdEnd,
        string command)
    {
        int start = cmdEnd;
        while (start < line.Length && char.IsWhiteSpace(line[start]))
        {
            start++;
        }

        if (start >= line.Length)
        {
            return null;
        }

        int comma = FindTopComma(line, start, line.Length);
        int end = comma < 0 ? line.Length : comma;

        if (line[start] == '"')
        {
            int close = FindQuote(line, start, line.Length);
            if (close < 0 || close > end)
            {
                return null;
            }

            string text = line.Substring(start + 1, close - start - 1);
            if (!IsWorth(text))
            {
                return null;
            }

            return new DraftEntry
            {
                Line = lineIndex + 1,
                LineStart = lineStart,
                FunctionName = functionName,
                Command = command,
                Kind = "button",
                Template = text,
                Span = new DraftPart(start + 1, close),
                Parts = { new DraftPart(start + 1, close) }
            };
        }

        if (line[start] == '@' && start + 1 < line.Length && line[start + 1] == '"')
        {
            DraftEntry? entry = BuildEntry(
                lineIndex, lineStart, functionName, line, start + 2,
                FindQuoteForm(line, start + 1, line.Length), "form", command);
            if (entry is not null)
            {
                entry.Kind = "button";
            }

            return entry;
        }

        if (HasPrefix(line, start, "STRFORM", end))
        {
            int leftParen = line.IndexOf('(', start);
            if (leftParen < 0 || leftParen >= end)
            {
                return null;
            }

            int inner = leftParen + 1;
            while (inner < end && char.IsWhiteSpace(line[inner]))
            {
                inner++;
            }

            if (inner < end && line[inner] == '"')
            {
                int close = FindQuote(line, inner, end);
                if (close < 0)
                {
                    return null;
                }

                string text = line.Substring(inner + 1, close - inner - 1);
                if (!IsWorth(text))
                {
                    return null;
                }

                return new DraftEntry
                {
                    Line = lineIndex + 1,
                    LineStart = lineStart,
                    FunctionName = functionName,
                    Command = command,
                    Kind = "button",
                    Template = text,
                    Span = new DraftPart(inner + 1, close),
                    Parts = { new DraftPart(inner + 1, close) }
                };
            }

            if (inner + 1 < end && line[inner] == '@' && line[inner + 1] == '"')
            {
                DraftEntry? entry = BuildEntry(
                    lineIndex, lineStart, functionName, line, inner + 2,
                    FindQuote(line, inner + 1, end), "form", command);
                if (entry is not null)
                {
                    entry.Kind = "button";
                }

                return entry;
            }
        }

        return null;
    }

    private static List<DraftEntry> BuildHtmlEntries(
        int lineIndex,
        int lineStart,
        string functionName,
        string line,
        int cmdEnd,
        string command,
        bool extractHtmlTitle)
    {
        var list = new List<DraftEntry>();
        int start = cmdEnd;
        while (start < line.Length && char.IsWhiteSpace(line[start]))
        {
            start++;
        }

        if (start >= line.Length)
        {
            return list;
        }

        bool form = false;
        if (line[start] == '@' && start + 1 < line.Length && line[start + 1] == '"')
        {
            form = true;
            start += 1;
        }

        if (line[start] != '"')
        {
            return list;
        }

        int close = form ? FindQuoteForm(line, start, line.Length) : FindQuote(line, start, line.Length);
        if (close < 0)
        {
            return list;
        }

        // What the player sees is the concatenation of the argument's adjacent string literals
        // (HTML_PRINT "<p>" + "text" + "</p>" renders one document), so merge them into a single
        // virtual string and remember where every piece came from.
        var merged = new StringBuilder();
        var segments = new List<(int MergedStart, int MergedEnd, int SourceStart)>();

        void AppendLiteral(int contentStart, int contentEnd)
        {
            if (contentEnd <= contentStart)
            {
                return;
            }

            int m0 = merged.Length;
            merged.Append(line, contentStart, contentEnd - contentStart);
            segments.Add((m0, merged.Length, contentStart));
        }

        AppendLiteral(start + 1, close);
        int nextLiteral = close + 1;
        while (nextLiteral < line.Length)
        {
            int q = nextLiteral;
            while (q < line.Length && char.IsWhiteSpace(line[q]))
            {
                q++;
            }

            if (q >= line.Length || line[q] != '+')
            {
                break;
            }

            q++;
            while (q < line.Length && char.IsWhiteSpace(line[q]))
            {
                q++;
            }

            if (q >= line.Length || line[q] != '"')
            {
                break;
            }

            int pieceEnd = FindQuoteForm(line, q, line.Length);
            if (pieceEnd < 0)
            {
                break;
            }

            AppendLiteral(q + 1, pieceEnd);
            nextLiteral = pieceEnd + 1;
        }

        if (merged.Length == 0)
        {
            return list;
        }

        string markup = merged.ToString();

        // A merged range can cover several source literals: emit one entry per covered piece so
        // every span stays inside a single physical line.
        void EmitMapped(int a, int b)
        {
            if (b <= a)
            {
                return;
            }

            foreach ((int mergedStart, int mergedEnd, int sourceStart) in segments)
            {
                int lo = Math.Max(a, mergedStart);
                int hi = Math.Min(b, mergedEnd);
                if (hi <= lo)
                {
                    continue;
                }

                int rawStart = sourceStart + (lo - mergedStart);
                int rawEnd = sourceStart + (hi - mergedStart);
                string raw = line.Substring(rawStart, rawEnd - rawStart);
                if (!IsWorth(raw.Trim()))
                {
                    continue;
                }

                int s = rawStart + (raw.Length - raw.TrimStart().Length);
                int e = rawEnd - (raw.Length - raw.TrimEnd().Length);
                string text = line.Substring(s, e - s);
                if (!IsWorth(text))
                {
                    continue;
                }

                list.Add(new DraftEntry
                {
                    Line = lineIndex + 1,
                    LineStart = lineStart,
                    FunctionName = functionName,
                    Command = command,
                    Kind = "html",
                    Template = text,
                    Span = new DraftPart(s, e),
                    Parts = { new DraftPart(s, e) }
                });
            }
        }

        void Flush(int a, int b) => EmitMapped(a, b);

        void ScanAttributeText(int valueStart, int valueEnd)
        {
            int a = valueStart;
            int k = valueStart;
            while (k < valueEnd)
            {
                char ac = markup[k];
                if (ac == '<')
                {
                    Flush(a, k);
                    int innerEnd = FindTagEnd(markup, k, valueEnd, form);
                    if (innerEnd < 0)
                    {
                        k = valueEnd;
                        break;
                    }

                    k = innerEnd + 1;
                    a = k;
                }
                else if (form && ac == '%')
                {
                    Flush(a, k);
                    k = SkipPercent(markup, k, valueEnd);
                    a = k;
                }
                else if (form && ac == '{')
                {
                    Flush(a, k);
                    k = SkipBrace(markup, k, valueEnd);
                    a = k;
                }
                else
                {
                    k++;
                }
            }

            Flush(a, valueEnd);
        }

        void EmitTagAttributes(int tagStart, int tagEnd)
        {
            int p = tagStart + 1;
            while (p < tagEnd && !char.IsWhiteSpace(markup[p]))
            {
                p++;
            }

            while (p < tagEnd)
            {
                while (p < tagEnd && char.IsWhiteSpace(markup[p]))
                {
                    p++;
                }

                int nameStart = p;
                while (p < tagEnd && markup[p] != '=' && markup[p] != '\'' && markup[p] != '"' && !char.IsWhiteSpace(markup[p]))
                {
                    p++;
                }

                int nameEnd = p;
                while (p < tagEnd && char.IsWhiteSpace(markup[p]))
                {
                    p++;
                }

                if (nameEnd == nameStart)
                {
                    // Not an attribute start (malformed markup): always advance so we cannot loop.
                    p++;
                    continue;
                }

                if (p >= tagEnd || markup[p] != '=')
                {
                    continue;
                }

                p++;
                while (p < tagEnd && char.IsWhiteSpace(markup[p]))
                {
                    p++;
                }

                int valueStart;
                int valueEnd;
                if (p < tagEnd && (markup[p] == '\'' || markup[p] == '"'))
                {
                    char quote = markup[p];
                    valueStart = p + 1;
                    valueEnd = FindQuoteEnd(markup, quote, valueStart, tagEnd, form);
                    if (valueEnd < 0)
                    {
                        break;
                    }

                    p = valueEnd + 1;
                }
                else
                {
                    valueStart = p;
                    while (p < tagEnd && !char.IsWhiteSpace(markup[p]))
                    {
                        p++;
                    }

                    valueEnd = p;
                }

                string name = markup.Substring(nameStart, nameEnd - nameStart);
                if (name.EndsWith("/", StringComparison.Ordinal))
                {
                    name = name[..^1];
                }

                // Only the tooltip text is shown to the player: value= is the button's return
                // value, src=/param= hold file names and numbers.
                if (extractHtmlTitle &&
                    string.Equals(name, "title", StringComparison.OrdinalIgnoreCase) &&
                    valueEnd > valueStart)
                {
                    ScanAttributeText(valueStart, valueEnd);
                }
            }
        }

        int i = 0;
        int literalStart = 0;
        while (i < markup.Length)
        {
            char c = markup[i];
            if (c == '<')
            {
                Flush(literalStart, i);
                int tagEnd = FindTagEnd(markup, i, markup.Length, form);
                if (tagEnd < 0)
                {
                    i = markup.Length;
                    break;
                }

                EmitTagAttributes(i, tagEnd);
                i = tagEnd + 1;
                literalStart = i;
            }
            else if (form && c == '%')
            {
                Flush(literalStart, i);
                i = SkipPercent(markup, i, markup.Length);
                literalStart = i;
            }
            else if (form && c == '{')
            {
                Flush(literalStart, i);
                i = SkipBrace(markup, i, markup.Length);
                literalStart = i;
            }
            else
            {
                i++;
            }
        }

        Flush(literalStart, markup.Length);
        return list;
    }

    // Finds the '>' that closes the tag starting at <paramref name="start"/>, skipping quoted
    // attribute values (a tooltip may contain "<br>" or ">" itself) and HTML comments.
    private static int FindTagEnd(string line, int start, int limit, bool form)
    {
        if (start + 3 < limit && line[start] == '<' && line[start + 1] == '!' && line[start + 2] == '-' && line[start + 3] == '-')
        {
            int comment = line.IndexOf("-->", start + 4, StringComparison.Ordinal);
            if (comment < 0 || comment + 2 >= limit)
            {
                return -1;
            }

            return comment + 2;
        }

        char quote = '\0';
        for (int i = start + 1; i < limit; i++)
        {
            char c = line[i];

            // Inside a formatted HTML string a quoted attribute may contain %..% or {..} whose
            // own quotes are part of the expression, not of the markup.
            if (form && (c == '%' || c == '{'))
            {
                int skipped = c == '%' ? SkipPercent(line, i, limit) : SkipBrace(line, i, limit);
                if (skipped > i)
                {
                    i = skipped - 1;
                    continue;
                }
            }

            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (c == '\'' || c == '"')
            {
                quote = c;
            }
            else if (c == '>')
            {
                return i;
            }
        }

        return -1;
    }

    private static DraftEntry? BuildEntry(
        int lineIndex,
        int lineStart,
        string functionName,
        string line,
        int start,
        int end,
        string mode,
        string command)
    {
        if (start > end)
        {
            return null;
        }

        var (parts, template, span) = BuildParts(line, start, end, mode);
        if (parts.Count == 0 || !IsWorth(template))
        {
            return null;
        }

        return new DraftEntry
        {
            Line = lineIndex + 1,
            LineStart = lineStart,
            FunctionName = functionName,
            Command = command,
            Kind = mode,
            Template = template,
            Parts = parts,
            Span = span
        };
    }

    private static (List<DraftPart> Parts, string Template, DraftPart Span) BuildParts(
        string line,
        int start,
        int end,
        string mode)
    {
        var parts = new List<DraftPart>();
        if (start >= end)
        {
            return (parts, string.Empty, default);
        }

        string whole = line.Substring(start, end - start);
        if (whole.Trim().Length == 0)
        {
            return (parts, string.Empty, default);
        }

        int spanStart = start + (whole.Length - whole.TrimStart().Length);
        int spanEnd = end - (whole.Length - whole.TrimEnd().Length);
        var span = new DraftPart(spanStart, spanEnd);

        if (mode == "plain")
        {
            parts.Add(span);
            return (parts, line.Substring(spanStart, spanEnd - spanStart), span);
        }

        ScanForm(line, start, end, parts, null);
        if (parts.Count == 0)
        {
            return (parts, string.Empty, default);
        }

        // The template keeps placeholders: Edge placeholders (%X%さん) would otherwise be cut off,
        // because the literal fragments start after them. Parts still locate only the literals.
        return (parts, line.Substring(spanStart, spanEnd - spanStart), span);
    }

    private static void ScanForm(string s, int start, int end, List<DraftPart> literals, List<string>? refs)
    {
        int i = start;
        int literalStart = start;

        void Flush(int a, int b)
        {
            if (b <= a)
            {
                return;
            }

            string raw = s.Substring(a, b - a);
            string trimmed = raw.Trim();
            if (!IsWorth(trimmed))
            {
                return;
            }

            int a2 = a + (raw.Length - raw.TrimStart().Length);
            int b2 = b - (raw.Length - raw.TrimEnd().Length);
            literals.Add(new DraftPart(a2, b2));
        }

        while (i < end)
        {
            char c = s[i];
            if (c == '%')
            {
                Flush(literalStart, i);
                int close = SkipPercent(s, i, end);
                refs?.Add(s.Substring(i + 1, Math.Max(0, close - i - 2)));
                TryFormattedStringLiteral(s, i + 1, Math.Min(close - 1, end), literals);
                ScanYenAtInRange(s, i + 1, Math.Min(close - 1, end), literals, refs);
                i = close;
                literalStart = i;
            }
            else if (c == '{')
            {
                Flush(literalStart, i);
                i = SkipBrace(s, i, end);
                literalStart = i;
            }
            else if (c == '\\' && i + 1 < end && s[i + 1] != '@')
            {
                // Escaped character (\%, \{, \}): the pair is displayed as the second character,
                // but it stays in the template so writing the translation back cannot create a
                // placeholder by accident.
                i += 2;
            }
            else if (c == '\\' && i + 1 < end && s[i + 1] == '@')
            {
                Flush(literalStart, i);
                int close = FindYenAtClose(s, i, end);
                if (close <= i)
                {
                    i = end;
                    break;
                }

                ScanYenAt(s, i + 2, close, literals, refs);
                i = close + 2;
                literalStart = i;
            }
            else
            {
                i++;
            }
        }

        Flush(literalStart, end);
    }

    /// <summary><c>%"text",width,ALIGN%</c> or <c>%"text"%</c>: the quoted string is the displayed value.</summary>
    private static void TryFormattedStringLiteral(string s, int start, int end, List<DraftPart> literals)
    {
        int i = start;
        while (i < end && char.IsWhiteSpace(s[i]))
        {
            i++;
        }

        if (i >= end || s[i] != '"')
        {
            return;
        }

        int close = FindQuote(s, i, end);
        if (close < 0)
        {
            return;
        }

        int r = close + 1;
        while (r < end && char.IsWhiteSpace(s[r]))
        {
            r++;
        }

        if (r < end)
        {
            if (s[r] != ',')
            {
                return;
            }

            for (int k = r; k < end; k++)
            {
                char c = s[k];
                if (!(char.IsDigit(c) || c == ',' || c == ' ' || c == '\t' ||
                      (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')))
                {
                    return;
                }
            }
        }

        string text = s.Substring(i + 1, close - i - 1);
        if (!IsWorth(text))
        {
            return;
        }

        literals.Add(new DraftPart(i + 1, close));
    }

    private static void ScanYenAtInRange(string s, int start, int end, List<DraftPart> literals, List<string>? refs)
    {
        int i = start;
        while (i < end - 1)
        {
            if (s[i] == '\\' && s[i + 1] == '@')
            {
                int close = FindYenAtClose(s, i, end);
                if (close <= i)
                {
                    return;
                }

                ScanYenAt(s, i + 2, close, literals, refs);
                i = close + 2;
            }
            else if (s[i] == '"')
            {
                int close = FindQuote(s, i, end);
                if (close < 0)
                {
                    return;
                }

                i = close + 1;
            }
            else
            {
                i++;
            }
        }
    }

    private static void ScanYenAt(string s, int start, int end, List<DraftPart> literals, List<string>? refs)
    {
        int question = FindTopLevel(s, start, end, '?');
        if (question < 0)
        {
            return;
        }

        int sharp = FindTopLevel(s, question + 1, end, '#');
        int trueStart = question + 1;
        int trueEnd = sharp < 0 ? end : sharp;
        ScanForm(s, trueStart, trueEnd, literals, refs);
        if (sharp >= 0)
        {
            ScanForm(s, sharp + 1, end, literals, refs);
        }
    }

    private static int FindTopLevel(string s, int start, int end, char target)
    {
        int i = start;
        while (i < end)
        {
            char c = s[i];
            if (c == '"')
            {
                int close = FindQuote(s, i, end);
                if (close < 0)
                {
                    return -1;
                }

                i = close + 1;
                continue;
            }

            if (c == '%')
            {
                i = SkipPercent(s, i, end);
                continue;
            }

            if (c == '{')
            {
                i = SkipBrace(s, i, end);
                continue;
            }

            if (c == '\\' && i + 1 < end && s[i + 1] == '@')
            {
                int close = FindYenAtClose(s, i, end);
                if (close <= i)
                {
                    return -1;
                }

                i = close + 2;
                continue;
            }

            if (c == target)
            {
                return i;
            }

            i++;
        }

        return -1;
    }

    private static int SkipPercent(string s, int i, int end)
    {
        int j = i + 1;
        bool quoted = false;
        while (j < end)
        {
            char c = s[j];
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (c == '%' && !quoted)
            {
                return j + 1;
            }

            j++;
        }

        return end;
    }

    private static int SkipBrace(string s, int i, int end)
    {
        int depth = 0;
        int j = i;
        bool quoted = false;
        while (j < end)
        {
            char c = s[j];
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (!quoted)
            {
                if (c == '{')
                {
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return j + 1;
                    }
                }
            }

            j++;
        }

        return end;
    }

    private static int FindYenAtClose(string s, int i, int end)
    {
        int depth = 1;
        int k = i + 2;
        while (k < end - 1)
        {
            if (s[k] == '%')
            {
                k = SkipPercent(s, k, end);
                continue;
            }

            if (s[k] == '{')
            {
                k = SkipBrace(s, k, end);
                continue;
            }

            if (s[k] == '"')
            {
                int quote = FindQuote(s, k, end);
                if (quote < 0)
                {
                    return -1;
                }

                k = quote + 1;
                continue;
            }

            if (s[k] == '\\' && s[k + 1] == '@')
            {
                depth--;
                if (depth == 0)
                {
                    return k;
                }

                depth++;
                k += 2;
                continue;
            }

            k++;
        }

        return -1;
    }

    // Scans for the closing quote of a formatted (@"...") string, stepping over %..% and {..}
    // placeholders: those may contain quotes of their own (e.g. %TOSTR(x,"X6")%).
    private static int FindQuoteForm(string s, int i, int end)
    {
        int j = i + 1;
        while (j < end)
        {
            char c = s[j];
            if (c == '%' || c == '{')
            {
                int skipped = c == '%' ? SkipPercent(s, j, end) : SkipBrace(s, j, end);
                if (skipped > j)
                {
                    j = skipped;
                    continue;
                }
            }

            if (c == '"')
            {
                if (j + 1 < end && s[j + 1] == '"')
                {
                    j += 2;
                    continue;
                }

                return j;
            }

            j++;
        }

        return -1;
    }

    // Finds the closing quote of an attribute value without being fooled by quotes that sit
    // inside a placeholder expression.
    private static int FindQuoteEnd(string s, char quote, int start, int limit, bool form)
    {
        for (int i = start; i < limit; i++)
        {
            char c = s[i];
            if (form && (c == '%' || c == '{'))
            {
                int skipped = c == '%' ? SkipPercent(s, i, limit) : SkipBrace(s, i, limit);
                if (skipped > i)
                {
                    i = skipped - 1;
                    continue;
                }
            }

            if (c == quote)
            {
                return i;
            }
        }

        return -1;
    }

    // A whole-argument template is only safe to translate as one entry when it has no quotes or
    // '+' operators: otherwise the translation would replace code as well as text.
    private static bool CanKeepWholeArgumentTemplate(string template) =>
        template.IndexOf('"') < 0 && template.IndexOf('+') < 0;

    private static int FindQuote(string s, int i, int end)
    {
        int j = i + 1;
        while (j < end)
        {
            if (s[j] == '"')
            {
                if (j + 1 < end && s[j + 1] == '"')
                {
                    j += 2;
                    continue;
                }

                return j;
            }

            j++;
        }

        return -1;
    }

    private static int FindTopComma(string s, int start, int end)
    {
        int i = start;
        while (i < end)
        {
            char c = s[i];
            if (c == '"')
            {
                int quote = FindQuote(s, i, end);
                if (quote < 0)
                {
                    return -1;
                }

                i = quote + 1;
                continue;
            }

            if (c == '%')
            {
                i = SkipPercent(s, i, end);
                continue;
            }

            if (c == '{')
            {
                i = SkipBrace(s, i, end);
                continue;
            }

            if (c == '\\' && i + 1 < end && s[i + 1] == '@')
            {
                int close = FindYenAtClose(s, i, end);
                if (close <= i)
                {
                    return -1;
                }

                i = close + 2;
                continue;
            }

            if (c == '(')
            {
                int depth = 1;
                i++;
                while (i < end && depth > 0)
                {
                    if (s[i] == '(')
                    {
                        depth++;
                    }
                    else if (s[i] == ')')
                    {
                        depth--;
                    }

                    i++;
                }

                continue;
            }

            if (c == ',')
            {
                return i;
            }

            i++;
        }

        return -1;
    }

    /// <summary>Emuera treats half-width space, tab, ideographic space (U+3000) and comma as separators.</summary>
    private static bool IsSeparator(char c) => c == ' ' || c == '\t' || c == '\u3000' || c == ',';

    private static bool HasPrefix(string s, int i, string prefix, int end)
    {
        if (i + prefix.Length > end)
        {
            return false;
        }

        return string.Compare(s, i, prefix, 0, prefix.Length, StringComparison.OrdinalIgnoreCase) == 0;
    }

    private static bool ContainsForm(string s) =>
        s.IndexOfAny(['%', '{']) >= 0 || s.Contains("\\@", StringComparison.Ordinal);

    /// <summary>
    /// Mirrors <c>Instraction.Child.cs</c>: the suffix after <c>PRINT</c> decides whether the
    /// instruction prints a number (<c>V</c>), a string (<c>S</c>) or a form (<c>FORM</c>).
    /// </summary>
    private static string Classify(string name)
    {
        int i = 5;
        if (HasPrefix(name, i, "SINGLE", name.Length))
        {
            i += 6;
        }

        if (HasPrefix(name, i, "V", name.Length))
        {
            return "num";
        }

        if (HasPrefix(name, i, "S", name.Length))
        {
            return "str";
        }

        if (HasPrefix(name, i, "FORMS", name.Length))
        {
            // FORMS takes a *string expression* and formats the resulting value: `PRINTFORMS STR:X`
            // must not be treated as a printable template, or the identifier would be translated.
            return "str";
        }

        if (HasPrefix(name, i, "FORM", name.Length))
        {
            return "form";
        }

        return "plain";
    }

    /// <summary>Japanese character test used to drop lines that contain no translatable text.</summary>
    private static bool IsJapaneseChar(char c) =>
        (c >= 0x3040 && c <= 0x30FF) ||
        (c >= 0x3400 && c <= 0x4DBF) ||
        (c >= 0x4E00 && c <= 0x9FFF) ||
        (c >= 0xFF61 && c <= 0xFF9F) ||
        c == 0x3005 ||
        c == 0x30FB ||
        c == 0x30FC;

    /// <summary>
    /// An entry is worth translating when it contains Japanese, or at least two Latin letters
    /// (short labels such as "OK" or single symbols are noise).
    /// </summary>
    /// <summary>Commands that never print, so an unknown-token report should ignore them.</summary>
    private static readonly HashSet<string> NonPrintTokens = new(StringComparer.Ordinal)
    {
        "IF", "SIF", "ELSE", "ELSEIF", "ENDIF", "SELECTCASE", "CASE", "CASEELSE", "ENDSELECT",
        "FOR", "NEXT", "WHILE", "WEND", "REPEAT", "REND", "TRY", "CATCH", "TRYCALLLIST", "ENDCATCH",
        "TRYCCALL", "TRYCCALLFORM", "CALL", "CALLF", "CALLFORM", "JUMP", "GOTO", "RETURN", "RETURNF",
        "RETURNV", "FUNC", "FUNCTIONS", "ENDFUNC", "DIM", "DIMS", "VARSET", "SETVAR", "GETBIT",
        "SETBIT", "CLEARBIT", "INVERTBIT", "SPLIT", "TIMES", "BAR", "BARL", "GETNUM", "SETCOLOR",
        "WAIT", "WAITKEY", "TWAIT", "INPUT", "INPUTS", "BINPUT", "CLEARLINE", "QUIT", "FORCE_QUIT",
        "DRAWLINE", "DRAWLINEFORM", "CLS", "ALIGNMENT", "STRLEN", "STRFIND", "STRCOUNT", "SUBSTRING",
        "STRFORM", "TOINT", "TOUPPER", "TOLOWER", "ISNUMERIC", "EXISTCSV", "GETTIME", "SAVEGLOBAL",
        "LOADGLOBAL", "SAVEDATA", "LOADDATA", "DELDATA", "RANDOMIZE", "RESETDATA", "ADDCHARA",
        "ADDSPCHARA", "ADDCOPYCHARA", "DELCHARA", "SWAPCHARA", "SORTCHARA", "PICKUPCHARA",
        "FINDCHARA", "EXISTCHARA", "ADDDEFCHARA", "ADDVOIDCHARA", "GETCHARAID", "GETSPCHARAID",
        "SETCHARAID", "CVARSET", "CVAR", "VAR", "SWAP", "SPRITECREATE", "SPRITEUPDATE", "SPRITEEND",
        "THROW", "ASSERT", "SET", "RESET", "BEGIN", "RESTART", "QUIT", "PRINT_IMG", "PRINT_RECT",
        "PRINT_SPACE", "HTML_TAG", "HTML_TAGSPLIT", "HTML_STRINGLEN", "HTML_SUBSTRING", "HTML_TOPLAINTEXT",
        "HTML_ESCAPE", "HTML_GETPRINTEDSTR", "HTML_GETKEYVALUE", "HTML_DOCTYPE", "HTML_SETPRINTLEN",
        "TRYCALLFORM", "LOOP", "ARRAYCOPY", "ARRAYSORT", "ARRAYSHIFT", "ARRAYREMOVE", "SETFONT",
        "GCREATEFROMFILE", "GSAVETEXT", "GLOADTEXT", "SAVETEXT", "LOADTEXT", "FINDELEMENT",
        "FINDLASTELEMENT", "FINDELEMENTBYNAME", "ADDVALUE", "OLDDATA", "DOTRAIN", "DOFUNC",
        "SAVECHARA", "LOADCHARA", "FIND_CHARADATA", "ARRAYMSORT", "ARRAY1D", "ARRAY2D", "ARRAY3D",
        "STRLENS", "STRLENFORM", "STRLENFORMU", "SUBSTRINGU", "SPRITEDISPOSE", "SPRITECREATED",
        "SPRITEANIMECREATE", "SPRITEANIMEADDFRAME", "GDISPOSE", "GDRAWSPRITE", "GDRAWG", "GSETCOLOR",
        "GCREATE", "GWIDTH", "GHEIGHT", "GROUPMATCH", "MATCH", "MIN", "MAX", "ABS", "TOHALF", "UNICODE",
        "COPYCHARA", "CHKDATA", "CHKFONT", "COLOR_FROMRGB", "RESET_STAIN", "OUTPUTLOG", "FONTSTYLE",
        "SKIPDISP", "TINPUT", "TONEINPUTS", "TRYCALL", "PRINT_MARK", "PRINT_PALAM"
    };

    private static bool HasJapanese(string text)
    {
        foreach (char c in text)
        {
            if (IsJapaneseChar(c))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Replaces <c>%..%</c> and <c>{..}</c> spans with spaces, keeping everything else.</summary>
    private static string StripPlaceholders(string text)
    {
        var builder = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '%')
            {
                i = Math.Max(SkipPercent(text, i, text.Length), i + 1);
                builder.Append(' ');
                continue;
            }

            if (c == '{')
            {
                i = Math.Max(SkipBrace(text, i, text.Length), i + 1);
                builder.Append(' ');
                continue;
            }

            builder.Append(c);
            i++;
        }

        return builder.ToString();
    }

    /// <summary>Takes the argument text of a line, cutting a trailing <c>;</c> comment.</summary>
    private static string SliceArgument(string line, int start)
    {
        int end = line.Length;
        bool quoted = false;
        for (int i = start; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (c == ';' && !quoted)
            {
                end = i;
                break;
            }
        }

        return line.Substring(start, end - start).Trim();
    }

    /// <summary>Detects <c>IDENT(:segment)* = value</c> for variables outside the tracked set.</summary>
    private static bool TryLiteralAssignment(string token, string line, int tokenEnd, out string varName, out int rhsStart)
    {
        varName = string.Empty;
        rhsStart = tokenEnd;

        if (token.Length == 0 || !(char.IsLetter(token[0]) || token[0] == '_'))
        {
            return false;
        }

        // "PRINTFORM  = ..." prints a literal '=', it is not an assignment.
        if (PrintCodes.Contains(token) || NonPrintTokens.Contains(token) ||
            DataOpen.Contains(token) || DataClose.Contains(token))
        {
            return false;
        }

        foreach (char c in token)
        {
            if (!(char.IsLetterOrDigit(c) || c == '_' || c == ':'))
            {
                return false;
            }
        }

        int i = tokenEnd;
        while (i < line.Length && char.IsWhiteSpace(line[i]))
        {
            i++;
        }

        if (i >= line.Length)
        {
            return false;
        }

        if (line[i] != '=')
        {
            int op = i;
            while (op < line.Length && "+-*/%'&|^<>".IndexOf(line[op]) >= 0)
            {
                op++;
            }

            if (op == i || op - i > 2 || op >= line.Length || line[op] != '=')
            {
                return false;
            }

            i = op;
        }

        if (i + 1 < line.Length && line[i + 1] == '=')
        {
            return false;
        }

        varName = token;
        rhsStart = i + 1;
        return true;
    }

    private static bool LooksLikeAssignment(string line, int at)
    {
        int i = at;
        if (i >= line.Length)
        {
            return false;
        }

        if (line[i] != '=')
        {
            int op = i;
            while (op < line.Length && "+-*/%'&|^<>".IndexOf(line[op]) >= 0)
            {
                op++;
            }

            if (op == i || op - i > 2 || op >= line.Length || line[op] != '=')
            {
                return false;
            }

            i = op;
        }

        return i + 1 >= line.Length || line[i + 1] != '=';
    }

    private static HashSet<string> CollectLabels(List<(string Line, int Start)> lines)
    {
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in lines)
        {
            string t = entry.Line.TrimStart();
            if (t.Length < 2 || t[0] != '@')
            {
                continue;
            }

            int end = 1;
            while (end < t.Length && !char.IsWhiteSpace(t[end]) && t[end] != ',')
            {
                end++;
            }

            if (end > 1)
            {
                labels.Add(t.Substring(1, end - 1));
            }
        }

        return labels;
    }

    /// <summary>Collects the variables a print line displays and flags CSV-only lines.</summary>
    private static void TrackPrintLine(
        string line,
        string functionName,
        VisibleTextDiagnostics diagnostics,
        HashSet<string> printedVars)
    {
        int argStart = ArgumentStart(line);
        if (argStart >= line.Length)
        {
            return;
        }

        var refs = new List<string>();
        CollectRefs(line, argStart, line.Length, refs);
        foreach (string raw in refs)
        {
            string reference = raw;
            int comma = reference.IndexOf(',');
            if (comma >= 0)
            {
                reference = reference.Substring(0, comma);
            }

            string? name = RegAnyVar(reference.Trim());
            if (name is not null)
            {
                printedVars.Add(functionName + "\u0000" + name);
            }
        }

        string argument = line.Substring(argStart);
        if (HasJapanese(argument) && !HasJapanese(StripPlaceholders(argument)))
        {
            diagnostics.PlaceholderOnlyPrintLines++;
        }
    }

    /// <summary>Records an unknown leading token whose argument contains Japanese text.</summary>
    private static void TrackUnknownCommand(
        string token,
        string line,
        int tokenEnd,
        int lineNo,
        string relativePath,
        VisibleTextDiagnostics diagnostics,
        HashSet<string> localLabels)
    {
        if (token.Length == 0 || !(char.IsLetter(token[0]) || token[0] == '_'))
        {
            return;
        }

        foreach (char c in token)
        {
            if (!(char.IsLetterOrDigit(c) || c == '_'))
            {
                return;
            }
        }

        if (localLabels.Contains(token) || NonPrintTokens.Contains(token) ||
            token.StartsWith("DT_", StringComparison.Ordinal) ||
            token.StartsWith("SQL_", StringComparison.Ordinal) ||
            token.StartsWith("DEBUGPRINT", StringComparison.Ordinal))
        {
            return;
        }

        int i = tokenEnd;
        while (i < line.Length && char.IsWhiteSpace(line[i]))
        {
            i++;
        }

        if (LooksLikeAssignment(line, i))
        {
            return;
        }

        string argument = SliceArgument(line, tokenEnd);
        if (argument.Length == 0 || !HasJapanese(argument))
        {
            return;
        }

        diagnostics.UnknownCommands[token] =
            diagnostics.UnknownCommands.TryGetValue(token, out int count) ? count + 1 : 1;

        if (diagnostics.UnknownCommandSamples.Count < 40)
        {
            string sample = argument.Length > 80 ? argument.Substring(0, 80) + "…" : argument;
            diagnostics.UnknownCommandSamples.Add($"{token} {relativePath}:{lineNo}  {sample}");
        }
    }

    private static bool IsWorth(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return false;
        }

        int latin = 0;
        foreach (char c in s)
        {
            if (IsJapaneseChar(c))
            {
                return true;
            }

            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
            {
                latin++;
            }
        }

        return latin >= 2;
    }
}
