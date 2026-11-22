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
    Lexer = 1,

    /// <summary>Reserved for a future engine-assisted mode.</summary>
    Engine = 2
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

/// <summary>Lexer result for a single ERB file.</summary>
public sealed record VisibleTextFileResult(
    string RelativePath,
    IReadOnlyList<VisibleTextEntry> Entries,
    IReadOnlyList<string> Warnings);

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
}
