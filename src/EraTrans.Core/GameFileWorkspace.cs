namespace EraTrans.Core;

/// <summary>
/// Creates editable working copies outside the source game directory.
/// Callers must modify the returned copy, never the original game file.
/// </summary>
public static class GameFileWorkspace
{
    public static string CopyForModification(
        string gameDirectory,
        string outputDirectory,
        string relativePath,
        bool overwrite = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        string gameRoot = Path.GetFullPath(gameDirectory);
        string outputRoot = Path.GetFullPath(outputDirectory);
        string sourcePath = Path.GetFullPath(Path.Combine(gameRoot, relativePath));

        if (!Directory.Exists(gameRoot))
        {
            throw new DirectoryNotFoundException($"Game directory does not exist: {gameRoot}");
        }
        if (Path.IsPathRooted(relativePath) || !IsInside(gameRoot, sourcePath) || sourcePath == gameRoot)
        {
            throw new ArgumentException("The requested file path must be relative to the game directory.", nameof(relativePath));
        }
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Game file was not found.", sourcePath);
        }
        if (!IsOutside(gameRoot, outputRoot))
        {
            throw new InvalidOperationException("Working-copy output directory must be outside the game directory.");
        }

        string destination = Path.GetFullPath(Path.Combine(outputRoot, relativePath));
        if (!IsInside(outputRoot, destination) || destination == outputRoot)
        {
            throw new ArgumentException("The requested relative path escapes the output directory.", nameof(relativePath));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(sourcePath, destination, overwrite);
        return destination;
    }

    private static bool IsInside(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path);
        return relative != "." &&
               !Path.IsPathRooted(relative) &&
               relative != ".." &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static bool IsOutside(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path);
        return Path.IsPathRooted(relative) ||
               relative == ".." ||
               relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}