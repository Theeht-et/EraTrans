namespace EraTrans.Core;

public enum GameFileKind
{
    Csv,
    Erb,
    Erd,
    ResourceCsv,
    Xml
}

public sealed record GameFile(string FullPath, string RelativePath, GameFileKind Kind);

/// <summary>
/// Finds files handled by the legacy dictionary extractor without modifying the game directory.
/// </summary>
public static class GameDirectoryScanner
{
    public static IReadOnlyList<GameFile> Scan(string gameDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);

        var root = Path.GetFullPath(gameDirectory);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Game directory does not exist: {root}");
        }

        var files = new List<GameFile>();
        AddFiles(Path.Combine(root, "CSV"), "*.csv", GameFileKind.Csv, root, files);
        AddErbFiles(Path.Combine(root, "ERB"), root, files);
        AddFiles(Path.Combine(root, "resources"), "*.csv", GameFileKind.ResourceCsv, root, files);
        AddFiles(Path.Combine(root, "XML"), "*.xml", GameFileKind.Xml, root, files);
        AddFiles(Path.Combine(root, "ERB"), "*.xml", GameFileKind.Xml, root, files);

        return files
            .DistinctBy(file => file.FullPath, StringComparer.OrdinalIgnoreCase)
            .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void AddErbFiles(string directory, string root, ICollection<GameFile> files)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        var paths = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToArray();
        var erbPaths = paths
            .Where(path => Path.GetExtension(path).Equals(".erb", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            var extension = Path.GetExtension(path);
            if (extension.Equals(".erd", StringComparison.OrdinalIgnoreCase))
            {
                files.Add(Create(path, root, GameFileKind.Erd));
            }
            else if (extension.Equals(".erb", StringComparison.OrdinalIgnoreCase))
            {
                files.Add(Create(path, root, GameFileKind.Erb));
            }
            else if (extension.Equals(".erh", StringComparison.OrdinalIgnoreCase))
            {
                var siblingErb = Path.ChangeExtension(path, ".erb");
                if (!erbPaths.Contains(siblingErb))
                {
                    files.Add(Create(path, root, GameFileKind.Erb));
                }
            }
        }
    }

    private static void AddFiles(
        string directory,
        string pattern,
        GameFileKind kind,
        string root,
        ICollection<GameFile> files)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories))
        {
            files.Add(Create(path, root, kind));
        }
    }

    private static GameFile Create(string path, string root, GameFileKind kind) =>
        new(Path.GetFullPath(path), Path.GetRelativePath(root, path), kind);
}
