using System.Text.Json;

namespace EraTrans.Core.WriteBack;

/// <summary>
/// Optional position sidecar: maps a game source file and a dictionary key to the exact
/// character range the extractor took the text from.
///
/// The file lives next to the existing grouping sidecars
/// (<c>&lt;output root&gt;/AI上下文/&lt;relative path&gt;.spans.json</c>) and is only used
/// when present. Dictionaries without it keep the legacy whole-file replacement path.
///
/// Shape:
/// <code>
/// { "schema": 1, "file": "ERB/foo.erb",
///   "spans": [ { "key": "文本ERB\\foo.00001", "start": 12, "end": 24,
///                "line": 7, "command": "PRINTFORM", "kind": "form" } ] }
/// </code>
/// <c>line</c>/<c>command</c>/<c>kind</c> are optional additions written by the visible-text
/// extractor; older sidecars without them are still accepted.
/// </summary>
public sealed class SpanCatalog
{
    private const string SidecarDirectoryName = "AI上下文";
    private const string SidecarSuffix = ".spans.json";

    private readonly Dictionary<string, Dictionary<string, SpanInfo>> _byFile;
    private readonly Dictionary<string, string> _hashByFile;

    private SpanCatalog(
        Dictionary<string, Dictionary<string, SpanInfo>> byFile,
        Dictionary<string, string> hashByFile)
    {
        _byFile = byFile;
        _hashByFile = hashByFile;
    }

    public static SpanCatalog Empty { get; } = new(
        new Dictionary<string, Dictionary<string, SpanInfo>>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    public int FileCount => _byFile.Count;

    public int SpanCount => _byFile.Values.Sum(spans => spans.Count);

    public IReadOnlyCollection<string> GamePaths => _byFile.Keys;

    public static SpanCatalog LoadFromSidecar(string dictionaryDirectory, Action<string>? onWarning = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dictionaryDirectory);

        string sidecarRoot = ResolveSidecarRoot(dictionaryDirectory);
        if (!Directory.Exists(sidecarRoot))
        {
            return Empty;
        }

        var byFile = new Dictionary<string, Dictionary<string, SpanInfo>>(StringComparer.OrdinalIgnoreCase);
        var hashByFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in Directory.EnumerateFiles(sidecarRoot, "*" + SidecarSuffix, SearchOption.AllDirectories))
        {
            string relativeSidecar = Path.GetRelativePath(sidecarRoot, path);
            string fallbackGamePath = relativeSidecar[..^SidecarSuffix.Length].Replace('\\', '/');

            try
            {
                using FileStream stream = File.OpenRead(path);
                using JsonDocument document = JsonDocument.Parse(stream);
                string relativeGamePath = ReadGamePath(document.RootElement, fallbackGamePath);
                if (!document.RootElement.TryGetProperty("spans", out JsonElement spans) ||
                    spans.ValueKind != JsonValueKind.Array)
                {
                    onWarning?.Invoke($"定位文件缺少 spans 数组，已忽略：{relativeSidecar}");
                    continue;
                }

                var map = new Dictionary<string, SpanInfo>(StringComparer.Ordinal);
                foreach (JsonElement item in spans.EnumerateArray())
                {
                    if (!TryReadSpan(item, out string key, out SpanInfo info))
                    {
                        continue;
                    }

                    map[key] = info;
                }

                // Register the file even when it has no spans: an empty locator means "nothing in
                // this file can be written by position", and falling back to a whole-file replace
                // would reintroduce the "same text replaced everywhere" bug this pipeline fixes.
                byFile[relativeGamePath] = map;
                if (document.RootElement.TryGetProperty("fileHash", out JsonElement hash) &&
                    hash.ValueKind == JsonValueKind.String)
                {
                    string? value = hash.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        hashByFile[relativeGamePath] = value;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                onWarning?.Invoke($"定位文件无法读取，已忽略：{relativeSidecar} - {ex.Message}");
            }
        }

        return byFile.Count == 0
            ? Empty
            : new SpanCatalog(byFile, hashByFile);
    }

    /// <summary>
    /// Exact-path test (no stem fallback). Used when a dictionary can map to several game files
    /// (.erb/.erh): only the file the locator was extracted from may be rewritten.
    /// </summary>
    /// <summary>Source fingerprint recorded when the locators were extracted, if any.</summary>
    public bool TryGetFileHash(string relativeGamePath, out string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeGamePath);
        string normalized = relativeGamePath.Replace('\\', '/');
        if (_hashByFile.TryGetValue(normalized, out hash!))
        {
            return true;
        }

        string stem = StripExtension(normalized);
        foreach ((string path, string value) in _hashByFile)
        {
            if (string.Equals(StripExtension(path), stem, StringComparison.OrdinalIgnoreCase))
            {
                hash = value;
                return true;
            }
        }

        hash = string.Empty;
        return false;
    }

    public bool ContainsGamePath(string relativeGamePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeGamePath);
        return _byFile.ContainsKey(relativeGamePath.Replace('\\', '/'));
    }

    public bool TryGetSpans(string relativeGamePath, out IReadOnlyDictionary<string, TextSpan> spans)
    {
        if (TryGetFile(relativeGamePath, out Dictionary<string, SpanInfo>? found))
        {
            var map = new Dictionary<string, TextSpan>(found.Count, StringComparer.Ordinal);
            foreach ((string key, SpanInfo info) in found)
            {
                map[key] = info.Span;
            }

            spans = map;
            return true;
        }

        spans = new Dictionary<string, TextSpan>();
        return false;
    }

    /// <summary>
    /// Looks up the extractor metadata of one entry. <paramref name="relativeGamePath"/> may be
    /// the recorded game file (<c>ERB/foo.erb</c>) or its stem (<c>ERB/foo</c>), and both slash
    /// directions are accepted.
    /// </summary>
    public bool TryGetInfo(string relativeGamePath, string key, out SpanInfo info)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (TryGetFile(relativeGamePath, out Dictionary<string, SpanInfo>? found) &&
            found!.TryGetValue(key, out info))
        {
            return true;
        }

        info = default;
        return false;
    }

    private bool TryGetFile(string relativeGamePath, out Dictionary<string, SpanInfo>? found)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeGamePath);
        string normalized = relativeGamePath.Replace('\\', '/');
        if (_byFile.TryGetValue(normalized, out found))
        {
            return true;
        }

        string stem = StripExtension(normalized);
        foreach ((string path, Dictionary<string, SpanInfo> map) in _byFile)
        {
            if (string.Equals(StripExtension(path), stem, StringComparison.OrdinalIgnoreCase))
            {
                found = map;
                return true;
            }
        }

        found = null;
        return false;
    }

    private static string StripExtension(string path)
    {
        int separator = path.LastIndexOf('/');
        int dot = path.LastIndexOf('.');
        return dot > separator ? path[..dot] : path;
    }

    private static string ReadGamePath(JsonElement root, string fallback)
    {
        if (root.TryGetProperty("file", out JsonElement element) && element.ValueKind == JsonValueKind.String)
        {
            string? value = element.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Replace('\\', '/');
            }
        }

        return fallback;
    }

    private static bool TryReadSpan(JsonElement item, out string key, out SpanInfo info)
    {
        key = string.Empty;
        info = default;

        if (item.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty("key", out JsonElement keyElement) ||
            !item.TryGetProperty("start", out JsonElement startElement) ||
            !item.TryGetProperty("end", out JsonElement endElement))
        {
            return false;
        }

        string? text = keyElement.GetString();
        if (string.IsNullOrEmpty(text) ||
            !startElement.TryGetInt32(out int start) ||
            !endElement.TryGetInt32(out int end))
        {
            return false;
        }

        var span = new TextSpan(start, end);
        if (!span.IsValid)
        {
            return false;
        }

        int line = item.TryGetProperty("line", out JsonElement lineElement) && lineElement.TryGetInt32(out int parsedLine)
            ? parsedLine
            : 0;
        string command = ReadString(item, "command");
        string kind = ReadString(item, "kind");

        key = text;
        info = new SpanInfo(span, line, command, kind);
        return true;
    }

    private static string ReadString(JsonElement item, string name) =>
        item.TryGetProperty(name, out JsonElement element) && element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : string.Empty;

    private static string ResolveSidecarRoot(string dictionaryDirectory)
    {
        string root = Path.GetFullPath(dictionaryDirectory);
        string outputRoot = string.Equals(Path.GetFileName(root), "字典", StringComparison.OrdinalIgnoreCase)
            ? Directory.GetParent(root)?.FullName ?? root
            : root;
        return Path.Combine(outputRoot, SidecarDirectoryName);
    }
}

/// <summary>One extractor entry: where it lives in the source plus which command produced it.</summary>
public readonly record struct SpanInfo(TextSpan Span, int Line, string Command, string Kind);
