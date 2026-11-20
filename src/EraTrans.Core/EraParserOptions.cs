using System.Text;

namespace EraTrans.Core;

/// <summary>
/// Settings retained from the legacy ERA parser. Defaults match the legacy generated config.
/// </summary>
public sealed class EraParserOptions
{
    public Encoding FileEncoding { get; init; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
    public bool ForceFilterVariableNames { get; init; } = true;
    public bool HideEnglishText { get; init; } = true;
    public bool MergeDuplicateEntries { get; init; } = false;
    public bool HideVariableEntries { get; init; } = true;
    public bool MergeConcatenatedStrings { get; init; } = true;
    public bool WriteContext { get; init; } = true;

    /// <summary>
    /// Matches the first-run default in the legacy parser's generated config.json.
    /// </summary>
    public bool EnableVariableAutoCorrection { get; init; } = true;

    /// <summary>
    /// Top-level CSV dictionary names used by the legacy autoReplace step,
    /// without the .json extension.
    /// </summary>
    public IReadOnlyList<string> AutoReplaceReferenceFiles { get; init; } =
    [
        "Abl", "Base", "CFlag", "CSTR", "Equip", "Ex",
        "Exp", "Flag", "Global", "Item", "Juel",
        "Mark", "Nowex", "Palam", "Source", "Stain",
        "Talent", "TCVAR", "TEquip", "Tflag", "Train",
        "TSTR", "修正字典"
    ];

    /// <summary>
    /// File extensions the legacy translator checks for a dictionary with the same relative path.
    /// </summary>
    public IReadOnlyList<string> TargetFileExtensions { get; init; } =
    [
        ".csv",
        ".erb",
        ".erh",
        ".erd"
    ];

    /// <summary>
    /// Characters that may be part of ERA variable names in the legacy expression parser.
    /// </summary>
    public IReadOnlyList<string> VariableNameCharacters { get; init; } =
    [
        "☆", "♡", "∀", "←", "→"
    ];

    /// <summary>
    /// Operators recognized by the legacy ERA expression parser.
    /// </summary>
    public IReadOnlyList<string> Operators { get; init; } =
    [
        "(", ")", "{", "}", "?", "#", "=", "+=", "-=", "*=", "/=",
        ">", "<", "==", "!=", ">=", "<=", "&&", "||", "&", "|", "!",
        "+", "-", "*", "/", "%", "++", "--", ",", ":", "TO"
    ];
}