using EraTrans.Core;
using Xunit;

namespace EraTrans.Tests;

public sealed class GameFileWorkspaceTests
{
    [Fact]
    public void CopyDirectoryForModification_CopiesAllFilesAndEmptyDirectories()
    {
        using var game = new TemporaryDirectory();
        using var outputParent = new TemporaryDirectory();

        string textFile = game.CreateFile("ERB/main.erb", "original script");
        string binaryFile = game.CreateFile("Graphics/title.bin", "binary resource");
        game.CreateFile(".hidden-config", "hidden");
        Directory.CreateDirectory(Path.Combine(game.Path, "EmptyFolder"));

        File.SetAttributes(
            textFile,
            File.GetAttributes(textFile) | FileAttributes.ReadOnly);

        string output = Path.Combine(outputParent.Path, "Output", "游戏副本");
        IReadOnlyList<string> copiedFiles =
            GameFileWorkspace.CopyDirectoryForModification(game.Path, output);

        Assert.Equal(3, copiedFiles.Count);
        Assert.Equal("original script", File.ReadAllText(Path.Combine(output, "ERB", "main.erb")));
        Assert.Equal("binary resource", File.ReadAllText(Path.Combine(output, "Graphics", "title.bin")));
        Assert.Equal("hidden", File.ReadAllText(Path.Combine(output, ".hidden-config")));
        Assert.True(Directory.Exists(Path.Combine(output, "EmptyFolder")));
        Assert.DoesNotContain(
            FileAttributes.ReadOnly,
            new[] { File.GetAttributes(Path.Combine(output, "ERB", "main.erb")) });
        Assert.True((File.GetAttributes(textFile) & FileAttributes.ReadOnly) != 0);
        Assert.Equal(textFile, Path.Combine(game.Path, "ERB", "main.erb"));
        Assert.Equal(binaryFile, Path.Combine(game.Path, "Graphics", "title.bin"));
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
                foreach (string file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
