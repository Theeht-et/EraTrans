using System.Text;
using System.Text.RegularExpressions;
using EraTrans.Core.WriteBack;

namespace EraTrans.Core;

/// <summary>
/// Creates editable working copies outside the source game directory.
/// Callers must modify the returned copy, never the original game files.
/// </summary>
public static class GameFileWorkspace
{
    /// <summary>
    /// Copies the complete game directory to a new destination. The destination must not
    /// already exist; this prevents a later run from replacing a user's edited output.
    /// Files are first copied to a sibling staging directory and then moved into place.
    /// </summary>
    /// <returns>Absolute paths of every file copied into the game workspace.</returns>
    public static IReadOnlyList<string> CopyDirectoryForModification(string gameDirectory, string outputDirectory) =>
        CopyDirectoryForModification(gameDirectory, outputDirectory, null, null);

    public static IReadOnlyList<string> CopyDirectoryForModification(
        string gameDirectory, string outputDirectory, EraParserOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return CopyDirectoryForModification(gameDirectory, outputDirectory, options.FileEncoding, options.TargetFileExtensions);
    }

    public static IReadOnlyList<string> CopyDirectoryForModification(
        string gameDirectory, string outputDirectory, Encoding? fallbackEncoding,
        IReadOnlyList<string>? targetFileExtensions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        string gameRoot = Path.GetFullPath(gameDirectory);
        string outputRoot = Path.GetFullPath(outputDirectory);

        if (!Directory.Exists(gameRoot))
        {
            throw new DirectoryNotFoundException($"Game directory does not exist: {gameRoot}");
        }
        if ((File.GetAttributes(gameRoot) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"原游戏目录本身是符号链接或其他 Reparse Point，不能安全地执行完整复制：{gameRoot}");
        }

        EnsureDirectoriesAreDisjoint(
            gameRoot,
            outputRoot,
            "The complete game-copy directory must not overlap the original game directory.");

        if (Directory.Exists(outputRoot) || File.Exists(outputRoot))
        {
            throw new InvalidOperationException(
                $"输出目录中的游戏副本已存在，为避免覆盖已有文件或用户修改，已停止操作：{outputRoot}。请先备份并移走或删除该游戏副本，再重新生成。");
        }

        string parentDirectory = Path.GetDirectoryName(outputRoot)
            ?? throw new ArgumentException("The game-copy destination must have a parent directory.", nameof(outputDirectory));
        string directoryName = Path.GetFileName(outputRoot);
        if (string.IsNullOrWhiteSpace(directoryName))
        {
            throw new ArgumentException("The game-copy destination must be a named directory.", nameof(outputDirectory));
        }

        var textExtensions = new HashSet<string>(
            [".erb", ".erh", ".csv", ".erd", ".res", ".xml"],
            StringComparer.OrdinalIgnoreCase);
        if (targetFileExtensions is not null)
        {
            foreach (string extension in targetFileExtensions)
            {
                if (string.IsNullOrWhiteSpace(extension) || !extension.StartsWith(".", StringComparison.Ordinal))
                    throw new ArgumentException($"无效的目标文件扩展名：{extension}", nameof(targetFileExtensions));
                textExtensions.Add(extension);
            }
        }
        if (fallbackEncoding is not null)
            PreflightTextFiles(gameRoot, fallbackEncoding, textExtensions);

        Directory.CreateDirectory(parentDirectory);
        string stagingDirectory = Path.Combine(
            parentDirectory,
            $".{directoryName}.{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(stagingDirectory);

        var relativeFiles = new List<string>();
        try
        {
            CopyDirectoryContents(gameRoot, stagingDirectory, gameRoot, stagingDirectory, relativeFiles, fallbackEncoding, textExtensions);
            Directory.Move(stagingDirectory, outputRoot);

            return relativeFiles
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(path => path, StringComparer.Ordinal)
                .Select(path => Path.Combine(outputRoot, path))
                .ToArray();
        }
        catch
        {
            try
            {
                if (Directory.Exists(stagingDirectory))
                {
                    Directory.Delete(stagingDirectory, recursive: true);
                }
            }
            catch
            {
                // Preserve the original copy failure. A leftover staging directory is
                // never treated as a completed game workspace.
            }

            throw;
        }
    }

    /// <summary>
    /// Copies a single file to a workspace. Kept for narrowly scoped tools; game-wide
    /// operations should use <see cref="CopyDirectoryForModification"/>.
    /// </summary>
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

    private static void CopyDirectoryContents(
        string sourceDirectory,
        string destinationDirectory,
        string sourceRoot,
        string workspaceRoot,
        List<string> copiedFiles,
        Encoding? fallbackEncoding,
        IReadOnlySet<string> textExtensions)
    {
        var enumerationOptions = new EnumerationOptions
        {
            AttributesToSkip = 0,
            IgnoreInaccessible = false,
            RecurseSubdirectories = false
        };

        foreach (string sourcePath in Directory.EnumerateFileSystemEntries(sourceDirectory, "*", enumerationOptions))
        {
            FileAttributes attributes = File.GetAttributes(sourcePath);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException(
                    $"游戏目录包含符号链接或其他 Reparse Point，出于安全考虑不能继续完整复制：{sourcePath}");
            }

            string name = Path.GetFileName(sourcePath);
            string destinationPath = Path.Combine(destinationDirectory, name);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                Directory.CreateDirectory(destinationPath);
                CopyDirectoryContents(sourcePath, destinationPath, sourceRoot, workspaceRoot, copiedFiles, fallbackEncoding, textExtensions);
                Directory.SetLastWriteTimeUtc(
                    destinationPath,
                    Directory.GetLastWriteTimeUtc(sourcePath));
                FileAttributes preservedDirectoryAttributes =
                    attributes & ~(FileAttributes.Directory | FileAttributes.ReadOnly | FileAttributes.ReparsePoint);
                File.SetAttributes(
                    destinationPath,
                    preservedDirectoryAttributes == 0
                        ? FileAttributes.Normal
                        : preservedDirectoryAttributes);
            }
            else
            {
                if (fallbackEncoding is not null && IsGameTextFile(sourcePath, textExtensions))
                {
                    string relativePath = Path.GetRelativePath(sourceRoot, sourcePath).Replace('\\', '/');
                    string text = GameFileText.Read(sourcePath, fallbackEncoding).Text;
                    if (text.Contains('\uFFFD'))
                    {
                        throw new InvalidDataException(
                            $"文本文件解码产生替换字符，拒绝生成可能乱码的 UTF-8 副本：{relativePath}。请检查游戏原始编码，并将异常文件转换为 UTF-8 BOM。");
                    }
                    if (Path.GetExtension(sourcePath).Equals(".xml", StringComparison.OrdinalIgnoreCase))
                        text = NormalizeXmlEncodingDeclaration(text);
                    File.WriteAllText(destinationPath, text,
                        new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true));
                }
                else
                {
                    File.Copy(sourcePath, destinationPath, overwrite: false);
                }
                FileAttributes writableAttributes = attributes & ~FileAttributes.ReadOnly;
                File.SetAttributes(destinationPath, writableAttributes);
                File.SetLastWriteTimeUtc(
                    destinationPath,
                    File.GetLastWriteTimeUtc(sourcePath));
                copiedFiles.Add(Path.GetRelativePath(workspaceRoot, destinationPath));
            }
        }
    }

    private static void PreflightTextFiles(string sourceRoot, Encoding fallbackEncoding, IReadOnlySet<string> textExtensions)
    {
        var errors = new List<string>();
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(sourceRoot);
        var enumerationOptions = new EnumerationOptions
        {
            AttributesToSkip = 0,
            IgnoreInaccessible = false,
            RecurseSubdirectories = false
        };

        while (pendingDirectories.TryPop(out string? directory))
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(directory, "*", enumerationOptions))
            {
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"游戏目录包含符号链接或其他 Reparse Point，出于安全考虑不能继续编码预检：{path}");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pendingDirectories.Push(path);
                    continue;
                }
                if (!IsGameTextFile(path, textExtensions))
                    continue;

                string relativePath = Path.GetRelativePath(sourceRoot, path).Replace('\\', '/');
                try
                {
                    string text = GameFileText.Read(path, fallbackEncoding).Text;
                    if (text.Contains('\uFFFD'))
                        throw new InvalidDataException("解码结果包含替换字符。");
                }
                catch (InvalidDataException ex)
                {
                    errors.Add($"{relativePath}：{ex.Message}");
                }
            }
        }

        if (errors.Count == 0)
            return;
        string details = string.Join(
            Environment.NewLine,
            errors.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ThenBy(value => value, StringComparer.Ordinal)
                .Take(20)
                .Select(value => " - " + value));
        if (errors.Count > 20)
            details += $"{Environment.NewLine} - 另有 {errors.Count - 20} 个文件存在编码问题。";
        throw new InvalidDataException(
            $"文本编码预检失败，未生成游戏副本。以下文件无法安全确定编码：{Environment.NewLine}{details}{Environment.NewLine}请在外部文本编辑器中确认这些文件的编码，并将有歧义或无法解码的文件转换为 UTF-8 BOM 后重试。");
    }

    private static bool IsGameTextFile(string path, IReadOnlySet<string> textExtensions) =>
        textExtensions.Contains(Path.GetExtension(path));

    private static string NormalizeXmlEncodingDeclaration(string text) =>
        Regex.Replace(text,
            "(<\\?xml\\b[^>]*\\bencoding\\s*=\\s*)(['\"])[^'\"]+(['\"])",
            match => match.Groups[1].Value + match.Groups[2].Value + "utf-8" + match.Groups[3].Value,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static void EnsureDirectoriesAreDisjoint(string first, string second, string message)
    {
        if (IsSameOrInside(first, second) || IsSameOrInside(second, first))
        {
            throw new InvalidOperationException(message);
        }
    }

    private static bool IsSameOrInside(string root, string candidate)
    {
        string relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(candidate));
        return relative.Equals(".", StringComparison.Ordinal) ||
               (!Path.IsPathRooted(relative) &&
                !relative.Equals("..", StringComparison.Ordinal) &&
                !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsInside(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path);
        return relative != "." &&
               !Path.IsPathRooted(relative) &&
               relative != ".." &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
               !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOutside(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path);
        return Path.IsPathRooted(relative) ||
               relative == ".." ||
               relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
