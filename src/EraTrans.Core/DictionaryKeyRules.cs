namespace EraTrans.Core;

/// <summary>
/// Rules for distinguishing translatable text entries from script identifiers.
/// </summary>
public static class DictionaryKeyRules
{
    /// <summary>
    /// Only keys beginning with the extractor's text prefix may enter translation.
    /// Variable entries must remain untouched.
    /// </summary>
    public static bool IsTranslatableTextKey(string? key) =>
        key?.StartsWith("文本", StringComparison.Ordinal) == true;
}
