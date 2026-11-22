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
        "PRINTDATAD", "PRINTDATADL", "PRINTDATADW", "DATALIST"
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
        IReadOnlyCollection<string> macros)
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
        int dataDepth = 0;
        string functionName = string.Empty;

        for (int li = 0; li < lines.Count; li++)
        {
            string line = lines[li].Line;
            int lineStart = lines[li].Start;

            int p = 0;
            while (p < line.Length && (line[p] == ' ' || line[p] == '\t')) p++;
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
                while (argStart < line.Length && (line[argStart] == ' ' || line[argStart] == '\t')) argStart++;
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
                DraftEntry? dataEntry = BuildLineEntry(
                    li, lineStart, functionName, line, tokenEnd2,
                    token == "DATAFORM" ? "form" : "plain", token);
                if (dataEntry is not null)
                {
                    dataEntry.Kind = "data";
                    result.Add(Materialize(dataEntry, normalizedRelativePath));
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

                var (parts, template, _) = BuildParts(
                    line, rhsStart, line.Length, ContainsForm(line.Substring(rhsStart)) ? "form" : "plain");
                if (parts.Count > 0 && IsWorth(template))
                {
                    assignment.Parts = parts;
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

            if (PrintCodes.Contains(token))
            {
                string cls = Classify(token);
                if (cls == "num")
                {
                    continue;
                }

                DraftEntry? entry = cls switch
                {
                    "form" => BuildLineEntry(li, lineStart, functionName, line, tokenEnd2, "form", token),
                    "str" => BuildStrExprEntry(li, lineStart, functionName, line, tokenEnd2, token),
                    _ => BuildLineEntry(li, lineStart, functionName, line, tokenEnd2, "plain", token)
                };
                if (entry is not null)
                {
                    if (cls != "form")
                    {
                        entry.Kind = "print";
                    }

                    result.Add(Materialize(entry, normalizedRelativePath));
                }

                AddVarRefs(line, scope, result, normalizedRelativePath);
                continue;
            }

            switch (token)
            {
                case "PRINTBUTTON":
                case "PRINTBUTTONC":
                case "PRINTBUTTONLC":
                {
                    DraftEntry? entry = BuildButtonEntry(li, lineStart, functionName, line, tokenEnd2, token);
                    if (entry is not null)
                    {
                        result.Add(Materialize(entry, normalizedRelativePath));
                    }

                    AddVarRefs(line, scope, result, normalizedRelativePath);
                    break;
                }

                case "HTML_PRINT":
                case "HTML_PRINT_ISLAND":
                {
                    foreach (DraftEntry entry in BuildHtmlEntries(li, lineStart, functionName, line, tokenEnd2, token))
                    {
                        result.Add(Materialize(entry, normalizedRelativePath));
                    }

                    AddVarRefs(line, scope, result, normalizedRelativePath);
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
                    break;
                }
            }
        }

        return new VisibleTextFileResult(normalizedRelativePath, result, warnings);
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

        VisibleTextSpan span = draft.Span.End > draft.Span.Start
            ? new VisibleTextSpan(draft.LineStart + draft.Span.Start, draft.LineStart + draft.Span.End)
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
        while (i < line.Length && (line[i] == ' ' || line[i] == '\t'))
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
        while (p < line.Length && (line[p] == ' ' || line[p] == '\t'))
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
        while (start < line.Length && (line[start] == ' ' || line[start] == '\t'))
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
                Parts = { new DraftPart(start + 1, close) }
            };
        }

        if (line[start] == '@' && start + 1 < line.Length && line[start + 1] == '"')
        {
            return BuildEntry(
                lineIndex, lineStart, functionName, line, start + 2,
                FindQuote(line, start + 1, line.Length), "form", command);
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
        while (start < line.Length && (line[start] == ' ' || line[start] == '\t'))
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
                Parts = { new DraftPart(start + 1, close) }
            };
        }

        if (line[start] == '@' && start + 1 < line.Length && line[start + 1] == '"')
        {
            DraftEntry? entry = BuildEntry(
                lineIndex, lineStart, functionName, line, start + 2,
                FindQuote(line, start + 1, line.Length), "form", command);
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
            while (inner < end && (line[inner] == ' ' || line[inner] == '\t'))
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
        string command)
    {
        var list = new List<DraftEntry>();
        int start = cmdEnd;
        while (start < line.Length && (line[start] == ' ' || line[start] == '\t'))
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

        int close = FindQuote(line, start, line.Length);
        if (close < 0)
        {
            return list;
        }

        int contentStart = start + 1;
        int contentEnd = close;

        // Walk the HTML: skip tags, treat placeholders as breaks, emit every text node.
        int i = contentStart;
        int literalStart = contentStart;

        void Flush(int a, int b)
        {
            if (b <= a)
            {
                return;
            }

            string raw = line.Substring(a, b - a);
            string trimmed = raw.Trim();
            if (!IsWorth(trimmed))
            {
                return;
            }

            int s = a + (raw.Length - raw.TrimStart().Length);
            int e = b - (raw.Length - raw.TrimEnd().Length);
            string text = line.Substring(s, e - s);
            list.Add(new DraftEntry
            {
                Line = lineIndex + 1,
                LineStart = lineStart,
                FunctionName = functionName,
                Command = command,
                Kind = "html",
                Template = text,
                Parts = { new DraftPart(s, e) }
            });
        }

        while (i < contentEnd)
        {
            char c = line[i];
            if (c == '<')
            {
                Flush(literalStart, i);
                int gt = line.IndexOf('>', i);
                if (gt < 0 || gt >= contentEnd)
                {
                    i = contentEnd;
                    break;
                }

                i = gt + 1;
                literalStart = i;
            }
            else if (form && c == '%')
            {
                Flush(literalStart, i);
                i = SkipPercent(line, i, contentEnd);
                literalStart = i;
            }
            else if (form && c == '{')
            {
                Flush(literalStart, i);
                i = SkipBrace(line, i, contentEnd);
                literalStart = i;
            }
            else
            {
                i++;
            }
        }

        Flush(literalStart, contentEnd);
        return list;
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
        while (i < end && (s[i] == ' ' || s[i] == '\t'))
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
        while (r < end && (s[r] == ' ' || s[r] == '\t'))
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
            return "form";
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
        c == 0x3005 ||
        c == 0x30FB ||
        c == 0x30FC;

    /// <summary>
    /// An entry is worth translating when it contains Japanese, or at least two Latin letters
    /// (short labels such as "OK" or single symbols are noise).
    /// </summary>
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
