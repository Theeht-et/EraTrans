using System.Text;
using System.Text.Json.Serialization;
using EraTrans.Core.VisibleText;

namespace EraTrans.Core;

/// <summary>
/// User-configurable application and parser settings.
/// Legacy ERB extraction settings are isolated under <see cref="Legacy"/>.
/// </summary>
public sealed class EraTransUserSettings
{
    public const int MinimumWindowCharacters = 500;
    public const int MaximumWindowCharacters = 20000;
    public const int MinimumWindowLines = 5;
    public const int MaximumWindowLines = 500;
    public const int DefaultWindowCharacters = 3000;
    public const int DefaultWindowLines = 60;

    public const int MinimumTranslationConcurrency = 1;
    public const int MaximumTranslationConcurrency = 50;
    public const int DefaultTranslationConcurrency = 5;

    public string FileEncodingId { get; set; } = "932";
    public bool AutoOpenFolder { get; set; } = true;
    public bool WriteContext { get; set; } = true;
    public bool EnableErbSemanticGroupingTranslation { get; set; } = true;

    /// <summary>
    /// When enabled and <c>辅助数据/variable-glossary.json</c> contains translated terms,
    /// injects <c>ERB/_ERATRANS_TR_NAME.ERB</c> and safely wraps display-only
    /// <c>%XXXNAME:...%</c> tokens in the generated game copy without modifying CSVs.
    /// </summary>
    public bool EnableDisplayNameTranslationInjection { get; set; } = true;

    /// <summary>
    /// Extractor used for the ERB branch: "lexer" (V2 visible text, default) or "legacy" (V1).
    /// The CSV/RES/XML/ERD branches always use the legacy parsers.
    /// </summary>
    public string ErbExtractionMode { get; set; } = "lexer";

    /// <summary>New extractor: include HTML title= tooltips.</summary>
    public bool ExtractHtmlTitle { get; set; } = true;

    /// <summary>New extractor: include PRINTDATA / DATALIST rows.</summary>
    public bool ExtractPrintDataEntries { get; set; } = true;

    /// <summary>New extractor: drop entries without Japanese text.</summary>
    public bool SkipNonJapaneseEntries { get; set; } = true;

    /// <summary>
    /// Size of one ordered translation window. Only used when
    /// <see cref="EnableErbSemanticGroupingTranslation"/> is enabled; the fixed
    /// batch mode always sends 12 lines per request.
    /// </summary>
    public int WindowMaxCharacters { get; set; } = DefaultWindowCharacters;
    public int WindowMaxLines { get; set; } = DefaultWindowLines;

    /// <summary>
    /// How many translation requests may be in flight at the same time.
    /// </summary>
    public int TranslationConcurrency { get; set; } = DefaultTranslationConcurrency;

    // Last opened workspace. Empty values keep older settings files fully compatible.
    public string LastGameDirectory { get; set; } = string.Empty;
    public string LastOutputRootDirectory { get; set; } = string.Empty;
    public string LastDictionaryFileRelativePath { get; set; } = string.Empty;

    public List<string> TargetFileExtensions { get; set; } = [.. EraParserOptions.DefaultTargetFileExtensions];

    /// <summary>
    /// Isolated settings for the legacy ERB extractor and legacy variable auto-correction.
    /// Persisted under the nested <c>Legacy</c> section in <c>settings.json</c>.
    /// </summary>
    public LegacyExtractionSettings Legacy { get; set; } = LegacyExtractionSettings.CreateDefault();

    // Compatibility forwarders for existing callers/tests; omitted from top-level JSON serialization.
    [JsonIgnore]
    public bool EnableVariableAutoCorrection
    {
        get => Legacy.EnableVariableAutoCorrection;
        set => Legacy.EnableVariableAutoCorrection = value;
    }

    [JsonIgnore]
    public bool HideEnglishText
    {
        get => Legacy.HideEnglishText;
        set => Legacy.HideEnglishText = value;
    }

    [JsonIgnore]
    public bool MergeDuplicateEntries
    {
        get => Legacy.MergeDuplicateEntries;
        set => Legacy.MergeDuplicateEntries = value;
    }

    [JsonIgnore]
    public bool HideVariableEntries
    {
        get => Legacy.HideVariableEntries;
        set => Legacy.HideVariableEntries = value;
    }

    [JsonIgnore]
    public bool MergeConcatenatedStrings
    {
        get => Legacy.MergeConcatenatedStrings;
        set => Legacy.MergeConcatenatedStrings = value;
    }

    [JsonIgnore]
    public List<string> AutoReplaceReferenceFiles
    {
        get => Legacy.AutoReplaceReferenceFiles;
        set => Legacy.AutoReplaceReferenceFiles = value;
    }

    [JsonIgnore]
    public List<string> VariableNameCharacters
    {
        get => Legacy.VariableNameCharacters;
        set => Legacy.VariableNameCharacters = value;
    }

    [JsonIgnore]
    public List<string> Operators
    {
        get => Legacy.Operators;
        set => Legacy.Operators = value;
    }

    public static EraTransUserSettings CreateDefault() => new();

    private static ExtractionMode ParseExtractionMode(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        "lexer" => ExtractionMode.Lexer,
        _ => ExtractionMode.Legacy
    };

    public Encoding CreateFileEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return FileEncodingId switch
        {
            "utf8-bom" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            "utf8" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            "932" => Encoding.GetEncoding(932),
            "936" => Encoding.GetEncoding(936),
            "950" => Encoding.GetEncoding(950),
            _ => throw new InvalidDataException($"不支持的文件编码设置：{FileEncodingId}")
        };
    }

    public EraParserOptions ToParserOptions()
    {
        Validate();

        return new EraParserOptions
        {
            ExtractionMode = ParseExtractionMode(ErbExtractionMode),
            FileEncoding = CreateFileEncoding(),
            ExtractHtmlTitle = ExtractHtmlTitle,
            ExtractPrintDataEntries = ExtractPrintDataEntries,
            SkipNonJapaneseEntries = SkipNonJapaneseEntries,
            WriteContext = WriteContext,
            TargetFileExtensions = TargetFileExtensions.ToArray(),
            Legacy = Legacy.Clone()
        };
    }

    public void Validate()
    {
        // UTF-8 is detected automatically from BOM / strict UTF-8. Older settings stored
        // utf8-bom as the default; migrate those values to the Japanese CP932 fallback.
        if (FileEncodingId is "utf8-bom" or "utf8")
            FileEncodingId = "932";

        if (FileEncodingId is not ("932" or "936" or "950"))
            throw new InvalidDataException($"不支持的默认回退编码设置：{FileEncodingId}");

        ErbExtractionMode = ErbExtractionMode?.Trim().ToLowerInvariant() switch
        {
            "lexer" => "lexer",
            _ => "legacy"
        };

        // Keep older settings files usable: an out-of-range number falls back into
        // the allowed range instead of failing to load.
        WindowMaxCharacters = Math.Clamp(
            WindowMaxCharacters, MinimumWindowCharacters, MaximumWindowCharacters);
        WindowMaxLines = Math.Clamp(
            WindowMaxLines, MinimumWindowLines, MaximumWindowLines);
        TranslationConcurrency = Math.Clamp(
            TranslationConcurrency, MinimumTranslationConcurrency, MaximumTranslationConcurrency);

        LegacyExtractionSettings.ValidateList(TargetFileExtensions, "游戏文件扩展名", value =>
            value.StartsWith(".", StringComparison.Ordinal) &&
            value.Length > 1 &&
            value.IndexOfAny(['/', '\\', '*', '?', ':']) < 0);

        Legacy ??= LegacyExtractionSettings.CreateDefault();
        Legacy.Validate();
    }
}
