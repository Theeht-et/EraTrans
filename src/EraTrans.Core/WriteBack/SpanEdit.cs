namespace EraTrans.Core.WriteBack;

/// <summary>
/// One text replacement anchored to an exact position in a game source file.
/// <paramref name="Original"/> is the extractor's text for that span and is used as a
/// guard: the edit is only applied when the source still contains exactly that text,
/// so a stale sidecar can never overwrite unrelated content.
/// </summary>
public sealed record SpanEdit(string Key, TextSpan Span, string Original, string Replacement);
