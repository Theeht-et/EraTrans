using System.Text;

namespace EraTrans.Core;

/// <summary>
/// Settings for parsing ERA source files and applying dictionary entries.
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
    /// Controls automatic variable correction when applying translations.
    /// </summary>
    public bool EnableVariableAutoCorrection { get; init; } = true;

    /// <summary>
    /// Top-level CSV dictionary names used for variable auto-correction,
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
    /// Game-file extensions checked when applying a dictionary at the same relative path.
    /// </summary>
    public IReadOnlyList<string> TargetFileExtensions { get; init; } =
    [
        ".csv",
        ".erb",
        ".erh",
        ".erd"
    ];

    /// <summary>
    /// Additional characters allowed in ERA variable names.
    /// </summary>
    public IReadOnlyList<string> VariableNameCharacters { get; init; } =
    [
        "☆", "♡", "∀", "←", "→"
    ];

    /// <summary>
    /// Operators recognized by the ERA expression parser.
    /// </summary>
    public IReadOnlyList<string> Operators { get; init; } =
    [
        "(", ")", "{", "}", "?", "#", "=", "+=", "-=", "*=", "/=",
        ">", "<", "==", "!=", ">=", "<=", "&&", "||", "&", "|", "!",
        "+", "-", "*", "/", "%", "++", "--", ",", ":", "TO"
    ];
}