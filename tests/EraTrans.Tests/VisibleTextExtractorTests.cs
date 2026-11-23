using System.Text;
using System.Text.Json;
using EraTrans.Core;
using EraTrans.Core.VisibleText;
using Xunit;

namespace EraTrans.Tests;

public sealed class VisibleTextExtractorTests
{
    private const string FormLine = "PRINTFORMW おはよう%CALLNAME%、いい朝だ";
    private const string PlainLine = "PRINTFORML こんにちは";

    [Fact]
    public void Extract_SkipsEntriesWithoutJapaneseByDefault()
    {
        string root = CreateTempRoot();
        try
        {
            string gameDirectory = Path.Combine(root, "game");
            string dictionaryRoot = Path.Combine(root, "out");
            Directory.CreateDirectory(Path.Combine(gameDirectory, "ERB"));
            File.WriteAllText(
                Path.Combine(gameDirectory, "ERB", "mixed.erb"),
                "@HELLO\nPRINTFORML こんにちは\nPRINTFORML OK\nPRINTFORML EXP+5\n",
                new UTF8Encoding(true));

            var extractor = new VisibleTextExtractor();
            VisibleTextExtractionResult defaults = extractor.Extract(
                gameDirectory, dictionaryRoot, "ERB", new EraParserOptions());
            Assert.Equal(1, defaults.EntryCount);

            VisibleTextExtractionResult keepAll = extractor.Extract(
                gameDirectory,
                Path.Combine(root, "out2"),
                "ERB",
                new EraParserOptions { SkipNonJapaneseEntries = false });
            Assert.Equal(3, keepAll.EntryCount);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Extract_HonorsHtmlTitleSwitch()
    {
        string root = CreateTempRoot();
        try
        {
            string gameDirectory = Path.Combine(root, "game");
            Directory.CreateDirectory(Path.Combine(gameDirectory, "ERB"));
            File.WriteAllText(
                Path.Combine(gameDirectory, "ERB", "html.erb"),
                "@HELLO\nHTML_PRINT \"<button value='1' title='戻る'>次へ</button>\"\n",
                new UTF8Encoding(true));

            var extractor = new VisibleTextExtractor();
            VisibleTextExtractionResult withTitle = extractor.Extract(
                gameDirectory, Path.Combine(root, "out1"), "ERB", new EraParserOptions());
            Assert.Equal(2, withTitle.EntryCount);

            VisibleTextExtractionResult withoutTitle = extractor.Extract(
                gameDirectory,
                Path.Combine(root, "out2"),
                "ERB",
                new EraParserOptions { ExtractHtmlTitle = false });
            Assert.Equal(1, withoutTitle.EntryCount);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Extract_HonorsPrintDataSwitch()
    {
        string root = CreateTempRoot();
        try
        {
            string gameDirectory = Path.Combine(root, "game");
            Directory.CreateDirectory(Path.Combine(gameDirectory, "ERB"));
            File.WriteAllText(
                Path.Combine(gameDirectory, "ERB", "data.erb"),
                "@HELLO\nPRINTDATA\nDATA こんにちは\nDATA さようなら\nENDDATA\n",
                new UTF8Encoding(true));

            var extractor = new VisibleTextExtractor();
            VisibleTextExtractionResult withData = extractor.Extract(
                gameDirectory, Path.Combine(root, "out1"), "ERB", new EraParserOptions());
            Assert.Equal(2, withData.EntryCount);

            VisibleTextExtractionResult withoutData = extractor.Extract(
                gameDirectory,
                Path.Combine(root, "out2"),
                "ERB",
                new EraParserOptions { ExtractPrintDataEntries = false });
            Assert.Equal(0, withoutData.EntryCount);
            Assert.False(File.Exists(Path.Combine(root, "out2", "ERB", "data.json")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Extract_WritesDictionaryAndSpanSidecar()
    {
        string root = CreateTempRoot();
        try
        {
            string gameDirectory = Path.Combine(root, "game");
            string dictionaryRoot = Path.Combine(root, "out", "字典");
            Directory.CreateDirectory(Path.Combine(gameDirectory, "ERB"));
            string source = Path.Combine(gameDirectory, "ERB", "hello.erb");
            File.WriteAllText(
                source,
                "; comment\r\n@HELLO\r\n" + FormLine + "\r\n" + PlainLine + "\r\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            var extractor = new VisibleTextExtractor();
            VisibleTextExtractionResult result = extractor.Extract(
                gameDirectory,
                dictionaryRoot,
                "ERB",
                new EraParserOptions { WriteContext = true });

            Assert.Equal(1, result.FileCount);
            Assert.Equal(2, result.EntryCount);
            Assert.Empty(result.Warnings);
            Assert.Equal(
                new[] { "hello.json", "hello.spans.json", "hello.groups.json" },
                result.GeneratedFiles.Select(Path.GetFileName).ToArray());

            string dictionaryPath = Path.Combine(dictionaryRoot, "ERB", "hello.json");
            Assert.True(File.Exists(dictionaryPath));

            using JsonDocument dictionary = JsonDocument.Parse(File.ReadAllBytes(dictionaryPath));
            JsonElement entries = dictionary.RootElement;
            Assert.Equal(JsonValueKind.Array, entries.ValueKind);
            Assert.Equal(2, entries.GetArrayLength());

            JsonElement first = entries[0];
            Assert.Equal(
                new[] { "key", "original", "translation", "stage", "context" },
                first.EnumerateObject().Select(property => property.Name).ToArray());
            Assert.Equal(@"文本ERB\hello.00000", first.GetProperty("key").GetString());
            Assert.Equal("おはよう%CALLNAME%、いい朝だ", first.GetProperty("original").GetString());
            Assert.Equal(string.Empty, first.GetProperty("translation").GetString());
            Assert.Equal(0, first.GetProperty("stage").GetInt32());
            Assert.Equal(
                "@HELLO 行 3: " + FormLine,
                first.GetProperty("context").GetString());

            Assert.Equal(@"文本ERB\hello.00001", entries[1].GetProperty("key").GetString());
            Assert.Equal("こんにちは", entries[1].GetProperty("original").GetString());

            string sidecarPath = Path.Combine(root, "out", "AI上下文", "ERB", "hello.spans.json");
            Assert.True(File.Exists(sidecarPath));

            using JsonDocument sidecar = JsonDocument.Parse(File.ReadAllBytes(sidecarPath));
            JsonElement sidecarRoot = sidecar.RootElement;
            Assert.Equal(1, sidecarRoot.GetProperty("schema").GetInt32());
            Assert.Equal("ERB/hello.erb", sidecarRoot.GetProperty("file").GetString());

            JsonElement spans = sidecarRoot.GetProperty("spans");
            // Every entry gets the span of its whole template, so slice(span) == original holds
            // even when the template contains placeholders. The write-back replaces the template
            // as a whole and keeps the placeholders verbatim.
            Assert.Equal(2, spans.GetArrayLength());
            string text = File.ReadAllText(source, Encoding.UTF8);
            foreach (JsonElement span in spans.EnumerateArray())
            {
                int start = span.GetProperty("start").GetInt32();
                int end = span.GetProperty("end").GetInt32();
                Assert.InRange(start, 0, text.Length);
                Assert.InRange(end, start, text.Length);
            }

            JsonElement firstSpan = spans[0];
            Assert.Equal(@"文本ERB\hello.00000", firstSpan.GetProperty("key").GetString());
            Assert.Equal(
                "おはよう%CALLNAME%、いい朝だ",
                text[firstSpan.GetProperty("start").GetInt32()..firstSpan.GetProperty("end").GetInt32()]);

            Assert.True(firstSpan.GetProperty("line").GetInt32() > 0);
            Assert.Equal("PRINTFORMW", firstSpan.GetProperty("command").GetString());
            Assert.Equal("form", firstSpan.GetProperty("kind").GetString());

            JsonElement secondSpan = spans[1];
            Assert.Equal(@"文本ERB\hello.00001", secondSpan.GetProperty("key").GetString());
            Assert.Equal(
                "こんにちは",
                text[secondSpan.GetProperty("start").GetInt32()..secondSpan.GetProperty("end").GetInt32()]);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Extract_RecordsAbsoluteSpansForEveryEntry()
    {
        string root = CreateTempRoot();
        try
        {
            string gameDirectory = Path.Combine(root, "game");
            Directory.CreateDirectory(Path.Combine(gameDirectory, "ERB"));
            string source = Path.Combine(gameDirectory, "ERB", "spans.erb");
            File.WriteAllText(source, "@F\r\n" + FormLine + "\r\n", new UTF8Encoding(true));

            VisibleTextFileResult file = ErbVisibleTextLexer.ExtractFile(
                "ERB/spans.erb",
                File.ReadAllText(source, Encoding.UTF8),
                Array.Empty<string>());

            Assert.Single(file.Entries);
            VisibleTextEntry entry = file.Entries[0];
            Assert.Equal(2, entry.Parts.Count);
            string text = File.ReadAllText(source, Encoding.UTF8);
            Assert.Equal("おはよう", Slice(text, entry.Parts[0]));
            Assert.Equal("、いい朝だ", Slice(text, entry.Parts[1]));
            Assert.Equal(
                "おはよう%CALLNAME%、いい朝だ",
                text[entry.Parts[0].Start..entry.Parts[1].End]);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Extract_GivesButtonAndLocalsEntriesUsableSpans()
    {
        string root = CreateTempRoot();
        try
        {
            string gameDirectory = Path.Combine(root, "game");
            string dictionaryRoot = Path.Combine(root, "out", "字典");
            Directory.CreateDirectory(Path.Combine(gameDirectory, "ERB"));
            string source = Path.Combine(gameDirectory, "ERB", "ui.erb");
            File.WriteAllText(
                source,
                "@UI\r\nLOCALS = 体力(%CALLNAME:MASTER%)\r\nPRINTBUTTON \"[自宅位置]\", 120\r\nPRINTFORM %LOCALS%\r\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            var extractor = new VisibleTextExtractor();
            extractor.Extract(gameDirectory, dictionaryRoot, "ERB", new EraParserOptions { WriteContext = true });

            string sidecarPath = Path.Combine(root, "out", "AI上下文", "ERB", "ui.spans.json");
            using JsonDocument sidecar = JsonDocument.Parse(File.ReadAllBytes(sidecarPath));
            string text = File.ReadAllText(source, Encoding.UTF8);

            var spans = sidecar.RootElement.GetProperty("spans").EnumerateArray()
                .Select(span => (
                    Key: span.GetProperty("key").GetString()!,
                    Start: span.GetProperty("start").GetInt32(),
                    End: span.GetProperty("end").GetInt32()))
                .ToList();

            // Button text and LOCALS content used to be dropped from the sidecar (zero-length span),
            // which silently prevented them from ever being written back into the game.
            Assert.Contains(spans, span => text[span.Start..span.End] == "[自宅位置]");
            Assert.Contains(spans, span => text[span.Start..span.End] == "体力(%CALLNAME:MASTER%)");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static string Slice(string text, VisibleTextSpan span) =>
        text.Substring(span.Start, span.End - span.Start);

    [Fact]
    public void Extract_RemovesDictionaryWhenTheFileNoLongerHasVisibleText()
    {
        string root = CreateTempRoot();
        try
        {
            string gameDirectory = Path.Combine(root, "game");
            string dictionaryRoot = Path.Combine(root, "out", "字典");
            Directory.CreateDirectory(Path.Combine(gameDirectory, "ERB"));
            string source = Path.Combine(gameDirectory, "ERB", "hello.erb");
            File.WriteAllText(source, "@HELLO\n" + PlainLine + "\n", new UTF8Encoding(true));

            var extractor = new VisibleTextExtractor();
            extractor.Extract(gameDirectory, dictionaryRoot, "ERB", new EraParserOptions());

            string dictionaryPath = Path.Combine(dictionaryRoot, "ERB", "hello.json");
            Assert.True(File.Exists(dictionaryPath));

            // The same file is rewritten without translatable text: last round's dictionary (and
            // its span sidecar) would otherwise keep showing entries that no longer exist.
            File.WriteAllText(source, "@HELLO\n; 表示するものがなくなった\n", new UTF8Encoding(true));
            extractor.Extract(gameDirectory, dictionaryRoot, "ERB", new EraParserOptions());

            Assert.False(File.Exists(dictionaryPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Extract_RejectsAmbiguousEncodingBeforeCreatingVisibleTextDictionaries()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string root = CreateTempRoot();
        try
        {
            string gameDirectory = Path.Combine(root, "game");
            string source = Path.Combine(gameDirectory, "ERB", "ambiguous.erb");
            string dictionaryRoot = Path.Combine(root, "out", "字典");
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            byte[] gbkBytes = Encoding.GetEncoding(936).GetBytes("学习");
            File.WriteAllBytes(source, gbkBytes);

            InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
                new VisibleTextExtractor().Extract(
                    gameDirectory,
                    dictionaryRoot,
                    "ERB",
                    new EraParserOptions { FileEncoding = Encoding.GetEncoding(936) }));

            Assert.Contains("ERB/ambiguous.erb", error.Message);
            Assert.Contains("歧义", error.Message);
            Assert.False(Directory.Exists(dictionaryRoot));
            Assert.Equal(gbkBytes, File.ReadAllBytes(source));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }


    [Fact]
    public void Extract_RemovesDictionaryWhoseSourceFileIsGone()
    {
        string root = CreateTempRoot();
        try
        {
            string gameDirectory = Path.Combine(root, "game");
            string dictionaryRoot = Path.Combine(root, "out", "字典");
            Directory.CreateDirectory(Path.Combine(gameDirectory, "ERB"));
            string source = Path.Combine(gameDirectory, "ERB", "hello.erb");
            File.WriteAllText(source, "@HELLO\n" + PlainLine + "\n", new UTF8Encoding(true));

            var extractor = new VisibleTextExtractor();
            extractor.Extract(gameDirectory, dictionaryRoot, "ERB", new EraParserOptions());
            string dictionaryPath = Path.Combine(dictionaryRoot, "ERB", "hello.json");
            Assert.True(File.Exists(dictionaryPath));

            File.Delete(source);
            VisibleTextExtractionResult second = extractor.Extract(
                gameDirectory, dictionaryRoot, "ERB", new EraParserOptions());

            Assert.False(File.Exists(dictionaryPath));
            Assert.Contains(second.Warnings, warning => warning.Contains("清理", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "eratrans-visibletext-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
