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
    public bool HideVariableEntries { get; init; } = false;
    public bool MergeConcatenatedStrings { get; init; } = true;
    public bool WriteContext { get; init; } = true;
}