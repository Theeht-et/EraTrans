namespace EraTrans.Core;

/// <summary>
/// Isolated configuration for the legacy ERB parser and legacy variable auto-correction
/// inherited from ErbParserForTranslate. Kept separate from EraTrans's own settings so
/// the legacy extraction path can be removed cleanly in the future.
/// </summary>
public sealed class LegacyExtractionSettings
{
    internal static readonly string[] DefaultAutoReplaceReferenceFiles =
    [
        "Abl", "Base", "CFlag", "CSTR", "Equip", "Ex",
        "Exp", "Flag", "Global", "Item", "Juel",
        "Mark", "Nowex", "Palam", "Source", "Stain",
        "Talent", "TCVAR", "TEquip", "Tflag", "Train",
        "TSTR", "修正字典"
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

    public bool HideEnglishText { get; set; } = true;
    public bool MergeDuplicateEntries { get; set; } = false;
    public bool HideVariableEntries { get; set; } = true;
    public bool MergeConcatenatedStrings { get; set; } = true;

    /// <summary>
    /// Controls legacy whole-script variable auto-correction when applying translations.
    /// Disabled by default in favor of display-layer variable name translation.
    /// </summary>
    public bool EnableVariableAutoCorrection { get; set; } = false;

    /// <summary>
    /// Top-level CSV dictionary names used for legacy variable auto-correction,
    /// without the .json extension.
    /// </summary>
    public List<string> AutoReplaceReferenceFiles { get; set; } = [.. DefaultAutoReplaceReferenceFiles];

    /// <summary>
    /// Additional characters allowed in ERA variable names by the legacy expression parser.
    /// </summary>
    public List<string> VariableNameCharacters { get; set; } = [.. DefaultVariableNameCharacters];

    /// <summary>
    /// Operators recognized by the legacy ERA expression parser.
    /// </summary>
    public List<string> Operators { get; set; } = [.. DefaultOperators];

    public static LegacyExtractionSettings CreateDefault() => new();

    public LegacyExtractionSettings Clone() => new()
    {
        HideEnglishText = HideEnglishText,
        MergeDuplicateEntries = MergeDuplicateEntries,
        HideVariableEntries = HideVariableEntries,
        MergeConcatenatedStrings = MergeConcatenatedStrings,
        EnableVariableAutoCorrection = EnableVariableAutoCorrection,
        AutoReplaceReferenceFiles = AutoReplaceReferenceFiles is null ? null! : [.. AutoReplaceReferenceFiles],
        VariableNameCharacters = VariableNameCharacters is null ? null! : [.. VariableNameCharacters],
        Operators = Operators is null ? null! : [.. Operators]
    };

    public void Validate()
    {
        ValidateList(AutoReplaceReferenceFiles, "变量自动修正参考文件", value =>
            !Path.IsPathRooted(value) &&
            Path.GetFileName(value).Equals(value, StringComparison.Ordinal) &&
            !value.Contains('/') &&
            !value.Contains('\\') &&
            !value.EndsWith(".json", StringComparison.OrdinalIgnoreCase));

        ValidateList(VariableNameCharacters, "变量名字符", value => value.Length == 1);
        ValidateList(Operators, "操作符", value => !value.Any(char.IsWhiteSpace));
    }

    internal static void ValidateList(
        List<string>? values,
        string displayName,
        Func<string, bool> predicate)
    {
        if (values is null || values.Count == 0)
        {
            throw new InvalidDataException($"{displayName}列表不能为空，且不能包含无效项目。");
        }

        for (int index = 0; index < values.Count; index++)
        {
            string? value = values[index];
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidDataException($"{displayName}列表不能为空，且不能包含无效项目。");
            }

            string normalizedValue = value.Trim();
            if (!predicate(normalizedValue))
            {
                throw new InvalidDataException($"{displayName}列表不能为空，且不能包含无效项目。");
            }

            values[index] = normalizedValue;
        }

        if (values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != values.Count)
        {
            throw new InvalidDataException($"{displayName}列表包含重复项目。");
        }
    }
}
