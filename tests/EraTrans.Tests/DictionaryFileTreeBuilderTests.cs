using EraTrans.Core;
using Xunit;

namespace EraTrans.Tests;

public sealed class DictionaryFileTreeBuilderTests
{
    [Fact]
    public void Build_CompactsSingleChildFolderChains()
    {
        var files = new[]
        {
            new DictionaryEditorFile("/dict/ERB/口上/キャラ別/001/EVENT_K01_01.json", Path.Combine("ERB", "口上", "キャラ別", "001", "EVENT_K01_01.json")),
            new DictionaryEditorFile("/dict/ERB/口上/キャラ別/001/EVENT_K01_02.json", Path.Combine("ERB", "口上", "キャラ別", "001", "EVENT_K01_02.json"))
        };

        DictionaryFileTreeFolder root = DictionaryFileTreeBuilder.Build(files);

        Assert.Empty(root.Files);
        DictionaryFileTreeFolder compacted = Assert.Single(root.Folders);
        Assert.Equal("ERB/口上/キャラ別/001", compacted.DisplayName);
        Assert.Equal(Path.Combine("ERB", "口上", "キャラ別", "001"), compacted.FullRelativePath);
        Assert.Empty(compacted.Folders);
        Assert.Equal(2, compacted.Files.Count);
    }

    [Fact]
    public void Build_DoesNotCompactBranchingOrFileContainingFolders()
    {
        var files = new[]
        {
            new DictionaryEditorFile("/dict/root.json", "root.json"),
            new DictionaryEditorFile("/dict/ERB/main.json", Path.Combine("ERB", "main.json")),
            new DictionaryEditorFile("/dict/ERB/口上/001/a.json", Path.Combine("ERB", "口上", "001", "a.json")),
            new DictionaryEditorFile("/dict/ERB/系统/sub/b.json", Path.Combine("ERB", "系统", "sub", "b.json"))
        };

        DictionaryFileTreeFolder root = DictionaryFileTreeBuilder.Build(files);

        Assert.Single(root.Files);
        Assert.Equal("root.json", root.Files[0].RelativePath);

        DictionaryFileTreeFolder erb = Assert.Single(root.Folders);
        Assert.Equal("ERB", erb.DisplayName);
        Assert.Single(erb.Files);
        Assert.Equal(2, erb.Folders.Count);
        Assert.Equal("口上/001", erb.Folders[0].DisplayName);
        Assert.Equal("系统/sub", erb.Folders[1].DisplayName);
    }

    [Fact]
    public void FormatMiddleEllipsis_PreservesStartAndDistinguishingSuffix()
    {
        static double FixedCharWidth(string s) => s.Length * 10.0;

        Assert.Equal(
            "short.json",
            DictionaryFileTreeBuilder.FormatMiddleEllipsis("short.json", 200.0, FixedCharWidth));

        string first = DictionaryFileTreeBuilder.FormatMiddleEllipsis(
            "EVENT_K01_LONG_DIALOGUE_001.json",
            170.0,
            FixedCharWidth);
        string second = DictionaryFileTreeBuilder.FormatMiddleEllipsis(
            "EVENT_K01_LONG_DIALOGUE_002.json",
            170.0,
            FixedCharWidth);

        Assert.Contains("…", first, StringComparison.Ordinal);
        Assert.True(FixedCharWidth(first) <= 170.0);
        Assert.StartsWith("EVENT_", first, StringComparison.Ordinal);
        Assert.EndsWith("001.json", first, StringComparison.Ordinal);
        Assert.EndsWith("002.json", second, StringComparison.Ordinal);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void FormatMiddleEllipsis_HandlesExtremeNarrowWidthAndEmptyText()
    {
        static double FixedCharWidth(string s) => s.Length * 10.0;

        Assert.Equal(
            string.Empty,
            DictionaryFileTreeBuilder.FormatMiddleEllipsis(string.Empty, 100.0, FixedCharWidth));
        Assert.Equal(
            "…",
            DictionaryFileTreeBuilder.FormatMiddleEllipsis("very_long_file.json", 10.0, FixedCharWidth));
    }
}
