using System.Text;

namespace EraTrans.Core.WriteBack;

public sealed record SpanApplyResult(
    string Text,
    int AppliedCount,
    int SkippedCount,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Applies position-anchored edits to one game source file.
/// Edits are sorted by position and applied in a single forward pass, which keeps the cost
/// linear even for files with thousands of spans. Every edit is verified against the text that
/// was extracted for that span before anything is replaced; overlapping or stale edits are
/// skipped with a warning instead of silently corrupting the file.
/// </summary>
public static class SpanApplier
{
    public static SpanApplyResult Apply(
        string text,
        IReadOnlyList<SpanEdit> edits,
        ICollection<SpanEdit>? acceptedEdits = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(edits);

        var warnings = new List<string>();
        if (edits.Count == 0)
        {
            return new SpanApplyResult(text, 0, 0, warnings);
        }

        var ordered = edits
            .OrderBy(edit => edit.Span.Start)
            .ThenBy(edit => edit.Span.End)
            .ToArray();

        int applied = 0;
        int skipped = 0;
        int cursor = 0;
        var builder = new StringBuilder(text.Length);

        foreach (SpanEdit edit in ordered)
        {
            string key = string.IsNullOrEmpty(edit.Key) ? "(无键)" : edit.Key;

            if (!edit.Span.IsValid || edit.Span.End > text.Length)
            {
                skipped++;
                warnings.Add($"定位越界，已跳过：{key} {edit.Span}");
                continue;
            }

            if (edit.Span.Length == 0)
            {
                skipped++;
                warnings.Add($"定位为空，已跳过：{key} {edit.Span}");
                continue;
            }

            if (edit.Span.Start < cursor)
            {
                skipped++;
                warnings.Add($"定位重叠，已跳过：{key} {edit.Span}");
                continue;
            }

            if (!Matches(text, edit.Span, edit.Original))
            {
                skipped++;
                warnings.Add($"原文与定位不符，已跳过：{key} {edit.Span}");
                continue;
            }

            builder.Append(text, cursor, edit.Span.Start - cursor);
            builder.Append(edit.Replacement);
            cursor = edit.Span.End;
            applied++;
            acceptedEdits?.Add(edit);
        }

        if (applied == 0)
        {
            return new SpanApplyResult(text, 0, skipped, warnings);
        }

        builder.Append(text, cursor, text.Length - cursor);
        return new SpanApplyResult(builder.ToString(), applied, skipped, warnings);
    }

    private static bool Matches(string text, TextSpan span, string expected)
    {
        if (span.Length != expected.Length)
        {
            return false;
        }

        return string.CompareOrdinal(text, span.Start, expected, 0, expected.Length) == 0;
    }
}
