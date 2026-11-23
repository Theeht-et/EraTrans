using System.Text;
using EraTrans.Core;
using Xunit;
using EraTrans.Core.WriteBack;

namespace EraTrans.Tests;

public sealed class FullGameWorkspaceCopyTests
{
    [Fact]
    public void CopyDirectoryForModification_CopiesAllFilesAndEmptyDirectories()
    {
        using var game = new TemporaryDirectory();
        using var outputParent = new TemporaryDirectory();

        string textFile = game.CreateFile("ERB/main.erb", "original script");
        string binaryFile = game.CreateFile("Graphics/title.bin", "binary resource");
        string hiddenFile = game.CreateFile(".hidden-config", "hidden");
        File.SetAttributes(
            hiddenFile,
            File.GetAttributes(hiddenFile) | FileAttributes.Hidden | FileAttributes.System);
        string hiddenDirectory = Path.Combine(game.Path, ".hidden-folder");
        Directory.CreateDirectory(hiddenDirectory);
        File.SetAttributes(
            hiddenDirectory,
            File.GetAttributes(hiddenDirectory) | FileAttributes.Hidden | FileAttributes.System);
        game.CreateFile(".hidden-folder/asset.bin", "hidden directory asset");
        Directory.CreateDirectory(Path.Combine(game.Path, "EmptyFolder"));

        File.SetAttributes(
            textFile,
            File.GetAttributes(textFile) | FileAttributes.ReadOnly);

        string output = Path.Combine(outputParent.Path, "Output", "游戏副本");
        IReadOnlyList<string> copiedFiles =
            GameFileWorkspace.CopyDirectoryForModification(game.Path, output);

        Assert.Equal(4, copiedFiles.Count);
        Assert.Equal("original script", File.ReadAllText(Path.Combine(output, "ERB", "main.erb")));
        Assert.Equal("binary resource", File.ReadAllText(Path.Combine(output, "Graphics", "title.bin")));
        string copiedHiddenFile = Path.Combine(output, ".hidden-config");
        Assert.Equal("hidden", File.ReadAllText(copiedHiddenFile));
        FileAttributes copiedHiddenAttributes = File.GetAttributes(copiedHiddenFile);
        Assert.True((copiedHiddenAttributes & FileAttributes.Hidden) != 0);
        Assert.True((copiedHiddenAttributes & FileAttributes.System) != 0);
        string copiedHiddenDirectory = Path.Combine(output, ".hidden-folder");
        Assert.True(Directory.Exists(copiedHiddenDirectory));
        FileAttributes copiedHiddenDirectoryAttributes = File.GetAttributes(copiedHiddenDirectory);
        Assert.True((copiedHiddenDirectoryAttributes & FileAttributes.Hidden) != 0);
        Assert.True((copiedHiddenDirectoryAttributes & FileAttributes.System) != 0);
        Assert.Equal("hidden directory asset", File.ReadAllText(Path.Combine(copiedHiddenDirectory, "asset.bin")));
        Assert.True(Directory.Exists(Path.Combine(output, "EmptyFolder")));
        Assert.False(
            (File.GetAttributes(Path.Combine(output, "ERB", "main.erb")) & FileAttributes.ReadOnly) != 0);
        Assert.True((File.GetAttributes(textFile) & FileAttributes.ReadOnly) != 0);
        Assert.Equal("original script", File.ReadAllText(textFile));
        Assert.Equal("binary resource", File.ReadAllText(binaryFile));
    }


    [Fact]
    public void CopyDirectoryForModification_TranscodesConfiguredEncodingAndCustomTargetsToUtf8Bom()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var game = new TemporaryDirectory();
        using var outputParent = new TemporaryDirectory();

        Encoding big5 = Encoding.GetEncoding(950);
        string erbText = "PRINTFORM 繁體中文\r\n";
        string csvText = "名稱,繁體中文\r\n";
        string xmlText = "<?xml version=\"1.0\" encoding=\"big5\"?>\r\n<root>繁體</root>";
        string customText = "繁體中文自訂副檔名";
        string erb = game.CreateFile("ERB/main.erb", string.Empty);
        string csv = game.CreateFile("CSV/table.csv", string.Empty);
        string xml = game.CreateFile("XML/ui.xml", string.Empty);
        string custom = game.CreateFile("Data/custom.txt", string.Empty);
        string binary = game.CreateFile("Graphics/asset.bin", "binary");
        File.WriteAllBytes(erb, big5.GetBytes(erbText));
        File.WriteAllBytes(csv, big5.GetBytes(csvText));
        File.WriteAllBytes(xml, big5.GetBytes(xmlText));
        File.WriteAllBytes(custom, big5.GetBytes(customText));

        var options = new EraParserOptions
        {
            FileEncoding = big5,
            TargetFileExtensions = [".csv", ".erb", ".erh", ".erd", ".txt"]
        };
        string output = Path.Combine(outputParent.Path, "Output", "游戏副本");
        GameFileWorkspace.CopyDirectoryForModification(game.Path, output, options);

        AssertUtf8BomText(Path.Combine(output, "ERB", "main.erb"), erbText);
        AssertUtf8BomText(Path.Combine(output, "CSV", "table.csv"), csvText);
        AssertUtf8BomText(Path.Combine(output, "XML", "ui.xml"),
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<root>繁體</root>");
        AssertUtf8BomText(Path.Combine(output, "Data", "custom.txt"), customText);
        Assert.Equal("binary", File.ReadAllText(Path.Combine(output, "Graphics", "asset.bin")));

        Assert.Equal(big5.GetBytes(erbText), File.ReadAllBytes(erb));
        Assert.Equal(big5.GetBytes(csvText), File.ReadAllBytes(csv));
        Assert.Equal(big5.GetBytes(xmlText), File.ReadAllBytes(xml));
        Assert.Equal(big5.GetBytes(customText), File.ReadAllBytes(custom));
        Assert.Equal("binary", File.ReadAllText(binary));
    }

    [Fact]
    public void CopyDirectoryForModification_RejectsUndecodableTextBeforeCreatingWorkspace()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var game = new TemporaryDirectory();
        using var outputParent = new TemporaryDirectory();

        byte[] invalidBytes = [0xFF, 0xFE, 0x00];
        string invalid = game.CreateFile("ERB/invalid.erb", string.Empty);
        File.WriteAllBytes(invalid, invalidBytes);

        var options = new EraParserOptions { FileEncoding = Encoding.GetEncoding(936) };
        string output = Path.Combine(outputParent.Path, "Output", "游戏副本");
        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => GameFileWorkspace.CopyDirectoryForModification(
                game.Path,
                output,
                options,
                ignoreEncodingIssueFiles: false));

        Assert.Contains("ERB/invalid.erb", error.Message);
        Assert.Contains("无损解码", error.Message);
        Assert.Contains("未生成游戏副本", error.Message);
        Assert.False(Directory.Exists(output));
        Assert.Equal(invalidBytes, File.ReadAllBytes(invalid));
    }

    [Fact]
    public void CopyDirectoryForModification_SkipsUndecodableFilesByDefaultAndCopiesThemByteForByte()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding gbk = Encoding.GetEncoding(936);

        using var game = new TemporaryDirectory();
        using var outputParent = new TemporaryDirectory();

        string normal = Path.Combine(game.Path, "ERB", "normal.erb");
        string gbkText = Path.Combine(game.Path, "ERB", "gbk-text.erb");
        string invalid = Path.Combine(game.Path, "ERB", "invalid.erb");
        Directory.CreateDirectory(Path.GetDirectoryName(normal)!);

        File.WriteAllBytes(normal, gbk.GetBytes("PRINTL 你好，世界\r\n"));
        File.WriteAllBytes(gbkText, gbk.GetBytes("PRINTL 学习\r\n"));
        byte[] invalidBytes = [0xFF, 0xFE, 0x00];
        File.WriteAllBytes(invalid, invalidBytes);

        string output = Path.Combine(outputParent.Path, "游戏副本");
        var options = new EraParserOptions { FileEncoding = gbk };

        var skipped = new List<GameFileEncodingIssue>();
        GameFileWorkspace.CopyDirectoryForModification(
            game.Path,
            output,
            options,
            skippedEncodingIssues: skipped);

        Assert.Single(skipped);
        Assert.Equal("ERB/invalid.erb", skipped[0].RelativePath);
        Assert.Contains("无损解码", skipped[0].Reason);
        AssertUtf8BomText(Path.Combine(output, "ERB", "normal.erb"), "PRINTL 你好，世界\r\n");
        AssertUtf8BomText(Path.Combine(output, "ERB", "gbk-text.erb"), "PRINTL 学习\r\n");
        Assert.Equal(invalidBytes, File.ReadAllBytes(Path.Combine(output, "ERB", "invalid.erb")));
    }

    private static void AssertUtf8BomText(string path, string expected)
    {
        byte[] bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length >= 3);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        Assert.Equal(expected, new UTF8Encoding(false, true).GetString(bytes, 3, bytes.Length - 3));
    }

    [Fact]
    public void CopyDirectoryForModification_RefusesExistingDestinationWithoutOverwritingIt()
    {
        using var game = new TemporaryDirectory();
        using var outputParent = new TemporaryDirectory();
        game.CreateFile("ERB/main.erb", "source");

        string output = Path.Combine(outputParent.Path, "游戏副本");
        Directory.CreateDirectory(output);
        string userFile = Path.Combine(output, "user-edited.txt");
        File.WriteAllText(userFile, "user data");

        Assert.Throws<InvalidOperationException>(() =>
            GameFileWorkspace.CopyDirectoryForModification(game.Path, output));

        Assert.Equal("user data", File.ReadAllText(userFile));
        Assert.Equal("source", File.ReadAllText(Path.Combine(game.Path, "ERB", "main.erb")));
    }

    [Fact]
    public void CopyDirectoryForModification_RejectsDestinationInsideOriginalGameDirectory()
    {
        using var game = new TemporaryDirectory();
        game.CreateFile("ERB/main.erb", "source");

        Assert.Throws<InvalidOperationException>(() =>
            GameFileWorkspace.CopyDirectoryForModification(
                game.Path,
                Path.Combine(game.Path, "Output", "游戏副本")));

        Assert.Equal("source", File.ReadAllText(Path.Combine(game.Path, "ERB", "main.erb")));
        Assert.False(Directory.Exists(Path.Combine(game.Path, "Output")));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "EraTransWorkspaceTests",
            Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public string CreateFile(string relativePath, string content)
        {
            string path = System.IO.Path.Combine(
                Path,
                relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                var options = new EnumerationOptions
                {
                    AttributesToSkip = 0,
                    RecurseSubdirectories = true
                };
                foreach (string entry in Directory.EnumerateFileSystemEntries(Path, "*", options)
                             .OrderByDescending(entry => entry.Length))
                {
                    File.SetAttributes(entry, FileAttributes.Normal);
                }
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
