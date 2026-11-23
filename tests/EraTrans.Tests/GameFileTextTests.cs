using System.Text;
using EraTrans.Core.WriteBack;
using Xunit;

namespace EraTrans.Tests;

public sealed class GameFileTextTests : IDisposable
{
    private readonly string _directory;

    public GameFileTextTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"EraTransGameFileText-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Read_Utf8WithBom_KeepsBomAndText()
    {
        string path = WriteRaw("bom.ERB", [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("PRINTL こんにちは\r\n")]);

        GameFileText file = GameFileText.Read(path);

        Assert.True(file.HasBom);
        Assert.Equal("PRINTL こんにちは\r\n", file.Text);
        Assert.Equal([0xEF, 0xBB, 0xBF], file.Preamble);
    }

    [Fact]
    public void Read_PlainUtf8_HasNoBom()
    {
        string path = WriteRaw("plain.ERB", Encoding.UTF8.GetBytes("PRINTL さようなら"));

        GameFileText file = GameFileText.Read(path);

        Assert.False(file.HasBom);
        Assert.Equal("PRINTL さようなら", file.Text);
    }

    [Fact]
    public void Read_ShiftJis_DecodesJapanese()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string path = WriteRaw("sjis.ERB", Encoding.GetEncoding(932).GetBytes("PRINTL おはよう"));

        GameFileText file = GameFileText.Read(path);

        Assert.Equal("PRINTL おはよう", file.Text);
        Assert.Equal(932, file.Encoding.CodePage);
    }

    [Fact]
    public void Write_ShiftJis_RoundTripsBytesExactly()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        byte[] original = Encoding.GetEncoding(932).GetBytes("PRINTL こんにちは\r\nPRINTL さようなら\r\n");
        string path = WriteRaw("roundtrip.ERB", original);

        GameFileText file = GameFileText.Read(path);
        file.Write(path);

        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void Write_Utf8Bom_PreservesBomAndCrlf()
    {
        byte[] original = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("PRINTL こんにちは\r\n")];
        string path = WriteRaw("bom-roundtrip.ERB", original);

        GameFileText file = GameFileText.Read(path);
        file.Text = file.Text.Replace("こんにちは", "你好");
        file.Write(path);

        byte[] written = File.ReadAllBytes(path);
        Assert.Equal([0xEF, 0xBB, 0xBF], written[..3]);
        Assert.Equal("PRINTL 你好\r\n", Encoding.UTF8.GetString(written, 3, written.Length - 3));
    }

    [Fact]
    public void DetectEncoding_ReturnsShiftJis_WhenBytesAreNotValidUtf8()
    {
        byte[] bytes = [0x82, 0xB1, 0x82, 0xF1]; // "こん" in Shift-JIS

        DetectedEncoding detected = GameFileText.DetectEncoding(bytes);

        Assert.Equal(932, detected.Encoding.CodePage);
        Assert.Equal(0, detected.BodyOffset);
    }

    [Fact]
    public void DetectEncoding_ReturnsUtf8_WhenBytesAreValidUtf8()
    {
        DetectedEncoding detected = GameFileText.DetectEncoding(Encoding.UTF8.GetBytes("さようなら"));

        Assert.Equal(new UTF8Encoding(false).CodePage, detected.Encoding.CodePage);
        Assert.Empty(detected.Preamble);
    }

    [Fact]
    public void Read_Utf16LeWithBom_DecodesAndRoundTripsBytes()
    {
        string content = "PRINTL こんにちは\r\n@MENU\r\nPRINTBUTTON \"[\" + LOCALS + \"] 突撃\", 100\r\n";
        byte[] original = [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(content)];
        string path = WriteRaw("utf16.ERB", original);

        GameFileText file = GameFileText.Read(path);

        Assert.Equal(content, file.Text);
        Assert.Equal([0xFF, 0xFE], file.Preamble);
        Assert.Equal(Encoding.Unicode.CodePage, file.Encoding.CodePage);

        file.Write(path);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void DetectEncoding_RecognizesBomlessUtf16Le()
    {
        byte[] bytes = Encoding.Unicode.GetBytes("PRINTL こんにちは\r\n@MENU\r\nPRINTFORML おはよう\r\n");

        DetectedEncoding detected = GameFileText.DetectEncoding(bytes);

        Assert.Equal(Encoding.Unicode.CodePage, detected.Encoding.CodePage);
        Assert.Equal(0, detected.BodyOffset);
        Assert.Empty(detected.Preamble);
    }

    [Fact]
    public void DetectEncoding_RecognizesBomlessUtf16Be()
    {
        byte[] bytes = Encoding.BigEndianUnicode.GetBytes("PRINTL こんにちは\r\n@MENU\r\nPRINTFORML おはよう\r\n");

        DetectedEncoding detected = GameFileText.DetectEncoding(bytes);

        Assert.Equal(Encoding.BigEndianUnicode.CodePage, detected.Encoding.CodePage);
    }


    [Fact]
    public void DetectEncoding_RejectsGbkBytesThatAreAlsoValidUtf8WithDifferentText()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding gbk = Encoding.GetEncoding(936);
        byte[] bytes = gbk.GetBytes("学习");

        Assert.Equal("学习", gbk.GetString(bytes));
        Assert.Equal("ѧϰ", new UTF8Encoding(false, true).GetString(bytes));

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => GameFileText.DetectEncoding(bytes, gbk));

        Assert.Contains("歧义", error.Message);
    }

    [Fact]
    public void Read_UsesXmlEncodingDeclarationBeforeConfiguredFallback()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        byte[] bytes = Encoding.GetEncoding(936).GetBytes(
            "<?xml version=\"1.0\" encoding=\"gbk\"?>\r\n<root>你好</root>");
        string path = WriteRaw("declared-gbk.xml", bytes);

        GameFileText file = GameFileText.Read(path, Encoding.GetEncoding(932));

        Assert.Equal(936, file.Encoding.CodePage);
        Assert.Contains("<root>你好</root>", file.Text);
    }

    private string WriteRaw(string name, byte[] bytes)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void Read_Cp932AliasNormalization_RespectsDoubleByteCharacterBoundaries()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        // 0x82 0x87 = full-width 'ｇ', 0x90 0x6C = '人', 0x87 0x90 = CP932 alias for '≒'.
        // Bytes [1..2] are 0x87 0x90 across a character boundary and must not be rewritten.
        byte[] bytes = [0x82, 0x87, 0x90, 0x6C, 0x87, 0x90];
        string path = WriteRaw("cp932-boundary.erb", bytes);

        GameFileText decoded = GameFileText.Read(path, Encoding.GetEncoding(932));

        Assert.Equal(932, decoded.Encoding.CodePage);
        Assert.Equal("ｇ人≒", decoded.Text);
    }

    [Fact]
    public void Read_UsesConfiguredEncodingAsFallbackForChineseLocalizedGames()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string path = Path.Combine(Path.GetTempPath(), "eratrans-gbk-" + Guid.NewGuid().ToString("N") + ".erb");
        try
        {
            File.WriteAllBytes(path, Encoding.GetEncoding(936).GetBytes("PRINTL 你好，世界"));

            GameFileText decoded = GameFileText.Read(path, Encoding.GetEncoding(936));
            Assert.Equal(936, decoded.Encoding.CodePage);
            Assert.Contains("你好", decoded.Text);
            Assert.DoesNotContain('\uFFFD', decoded.Text);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
