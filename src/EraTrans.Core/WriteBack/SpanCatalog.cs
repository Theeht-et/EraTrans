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
///   "spans": [ { "key": "文本ERB\\foo.00001", "start": 12, "end": 24 } ] }
/// </code>
/// </summary>
public sealed class SpanCatalog
{
    private const string SidecarDirectoryName = "AI上下文";
    private const string SidecarSuffix = ".spans.json";

    private readonly Dictionary<string, Dictionary<string, TextSpan>> _byFile;

    private SpanCatalog(Dictionary<string, Dictionary<string, TextSpan>> byFile)
    {
        _byFile = byFile;
    }

    public static SpanCatalog Empty { get; } = new(new Dictionary<string, Dictionary<string, TextSpan>>(StringComparer.OrdinalIgnoreCase));

    public int FileCount => _byFile.Count;

    public int SpanCount => _byFile.Values.Sum(spans => spans.Count);

    public static SpanCatalog LoadFromSidecar(string dictionaryDirectory, Action<string>? onWarning = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dictionaryDirectory);

        string sidecarRoot = ResolveSidecarRoot(dictionaryDirectory);
        if (!Directory.Exists(sidecarRoot))
        {
            return Empty;
        }

        var byFile = new Dictionary<string, Dictionary<string, TextSpan>>(StringComparer.OrdinalIgnoreCase);
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

                var map = new Dictionary<string, TextSpan>(StringComparer.Ordinal);
                foreach (JsonElement item in spans.EnumerateArray())
                {
                    if (!TryReadSpan(item, out string key, out TextSpan span))
                    {
                        continue;
                    }

                    map[key] = span;
                }

                if (map.Count > 0)
                {
                    byFile[relativeGamePath] = map;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                onWarning?.Invoke($"定位文件无法读取，已忽略：{relativeSidecar} - {ex.Message}");
            }
        }

        return byFile.Count == 0
            ? Empty
            : new SpanCatalog(byFile);
    }

    public bool TryGetSpans(string relativeGamePath, out IReadOnlyDictionary<string, TextSpan> spans)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeGamePath);
        string normalized = relativeGamePath.Replace('\\', '/');
        if (_byFile.TryGetValue(normalized, out Dictionary<string, TextSpan>? found))
        {
            spans = found;
            return true;
        }

        spans = new Dictionary<string, TextSpan>();
        return false;
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

    private static bool TryReadSpan(JsonElement item, out string key, out TextSpan span)
    {
        key = string.Empty;
        span = default;

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

        key = text;
        span = new TextSpan(start, end);
        return span.IsValid;
    }

    private static string ResolveSidecarRoot(string dictionaryDirectory)
    {
        string root = Path.GetFullPath(dictionaryDirectory);
        string outputRoot = string.Equals(Path.GetFileName(root), "字典", StringComparison.OrdinalIgnoreCase)
            ? Directory.GetParent(root)?.FullName ?? root
            : root;
        return Path.Combine(outputRoot, SidecarDirectoryName);
    }
}
