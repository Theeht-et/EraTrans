using EraTrans.Core;
using Xunit;

namespace EraTrans.Tests;

public sealed class EraTransOutputLayoutTests
{
    [Fact]
    public void Create_SeparatesAllGeneratedOutputsByOriginalGameDirectoryName()
    {
        using var temporary = new TemporaryDirectory();
        string gameDirectory = Path.Combine(temporary.Path, "Games", "ExampleGame");
        string outputRoot = Path.Combine(temporary.Path, "Translations");
        Directory.CreateDirectory(gameDirectory);

        EraTransOutputLayout layout = EraTransOutputLayout.Create(outputRoot, gameDirectory);

        string gameOutputRoot = Path.Combine(outputRoot, "ExampleGame");
        Assert.Equal(gameOutputRoot, layout.GameOutputRoot);
        Assert.Equal(Path.Combine(gameOutputRoot, "字典"), layout.DictionaryDirectory);
        Assert.Equal(Path.Combine(gameOutputRoot, "AI上下文"), layout.AiContextDirectory);
        Assert.Equal(Path.Combine(gameOutputRoot, "游戏副本"), layout.GameCopyDirectory);
    }

    [Fact]
    public void Create_RejectsOutputThatWouldBeInsideTheOriginalGameDirectory()
    {
        using var temporary = new TemporaryDirectory();
        string gameDirectory = Path.Combine(temporary.Path, "ExampleGame");
        Directory.CreateDirectory(gameDirectory);

        Assert.Throws<InvalidOperationException>(() =>
            EraTransOutputLayout.Create(
                Path.Combine(gameDirectory, "Translations"),
                gameDirectory));
    }

    [Fact]
    public void Create_RejectsUsingTheGamesParentAsOutputWhenNameScopedFolderWouldBeTheGameItself()
    {
        using var temporary = new TemporaryDirectory();
        string gameDirectory = Path.Combine(temporary.Path, "ExampleGame");
        Directory.CreateDirectory(gameDirectory);

        Assert.Throws<InvalidOperationException>(() =>
            EraTransOutputLayout.Create(temporary.Path, gameDirectory));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "EraTransOutputLayoutTests",
            Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
