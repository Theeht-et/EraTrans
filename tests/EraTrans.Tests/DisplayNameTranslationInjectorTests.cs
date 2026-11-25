using System.Text;
using EraTrans.Core;
using Xunit;

namespace EraTrans.Tests;

public sealed class DisplayNameTranslationInjectorTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "EraTrans.DisplayNameTests." + Guid.NewGuid().ToString("N"));

    public DisplayNameTranslationInjectorTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData(
        "PRINTFORMW 『%ITEMNAME:商品ID%』はすでに所持しています。",
        "PRINTFORMW 『%ERATRANS_TR_NAME(ITEMNAME:商品ID)%』はすでに所持しています。",
        1)]
    [InlineData(
        "PRINTFORM   [{商品ID, 3}] %ITEMNAME:商品ID, 18, LEFT%",
        "PRINTFORM   [{商品ID, 3}] %ERATRANS_TR_NAME(ITEMNAME:商品ID), 18, LEFT%",
        1)]
    [InlineData(
        "PRINTFORML 仕掛けたエサ：%ITEMNAME:(GET_MUSHIDATA(MUSHI_TRAP, \"エサ番号\"))%",
        "PRINTFORML 仕掛けたエサ：%ERATRANS_TR_NAME(ITEMNAME:(GET_MUSHIDATA(MUSHI_TRAP, \"エサ番号\")))%",
        1)]
    [InlineData(
        "CALL COLORMESSAGE(@\"%BASENAME:BASE_ID%＋{回復量}\", C_GREEN, 1)",
        "CALL COLORMESSAGE(@\"%ERATRANS_TR_NAME(BASENAME:BASE_ID)%＋{回復量}\", C_GREEN, 1)",
        1)]
    public void RewriteLine_WrapsSafeDisplayTokensAndPreservesFormatAndNestedParens(
        string input,
        string expected,
        int expectedCount)
    {
        string actual = DisplayNameTranslationInjector.RewriteLine(input, out int count);

        Assert.Equal(expected, actual);
        Assert.Equal(expectedCount, count);
    }

    [Theory]
    [InlineData("IF ABLNAME:能力 == \"性知識\"")]
    [InlineData("SELECTCASE ITEMNAME:DISH_DRG")]
    [InlineData("IF FIRSTTIME(TRAINNAME:SELECTCOM)")]
    [InlineData("PRINTFORML 「\\@ TRAINNAME:PREVCOM == \"掃除\" ? 掃除中 # くつろぎ \\@」")]
    [InlineData("LOCALS = %\"[\" + ITEMNAME:(酒配列:LOCAL) + \"]\", 10, LEFT%")]
    [InlineData("PRINTFORM %SUBSTRINGU(ABLNAME:(ARG:LOCAL), 0, 1)%:")]
    [InlineData("; PRINTFORM %ITEMNAME:商品ID%")]
    public void RewriteLine_SkipsLogicComparisonsTernaryAndCompoundExpressions(string input)
    {
        string actual = DisplayNameTranslationInjector.RewriteLine(input, out int count);

        Assert.Equal(input, actual);
        Assert.Equal(0, count);
    }

    [Fact]
    public void ApplyToGameCopy_InjectsHelperAndWrapsDisplayTokensWithoutTouchingCsvOrSav()
    {
        string copyDir = Path.Combine(_tempRoot, "GameCopy");
        string erbDir = Path.Combine(copyDir, "ERB");
        string csvDir = Path.Combine(copyDir, "CSV");
        string savDir = Path.Combine(copyDir, "sav");
        Directory.CreateDirectory(erbDir);
        Directory.CreateDirectory(csvDir);
        Directory.CreateDirectory(savDir);

        string csvPath = Path.Combine(csvDir, "Item.csv");
        string savPath = Path.Combine(savDir, "save00.dat");
        string erbPath = Path.Combine(erbDir, "SHOP.ERB");
        File.WriteAllText(csvPath, "0,ローター,100\n", Encoding.UTF8);
        File.WriteAllText(savPath, "SAVE_DATA", Encoding.UTF8);
        File.WriteAllText(
            erbPath,
            """
            @SHOP_TEST
            IF ITEMNAME:0 == "ローター"
                PRINTFORML [%ITEMNAME:0, 14, LEFT%]
            ENDIF
            """,
            Encoding.UTF8);

        var glossary = new[]
        {
            new VariableGlossaryEntry("ITEM", "ローター", "跳蛋"),
            new VariableGlossaryEntry("TRAIN", "キス", "接吻"),
            new VariableGlossaryEntry("ITEM", "オナホール", "")
        };

        DisplayNameInjectionResult first = DisplayNameTranslationInjector.ApplyToGameCopy(copyDir, glossary);

        Assert.Equal(2, first.MappedTermCount);
        Assert.Equal(1, first.ModifiedFileCount);
        Assert.Equal(1, first.WrappedTokenCount);
        Assert.NotNull(first.HelperFilePath);
        Assert.True(File.Exists(first.HelperFilePath));

        string helperSource = File.ReadAllText(first.HelperFilePath!, Encoding.UTF8);
        Assert.Contains("@ERATRANS_TR_NAME(ARGS)", helperSource, StringComparison.Ordinal);
        Assert.Contains("CASE \"ローター\"", helperSource, StringComparison.Ordinal);
        Assert.Contains("RETURNF \"跳蛋\"", helperSource, StringComparison.Ordinal);
        Assert.DoesNotContain("オナホール", helperSource, StringComparison.Ordinal);

        string updatedErb = File.ReadAllText(erbPath, Encoding.UTF8);
        Assert.Contains("IF ITEMNAME:0 == \"ローター\"", updatedErb, StringComparison.Ordinal);
        Assert.Contains("[%ERATRANS_TR_NAME(ITEMNAME:0), 14, LEFT%]", updatedErb, StringComparison.Ordinal);

        // CSV and sav/ remain completely untouched.
        Assert.Equal("0,ローター,100\n", File.ReadAllText(csvPath, Encoding.UTF8));
        Assert.Equal("SAVE_DATA", File.ReadAllText(savPath, Encoding.UTF8));

        // Second run is idempotent (already wrapped tokens are not double-wrapped).
        DisplayNameInjectionResult second = DisplayNameTranslationInjector.ApplyToGameCopy(copyDir, glossary);
        Assert.Equal(0, second.WrappedTokenCount);
    }

    [Fact]
    public void EraAnalyzerService_ResolvesBuiltInDllPathInToolsAnalyzerFolder()
    {
        string fakeBase = Path.Combine(_tempRoot, "AppBase");
        string analyzerDir = Path.Combine(fakeBase, "tools", "analyzer");
        Directory.CreateDirectory(analyzerDir);
        string dllPath = Path.Combine(analyzerDir, "Emuera.Core.dll");
        File.WriteAllText(dllPath, "stub", Encoding.UTF8);

        string? resolved = EraAnalyzerService.ResolveBuiltInCoreDllPath(fakeBase);

        Assert.Equal(Path.GetFullPath(dllPath), resolved);
    }

    [Fact]
    public async Task EraAnalyzerService_ThrowsWhenGameCopyDirectoryDoesNotExist()
    {
        var service = new EraAnalyzerService();
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            service.AnalyzeAsync(Path.Combine(_tempRoot, "MissingCopy")));
    }
}
