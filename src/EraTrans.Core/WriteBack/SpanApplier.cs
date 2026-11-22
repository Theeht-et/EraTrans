using System.Text;

namespace EraTrans.Core.WriteBack;

public sealed record SpanApplyResult(
    string Text,
    int AppliedCount,
    int SkippedCount,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Applies position-anchored edits to one game source file.
/// Edits are applied from the end of the file backwards so that an earlier edit never
/// invalidates the offsets of a later one, and every edit is verified against the text
/// that was extracted for that span before anything is replaced.
/// </summary>
public static class SpanApplier
{
    public static SpanApplyResult Apply(string text, IReadOnlyList<SpanEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(edits);

        var warnings = new List<string>();
        if (edits.Count == 0)
        {
            return new SpanApplyResult(text, 0, 0, warnings);
        }

        // Descending order: later edits are applied first, so the prefix positions of the
        // remaining edits are still valid.
        var ordered = edits
            .OrderByDescending(edit => edit.Span.Start)
            .ThenByDescending(edit => edit.Span.End)
            .ToArray();

        int applied = 0;
        int skipped = 0;
        int limit = text.Length;
        var builder = new StringBuilder(text);

        foreach (SpanEdit edit in ordered)
        {
            string key = string.IsNullOrEmpty(edit.Key) ? "(无键)" : edit.Key;

            if (!edit.Span.IsValid || edit.Span.End > text.Length)
            {
                skipped++;
                warnings.Add($"定位越界，已跳过：{key} {edit.Span}");
                continue;
            }

            if (edit.Span.End > limit)
            {
                skipped++;
                warnings.Add($"定位重叠，已跳过：{key} {edit.Span}");
                continue;
            }

            if (!Matches(builder, edit.Span, edit.Original))
            {
                skipped++;
                warnings.Add($"原文与定位不符，已跳过：{key} {edit.Span}");
                continue;
            }

            builder.Remove(edit.Span.Start, edit.Span.Length);
            builder.Insert(edit.Span.Start, edit.Replacement);
            limit = edit.Span.Start;
            applied++;
        }

        return new SpanApplyResult(builder.ToString(), applied, skipped, warnings);
    }

    private static bool Matches(StringBuilder builder, TextSpan span, string expected)
    {
        if (span.Length != expected.Length)
        {
            return false;
        }

        for (int i = 0; i < expected.Length; i++)
        {
            if (builder[span.Start + i] != expected[i])
            {
                return false;
            }
        }

        return true;
    }
}
