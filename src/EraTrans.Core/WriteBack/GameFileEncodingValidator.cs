using System.Text;

namespace EraTrans.Core.WriteBack;

public sealed record GameFileEncodingIssue(
    string FullPath,
    string RelativePath,
    string Reason);

public sealed class GameFileEncodingPreflightException : Exception
{
    public GameFileEncodingPreflightException(
        string message,
        IReadOnlyList<GameFileEncodingIssue> issues)
        : base(message)
    {
        Issues = issues;
    }

    public IReadOnlyList<GameFileEncodingIssue> Issues { get; }

    public static bool TryExtract(Exception exception, out GameFileEncodingPreflightException? preflight)
    {
        if (exception is GameFileEncodingPreflightException direct)
        {
            preflight = direct;
            return true;
        }

        if (exception.InnerException is GameFileEncodingPreflightException inner)
        {
            preflight = inner;
            return true;
        }

        preflight = null;
        return false;
    }
}

/// <summary>
/// Performs a full input validation pass before extractors or workspace builders write output.
/// This is important for legacy parsers that catch per-file read exceptions and continue.
/// </summary>
internal static class GameFileEncodingValidator
{
    internal static HashSet<string> CreateTextExtensions(IEnumerable<string>? targetFileExtensions)
    {
        var extensions = new HashSet<string>(
            [".erb", ".erh", ".csv", ".erd", ".res", ".xml"],
            StringComparer.OrdinalIgnoreCase);

        if (targetFileExtensions is null)
            return extensions;

        foreach (string extension in targetFileExtensions)
        {
            if (string.IsNullOrWhiteSpace(extension) || !extension.StartsWith(".", StringComparison.Ordinal))
                throw new ArgumentException($"无效的目标文件扩展名：{extension}", nameof(targetFileExtensions));
            extensions.Add(extension);
        }

        return extensions;
    }

    internal static IReadOnlyList<GameFileEncodingIssue> FindDirectoryIssues(
        string directory,
        Encoding fallbackEncoding,
        IReadOnlySet<string> textExtensions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(fallbackEncoding);
        ArgumentNullException.ThrowIfNull(textExtensions);

        string root = Path.GetFullPath(directory);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"文本编码预检目录不存在：{root}");
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"文本编码预检目录本身是符号链接或其他 Reparse Point：{root}");

        var files = new List<string>();
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(root);
        var enumerationOptions = new EnumerationOptions
        {
            AttributesToSkip = 0,
            IgnoreInaccessible = false,
            RecurseSubdirectories = false
        };

        while (pendingDirectories.TryPop(out string? currentDirectory))
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(currentDirectory, "*", enumerationOptions))
            {
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"游戏目录包含符号链接或其他 Reparse Point，出于安全考虑不能继续编码预检：{path}");

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pendingDirectories.Push(path);
                    continue;
                }

                if (textExtensions.Contains(Path.GetExtension(path)))
                    files.Add(path);
            }
        }

        return FindFileIssues(files, fallbackEncoding, root);
    }

    internal static IReadOnlyList<GameFileEncodingIssue> FindFileIssues(
        IEnumerable<string> filePaths,
        Encoding fallbackEncoding,
        string? relativeTo = null)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        ArgumentNullException.ThrowIfNull(fallbackEncoding);

        var issues = new List<GameFileEncodingIssue>();
        foreach (string path in filePaths
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(value => value, StringComparer.Ordinal))
        {
            string fullPath = Path.GetFullPath(path);
            string displayPath = string.IsNullOrWhiteSpace(relativeTo)
                ? fullPath
                : Path.GetRelativePath(relativeTo, fullPath).Replace('\\', '/');

            try
            {
                // Decoding uses strict fallbacks, so a successful read never substitutes
                // characters. A literal U+FFFD in the file is therefore author content,
                // not a decoding failure, and must not be reported as one.
                _ = GameFileText.Read(fullPath, fallbackEncoding);
            }
            catch (InvalidDataException ex)
            {
                issues.Add(new GameFileEncodingIssue(fullPath, displayPath, ExtractReason(ex)));
            }
        }

        return issues;
    }

    internal static void ValidateDirectory(
        string directory,
        Encoding fallbackEncoding,
        IReadOnlySet<string> textExtensions)
    {
        IReadOnlyList<GameFileEncodingIssue> issues = FindDirectoryIssues(directory, fallbackEncoding, textExtensions);
        if (issues.Count > 0)
            throw CreatePreflightException(issues);
    }

    internal static void ValidateFiles(
        IEnumerable<string> filePaths,
        Encoding fallbackEncoding,
        string? relativeTo = null)
    {
        IReadOnlyList<GameFileEncodingIssue> issues = FindFileIssues(filePaths, fallbackEncoding, relativeTo);
        if (issues.Count > 0)
            throw CreatePreflightException(issues);
    }

    internal static InvalidDataException CreatePreflightException(
        IReadOnlyList<GameFileEncodingIssue> issues)
    {
        string details = string.Join(
            Environment.NewLine,
            issues.Take(20).Select(issue => $" - {issue.RelativePath}：{issue.Reason}"));
        if (issues.Count > 20)
            details += $"{Environment.NewLine} - 另有 {issues.Count - 20} 个文件存在编码问题。";

        string message =
            $"文本编码预检失败，已停止处理，未生成游戏副本或提取输出。以下文件无法安全确定编码：{Environment.NewLine}{details}{Environment.NewLine}请在外部文本编辑器中确认这些文件的编码，并将它们转换为 UTF-8 BOM 后重试。";

        return new InvalidDataException(message, new GameFileEncodingPreflightException(message, issues));
    }

    private static string ExtractReason(InvalidDataException ex)
    {
        if (ex.InnerException is InvalidDataException inner && !string.IsNullOrWhiteSpace(inner.Message))
            return inner.Message;

        int newlineIndex = ex.Message.IndexOf('\n');
        if (newlineIndex >= 0 && newlineIndex + 1 < ex.Message.Length)
            return ex.Message[(newlineIndex + 1)..].Trim();

        return ex.Message;
    }
}
