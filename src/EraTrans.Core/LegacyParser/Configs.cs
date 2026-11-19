using System.Text;

namespace EraTrans.Core;

// Compatibility settings used internally by the migrated parser implementation.
// Values are set once per extraction while DictionaryExtractor holds the parser lock.
internal static class Configs
{
    internal static Encoding fileEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
    internal static List<string> operators =
    [
        "(", ")", "{", "}", "?", "#", "=", "+=", "-=", "*=", "/=",
        ">", "<", "==", "!=", ">=", "<=", "&&", "||", "&", "|", "!",
        "+", "-", "*", "/", "%", "++", "--", ",", ":", "TO"
    ];
    internal static List<string> var_operators = ["☆", "♡", "∀", "←", "→"];
    internal static bool forceFilter = true;
    internal static bool hideEngText = true;
    internal static bool mergeSameText = false;
    internal static bool hideVarOutput = false;
    internal static bool mergeString = true;
    internal static bool writeContext = true;

    internal static void Apply(EraParserOptions options)
    {
        fileEncoding = options.FileEncoding;
        forceFilter = options.ForceFilterVariableNames;
        hideEngText = options.HideEnglishText;
        mergeSameText = options.MergeDuplicateEntries;
        hideVarOutput = options.HideVariableEntries;
        mergeString = options.MergeConcatenatedStrings;
        writeContext = options.WriteContext;

        ArgumentNullException.ThrowIfNull(options.Operators);
        ArgumentNullException.ThrowIfNull(options.VariableNameCharacters);
        operators = options.Operators.ToList();
        var_operators = options.VariableNameCharacters.ToList();
    }
}