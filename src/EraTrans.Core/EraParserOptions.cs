using System.Text;
using EraTrans.Core.VisibleText;

namespace EraTrans.Core;

/// <summary>
/// Settings for parsing ERA source files and applying dictionary entries.
/// </summary>
public sealed class EraParserOptions
{
    /// <summary>
    /// Which extractor produces the ERB dictionaries. The CSV/RES/XML/ERD branches always
    /// use the legacy parsers; only the ERB branch switches.
    /// </summary>
    public ExtractionMode ExtractionMode { get; init; } = ExtractionMode.Legacy;

    /// <summary>
    /// New extractor: include the <c>title=</c> tooltip text of HTML_PRINT / HTML_PRINT_ISLAND tags.
    /// </summary>
    public bool ExtractHtmlTitle { get; init; } = true;

    /// <summary>
    /// New extractor: include DATA / DATAFORM rows inside PRINTDATA / DATALIST blocks.
    /// </summary>
    public bool ExtractPrintDataEntries { get; init; } = true;

    /// <summary>
    /// New extractor: drop entries whose template contains no Japanese characters
    /// (plain English words, symbols) instead of sending them to the translator.
    /// </summary>
    public bool SkipNonJapaneseEntries { get; init; } = true;

    private static Encoding CreateDefaultFileEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(932);
    }

    /// <summary>Fallback for BOM-less legacy files; CP932 is the default for Japanese ERA games.</summary>
    public Encoding FileEncoding { get; init; } = CreateDefaultFileEncoding();

    public bool HideEnglishText { get; init; } = true;
    public bool MergeDuplicateEntries { get; init; } = false;
    public bool HideVariableEntries { get; init; } = true;
    public bool MergeConcatenatedStrings { get; init; } = true;
    public bool WriteContext { get; init; } = true;

    /// <summary>
    /// Controls automatic variable correction when applying translations.
    /// </summary>
    public bool EnableVariableAutoCorrection { get; init; } = false;

    internal static readonly string[] DefaultAutoReplaceReferenceFiles =
    [
        "Abl", "Base", "CFlag", "CSTR", "Equip", "Ex",
        "Exp", "Flag", "Global", "Item", "Juel",
        "Mark", "Nowex", "Palam", "Source", "Stain",
        "Talent", "TCVAR", "TEquip", "Tflag", "Train",
        "TSTR", "修正字典"
    ];

    internal static readonly string[] DefaultTargetFileExtensions =
    [
        ".csv",
        ".erb",
        ".erh",
        ".erd"
    ];

    internal static readonly string[] DefaultVariableNameCharacters =
    [
        "☆", "♡", "∀", "←", "→"
    ];

    internal static readonly string[] DefaultOperators =
    [
        "(", ")", "{", "}", "?", "#", "=", "+=", "-=", "*=", "/=",
        ">", "<", "==", "!=", ">=", "<=", "&&", "||", "&", "|", "!",
        "+", "-", "*", "/", "%", "++", "--", ",", ":", "TO"
    ];

    /// <summary>
    /// Top-level CSV dictionary names used for variable auto-correction,
    /// without the .json extension.
    /// </summary>
    public IReadOnlyList<string> AutoReplaceReferenceFiles { get; init; } = DefaultAutoReplaceReferenceFiles;

    /// <summary>
    /// Game-file extensions checked when applying a dictionary at the same relative path.
    /// </summary>
    public IReadOnlyList<string> TargetFileExtensions { get; init; } = DefaultTargetFileExtensions;

    /// <summary>
    /// Additional characters allowed in ERA variable names.
    /// </summary>
    public IReadOnlyList<string> VariableNameCharacters { get; init; } = DefaultVariableNameCharacters;

    /// <summary>
    /// Operators recognized by the ERA expression parser.
    /// </summary>
    public IReadOnlyList<string> Operators { get; init; } = DefaultOperators;
}
