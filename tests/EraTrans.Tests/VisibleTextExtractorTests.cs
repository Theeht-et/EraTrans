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
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

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
            File.WriteAllText(source, "@F\r\n" + FormLine + "\r\n", new UTF8Encoding(false));

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

    private static string Slice(string text, VisibleTextSpan span) =>
        text.Substring(span.Start, span.End - span.Start);

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
