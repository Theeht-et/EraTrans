using System.Text.RegularExpressions;

namespace EraTrans.Core;

/// <summary>
/// Non-blocking pre-writeback checks shown in the dictionary editor so translators can spot
/// script-breaking newlines or altered ERA placeholders before applying translations.
/// </summary>
public static class TranslationPreValidator
{
    private static readonly Regex PercentPlaceholderRegex = new(
        @"%[A-Za-z_\u3040-\u9FFF][A-Za-z0-9_:\u3040-\u9FFF\(\)\[\],\.\+\-\*/\s""']{0,80}%",
        RegexOptions.Compiled);

    private static readonly Regex BracePlaceholderRegex = new(
        @"\{[A-Za-z0-9_:\u3040-\u9FFF\(\)\[\],\.\+\-\*/\s""']{1,80}\}",
        RegexOptions.Compiled);

    public static string Validate(string original, string translation, bool isScriptTarget)
    {
        if (string.IsNullOrWhiteSpace(translation))
        {
            return string.Empty;
        }

        var issues = new List<string>();

        if (isScriptTarget && translation.IndexOfAny(['\r', '\n']) >= 0)
        {
            issues.Add("译文含换行（写回脚本时将被跳过）");
        }

        Dictionary<string, int> originalPlaceholders = ExtractPlaceholders(original);
        Dictionary<string, int> translationPlaceholders = ExtractPlaceholders(translation);

        if (originalPlaceholders.Count > 0 || translationPlaceholders.Count > 0)
        {
            var missing = new List<string>();
            foreach ((string token, int expectedCount) in originalPlaceholders)
            {
                int actualCount = translationPlaceholders.GetValueOrDefault(token);
                if (actualCount < expectedCount)
                {
                    missing.Add(token);
                }
            }

            var extra = new List<string>();
            foreach ((string token, int actualCount) in translationPlaceholders)
            {
                int expectedCount = originalPlaceholders.GetValueOrDefault(token);
                if (actualCount > expectedCount)
                {
                    extra.Add(token);
                }
            }

            if (missing.Count > 0)
            {
                issues.Add("缺少占位符：" + FormatSample(missing));
            }

            if (extra.Count > 0)
            {
                issues.Add("多出占位符：" + FormatSample(extra));
            }
        }

        int originalTernaryMarkers = CountTernaryMarkers(original);
        int translationTernaryMarkers = CountTernaryMarkers(translation);
        if (translationTernaryMarkers % 2 != 0)
        {
            issues.Add(@"存在未闭合的 \@...\@ 条件表达式");
        }
        else
        {
            int originalTernaryBlocks = originalTernaryMarkers / 2;
            int translationTernaryBlocks = translationTernaryMarkers / 2;
            if (originalTernaryBlocks != translationTernaryBlocks)
            {
                issues.Add($@"\@...\@ 条件块数量不一致（原文 {originalTernaryBlocks} 处，译文 {translationTernaryBlocks} 处）");
            }
        }

        return issues.Count == 0 ? string.Empty : string.Join("；", issues);
    }

    public static string DescribeEntryType(string? kind, string? command)
    {
        string k = (kind ?? string.Empty).Trim();
        string c = (command ?? string.Empty).Trim();

        if (k.Equals("html_title", StringComparison.OrdinalIgnoreCase))
            return "悬停提示";
        if (k.Equals("html", StringComparison.OrdinalIgnoreCase) ||
            c.StartsWith("HTML_", StringComparison.OrdinalIgnoreCase))
            return "界面";
        if (k.Equals("button", StringComparison.OrdinalIgnoreCase) ||
            c.StartsWith("PRINTBUTTON", StringComparison.OrdinalIgnoreCase))
            return "按钮";
        if (k.Equals("data", StringComparison.OrdinalIgnoreCase) ||
            c.Equals("DATA", StringComparison.OrdinalIgnoreCase) ||
            c.Equals("DATAFORM", StringComparison.OrdinalIgnoreCase) ||
            c.Equals("DATALIST", StringComparison.OrdinalIgnoreCase))
            return "数据块";
        if (c.Equals("THROW", StringComparison.OrdinalIgnoreCase) ||
            c.Equals("PRINT_ERROR", StringComparison.OrdinalIgnoreCase))
            return "报错";
        if (k.Equals("print", StringComparison.OrdinalIgnoreCase) ||
            k.Equals("form", StringComparison.OrdinalIgnoreCase) ||
            k.Equals("custom", StringComparison.OrdinalIgnoreCase) ||
            c.StartsWith("PRINT", StringComparison.OrdinalIgnoreCase) ||
            c.Equals("REUSELASTLINE", StringComparison.OrdinalIgnoreCase) ||
            c.Equals("PUTFORM", StringComparison.OrdinalIgnoreCase))
            return "正文";

        return string.Empty;
    }

    private static Dictionary<string, int> ExtractPlaceholders(string text)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(text))
        {
            return counts;
        }

        CollectMatches(PercentPlaceholderRegex, text, counts);
        CollectMatches(BracePlaceholderRegex, text, counts);
        return counts;
    }

    private static int CountTernaryMarkers(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(@"\@", index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += 2;
        }

        return count;
    }

    private static void CollectMatches(Regex regex, string text, Dictionary<string, int> counts)
    {
        foreach (Match match in regex.Matches(text))
        {
            string value = match.Value;
            counts[value] = counts.GetValueOrDefault(value) + 1;
        }
    }

    private static string FormatSample(IReadOnlyList<string> items)
    {
        return items.Count <= 3
            ? string.Join("、", items)
            : string.Join("、", items.Take(3)) + $" 等 {items.Count} 处";
    }
}
