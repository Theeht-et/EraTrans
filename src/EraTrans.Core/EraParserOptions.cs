using System.Text;
using EraTrans.Core.VisibleText;

namespace EraTrans.Core;

/// <summary>
/// Settings for parsing ERA source files and applying dictionary entries.
/// </summary>
public sealed class EraParserOptions
{
    internal static readonly string[] DefaultTargetFileExtensions =
    [
        ".csv",
        ".erb",
        ".erh",
        ".erd"
    ];

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

    public bool WriteContext { get; init; } = true;

    /// <summary>
    /// Game-file extensions checked when applying a dictionary at the same relative path.
    /// </summary>
    public IReadOnlyList<string> TargetFileExtensions { get; init; } = DefaultTargetFileExtensions;

    /// <summary>
    /// Isolated settings used exclusively by the legacy ERB parser and legacy variable auto-correction.
    /// </summary>
    public LegacyExtractionSettings Legacy { get; init; } = LegacyExtractionSettings.CreateDefault();

    // Compatibility forwarders for LegacyParser/Configs.cs and existing callers.
    public bool HideEnglishText
    {
        get => Legacy.HideEnglishText;
        init => Legacy.HideEnglishText = value;
    }

    public bool MergeDuplicateEntries
    {
        get => Legacy.MergeDuplicateEntries;
        init => Legacy.MergeDuplicateEntries = value;
    }

    public bool HideVariableEntries
    {
        get => Legacy.HideVariableEntries;
        init => Legacy.HideVariableEntries = value;
    }

    public bool MergeConcatenatedStrings
    {
        get => Legacy.MergeConcatenatedStrings;
        init => Legacy.MergeConcatenatedStrings = value;
    }

    public bool EnableVariableAutoCorrection
    {
        get => Legacy.EnableVariableAutoCorrection;
        init => Legacy.EnableVariableAutoCorrection = value;
    }

    public IReadOnlyList<string> AutoReplaceReferenceFiles
    {
        get => Legacy.AutoReplaceReferenceFiles;
        init => Legacy.AutoReplaceReferenceFiles = value is null ? null! : [.. value];
    }

    public IReadOnlyList<string> VariableNameCharacters
    {
        get => Legacy.VariableNameCharacters;
        init => Legacy.VariableNameCharacters = value is null ? null! : [.. value];
    }

    public IReadOnlyList<string> Operators
    {
        get => Legacy.Operators;
        init => Legacy.Operators = value is null ? null! : [.. value];
    }
}
