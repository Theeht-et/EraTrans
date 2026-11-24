namespace EraTrans.Core;

/// <summary>
/// One folder node in the dictionary editor's left file tree.
/// Single-child folder chains with no direct files are compacted into one node
/// (e.g. "ERB/口上/001") to avoid wasting horizontal indentation.
/// </summary>
public sealed record DictionaryFileTreeFolder(
    string DisplayName,
    string FullRelativePath,
    IReadOnlyList<DictionaryFileTreeFolder> Folders,
    IReadOnlyList<DictionaryEditorFile> Files);

/// <summary>
/// Builds a compacted folder tree for the dictionary file browser and formats
/// middle-ellipsis filenames when horizontal space is constrained.
/// </summary>
public static class DictionaryFileTreeBuilder
{
    private static readonly char[] PathSeparators = ['/', '\\'];

    public static DictionaryFileTreeFolder Build(IReadOnlyList<DictionaryEditorFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var root = new MutableFolder(string.Empty, string.Empty);
        foreach (DictionaryEditorFile file in files)
        {
            string[] segments = file.RelativePath.Split(
                PathSeparators,
                StringSplitOptions.RemoveEmptyEntries);

            MutableFolder current = root;
            string currentPath = string.Empty;
            for (int index = 0; index < segments.Length - 1; index++)
            {
                string segment = segments[index];
                currentPath = currentPath.Length == 0
                    ? segment
                    : Path.Combine(currentPath, segment);
                current = current.GetOrAddFolder(segment, currentPath);
            }

            current.Files.Add(file);
        }

        return new DictionaryFileTreeFolder(
            string.Empty,
            string.Empty,
            root.Folders.Select(CompactFolder).ToArray(),
            root.Files.ToArray());
    }

    public static string FormatMiddleEllipsis(
        string text,
        double maxWidth,
        Func<string, double> measureWidth,
        string ellipsis = "…")
    {
        ArgumentNullException.ThrowIfNull(measureWidth);

        if (string.IsNullOrEmpty(text) ||
            maxWidth <= 0 ||
            double.IsNaN(maxWidth) ||
            double.IsInfinity(maxWidth))
        {
            return text ?? string.Empty;
        }

        if (measureWidth(text) <= maxWidth)
        {
            return text;
        }

        if (measureWidth(ellipsis) >= maxWidth)
        {
            return ellipsis;
        }

        int low = 1;
        int high = text.Length - 1;
        string best = ellipsis;

        while (low <= high)
        {
            int mid = low + ((high - low) / 2);
            string candidate = BuildMiddleCandidate(text, mid, ellipsis);
            if (measureWidth(candidate) <= maxWidth)
            {
                best = candidate;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return best;
    }

    private static string BuildMiddleCandidate(string text, int keptCharacters, string ellipsis)
    {
        int headLength = keptCharacters == 1 ? 1 : keptCharacters / 2;
        int tailLength = keptCharacters - headLength;

        if (headLength > 0 && char.IsHighSurrogate(text[headLength - 1]))
        {
            headLength--;
        }

        if (tailLength > 0 && char.IsLowSurrogate(text[text.Length - tailLength]))
        {
            tailLength--;
        }

        if (headLength <= 0 && tailLength <= 0)
        {
            return ellipsis;
        }

        return string.Concat(
            text.AsSpan(0, headLength),
            ellipsis.AsSpan(),
            text.AsSpan(text.Length - tailLength, tailLength));
    }

    private static DictionaryFileTreeFolder CompactFolder(MutableFolder folder)
    {
        MutableFolder current = folder;
        string displayName = current.Name;
        string fullRelativePath = current.FullRelativePath;

        while (current.Files.Count == 0 && current.Folders.Count == 1)
        {
            MutableFolder onlyChild = current.Folders[0];
            displayName = $"{displayName}/{onlyChild.Name}";
            fullRelativePath = onlyChild.FullRelativePath;
            current = onlyChild;
        }

        return new DictionaryFileTreeFolder(
            displayName,
            fullRelativePath,
            current.Folders.Select(CompactFolder).ToArray(),
            current.Files.ToArray());
    }

    private sealed class MutableFolder(string name, string fullRelativePath)
    {
        private readonly Dictionary<string, MutableFolder> _folderLookup = new(StringComparer.OrdinalIgnoreCase);

        public string Name { get; } = name;
        public string FullRelativePath { get; } = fullRelativePath;
        public List<MutableFolder> Folders { get; } = [];
        public List<DictionaryEditorFile> Files { get; } = [];

        public MutableFolder GetOrAddFolder(string name, string fullRelativePath)
        {
            if (!_folderLookup.TryGetValue(name, out MutableFolder? child))
            {
                child = new MutableFolder(name, fullRelativePath);
                _folderLookup[name] = child;
                Folders.Add(child);
            }

            return child;
        }
    }
}
