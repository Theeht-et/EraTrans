using EraTrans.Core;
using Xunit;

namespace EraTrans.Tests;

public sealed class GameDirectoryScannerTests
{
    [Fact]
    public void Scan_FindsSupportedFilesAndSkipsErhWhenSiblingErbExists()
    {
        using var game = new TemporaryDirectory();
        game.CreateFile("CSV/ABL.CSV");
        game.CreateFile("ERB/MAIN.ERB");
        game.CreateFile("ERB/MAIN.ERH");
        game.CreateFile("ERB/ONLY_HEADER.ERH");
        game.CreateFile("ERB/DATA.ERD");
        game.CreateFile("ERB/EXTRA.XML");
        game.CreateFile("resources/RESOURCE.CSV");
        game.CreateFile("XML/CONFIG.XML");
        game.CreateFile("Other/IGNORED.TXT");

        var files = GameDirectoryScanner.Scan(game.Path);

        Assert.Equal(
            new[]
            {
                "CSV/ABL.CSV",
                "ERB/DATA.ERD",
                "ERB/EXTRA.XML",
                "ERB/MAIN.ERB",
                "ERB/ONLY_HEADER.ERH",
                "XML/CONFIG.XML",
                "resources/RESOURCE.CSV"
            }.OrderBy(path => path, StringComparer.OrdinalIgnoreCase),
            files.Select(file => file.RelativePath).OrderBy(path => path, StringComparer.OrdinalIgnoreCase));

        Assert.DoesNotContain(files, file => file.RelativePath.Equals("ERB/MAIN.ERH", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(files, file => file.Kind == GameFileKind.ResourceCsv);
        Assert.Contains(files, file => file.Kind == GameFileKind.Erd);
    }

    [Fact]
    public void Scan_DoesNotModifyGameFiles()
    {
        using var game = new TemporaryDirectory();
        var file = game.CreateFile("CSV/KEEP.CSV", "name,original");

        GameDirectoryScanner.Scan(game.Path);

        Assert.True(File.Exists(file));
        Assert.Equal("name,original", File.ReadAllText(file));
    }

    [Fact]
    public void Scan_ThrowsWhenDirectoryDoesNotExist()
    {
        using var parent = new TemporaryDirectory();

        Assert.Throws<DirectoryNotFoundException>(() =>
            GameDirectoryScanner.Scan(Path.Combine(parent.Path, "missing")));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "EraTransTests",
            Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public string CreateFile(string relativePath, string content = "")
        {
            var path = System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
