namespace EraTrans.Core;

/// <summary>
/// Resolves all generated files for one original game under a name-scoped output folder.
/// </summary>
public sealed record EraTransOutputLayout(
    string OutputRoot,
    string GameDirectory,
    string GameDirectoryName,
    string GameOutputRoot,
    string DictionaryDirectory,
    string AiContextDirectory,
    string GameCopyDirectory)
{
    public static EraTransOutputLayout Create(string outputRoot, string gameDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);

        string fullOutputRoot = Path.GetFullPath(outputRoot);
        string fullGameDirectory = Path.GetFullPath(gameDirectory);
        if (!Directory.Exists(fullGameDirectory))
        {
            throw new DirectoryNotFoundException($"Game directory does not exist: {fullGameDirectory}");
        }

        string gameName = new DirectoryInfo(fullGameDirectory).Name;
        if (string.IsNullOrWhiteSpace(gameName) ||
            gameName is "." or ".." ||
            gameName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException(
                "无法从原游戏目录取得有效的目录名称，请选择实际的游戏文件夹，而不是磁盘根目录。");
        }

        string gameOutputRoot = Path.GetFullPath(Path.Combine(fullOutputRoot, gameName));
        EnsureOutputIsOutsideGame(fullGameDirectory, gameOutputRoot);

        return new EraTransOutputLayout(
            fullOutputRoot,
            fullGameDirectory,
            gameName,
            gameOutputRoot,
            Path.Combine(gameOutputRoot, "字典"),
            Path.Combine(gameOutputRoot, "AI上下文"),
            Path.Combine(gameOutputRoot, "游戏副本"));
    }

    private static void EnsureOutputIsOutsideGame(string gameRoot, string outputRoot)
    {
        string relative = Path.GetRelativePath(gameRoot, outputRoot);
        if (relative == "." ||
            (!Path.IsPathRooted(relative) &&
             relative != ".." &&
             !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "按游戏名称分类后的输出目录会位于原游戏目录内部。请将统一输出目录选择在原游戏目录之外。");
        }
    }
}
