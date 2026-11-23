using System.Text;
using EraTrans.Core;
using EraTrans.Core.VisibleText;
using Xunit;

namespace EraTrans.Tests;

public sealed class VisibleTextGroupingTests
{
    [Fact]
    public void Extract_WritesGroupingSidecarThatThePlannerAccepts()
    {
        string root = CreateTempRoot();
        try
        {
            string gameDirectory = Path.Combine(root, "game");
            string dictionaryRoot = Path.Combine(root, "out", "字典");
            Directory.CreateDirectory(Path.Combine(gameDirectory, "ERB"));
            File.WriteAllText(
                Path.Combine(gameDirectory, "ERB", "talk.erb"),
                "@FIRST\r\nPRINTL こんにちは\r\nPRINTFORML おはよう\r\n@SECOND\r\nPRINTL さようなら\r\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            new VisibleTextExtractor().Extract(
                gameDirectory,
                dictionaryRoot,
                "ERB",
                new EraParserOptions { WriteContext = false });

            string dictionaryPath = Path.Combine(dictionaryRoot, "ERB", "talk.json");
            string groupingPath = Path.Combine(root, "out", "AI上下文", "ERB", "talk.groups.json");
            Assert.True(File.Exists(groupingPath));

            var document = new DictionaryEditorService().Load(dictionaryPath);
            var grouping = new ErbTranslationGroupingService();
            Assert.True(
                grouping.TryLoad(dictionaryRoot, dictionaryPath, document.Entries, gameDirectory, out var index, out string? reason),
                reason);
            Assert.NotNull(index);
            Assert.Equal(3, index!.Entries.Count);
            Assert.Equal(new[] { "FIRST", "FIRST", "SECOND" }, index.Entries.Select(entry => entry.FunctionId).ToArray());
            Assert.Equal(
                new[] { @"文本ERB\talk.00000", @"文本ERB\talk.00001", @"文本ERB\talk.00002" },
                index.Entries.Select(entry => entry.Key).ToArray());

            Assert.Equal("PRINTL", index.Entries[0].Command);
            Assert.Equal("print", index.Entries[0].Kind);
            Assert.Equal("PRINTFORML", index.Entries[1].Command);
            Assert.Equal("form", index.Entries[1].Kind);
            Assert.Equal("PRINTL", index.Entries[2].Command);

            var lines = document.Entries
                .Select((entry, position) => new WindowSourceLine(position, entry.Key, entry.Original, true))
                .ToArray();
            WindowPlan plan = new SequentialTranslationPlanner().Build(lines, index);
            Assert.NotEmpty(plan.Windows);
            Assert.Equal(3, plan.TargetLineCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Extract_SplitsScreensAtWaitAndThePlannerPacksByScreen()
    {
        string root = CreateTempRoot();
        try
        {
            string gameDirectory = Path.Combine(root, "game");
            string dictionaryRoot = Path.Combine(root, "out", "字典");
            Directory.CreateDirectory(Path.Combine(gameDirectory, "ERB"));
            File.WriteAllText(
                Path.Combine(gameDirectory, "ERB", "screen.erb"),
                "@TALK\r\nPRINTL おはよう\r\nPRINTL いい天気だ\r\nWAIT\r\nPRINTL さようなら\r\n",
                new UTF8Encoding(true));

            new VisibleTextExtractor().Extract(
                gameDirectory,
                dictionaryRoot,
                "ERB",
                new EraParserOptions { WriteContext = false });

            string dictionaryPath = Path.Combine(dictionaryRoot, "ERB", "screen.json");
            var document = new DictionaryEditorService().Load(dictionaryPath);
            var grouping = new ErbTranslationGroupingService();
            Assert.True(
                grouping.TryLoad(dictionaryRoot, dictionaryPath, document.Entries, gameDirectory, out var index, out string? reason),
                reason);

            Assert.Equal("screen", index!.Packing);
            Assert.Equal(3, index.Entries.Count);
            Assert.Equal(index.Entries[0].ControlScopeId, index.Entries[1].ControlScopeId);
            Assert.NotEqual(index.Entries[1].ControlScopeId, index.Entries[2].ControlScopeId);

            var lines = document.Entries
                .Select((entry, position) => new WindowSourceLine(position, entry.Key, entry.Original, true))
                .ToArray();
            WindowPlan plan = new SequentialTranslationPlanner().Build(lines, index);
            Assert.Equal(3, plan.TargetLineCount);
            Assert.Equal(2, plan.FunctionCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TryWrite_LeavesNoSidecarWhenTheDictionaryIsMissing()
    {
        string root = CreateTempRoot();
        try
        {
            string gameDirectory = Path.Combine(root, "game");
            Directory.CreateDirectory(Path.Combine(gameDirectory, "ERB"));
            string source = Path.Combine(gameDirectory, "ERB", "x.erb");
            File.WriteAllText(source, "@X\r\nPRINTL テスト\r\n", new UTF8Encoding(true));

            string? path = VisibleTextGrouping.TryWrite(
                Path.Combine(root, "out", "字典"),
                Path.Combine(root, "out", "字典", "ERB", "x.json"),
                source,
                "ERB/x.erb",
                File.ReadAllText(source),
                new[]
                {
                    new VisibleTextEntry(
                        "ERB/x.erb",
                        2,
                        "PRINTL",
                        "print",
                        "テスト",
                        new[] { new VisibleTextSpan(5, 8) },
                        new VisibleTextSpan(5, 8))
                },
                out string? warning);

            Assert.Null(path);
            Assert.NotNull(warning);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "eratrans-grouping-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
