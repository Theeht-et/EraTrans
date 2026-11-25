using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace EraTrans.Core;

public sealed record MaskedPlaceholder(int Index, string Token, string Original);

public sealed record MaskedLineText(
    string MaskedText,
    IReadOnlyList<MaskedPlaceholder> Placeholders);

public sealed record MaskedTranslationWindow(
    TranslationWindow MaskedWindow,
    IReadOnlyDictionary<int, IReadOnlyList<MaskedPlaceholder>> PlaceholdersByLineId);

/// <summary>
/// Protects ERA script placeholders (<c>{...}</c>, <c>%...%</c>) and inline ternary
/// conditions (<c>\@ condition ? A # B \@</c>) from being translated or simplified
/// (e.g. <c>{SOURCE:恭順}</c> -> <c>{SOURCE:恭顺}</c>) by replacing them with opaque
/// tokens (<c>[[P0]]</c>, <c>[[P1]]</c>, ...) before sending text to an LLM and
/// restoring the exact original expressions afterward.
/// </summary>
public static class AiPlaceholderMasker
{
    private const int MaxPercentExpressionLength = 200;

    public static MaskedLineText Mask(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new MaskedLineText(text ?? string.Empty, Array.Empty<MaskedPlaceholder>());
        }

        var placeholders = new List<MaskedPlaceholder>();
        var output = new StringBuilder(text.Length);
        int nextIndex = 0;
        int i = 0;
        int length = text.Length;

        while (i < length)
        {
            // 1. Inline ternary condition: \@ condition ? branchA # branchB \@
            //    Mask ONLY the condition expression between \@ and ? so both branches
            //    remain visible for translation.
            if (text[i] == '\\' && i + 1 < length && text[i + 1] == '@' &&
                !IsEscaped(text, i))
            {
                if (TryFindTernaryConditionEnd(text, i + 2, out int questionIndex))
                {
                    string rawCondition = text[(i + 2)..questionIndex];
                    string trimmedCondition = rawCondition.Trim();
                    if (trimmedCondition.Length > 0)
                    {
                        int leadingCount = 0;
                        while (leadingCount < rawCondition.Length && char.IsWhiteSpace(rawCondition[leadingCount]))
                        {
                            leadingCount++;
                        }

                        int trailingCount = 0;
                        while (trailingCount < rawCondition.Length - leadingCount &&
                               char.IsWhiteSpace(rawCondition[rawCondition.Length - 1 - trailingCount]))
                        {
                            trailingCount++;
                        }

                        MaskedPlaceholder placeholder = AllocatePlaceholder(text, trimmedCondition, ref nextIndex);
                        placeholders.Add(placeholder);

                        output.Append(@"\@");
                        output.Append(rawCondition, 0, leadingCount);
                        output.Append(placeholder.Token);
                        output.Append(rawCondition, rawCondition.Length - trailingCount, trailingCount);
                        output.Append('?');
                        i = questionIndex + 1;
                        continue;
                    }
                }

                output.Append(@"\@");
                i += 2;
                continue;
            }

            // 2. Brace expression placeholder: {SOURCE:恭順}, {EXP:ARG:LOCAL, 5, LEFT}, {ABS(回復量)}
            if (text[i] == '{' && !IsEscaped(text, i))
            {
                if (TryFindBalancedBraceEnd(text, i, out int closeIndex))
                {
                    string original = text[i..(closeIndex + 1)];
                    if (!string.IsNullOrWhiteSpace(original.AsSpan(1, original.Length - 2).ToString()))
                    {
                        MaskedPlaceholder placeholder = AllocatePlaceholder(text, original, ref nextIndex);
                        placeholders.Add(placeholder);
                        output.Append(placeholder.Token);
                        i = closeIndex + 1;
                        continue;
                    }
                }
            }

            // 3. Percent expression placeholder: %CALLNAME:MASTER%, %ITEMNAME:ID, 18, LEFT%, %ANAME(ARG:0)%
            if (text[i] == '%' && !IsEscaped(text, i) && !IsPrecededByAsciiDigit(text, i))
            {
                if (TryFindBalancedPercentEnd(text, i, out int closeIndex))
                {
                    string original = text[i..(closeIndex + 1)];
                    MaskedPlaceholder placeholder = AllocatePlaceholder(text, original, ref nextIndex);
                    placeholders.Add(placeholder);
                    output.Append(placeholder.Token);
                    i = closeIndex + 1;
                    continue;
                }
            }

            output.Append(text[i]);
            i++;
        }

        if (placeholders.Count == 0)
        {
            return new MaskedLineText(text, Array.Empty<MaskedPlaceholder>());
        }

        return new MaskedLineText(output.ToString(), placeholders);
    }

    public static string Restore(
        string translatedText,
        IReadOnlyList<MaskedPlaceholder>? placeholders)
    {
        if (string.IsNullOrEmpty(translatedText) || placeholders is null || placeholders.Count == 0)
        {
            return translatedText ?? string.Empty;
        }

        string result = translatedText;
        for (int index = placeholders.Count - 1; index >= 0; index--)
        {
            MaskedPlaceholder item = placeholders[index];
            if (result.Contains(item.Token, StringComparison.Ordinal))
            {
                result = result.Replace(item.Token, item.Original, StringComparison.Ordinal);
                continue;
            }

            // Tolerate minor whitespace or casing changes that an LLM may introduce
            // around the placeholder token (e.g. "[[ P0 ]]" or "[[p0]]").
            string pattern = $@"\[\[\s*P{item.Index.ToString(CultureInfo.InvariantCulture)}\s*\]\]";
            if (Regex.IsMatch(result, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                result = Regex.Replace(
                    result,
                    pattern,
                    _ => item.Original,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
        }

        return result;
    }

    public static MaskedTranslationWindow MaskWindow(TranslationWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        var maskedLines = new List<WindowLine>(window.Lines.Count);
        var placeholdersByLineId = new Dictionary<int, IReadOnlyList<MaskedPlaceholder>>();

        foreach (WindowLine line in window.Lines)
        {
            MaskedLineText masked = Mask(line.Text);
            if (masked.Placeholders.Count > 0)
            {
                maskedLines.Add(line with { Text = masked.MaskedText });
                placeholdersByLineId[line.Id] = masked.Placeholders;
            }
            else
            {
                maskedLines.Add(line);
            }
        }

        TranslationWindow maskedWindow = placeholdersByLineId.Count == 0
            ? window
            : window with { Lines = maskedLines };

        return new MaskedTranslationWindow(maskedWindow, placeholdersByLineId);
    }

    public static WindowTranslationOutcome RestoreOutcome(
        WindowTranslationOutcome outcome,
        IReadOnlyDictionary<int, IReadOnlyList<MaskedPlaceholder>> placeholdersByLineId)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(placeholdersByLineId);

        if (placeholdersByLineId.Count == 0 || outcome.Translations.Count == 0)
        {
            return outcome;
        }

        var restored = new List<AiTranslationResult>(outcome.Translations.Count);
        foreach (AiTranslationResult item in outcome.Translations)
        {
            if (placeholdersByLineId.TryGetValue(item.Id, out IReadOnlyList<MaskedPlaceholder>? masks) &&
                masks.Count > 0)
            {
                restored.Add(item with { Translation = Restore(item.Translation, masks) });
            }
            else
            {
                restored.Add(item);
            }
        }

        return outcome with { Translations = restored };
    }

    private static MaskedPlaceholder AllocatePlaceholder(
        string fullText,
        string original,
        ref int nextIndex)
    {
        while (true)
        {
            int index = nextIndex++;
            string token = $"[[P{index.ToString(CultureInfo.InvariantCulture)}]]";
            if (!fullText.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return new MaskedPlaceholder(index, token, original);
            }
        }
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

    private static bool IsPrecededByAsciiDigit(string text, int index) =>
        index > 0 && char.IsAsciiDigit(text[index - 1]);

    private static bool TryFindTernaryConditionEnd(string text, int startIndex, out int questionIndex)
    {
        questionIndex = -1;
        int parenDepth = 0;
        bool inString = false;

        for (int j = startIndex; j < text.Length; j++)
        {
            char ch = text[j];
            if (ch is '\r' or '\n')
            {
                return false;
            }

            if (inString)
            {
                if (ch == '"' && !IsEscaped(text, j))
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
            else if (ch == '\\' && j + 1 < text.Length && text[j + 1] == '@')
            {
                // Hit another \@ before finding ? -> this \@ is a closing marker or malformed.
                return false;
            }
            else if (ch == '?' && parenDepth == 0)
            {
                // Ensure there is a closing \@ later on the same line.
                int lineEnd = text.IndexOfAny(['\r', '\n'], j + 1);
                int searchLimit = lineEnd < 0 ? text.Length : lineEnd;
                int closingIndex = text.IndexOf(@"\@", j + 1, searchLimit - (j + 1), StringComparison.Ordinal);
                if (closingIndex < 0)
                {
                    return false;
                }

                questionIndex = j;
                return true;
            }
        }

        return false;
    }

    private static bool TryFindBalancedBraceEnd(string text, int openIndex, out int closeIndex)
    {
        closeIndex = -1;
        int braceDepth = 1;
        int parenDepth = 0;
        bool inString = false;

        for (int j = openIndex + 1; j < text.Length; j++)
        {
            char ch = text[j];
            if (ch is '\r' or '\n')
            {
                return false;
            }

            if (inString)
            {
                if (ch == '"' && !IsEscaped(text, j))
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
            else if (ch == '{')
            {
                braceDepth++;
            }
            else if (ch == '}')
            {
                braceDepth--;
                if (braceDepth == 0)
                {
                    if (parenDepth != 0)
                    {
                        return false;
                    }

                    closeIndex = j;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryFindBalancedPercentEnd(string text, int openIndex, out int closeIndex)
    {
        closeIndex = -1;
        int start = openIndex + 1;
        if (start >= text.Length || char.IsWhiteSpace(text[start]) || text[start] == '%')
        {
            return false;
        }

        if (!IsValidPercentExpressionStart(text[start]))
        {
            return false;
        }

        int parenDepth = 0;
        bool inString = false;
        int limit = Math.Min(text.Length, start + MaxPercentExpressionLength + 1);

        for (int j = start; j < limit; j++)
        {
            char ch = text[j];
            if (ch is '\r' or '\n')
            {
                return false;
            }

            if (inString)
            {
                if (ch == '"' && !IsEscaped(text, j))
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
                if (parenDepth == 0)
                {
                    return false;
                }
                parenDepth--;
            }
            else if (ch == '%' && parenDepth == 0)
            {
                if (j == start)
                {
                    return false;
                }

                closeIndex = j;
                return true;
            }
            else if (parenDepth == 0 && IsDisallowedProsePunctuation(ch))
            {
                return false;
            }
        }

        return false;
    }

    private static bool IsValidPercentExpressionStart(char ch) =>
        char.IsLetter(ch) || ch is '_' or '(' or '"' or '@';

    private static bool IsDisallowedProsePunctuation(char ch) =>
        ch is '。' or '、' or '！' or '？' or '「' or '」' or '『' or '』' or '【' or '】' or '…';
}
