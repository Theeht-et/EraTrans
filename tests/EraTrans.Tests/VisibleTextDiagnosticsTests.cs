using EraTrans.Core;
using EraTrans.Core.VisibleText;
using Xunit;

namespace EraTrans.Tests;

public sealed class VisibleTextDiagnosticsTests
{
    private static VisibleTextFileResult Lex(string text)
    {
        return ErbVisibleTextLexer.ExtractFile("ERB/test.erb", text, Array.Empty<string>());
    }

    [Fact]
    public void UnknownCommandWithJapaneseArgument_IsReported()
    {
        VisibleTextFileResult result = Lex("@MAIN\nFOO 日本語のテキスト\n");

        Assert.Equal(1, result.Diagnostics.UnknownCommands["FOO"]);
        Assert.Contains(result.Diagnostics.UnknownCommandSamples, sample => sample.StartsWith("FOO "));
        Assert.Empty(result.Entries);
    }

    [Fact]
    public void KnownNonPrintCommand_IsNotReported()
    {
        VisibleTextFileResult result = Lex("@MAIN\nTHROW エラーです\nDT_CLEAR \"キャラＤＢ\"\n");

        Assert.Empty(result.Diagnostics.UnknownCommands);
    }

    [Fact]
    public void AssignedLiteralPrintedBySameFunction_IsReported()
    {
        VisibleTextFileResult result = Lex(
            "@MAIN\nCITY_NAME = 東京\nPRINTFORM %CITY_NAME%へ行く\n");

        Assert.Equal(1, result.Diagnostics.AssignedAndPrintedLines);
        Assert.Equal(1, result.Diagnostics.AssignedAndPrintedVars["CITY_NAME"]);
    }

    [Fact]
    public void AssignmentInAnotherFunction_IsNotReported()
    {
        VisibleTextFileResult result = Lex(
            "@INIT\nCITY_NAME = 東京\n@MAIN\nPRINTFORM %CITY_NAME%へ行く\n");

        Assert.Equal(0, result.Diagnostics.AssignedAndPrintedLines);
    }

    [Fact]
    public void JapaneseOnlyInsidePlaceholder_IsCounted()
    {
        VisibleTextFileResult result = Lex("@MAIN\nPRINTFORML %CALLNAME:マスター%\n");

        Assert.Equal(1, result.Diagnostics.PlaceholderOnlyPrintLines);
    }

    [Fact]
    public void Extraction_ReportsDiagnosticsInWarnings()
    {
        string root = Path.Combine(Path.GetTempPath(), "EraTransDiagnostics-" + Guid.NewGuid().ToString("N"));
        try
        {
            string erb = Path.Combine(root, "game", "ERB");
            Directory.CreateDirectory(erb);
            File.WriteAllText(Path.Combine(erb, "a.erb"), "@MAIN\nFOO 日本語\nPRINTFORMW こんにちは\n", new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            string dictionary = Path.Combine(root, "字典");
            VisibleTextExtractionResult result =
                new VisibleTextExtractor().Extract(Path.Combine(root, "game"), dictionary, "ERB", new EraParserOptions());

            Assert.Equal(1, result.EntryCount);
            Assert.Equal(1, result.Diagnostics.UnknownCommands["FOO"]);
            Assert.Contains(result.Warnings, warning => warning.StartsWith("覆盖率自检：", StringComparison.Ordinal));
            Assert.True(File.Exists(Path.Combine(root, "辅助数据", "visibletext-report.json")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
