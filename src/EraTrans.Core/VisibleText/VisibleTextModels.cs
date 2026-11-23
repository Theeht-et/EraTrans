namespace EraTrans.Core.VisibleText;

/// <summary>
/// Selects which extraction strategy the application uses for ERB visible text.
/// Kept explicit so callers can fall back to the legacy dictionary parser.
/// </summary>
public enum ExtractionMode
{
    /// <summary>Original whole-file dictionary parser (values are not position anchored).</summary>
    Legacy = 0,

    /// <summary>Line-oriented visible text lexer (this namespace).</summary>
    Lexer = 1
}

/// <summary>
/// A half-open character range <c>[Start, End)</c> of one decoded ERB file.
/// Offsets are character (not byte) offsets into the whole file, so they can be used
/// directly against the string produced by <see cref="EraTrans.Core.WriteBack.GameFileText"/>.
/// </summary>
public readonly record struct VisibleTextSpan(int Start, int End)
{
    public int Length => End - Start;

    public bool IsValid => Start >= 0 && End >= Start;

    public override string ToString() => $"[{Start},{End})";
}

/// <summary>
/// One visible text entry.
/// <para><see cref="Template"/> keeps placeholders (<c>%..%</c>, <c>{..}</c>, <c>\@ .. \@</c>) verbatim.</para>
/// <para><see cref="Parts"/> are the literal fragments of <see cref="Template"/>; the text between
/// two parts is a placeholder. Every part can be sliced out of the source file with its span.</para>
/// </summary>
/// <param name="RelativePath">Game-relative path with forward slashes, e.g. <c>ERB/コマンド/COMF400_移動.erb</c>.</param>
/// <param name="Line">1-based line number the entry starts on.</param>
/// <param name="Command">Instruction that produced the entry (for variable entries the variable name plus <c> (var)</c>).</param>
/// <param name="Kind">One of <c>print</c>, <c>form</c>, <c>plain</c>, <c>html</c>, <c>button</c>, <c>data</c>, <c>var</c>, <c>custom</c>.</param>
/// <param name="Template">Source template with placeholders kept.</param>
/// <param name="Parts">Absolute literal fragments of the template, in source order.</param>
/// <param name="Span">Absolute range of the whole template; <c>slice(Span) == Template</c>.</param>
public sealed record VisibleTextEntry(
    string RelativePath,
    int Line,
    string Command,
    string Kind,
    string Template,
    IReadOnlyList<VisibleTextSpan> Parts,
    VisibleTextSpan Span);

/// <summary>
/// Coverage hints collected while lexing: lines whose visible text may come from a source the
/// lexer cannot read statically (unknown commands, variables assigned elsewhere, CSV names).
/// They are reported to the user instead of silently dropping the text.
/// </summary>
public sealed class VisibleTextDiagnostics
{
    /// <summary>Unknown leading token → number of lines whose argument contains Japanese text.</summary>
    public Dictionary<string, int> UnknownCommands { get; } = new(StringComparer.Ordinal);

    /// <summary>Up to three samples per unknown command, formatted <c>TOKEN file:line  text</c>.</summary>
    public List<string> UnknownCommandSamples { get; } = new();

    /// <summary>Variable → number of Japanese literal assignments whose value is later printed.</summary>
    public Dictionary<string, int> AssignedAndPrintedVars { get; } = new(StringComparer.Ordinal);

    /// <summary>Number of Japanese literal assignments that are printed but not extracted.</summary>
    public int AssignedAndPrintedLines { get; set; }

    /// <summary>Print lines whose Japanese text sits only inside placeholders (e.g. <c>%CALLNAME%</c>).</summary>
    public int PlaceholderOnlyPrintLines { get; set; }

    public bool IsEmpty =>
        UnknownCommands.Count == 0 && AssignedAndPrintedLines == 0 && PlaceholderOnlyPrintLines == 0;

    /// <summary>Merges another file's diagnostics into this aggregate.</summary>
    public void MergeFrom(VisibleTextDiagnostics other)
    {
        ArgumentNullException.ThrowIfNull(other);
        foreach ((string command, int count) in other.UnknownCommands)
        {
            UnknownCommands[command] = UnknownCommands.TryGetValue(command, out int existing)
                ? existing + count
                : count;
        }

        UnknownCommandSamples.AddRange(other.UnknownCommandSamples);
        foreach ((string name, int count) in other.AssignedAndPrintedVars)
        {
            AssignedAndPrintedVars[name] = AssignedAndPrintedVars.TryGetValue(name, out int existing)
                ? existing + count
                : count;
        }

        AssignedAndPrintedLines += other.AssignedAndPrintedLines;
        PlaceholderOnlyPrintLines += other.PlaceholderOnlyPrintLines;
    }
}

/// <summary>Lexer result for a single ERB file.</summary>
public sealed record VisibleTextFileResult(
    string RelativePath,
    IReadOnlyList<VisibleTextEntry> Entries,
    IReadOnlyList<string> Warnings)
{
    /// <summary>Coverage hints for this file.</summary>
    public VisibleTextDiagnostics Diagnostics { get; init; } = new();
}

/// <summary>Result of extracting a whole ERB directory.</summary>
/// <param name="GeneratedFiles">Dictionary JSON and position sidecar files written.</param>
/// <param name="Warnings">Per-file problems; the affected file is skipped and extraction continues.</param>
/// <param name="FileCount">Number of ERB files scanned.</param>
/// <param name="EntryCount">Number of extracted entries over all files.</param>
public sealed record VisibleTextExtractionResult(
    IReadOnlyList<string> GeneratedFiles,
    IReadOnlyList<string> Warnings,
    int FileCount,
    int EntryCount)
{
    /// <summary>Entries whose existing translation was carried over (same key and original).</summary>
    public int PreservedTranslations { get; init; }

    /// <summary>Entries that had a translation for the key but the source text changed.</summary>
    public int ResetTranslations { get; init; }

    /// <summary>Coverage hints aggregated over all files.</summary>
    public VisibleTextDiagnostics Diagnostics { get; init; } = new();
}
