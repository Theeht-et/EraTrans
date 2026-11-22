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
        DetectedEncoding detected = GameFileText.DetectEncoding(Encoding.UTF8.GetBytes("こんにちは"));

        Assert.Equal(new UTF8Encoding(false).CodePage, detected.Encoding.CodePage);
        Assert.Empty(detected.Preamble);
    }

    private string WriteRaw(string name, byte[] bytes)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
