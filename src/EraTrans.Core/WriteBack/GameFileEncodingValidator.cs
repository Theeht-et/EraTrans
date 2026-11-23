using System.Text;

namespace EraTrans.Core.WriteBack;

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

    internal static void ValidateDirectory(
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

        ValidateFiles(files, fallbackEncoding, root);
    }

    internal static void ValidateFiles(
        IEnumerable<string> filePaths,
        Encoding fallbackEncoding,
        string? relativeTo = null)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        ArgumentNullException.ThrowIfNull(fallbackEncoding);

        var errors = new List<string>();
        foreach (string path in filePaths
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(value => value, StringComparer.Ordinal))
        {
            string displayPath = string.IsNullOrWhiteSpace(relativeTo)
                ? path
                : Path.GetRelativePath(relativeTo, path).Replace('\\', '/');

            try
            {
                string text = GameFileText.Read(path, fallbackEncoding).Text;
                if (text.Contains('\uFFFD'))
                    throw new InvalidDataException("解码结果包含替换字符。");
            }
            catch (InvalidDataException ex)
            {
                errors.Add($"{displayPath}：{ex.Message}");
            }
        }

        if (errors.Count == 0)
            return;

        string details = string.Join(
            Environment.NewLine,
            errors.Take(20).Select(value => " - " + value));
        if (errors.Count > 20)
            details += $"{Environment.NewLine} - 另有 {errors.Count - 20} 个文件存在编码问题。";

        throw new InvalidDataException(
            $"文本编码预检失败，已停止处理，未生成游戏副本或提取输出。以下文件无法安全确定编码：{Environment.NewLine}{details}{Environment.NewLine}请在外部文本编辑器中确认这些文件的编码，并将有歧义或无法解码的文件转换为 UTF-8 BOM 后重试。");
    }
}
