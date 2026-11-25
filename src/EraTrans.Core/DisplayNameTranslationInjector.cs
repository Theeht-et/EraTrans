using System.Text;
using EraTrans.Core.WriteBack;

namespace EraTrans.Core;

public sealed record DisplayNameInjectionResult(
    int MappedTermCount,
    int ModifiedFileCount,
    int WrappedTokenCount,
    string? HelperFilePath);

/// <summary>
/// Safely injects display-layer variable name translation into a generated game working copy
/// without modifying any original CSV variable definitions or script logic comparisons.
/// Only strictly balanced <c>%XXXNAME:expr[, width[, align]]%</c> display tokens are wrapped
/// with <c>ERATRANS_TR_NAME(...)</c>; complex expressions, concatenation, logic checks, and
/// inline ternary conditions are intentionally left untouched.
/// </summary>
public static class DisplayNameTranslationInjector
{
    public const string HelperFileName = "_ERATRANS_TR_NAME.ERB";
    public const string HelperFunctionName = "ERATRANS_TR_NAME";

    private static readonly HashSet<string> VisibleNamePrefixes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "TALENTNAME",
            "ABLNAME",
            "SOURCENAME",
            "PALAMNAME",
            "EXPNAME",
            "ITEMNAME",
            "TRAINNAME",
            "MARKNAME",
            "BASENAME",
            "EXNAME",
            "EQUIPNAME",
            "STAINNAME"
        };

    private static readonly UTF8Encoding Utf8WithBom =
        new(encoderShouldEmitUTF8Identifier: true);

    public static DisplayNameInjectionResult ApplyToGameCopy(
        string gameCopyDirectory,
        IReadOnlyList<VariableGlossaryEntry>? glossaryEntries,
        Encoding? fallbackEncoding = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameCopyDirectory);

        if (!Directory.Exists(gameCopyDirectory) || glossaryEntries is null || glossaryEntries.Count == 0)
        {
            return new DisplayNameInjectionResult(0, 0, 0, null);
        }

        string? erbDirectory = FindErbDirectory(gameCopyDirectory);
        if (erbDirectory is null)
        {
            return new DisplayNameInjectionResult(0, 0, 0, null);
        }

        string helperPath = Path.Combine(erbDirectory, HelperFileName);

        // Only keep valid, distinct mappings where translation differs from original
        // and contains no raw quotes or newlines that could break ERB CASE literals.
        Dictionary<string, string> mappings = BuildSafeMappings(glossaryEntries);
        if (mappings.Count == 0)
        {
            // Keep an existing helper valid as a pass-through so already-wrapped ERBs never break.
            if (File.Exists(helperPath))
            {
                File.WriteAllText(helperPath, BuildHelperErbSource(mappings), Utf8WithBom);
                return new DisplayNameInjectionResult(0, 0, 0, helperPath);
            }

            return new DisplayNameInjectionResult(0, 0, 0, null);
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding readFallback = fallbackEncoding ?? Encoding.GetEncoding(932);

        int modifiedFiles = 0;
        int wrappedTokens = 0;

        foreach (string erbFile in Directory.EnumerateFiles(erbDirectory, "*", SearchOption.AllDirectories))
        {
            if (!Path.GetExtension(erbFile).Equals(".erb", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (Path.GetFileName(erbFile).Equals(HelperFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string originalText = ReadErbTextSafe(erbFile, readFallback);
            string rewritten = RewriteContent(originalText, out int fileWrapped);
            if (fileWrapped > 0)
            {
                File.WriteAllText(erbFile, rewritten, Utf8WithBom);
                modifiedFiles++;
                wrappedTokens += fileWrapped;
            }
        }

        if (wrappedTokens == 0 && !File.Exists(helperPath))
        {
            return new DisplayNameInjectionResult(mappings.Count, 0, 0, null);
        }

        File.WriteAllText(helperPath, BuildHelperErbSource(mappings), Utf8WithBom);

        return new DisplayNameInjectionResult(mappings.Count, modifiedFiles, wrappedTokens, helperPath);
    }

    public static string RewriteContent(string content, out int totalWrapped)
    {
        totalWrapped = 0;
        if (string.IsNullOrEmpty(content))
        {
            return content ?? string.Empty;
        }

        var builder = new StringBuilder(content.Length + 64);
        int pos = 0;
        int length = content.Length;

        while (pos < length)
        {
            int lineEnd = content.IndexOfAny(['\r', '\n'], pos);
            int nextPos;
            string line;
            string newline;

            if (lineEnd < 0)
            {
                line = content[pos..];
                newline = string.Empty;
                nextPos = length;
            }
            else
            {
                line = content[pos..lineEnd];
                if (content[lineEnd] == '\r' && lineEnd + 1 < length && content[lineEnd + 1] == '\n')
                {
                    newline = "\r\n";
                    nextPos = lineEnd + 2;
                }
                else
                {
                    newline = content[lineEnd].ToString();
                    nextPos = lineEnd + 1;
                }
            }

            builder.Append(RewriteLine(line, out int lineWrapped));
            builder.Append(newline);
            totalWrapped += lineWrapped;
            pos = nextPos;
        }

        return builder.ToString();
    }

    /// <summary>
    /// Rewrites only strictly balanced <c>%XXXNAME:expr[, width[, align]]%</c> display
    /// tokens on a single ERB line. Comment lines, logic comparisons, function arguments,
    /// inline ternary conditions, and compound expressions with top-level operators are
    /// always left untouched.
    /// </summary>
    public static string RewriteLine(string line, out int wrappedCount)
    {
        wrappedCount = 0;
        if (string.IsNullOrEmpty(line))
        {
            return line ?? string.Empty;
        }

        ReadOnlySpan<char> trimmed = line.AsSpan().TrimStart();
        if (trimmed.IsEmpty || trimmed[0] == ';')
        {
            return line;
        }

        if (!line.Contains('%'))
        {
            return line;
        }

        var output = new StringBuilder(line.Length + 32);
        int i = 0;
        int n = line.Length;

        while (i < n)
        {
            if (line[i] != '%' || IsEscaped(line, i))
            {
                output.Append(line[i]);
                i++;
                continue;
            }

            if (!TryMatchVisibleNamePrefix(line, i + 1, out _))
            {
                output.Append(line[i]);
                i++;
                continue;
            }

            // Scan forward with balanced parentheses and string literals until closing '%'.
            int j = i + 1;
            int parenDepth = 0;
            bool inString = false;
            int firstTopLevelComma = -1;
            bool closed = false;

            while (j < n)
            {
                char ch = line[j];
                if (inString)
                {
                    if (ch == '"' && !IsEscaped(line, j))
                    {
                        inString = false;
                    }
                }
                else
                {
                    if (ch == '"')
                    {
                        inString = true;
                    }
                    else if (ch == '(')
                    {
                        parenDepth++;
                    }
                    else if (ch == ')')
                    {
                        if (parenDepth == 0)
                        {
                            break;
                        }
                        parenDepth--;
                    }
                    else if (ch == ',' && parenDepth == 0 && firstTopLevelComma < 0)
                    {
                        firstTopLevelComma = j;
                    }
                    else if (ch == '%' && parenDepth == 0)
                    {
                        closed = true;
                        break;
                    }
                }

                j++;
            }

            if (!closed || parenDepth != 0 || inString)
            {
                output.Append(line[i]);
                i++;
                continue;
            }

            string expr = firstTopLevelComma >= 0
                ? line[(i + 1)..firstTopLevelComma].Trim()
                : line[(i + 1)..j].Trim();
            string formatSuffix = firstTopLevelComma >= 0
                ? line[firstTopLevelComma..j]
                : string.Empty;

            if (expr.Length == 0 || HasTopLevelOperator(expr))
            {
                output.Append(line, i, j - i + 1);
            }
            else
            {
                output.Append('%');
                output.Append(HelperFunctionName);
                output.Append('(');
                output.Append(expr);
                output.Append(')');
                output.Append(formatSuffix);
                output.Append('%');
                wrappedCount++;
            }

            i = j + 1;
        }

        return wrappedCount == 0 ? line : output.ToString();
    }

    public static string BuildHelperErbSource(IReadOnlyDictionary<string, string> mappings)
    {
        var sb = new StringBuilder();
        sb.AppendLine("; Auto-generated by EraTrans (display-layer variable name translation).");
        sb.AppendLine("; Original CSV variable definitions remain untouched.");
        sb.AppendLine($"@{HelperFunctionName}(ARGS)");
        sb.AppendLine("#FUNCTIONS");
        sb.AppendLine("SELECTCASE ARGS");

        foreach ((string original, string translation) in mappings.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            sb.AppendLine($"\tCASE \"{original}\"");
            sb.AppendLine($"\t\tRETURNF \"{translation}\"");
        }

        sb.AppendLine("\tCASEELSE");
        sb.AppendLine("\t\tRETURNF ARGS");
        sb.AppendLine("ENDSELECT");
        return sb.ToString();
    }

    private static Dictionary<string, string> BuildSafeMappings(IReadOnlyList<VariableGlossaryEntry> entries)
    {
        var mappings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (VariableGlossaryEntry entry in entries)
        {
            string orig = (entry.Original ?? string.Empty).Trim();
            string trans = (entry.Translation ?? string.Empty).Trim();
            if (orig.Length == 0 || trans.Length == 0 || string.Equals(orig, trans, StringComparison.Ordinal))
            {
                continue;
            }

            if (ContainsUnsafeErbStringChar(orig) || ContainsUnsafeErbStringChar(trans))
            {
                continue;
            }

            mappings.TryAdd(orig, trans);
        }

        return mappings;
    }

    private static bool ContainsUnsafeErbStringChar(string value) =>
        value.IndexOfAny(['"', '\r', '\n']) >= 0;

    private static bool TryMatchVisibleNamePrefix(string line, int startIndex, out int colonIndex)
    {
        colonIndex = -1;
        int pos = startIndex;
        while (pos < line.Length && char.IsAsciiLetter(line[pos]))
        {
            pos++;
        }

        if (pos == startIndex)
        {
            return false;
        }

        string ident = line[startIndex..pos];
        if (!VisibleNamePrefixes.Contains(ident))
        {
            return false;
        }

        while (pos < line.Length && char.IsWhiteSpace(line[pos]))
        {
            pos++;
        }

        if (pos < line.Length && line[pos] == ':')
        {
            colonIndex = pos;
            return true;
        }

        return false;
    }

    private static bool HasTopLevelOperator(string expr)
    {
        int parenDepth = 0;
        bool inString = false;

        for (int idx = 0; idx < expr.Length; idx++)
        {
            char ch = expr[idx];
            if (inString)
            {
                if (ch == '"' && !IsEscaped(expr, idx))
                {
                    inString = false;
                }
                continue;
            }

            if (ch == '"')
            {
                inString = true;
            }
            else if (ch == '(')
            {
                parenDepth++;
            }
            else if (ch == ')')
            {
                if (parenDepth > 0)
                {
                    parenDepth--;
                }
            }
            else if (parenDepth == 0 &&
                     ch is '+' or '-' or '*' or '/' or '=' or '!' or '<' or '>' or '?' or '&' or '|')
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsEscaped(string text, int index)
    {
        int backslashes = 0;
        for (int pos = index - 1; pos >= 0 && text[pos] == '\\'; pos--)
        {
            backslashes++;
        }

        return (backslashes & 1) == 1;
    }

    private static string? FindErbDirectory(string gameCopyDirectory)
    {
        string direct = Path.Combine(gameCopyDirectory, "ERB");
        if (Directory.Exists(direct))
        {
            return direct;
        }

        foreach (string subDir in Directory.EnumerateDirectories(gameCopyDirectory))
        {
            if (Path.GetFileName(subDir).Equals("ERB", StringComparison.OrdinalIgnoreCase))
            {
                return subDir;
            }
        }

        return null;
    }

    private static string ReadErbTextSafe(string path, Encoding fallbackEncoding)
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
