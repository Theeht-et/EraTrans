using EraTrans.Core.VisibleText;

namespace EraTrans.Core;

/// <summary>
/// Resolves all generated files for one original game under a name-scoped output folder.
/// Legacy (V1) and visible-text (V2) dictionaries and auxiliary data are kept in separate
/// version folders so switching extractors never overwrites the other version's dictionaries.
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
    public const string DictionaryFolderName = "字典";
    public const string AuxiliaryDataFolderName = "辅助数据";
    public const string LegacyAuxiliaryDataFolderName = "AI上下文";
    public const string GameCopyFolderName = "游戏副本";

    public static string GetVersionFolderName(ExtractionMode extractionMode) =>
        extractionMode == ExtractionMode.Lexer ? "V2" : "V1";

    public static EraTransOutputLayout Create(string outputRoot, string gameDirectory) =>
        Create(outputRoot, gameDirectory, ExtractionMode.Legacy);

    public static EraTransOutputLayout Create(
        string outputRoot,
        string gameDirectory,
        ExtractionMode extractionMode)
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

        string versionRoot = Path.Combine(gameOutputRoot, GetVersionFolderName(extractionMode));
        return new EraTransOutputLayout(
            fullOutputRoot,
            fullGameDirectory,
            gameName,
            gameOutputRoot,
            Path.Combine(versionRoot, DictionaryFolderName),
            Path.Combine(versionRoot, AuxiliaryDataFolderName),
            Path.Combine(gameOutputRoot, GameCopyFolderName));
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
