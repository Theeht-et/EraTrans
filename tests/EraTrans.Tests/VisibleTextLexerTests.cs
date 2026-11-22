using EraTrans.Core.VisibleText;
using Xunit;

namespace EraTrans.Tests;

public sealed class VisibleTextLexerTests
{
    private static readonly IReadOnlyCollection<string> NoMacros = Array.Empty<string>();

    [Fact]
    public void PrintButton_ConcatenatedLiteral_IsExtractedWithItsOwnSpan()
    {
        const string text = "@MENU\nPRINTBUTTON \"[\" + LOCALS + \"] 突撃\", 100\n";

        VisibleTextFileResult result = Lex(text);

        VisibleTextEntry entry = Assert.Single(result.Entries);
        Assert.Equal("] 突撃", entry.Template);
        Assert.Equal("] 突撃", Slice(text, entry.Span));
        Assert.Equal("\"", text.Substring(entry.Span.Start - 1, 1));
        Assert.Equal("\"", text.Substring(entry.Span.End, 1));
    }

    [Fact]
    public void PrintForm_ConcatenatedLiteralInsidePlaceholder_IsExtracted()
    {
        const string text = "@SHOW\nPRINTFORML %GET_SCHOOL_NAME(MASTER) + \"所属\",24,LEFT%\n";

        VisibleTextFileResult result = Lex(text);

        VisibleTextEntry entry = Assert.Single(result.Entries);
        Assert.Equal("所属", entry.Template);
        Assert.Equal("所属", Slice(text, entry.Span));
    }

    [Fact]
    public void PrintForm_SingleQuotedFormatString_IsNotDuplicated()
    {
        const string text = "@SHOW\nPRINTFORM %\"[901] - 先頭\",14,LEFT%\n";

        VisibleTextFileResult result = Lex(text);

        VisibleTextEntry entry = Assert.Single(result.Entries);
        Assert.Equal("%\"[901] - 先頭\",14,LEFT%", entry.Template);
        Assert.Equal("[901] - 先頭", Slice(text, Assert.Single(entry.Parts)));
    }

    private static VisibleTextFileResult Lex(string text, IReadOnlyCollection<string>? macros = null)
    {
        return ErbVisibleTextLexer.ExtractFile("ERB/test.erb", text, macros ?? NoMacros);
    }

    private static string Slice(string text, VisibleTextSpan span)
    {
        return text.Substring(span.Start, span.End - span.Start);
    }

    [Fact]
    public void PrintFormw_SplitsLiteralsAroundPlaceholders()
    {
        const string text = "@SYSTEM_TITLE\nPRINTFORMW おはよう%CALLNAME:MASTER%、いい朝だ\n";
        VisibleTextFileResult result = Lex(text);

        VisibleTextEntry entry = Assert.Single(result.Entries);
        Assert.Equal("form", entry.Kind);
        Assert.Equal("PRINTFORMW", entry.Command);
        Assert.Equal(2, entry.Line);
        Assert.Equal("おはよう%CALLNAME:MASTER%、いい朝だ", entry.Template);
        Assert.Equal(2, entry.Parts.Count);
        Assert.Equal("おはよう", Slice(text, entry.Parts[0]));
        Assert.Equal("、いい朝だ", Slice(text, entry.Parts[1]));
    }

    [Fact]
    public void Form_KeepsConditionalSyntaxAndBothBranches()
    {
        const string text = "@FUNC\nPRINTFORML \\@ TALENT:MASTER:素質 ? \"晴れ\" # \"雨\" \\@の日\n";
        VisibleTextFileResult result = Lex(text);

        VisibleTextEntry entry = Assert.Single(result.Entries);
        Assert.Equal("form", entry.Kind);
        Assert.Contains("\\@", entry.Template, StringComparison.Ordinal);
        Assert.Contains("#", entry.Template, StringComparison.Ordinal);
        Assert.Equal(3, entry.Parts.Count);
        Assert.Equal("\"晴れ\"", Slice(text, entry.Parts[0]));
        Assert.Equal("\"雨\"", Slice(text, entry.Parts[1]));
        Assert.Equal("の日", Slice(text, entry.Parts[2]));
    }

    [Fact]
    public void PrintButton_TakesOnlyDisplayString()
    {
        const string text = "@FUNC\nPRINTBUTTON \"押してください\", 12, 34\n";
        VisibleTextFileResult result = Lex(text);

        VisibleTextEntry entry = Assert.Single(result.Entries);
        Assert.Equal("button", entry.Kind);
        Assert.Equal("PRINTBUTTON", entry.Command);
        Assert.Equal("押してください", entry.Template);
        Assert.Equal("押してください", Slice(text, Assert.Single(entry.Parts)));
    }

    [Fact]
    public void HtmlPrint_StripsTagsAndEmitsTextNodes()
    {
        const string text = "@FUNC\nHTML_PRINT \"<b>太字</b>と<font color='red'>赤い文字</font>\"\n";
        VisibleTextFileResult result = Lex(text);

        Assert.Equal(3, result.Entries.Count);
        Assert.All(result.Entries, entry => Assert.Equal("html", entry.Kind));
        Assert.Equal(new[] { "太字", "と", "赤い文字" }, result.Entries.Select(e => e.Template).ToArray());
        Assert.All(result.Entries, entry => Assert.Equal(Slice(text, Assert.Single(entry.Parts)), entry.Template));
    }

    [Fact]
    public void PrintData_EmitsDataAndDataformEntries()
    {
        const string text = "@FUNC\nPRINTDATA\n    DATA 選択肢の一\n    DATA 選択肢の二\n    DATAFORM %NAME%です\nENDDATA\n";
        VisibleTextFileResult result = Lex(text);

        Assert.Equal(3, result.Entries.Count);
        Assert.All(result.Entries, entry => Assert.Equal("data", entry.Kind));
        Assert.Equal(
            new[] { "選択肢の一", "選択肢の二", "%NAME%です" },
            result.Entries.Select(e => e.Template).ToArray());
        Assert.Equal(new[] { 3, 4, 5 }, result.Entries.Select(e => e.Line).ToArray());
    }

    [Fact]
    public void LocalsAssignment_EmitsVarEntryForLaterPrint()
    {
        const string text = "@FUNC\nLOCALS = 俺の名前\nPRINTFORML %LOCALS%だ\n";
        VisibleTextFileResult result = Lex(text);

        Assert.Equal(2, result.Entries.Count);

        VisibleTextEntry varEntry = Assert.Single(result.Entries, e => e.Kind == "var");
        Assert.Equal("LOCALS (var)", varEntry.Command);
        Assert.Equal(2, varEntry.Line);
        Assert.Equal("俺の名前", varEntry.Template);

        VisibleTextEntry printEntry = Assert.Single(result.Entries, e => e.Kind == "form");
        Assert.Equal("PRINTFORML", printEntry.Command);
        Assert.Equal("%LOCALS%だ", printEntry.Template);
    }

    [Fact]
    public void SkipStartBlock_IsSkipped()
    {
        const string text = "@FUNC\nPRINTFORML 表に見える\n[SKIPSTART]\nPRINTFORML 裏に隠れる\n[SKIPEND]\nPRINTFORML また見える\n";
        VisibleTextFileResult result = Lex(text);

        Assert.Equal(new[] { "表に見える", "また見える" }, result.Entries.Select(e => e.Template).ToArray());
    }

    [Fact]
    public void IfDebugBlock_IsSkipped()
    {
        const string text = "@FUNC\n[IF_DEBUG]\nPRINTFORML デバッグ表示\n[ENDIF]\nPRINTFORML 通常表示\n";
        VisibleTextFileResult result = Lex(text);

        VisibleTextEntry entry = Assert.Single(result.Entries);
        Assert.Equal("通常表示", entry.Template);
    }

    [Fact]
    public void IfMacro_UsesProvidedMacroTable()
    {
        const string text = "@FUNC\n[IF EXTRA_SCENE]\nPRINTFORML 追加の場面\n[ENDIF]\nPRINTFORML 基本の場面\n";

        Assert.Equal(new[] { "基本の場面" }, Lex(text).Entries.Select(e => e.Template).ToArray());
        Assert.Equal(
            new[] { "追加の場面", "基本の場面" },
            Lex(text, new[] { "EXTRA_SCENE" }).Entries.Select(e => e.Template).ToArray());
    }

    [Fact]
    public void Spans_AreWholeFileOffsetsWithCrLf()
    {
        const string text = ";\r\n@FUNC\r\nPRINTFORML あ%C%い\r\n";
        VisibleTextFileResult result = Lex(text);

        VisibleTextEntry entry = Assert.Single(result.Entries);
        Assert.Equal(2, entry.Parts.Count);
        Assert.Equal("あ", Slice(text, entry.Parts[0]));
        Assert.Equal("い", Slice(text, entry.Parts[1]));

        // The literal offsets must be usable for write-back, i.e. they point into the original
        // text exactly once and in order.
        Assert.Equal(21, entry.Parts[0].Start);
        Assert.True(entry.Parts[0].End <= entry.Parts[1].Start);
    }

    [Fact]
    public void FullWidthSpace_IsSeparatorAndPlainLinesDropIt()
    {
        const string text = "@FUNC\nPRINTFORML\u3000全角の空白\n";
        VisibleTextFileResult result = Lex(text);

        VisibleTextEntry entry = Assert.Single(result.Entries);
        Assert.Equal("全角の空白", entry.Template);
    }

    [Fact]
    public void StrdadaBlock_RowsAreExtractedAsData()
    {
        const string text = "@FUNC\nSTRDATA LOCALS\nDATA ひとつ\nDATA ふたつ\nENDDATA\nPRINTFORML %LOCALS%\n";
        VisibleTextFileResult result = Lex(text);

        Assert.Equal(new[] { "ひとつ", "ふたつ" }, result.Entries.Where(e => e.Kind == "data").Select(e => e.Template).ToArray());
    }

    [Fact]
    public void BraceJoinedLines_AreSkipped()
    {
        const string text = "@FUNC\nSELECTCASE 1\n{\nCASE あ, い\n}\nPRINTFORML 表示される\n";
        VisibleTextFileResult result = Lex(text);

        VisibleTextEntry entry = Assert.Single(result.Entries);
        Assert.Equal("表示される", entry.Template);
    }

    [Fact]
    public void EscapedBrace_DoesNotOpenAJoin()
    {
        const string text = "@FUNC\nLOCALS:0 = \\{\nPRINTFORML 表示される\n";
        VisibleTextFileResult result = Lex(text);

        Assert.Contains(result.Entries, e => e.Template == "表示される");
    }

    [Fact]
    public void FullWidthSpaceIndentation_DoesNotHideTheCommand()
    {
        // Imported scripts often indent with U+3000 (the "full-width space" that IDEs insert).
        const string text = "@FUNC\n\u3000\u3000PRINTFORMW \u300c\u4eca\u65e5\u306f\u6674\u5929\u3060\u300d\n";
        VisibleTextFileResult result = Lex(text);

        VisibleTextEntry entry = Assert.Single(result.Entries);
        Assert.Equal("PRINTFORMW", entry.Command);
        Assert.Equal("\u300c\u4eca\u65e5\u306f\u6674\u5929\u3060\u300d", entry.Template);
    }

    [Fact]
    public void AsciiArtLineEndingWithBrace_DoesNotSwallowFollowingLines()
    {
        // Only a line that is exactly "{" starts an Emuera line continuation. ASCII art that happens
        // to end with a brace is ordinary text and must not hide the rest of the file.
        const string text = "@FUNC\nPRINTL \u2500\u2500\u2510 {{ \u2500\u2500 {\nPRINTFORML \u8868\u793a\u3055\u308c\u308b\n";
        VisibleTextFileResult result = Lex(text);

        // The art line itself is not Japanese, so it is not an entry; the point is that it must not
        // open a continuation block that hides everything after it.
        VisibleTextEntry entry = Assert.Single(result.Entries);
        Assert.Equal(3, entry.Line);
        Assert.Equal("\u8868\u793a\u3055\u308c\u308b", entry.Template);
    }

    [Fact]
    public void HalfWidthKatakanaSfx_IsExtracted()
    {
        const string text = "@FUNC\nPRINTFORMW \u300c\u2026\u2026\u2026\u300d\uff7c\uff9e\uff70\uff6f\n";
        VisibleTextFileResult result = Lex(text);

        VisibleTextEntry entry = Assert.Single(result.Entries);
        Assert.Contains("\uff7c\uff9e\uff70\uff6f", entry.Template);
    }
}
