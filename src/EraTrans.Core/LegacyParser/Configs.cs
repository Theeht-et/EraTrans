using System.Text;

namespace EraTrans.Core;

// Compatibility settings used internally by the migrated parser implementation.
// Values are set once per extraction while DictionaryExtractor holds the parser lock.
internal static class Configs
{
    internal static Encoding fileEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
    private static Func<string, string>? fileTextReader;
    internal static string ReadAllText(string path) =>
        fileTextReader is null ? File.ReadAllText(path, fileEncoding) : fileTextReader(path);
    internal static string[] ReadAllLines(string path)
    {
        using var reader = new StringReader(ReadAllText(path));
        var lines = new List<string>();
        string? line;
        while ((line = reader.ReadLine()) is not null) lines.Add(line);
        return lines.ToArray();
    }
    internal static IDisposable UseFileTextReader(Func<string, string> reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        Func<string, string>? previous = fileTextReader;
        fileTextReader = reader;
        return new FileTextReaderScope(previous);
    }
    private sealed class FileTextReaderScope(Func<string, string>? previous) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            fileTextReader = previous;
            _disposed = true;
        }
    }
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