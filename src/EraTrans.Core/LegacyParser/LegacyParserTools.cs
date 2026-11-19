using System.Text;
using System.Text.RegularExpressions;

namespace EraTrans.Core;

internal static class Tools
{
    internal static Regex engArrayFilter = null!;
    internal static Regex sysArrayFilter = null!;
    internal static Regex lastNum = null!;
    internal static Regex dimFunction = null!;
    internal static Regex resultsCatch = null!;
    internal static Regex englishTextCatch = null!;
    internal static Regex haventDoubleByte = null!;

    internal static void Init()
    {
        string kw_eng = @"[()_a-zA-Z0-9]*";
        engArrayFilter = new Regex($@"^[-+]?{kw_eng}(:{kw_eng}){{0,2}}$", RegexOptions.Compiled);

        string kw___var__ = "(?:__(?:FILE|FUNCTION|INT_MAX|INT_MIN|LINE)__)";
        string kw_BASE = "(?:(?:DOWN|LOSE|MAX)?BASE)";
        string kw_CDFLAG = "(?:CDFLAG)";
        string kw_CDFLAGNAME = $"(?:{kw_CDFLAG}(?:NAME1|NAME2))";
        string kw_GAMEBASE = "(?:GAMEBASE_(?:ALLOWVERSION|AUTHOR|DEFAULTCHARA|GAMECODE|INFO|NOITEM|TITLE|VERSION|YEAR))";
        string kw_var_int1 = "(?:ARG|GLOBAL|LOCAL|RESULT)";
        string kw_var_int2 = "(?:ABL|BASE|CFLAG|CSTR|EQUIP|EX|EXP|FLAG|GLOBAL|ITEM|JUEL|MARK|MASTER|PALAM|SOURCE|STAIN|TALENT|TCVAR|TEQUIP|TFLAG)";
        string kw_var_str2 = "(?:SAVESTR|STR|TSTR)";
        string kw_var__S = "(?:{{kw_var_int1}}(?:S))";
        string kw_var__NAME = $"(?:(?:{kw_var_int2}|{kw_var_str2})(?:NAME))";
        string kw_num = @"\d{1,4}";
        string kw_var_string = $"(?:{kw_var__S}|{kw_var__NAME}|{kw_var_str2}|{kw_CDFLAGNAME}|CALLNAME|DRAWLINESTR|GLOBALSNAME|LASTLOAD_TEXT|MONEYLABEL|NAME|NICKNAME|SAVEDATA_TEXT|TRAINNAME|WINDOW_TITLE)";
        string kw_variable = $"(?:{kw_num}|{kw___var__}|{kw_var_int1}|{kw_var_int2}|{kw_var_string}|{kw_BASE}|{kw_CDFLAG}|{kw_GAMEBASE}|ASSI|ASSIPLAY|BOUGHT|CDOWN|CHARANUM|COUNT|CUP|DA|DAY|DB|DC|DD|DE|DITEMTYPE|DOWN|EJAC|EXPLV|GOTJUEL|ISASSI|ISTIMEOUT|ITEMPRICE|ITEMSALES|LASTLOAD_NO|LASTLOAD_VERSION|LINECOUNT|MONEY|NEXTCOM|NO|NOITEM|NOWEX|PBAND|PLAYER|PREVCOM|RAND|RANDDATA|PALAMLV|RELATION|SELECTCOM|TA|TARGET|TB|TIME|UP)";

        sysArrayFilter = new Regex($"^{kw_variable}(:{kw_variable}){{0,2}}$", RegexOptions.Compiled);
        lastNum = new Regex(@"(\d+)(?=\D*$)", RegexOptions.Compiled);
        dimFunction = new Regex(@"(\w+)\((.*?)\)", RegexOptions.Compiled);
        resultsCatch = new Regex(@"(?<=RESULTS:?.* = ).+", RegexOptions.Compiled);
        englishTextCatch = new Regex(@"^[a-zA-Z0-9_.%\(\),+\-\*\\ \[\]{}'""]+$", RegexOptions.Compiled);
        haventDoubleByte = new Regex(@"^[\x00-\xff]+$", RegexOptions.Compiled);
    }

    internal static bool IsArray(string text) =>
        Configs.forceFilter ? engArrayFilter.IsMatch(text) : sysArrayFilter.IsMatch(text);

    internal static IEnumerable<string>? GetCSVValue(string line, bool banNumber = true)
    {
        var indexA = line.IndexOf(';');
        if (indexA == 0) return null;
        var contentWithoutComment = indexA != -1 ? line.Substring(0, indexA) : line;
        var indexB = contentWithoutComment.IndexOf('(');
        contentWithoutComment = indexB != -1 ? contentWithoutComment.Substring(0, indexB) : contentWithoutComment;
        var parts = contentWithoutComment.Split(',');
        IEnumerable<string> result = parts.Skip(1).Where(text => !string.IsNullOrWhiteSpace(text));
        if (banNumber)
        {
            result = result.Where(text => !int.TryParse(text.Trim(), out _));
        }
        return result.Select(s => s.TrimStart());
    }

    internal static string? GetResValue(string line, bool banNumber = true)
    {
        var indexA = line.IndexOf(';');
        if (indexA == 0) return null;
        var contentWithoutComment = indexA != -1 ? line.Substring(0, indexA) : line;
        var indexB = contentWithoutComment.IndexOf('(');
        contentWithoutComment = indexB != -1 ? contentWithoutComment.Substring(0, indexB) : contentWithoutComment;
        var parts = contentWithoutComment.Split(',');
        return parts.FirstOrDefault()?.Trim();
    }

    internal static string RemoveSkippedText(string input)
    {
        var result = new StringBuilder();
        int currentIndex = 0;
        bool inSkipMode = false;
        var commentedLineRegex = new Regex(@"^\s*;", RegexOptions.Multiline | RegexOptions.Compiled);

        while (currentIndex < input.Length)
        {
            int startIndex = input.IndexOf("[SKIPSTART]", currentIndex, StringComparison.Ordinal);
            int endIndex = input.IndexOf("[SKIPEND]", currentIndex, StringComparison.Ordinal);

            if (inSkipMode)
            {
                if (endIndex == -1) break;
                inSkipMode = IsLineCommented(input, endIndex, commentedLineRegex);
                currentIndex = endIndex + "[SKIPEND]".Length;
                continue;
            }

            if (startIndex == -1)
            {
                result.Append(input.Substring(currentIndex));
                break;
            }

            if (!IsLineCommented(input, startIndex, commentedLineRegex))
            {
                inSkipMode = true;
                result.Append(input, currentIndex, startIndex - currentIndex);
            }
            else
            {
                result.Append(input, currentIndex, "[SKIPSTART]".Length + (startIndex - currentIndex));
            }
            currentIndex = startIndex + "[SKIPSTART]".Length;
        }
        return result.ToString();
    }

    private static bool IsLineCommented(string input, int position, Regex commentedLineRegex)
    {
        int lineStart = input.LastIndexOf('\n', position) + 1;
        if (lineStart < 0) lineStart = 0;
        int lineEnd = input.IndexOf('\n', position);
        if (lineEnd == -1) lineEnd = input.Length;
        string line = input.Substring(lineStart, lineEnd - lineStart);
        return commentedLineRegex.IsMatch(line);
    }

    internal static int GetSpaceIndex(string context)
    {
        int value = context.IndexOf(' ');
        if (value == -1) value = context.IndexOf('　');
        if (value == -1 && context.Contains("<Tab>", StringComparison.Ordinal))
        {
            if (context.IndexOf('>') > context.IndexOf('<')) value = context.IndexOf('>');
        }
        return value;
    }

    internal static (string? left, string? right) GetSlashStringCouple(string context, char character)
    {
        int index = context.IndexOf(character);
        return index == -1
            ? (null, null)
            : (context.Substring(0, index), context.Substring(index + 1));
    }

    internal static string TrimEndTabs(string input)
    {
        const string tabLiteral = "<Tab>";
        while (input.EndsWith(tabLiteral, StringComparison.Ordinal))
        {
            input = input.Substring(0, input.Length - tabLiteral.Length);
        }
        return input;
    }
}