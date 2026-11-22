using System.Text;
using EraTrans.Core;
using EraTrans.Core.WriteBack;
using Newtonsoft.Json.Linq;
using Xunit;

namespace EraTrans.Tests;

public sealed class SpanWriteBackTests
{
    [Fact]
    public void Apply_UsesSpanSidecar_AndLeavesOtherOccurrencesUntouched()
    {
        string root = CreateTempDirectory();
        string game = Path.Combine(root, "Game");
        string dictionaries = Path.Combine(root, "Dictionaries");
        string output = Path.Combine(root, "WorkingCopy");

        Write(game, "ERB/script.erb", "PRINTFORM 猫咪和猫\nPRINTFORM 猫咪和猫\n");
        WriteJson(dictionaries, "ERB/script.json",
        [
            Entry("文本ERB\\script.00000", "猫咪和猫", "Kitty", 1)
        ]);
        WriteSpanSidecar(dictionaries, "ERB/script",
            ("文本ERB\\script.00000", 10, 14));

        var result = new TranslationApplier().Apply(game, dictionaries, output);

        Assert.Equal(
            "PRINTFORM Kitty\nPRINTFORM 猫咪和猫\n",
            File.ReadAllText(Path.Combine(output, "ERB", "script.erb")));
        Assert.Equal(1, result.SpanReplacedCount);
        Assert.Equal(0, result.FallbackReplacedCount);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Apply_PreservesShiftJisBytesAndLineEndings_WhenSpansUsed()
    {
        string root = CreateTempDirectory();
        string game = Path.Combine(root, "Game");
        string dictionaries = Path.Combine(root, "Dictionaries");
        string output = Path.Combine(root, "WorkingCopy");

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding shiftJis = Encoding.GetEncoding(932);
        const string original = "PRINTL こんにちは\r\nPRINTL そのまま\r\n";
        byte[] originalBytes = shiftJis.GetBytes(original);
        WriteBytes(game, "ERB/script.erb", originalBytes);

        WriteJson(dictionaries, "ERB/script.json",
        [
            Entry("文本ERB\\script.00000", "こんにちは", "こんばんは", 1)
        ]);
        WriteSpanSidecar(dictionaries, "ERB/script",
            ("文本ERB\\script.00000", 7, 12));

        var result = new TranslationApplier().Apply(game, dictionaries, output);

        byte[] written = File.ReadAllBytes(Path.Combine(output, "ERB", "script.erb"));
        string decoded = shiftJis.GetString(written);
        Assert.Equal("PRINTL こんばんは\r\nPRINTL そのまま\r\n", decoded);
        Assert.DoesNotContain((byte)0xEF, written.Take(3));
        Assert.Equal(shiftJis.GetBytes("PRINTL "), written.Take(7).ToArray());
        Assert.Equal(shiftJis.GetBytes("そのまま"), written.Skip(26).Take(shiftJis.GetBytes("そのまま").Length).ToArray());
        Assert.Equal(1, result.SpanReplacedCount);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Apply_SkipsTranslationThatShiftJisCannotRepresent()
    {
        string root = CreateTempDirectory();
        string game = Path.Combine(root, "Game");
        string dictionaries = Path.Combine(root, "Dictionaries");
        string output = Path.Combine(root, "WorkingCopy");

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding shiftJis = Encoding.GetEncoding(932);
        byte[] originalBytes = shiftJis.GetBytes("PRINTL こんにちは\r\n");
        WriteBytes(game, "ERB/script.erb", originalBytes);

        WriteJson(dictionaries, "ERB/script.json",
        [
            Entry("文本ERB\\script.00000", "こんにちは", "你好", 1)
        ]);
        WriteSpanSidecar(dictionaries, "ERB/script",
            ("文本ERB\\script.00000", 7, 12));

        var result = new TranslationApplier().Apply(game, dictionaries, output);

        byte[] written = File.ReadAllBytes(Path.Combine(output, "ERB", "script.erb"));
        Assert.Equal(originalBytes, written);
        Assert.Equal(0, result.SpanReplacedCount);
        Assert.Equal(1, result.SkippedByEncodingCount);
        Assert.Contains(
            result.Warnings,
            warning => warning.Contains("无法表示的字符", StringComparison.Ordinal)
                && warning.Contains("文本ERB\\script.00000", StringComparison.Ordinal));
    }

    [Fact]
    public void Apply_SkipsStaleSpan_AndLeavesCopyByteIdentical()
    {
        string root = CreateTempDirectory();
        string game = Path.Combine(root, "Game");
        string dictionaries = Path.Combine(root, "Dictionaries");
        string output = Path.Combine(root, "WorkingCopy");

        Write(game, "ERB/script.erb", "PRINTFORM 猫咪和猫\n");
        WriteJson(dictionaries, "ERB/script.json",
        [
            Entry("文本ERB\\script.00000", "猫咪和猫", "Kitty", 1)
        ]);
        // The sidecar points at a range that no longer holds the extracted text.
        WriteSpanSidecar(dictionaries, "ERB/script",
            ("文本ERB\\script.00000", 10, 11));

        var result = new TranslationApplier().Apply(game, dictionaries, output);

        Assert.Equal(
            "PRINTFORM 猫咪和猫\n",
            File.ReadAllText(Path.Combine(output, "ERB", "script.erb")));
        Assert.Equal(0, result.SpanReplacedCount);
        Assert.Contains(result.Warnings, warning => warning.Contains("原文与定位不符", StringComparison.Ordinal));
    }

    [Fact]
    public void Apply_DoesNotRewriteFilesWithoutChanges()
    {
        string root = CreateTempDirectory();
        string game = Path.Combine(root, "Game");
        string dictionaries = Path.Combine(root, "Dictionaries");
        string output = Path.Combine(root, "WorkingCopy");

        byte[] originalBytes = Encoding.UTF8.GetBytes("PRINTFORM 原文\r\n");
        WriteBytes(game, "ERB/script.erb", originalBytes);
        WriteJson(dictionaries, "ERB/script.json",
        [
            Entry("文本ERB\\script.00000", "原文", "译文", 0)
        ]);

        var result = new TranslationApplier().Apply(game, dictionaries, output);

        Assert.Equal(originalBytes, File.ReadAllBytes(Path.Combine(output, "ERB", "script.erb")));
        Assert.Empty(result.ModifiedFiles);
    }

    [Fact]
    public void SpanApplier_AppliesEditsFromTheEndOfTheFile()
    {
        string text = "a b c d";
        var edits = new[]
        {
            new SpanEdit("1", new TextSpan(0, 1), "a", "AAA"),
            new SpanEdit("2", new TextSpan(4, 5), "c", "CCCCCC"),
            new SpanEdit("3", new TextSpan(6, 7), "d", "D")
        };

        SpanApplyResult result = SpanApplier.Apply(text, edits);

        Assert.Equal("AAA b CCCCCC D", result.Text);
        Assert.Equal(3, result.AppliedCount);
        Assert.Equal(0, result.SkippedCount);
    }

    [Fact]
    public void SpanApplier_SkipsOverlappingAndOutOfRangeEdits()
    {
        string text = "abcdef";
        var edits = new[]
        {
            new SpanEdit("out", new TextSpan(4, 99), "ef", "X"),
            new SpanEdit("overlap", new TextSpan(1, 4), "bcd", "Y"),
            new SpanEdit("ok", new TextSpan(0, 3), "abc", "Z")
        };

        SpanApplyResult result = SpanApplier.Apply(text, edits);

        // Edits are applied left to right: the earliest span wins and later overlapping
        // spans are skipped, so the visible result keeps the first replacement.
        Assert.Equal("Zdef", result.Text);
        Assert.Equal(1, result.AppliedCount);
        Assert.Equal(2, result.SkippedCount);
        Assert.Equal(2, result.Warnings.Count);
    }

    [Fact]
    public void Apply_SkipsTranslationContainingNewLine_ForScriptTargets()
    {
        string root = CreateTempDirectory();
        string game = Path.Combine(root, "Game");
        string dictionaries = Path.Combine(root, "Dictionaries");
        string output = Path.Combine(root, "WorkingCopy");

        Write(game, "ERB/script.erb", "PRINTL こんにちは\n");
        WriteJson(dictionaries, "ERB/script.json",
        [
            Entry("文本ERB\\script.00000", "こんにちは", "第一行\n第二行", 1)
        ]);
        WriteSpanSidecar(dictionaries, "ERB/script",
            ("文本ERB\\script.00000", 7, 12));

        var result = new TranslationApplier().Apply(game, dictionaries, output);

        Assert.Equal("PRINTL こんにちは\n", File.ReadAllText(Path.Combine(output, "ERB", "script.erb")));
        Assert.Equal(1, result.SkippedByNewLineCount);
        Assert.Equal(0, result.SpanReplacedCount);
        Assert.Contains(result.Warnings, warning => warning.Contains("译文含换行", StringComparison.Ordinal));
    }

    [Fact]
    public void Apply_FallsBackToOriginalMatch_WhenSidecarHashDoesNotMatch()
    {
        string root = CreateTempDirectory();
        string game = Path.Combine(root, "Game");
        string dictionaries = Path.Combine(root, "Dictionaries");
        string output = Path.Combine(root, "WorkingCopy");

        Write(game, "ERB/script.erb", "PRINTL こんにちは\n");
        WriteJson(dictionaries, "ERB/script.json",
        [
            Entry("文本ERB\\script.00000", "こんにちは", "こんばんは", 1)
        ]);
        WriteSpanSidecar(dictionaries, "ERB/script", new string('0', 64),
            ("文本ERB\\script.00000", 7, 12));

        var result = new TranslationApplier().Apply(game, dictionaries, output);

        Assert.Equal("PRINTL こんばんは\n", File.ReadAllText(Path.Combine(output, "ERB", "script.erb")));
        Assert.Equal(0, result.SpanReplacedCount);
        Assert.Equal(1, result.FallbackReplacedCount);
        Assert.Contains(result.Warnings, warning => warning.Contains("定位信息已失效", StringComparison.Ordinal));
    }

    [Fact]
    public void Apply_KeepsUntouchedShiftJisBytes_WithDuplicateMappings()
    {
        string root = CreateTempDirectory();
        string game = Path.Combine(root, "Game");
        string dictionaries = Path.Combine(root, "Dictionaries");
        string output = Path.Combine(root, "WorkingCopy");

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding shiftJis = Encoding.GetEncoding(932);

        // 0x8790 decodes to "≒" but the encoder always writes "≒" as 0x81E0.
        // A full re-encode would therefore rewrite an untouched byte on line 1.
        byte[] body = shiftJis.GetBytes("PRINTL ")
            .Concat(new byte[] { 0x87, 0x90 })
            .Concat(shiftJis.GetBytes("\r\nPRINTL こんにちは\r\n"))
            .ToArray();
        WriteBytes(game, "ERB/script.erb", body);

        WriteJson(dictionaries, "ERB/script.json",
        [
            Entry("文本ERB\\script.00000", "こんにちは", "こんばんは", 1)
        ]);
        WriteSpanSidecar(dictionaries, "ERB/script",
            ("文本ERB\\script.00000", 17, 22));

        var result = new TranslationApplier().Apply(game, dictionaries, output);

        byte[] written = File.ReadAllBytes(Path.Combine(output, "ERB", "script.erb"));
        Assert.Equal(new byte[] { 0x87, 0x90 }, written.Skip(7).Take(2).ToArray());
        Assert.Equal(1, result.SpanReplacedCount);
        Assert.Empty(result.Warnings);
        Assert.Equal(
            "PRINTL ≒\r\nPRINTL こんばんは\r\n",
            shiftJis.GetString(written));
    }

    [Fact]
    public void Apply_DoesNotFallBackToWholeFileReplace_WhenSidecarHasNoSpans()
    {
        string root = CreateTempDirectory();
        string game = Path.Combine(root, "Game");
        string dictionaries = Path.Combine(root, "Dictionaries");
        string output = Path.Combine(root, "WorkingCopy");

        Write(game, "ERB/script.erb", "PRINTL こんにちは\nPRINTL こんにちは\n");
        WriteJson(dictionaries, "ERB/script.json",
        [
            Entry("文本ERB\\script.00000", "こんにちは", "こんばんは", 1)
        ]);
        WriteSpanSidecar(dictionaries, "ERB/script");

        var result = new TranslationApplier().Apply(game, dictionaries, output);

        Assert.Equal(
            "PRINTL こんにちは\nPRINTL こんにちは\n",
            File.ReadAllText(Path.Combine(output, "ERB", "script.erb")));
        Assert.Equal(0, result.SpanReplacedCount);
        Assert.Equal(0, result.FallbackReplacedCount);
        Assert.Contains(result.Warnings, warning => warning.Contains("缺少定位信息", StringComparison.Ordinal));
    }

    private static JObject Entry(string key, string original, string translation, int stage)
    {
        return new JObject
        {
            ["key"] = key,
            ["original"] = original,
            ["translation"] = translation,
            ["stage"] = stage,
            ["context"] = string.Empty
        };
    }

    private static void WriteJson(string root, string relativePath, IEnumerable<JObject> entries)
    {
        string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, new JArray(entries).ToString());
    }

    private static void WriteSpanSidecar(
        string dictionaryRoot,
        string relativePath,
        params (string Key, int Start, int End)[] spans)
    {
        WriteSpanSidecar(dictionaryRoot, relativePath, null, spans);
    }

    private static void WriteSpanSidecar(
        string dictionaryRoot,
        string relativePath,
        string? fileHash,
        params (string Key, int Start, int End)[] spans)
    {
        string path = Path.Combine(
            dictionaryRoot,
            "AI上下文",
            relativePath.Replace('/', Path.DirectorySeparatorChar) + ".spans.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var document = new JObject
        {
            ["schema"] = 1,
            ["file"] = relativePath + ".erb",
            ["spans"] = new JArray(spans.Select(span => new JObject
            {
                ["key"] = span.Key,
                ["start"] = span.Start,
                ["end"] = span.End
            }))
        };

        if (fileHash is not null)
        {
            document["fileHash"] = fileHash;
        }

        File.WriteAllText(path, document.ToString());
    }

    private static void Write(string root, string relativePath, string contents)
    {
        string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    private static void WriteBytes(string root, string relativePath, byte[] contents)
    {
        string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, contents);
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "EraTransSpanTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
