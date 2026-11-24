using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EraTrans.Core;

public sealed record DictionaryEditorFile(string FullPath, string RelativePath);

public sealed record DictionaryStageOption(int Value, string Label);

/// <summary>
/// Finds and loads the legacy JSON dictionary files without changing their contents.
/// </summary>
public sealed class DictionaryEditorService
{
    public IReadOnlyList<DictionaryEditorFile> ListDictionaryFiles(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        string root = Path.GetFullPath(directory);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Dictionary directory does not exist: {root}");
        }

        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
            .Select(path => new DictionaryEditorFile(
                path,
                Path.GetRelativePath(root, path)))
            .Where(file => !IsNonDictionaryJson(file.RelativePath))
            .OrderBy(file => file.RelativePath, NaturalPathComparer.Instance)
            .ThenBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsNonDictionaryJson(string relativePath)
    {
        string normalized = relativePath.Replace('\\', '/');
        return normalized.Equals("extractor.json", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("visibletext-report.json", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("辅助数据/", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("AI上下文/", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith(".spans.json", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith(".groups.json", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class NaturalPathComparer : IComparer<string>
    {
        public static NaturalPathComparer Instance { get; } = new();

        public int Compare(string? left, string? right)
        {
            if (ReferenceEquals(left, right))
                return 0;
            if (left is null)
                return -1;
            if (right is null)
                return 1;

            int leftIndex = 0;
            int rightIndex = 0;
            while (leftIndex < left.Length && rightIndex < right.Length)
            {
                if (IsAsciiDigit(left[leftIndex]) && IsAsciiDigit(right[rightIndex]))
                {
                    int leftEnd = leftIndex;
                    int rightEnd = rightIndex;
                    while (leftEnd < left.Length && IsAsciiDigit(left[leftEnd]))
                        leftEnd++;
                    while (rightEnd < right.Length && IsAsciiDigit(right[rightEnd]))
                        rightEnd++;

                    int leftNumberStart = leftIndex;
                    int rightNumberStart = rightIndex;
                    while (leftNumberStart < leftEnd - 1 && left[leftNumberStart] == '0')
                        leftNumberStart++;
                    while (rightNumberStart < rightEnd - 1 && right[rightNumberStart] == '0')
                        rightNumberStart++;

                    int leftNumberLength = leftEnd - leftNumberStart;
                    int rightNumberLength = rightEnd - rightNumberStart;
                    int numberLengthComparison = leftNumberLength.CompareTo(rightNumberLength);
                    if (numberLengthComparison != 0)
                        return numberLengthComparison;

                    int numberComparison = string.CompareOrdinal(
                        left, leftNumberStart, right, rightNumberStart, leftNumberLength);
                    if (numberComparison != 0)
                        return numberComparison;

                    int digitRunLengthComparison =
                        (leftEnd - leftIndex).CompareTo(rightEnd - rightIndex);
                    if (digitRunLengthComparison != 0)
                        return digitRunLengthComparison;

                    leftIndex = leftEnd;
                    rightIndex = rightEnd;
                    continue;
                }

                int characterComparison =
                    char.ToUpperInvariant(left[leftIndex]).CompareTo(
                        char.ToUpperInvariant(right[rightIndex]));
                if (characterComparison != 0)
                    return characterComparison;

                leftIndex++;
                rightIndex++;
            }

            int remainingLengthComparison =
                (left.Length - leftIndex).CompareTo(right.Length - rightIndex);
            return remainingLengthComparison != 0
                ? remainingLengthComparison
                : StringComparer.Ordinal.Compare(left, right);
        }

        private static bool IsAsciiDigit(char value) => value is >= '0' and <= '9';
    }

    public DictionaryEditorDocument Load(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        string fullPath = Path.GetFullPath(filePath);
        if (!Path.GetExtension(fullPath).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Only JSON dictionary files can be opened.", nameof(filePath));
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Dictionary file was not found.", fullPath);
        }

        byte[] originalBytes = File.ReadAllBytes(fullPath);
        string json = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
            .GetString(RemoveUtf8Bom(originalBytes));

        JArray entries;
        try
        {
            entries = JArray.Parse(json, new JsonLoadSettings
            {
                DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
                CommentHandling = CommentHandling.Ignore
            });
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Dictionary JSON is invalid: {fullPath}", ex);
        }

        return new DictionaryEditorDocument(fullPath, entries, SHA256.HashData(originalBytes));
    }

    private static byte[] RemoveUtf8Bom(byte[] content)
    {
        return content.Length >= 3 &&
               content[0] == 0xEF &&
               content[1] == 0xBB &&
               content[2] == 0xBF
            ? content[3..]
            : content;
    }
}

/// <summary>
/// Editable view of a single dictionary entry. The original JObject remains the source
/// of truth, so fields not shown by the editor are retained when the file is saved.
/// </summary>
public sealed class DictionaryEditorEntry : INotifyPropertyChanged
{
    private static readonly IReadOnlyList<DictionaryStageOption> StandardStageOptions =
        Array.AsReadOnly(new[]
        {
            new DictionaryStageOption(-1, "忽略（-1）"),
            new DictionaryStageOption(0, "未翻译（0）"),
            new DictionaryStageOption(1, "已翻译（1）")
        });

    private readonly JObject _source;
    private readonly Action _markDocumentDirty;
    private string _translation;
    private int _stage;
    private string _semanticGroupLabel = string.Empty;
    private string _semanticGroupEntriesPreview = string.Empty;
    private string _entryTypeLabel = string.Empty;
    private bool _isScriptEntry;
    private string _validationWarning = string.Empty;

    internal DictionaryEditorEntry(JObject source, Action markDocumentDirty, int index)
    {
        _source = source;
        _markDocumentDirty = markDocumentDirty;
        Index = index;

        Key = source["key"]?.ToString() ?? string.Empty;
        Original = source["original"]?.ToString() ?? string.Empty;
        Context = source["context"]?.ToString() ?? string.Empty;
        _translation = source["translation"]?.ToString() ?? string.Empty;

        JToken? stageToken = source["stage"];
        if (stageToken is null)
        {
            _stage = 0;
        }
        else if (!int.TryParse(
                     stageToken.ToString(),
                     NumberStyles.Integer,
                     CultureInfo.InvariantCulture,
                     out _stage))
        {
            throw new InvalidDataException(
                $"Dictionary entry {index} has an invalid stage value.");
        }

        IsVariable = Key.StartsWith("变量", StringComparison.Ordinal);
        IsEditable = !IsVariable;

        StageOptions = StandardStageOptions.Any(option => option.Value == _stage)
            ? StandardStageOptions
            : StandardStageOptions
                .Append(new DictionaryStageOption(_stage, $"自定义（{_stage}）"))
                .ToArray();

        _validationWarning = TranslationPreValidator.Validate(Original, _translation, _isScriptEntry);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Index { get; }
    public string Key { get; }
    public string DisplayKey =>
        Key.Length >= 5 && Key[^5..].All(char.IsDigit)
            ? Key[^5..]
            : Key;
    public string Original { get; }
    public string Context { get; }
    public bool IsVariable { get; }
    public bool IsEditable { get; }
    public IReadOnlyList<DictionaryStageOption> StageOptions { get; }
    public string SemanticGroupLabel => _semanticGroupLabel;
    public string SemanticGroupEntriesPreview => _semanticGroupEntriesPreview;
    public string EntryTypeLabel => _entryTypeLabel;
    public bool IsScriptEntry => _isScriptEntry;
    public string ValidationWarning => _validationWarning;
    public bool HasValidationWarning => _validationWarning.Length > 0;

    public void SetSemanticGroupInfo(string label, string entriesPreview)
    {
        _semanticGroupLabel = label ?? string.Empty;
        _semanticGroupEntriesPreview = entriesPreview ?? string.Empty;
        OnPropertyChanged(nameof(SemanticGroupLabel));
        OnPropertyChanged(nameof(SemanticGroupEntriesPreview));
    }

    public void SetEntryMetadata(string typeLabel, bool isScriptEntry)
    {
        _entryTypeLabel = typeLabel ?? string.Empty;
        _isScriptEntry = isScriptEntry;
        OnPropertyChanged(nameof(EntryTypeLabel));
        OnPropertyChanged(nameof(IsScriptEntry));
        RefreshValidationWarning();
    }

    public string Translation
    {
        get => _translation;
        set
        {
            EnsureEditable();
            value ??= string.Empty;
            if (string.Equals(_translation, value, StringComparison.Ordinal))
            {
                return;
            }

            _translation = value;
            _source["translation"] = value;
            _markDocumentDirty();
            OnPropertyChanged();
            RefreshValidationWarning();
        }
    }

    public int Stage
    {
        get => _stage;
        set
        {
            EnsureEditable();
            if (_stage == value)
            {
                return;
            }

            _stage = value;
            _source["stage"] = value;
            _markDocumentDirty();
            OnPropertyChanged();
            OnPropertyChanged(nameof(StageDescription));
        }
    }

    public string StageDescription => _stage switch
    {
        -1 => "忽略",
        0 => "未翻译",
        1 => "已翻译",
        _ => $"自定义（{_stage}）"
    };

    public bool TryApplyAiTranslation(
        string expectedKey,
        string expectedOriginal,
        string translation)
    {
        if (!IsEditable ||
            Stage != 0 ||
            !string.IsNullOrWhiteSpace(Translation) ||
            !string.Equals(Key, expectedKey, StringComparison.Ordinal) ||
            !string.Equals(Original, expectedOriginal, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(translation))
        {
            return false;
        }

        _translation = translation;
        _source["translation"] = translation;
        _stage = 1;
        _source["stage"] = 1;
        _markDocumentDirty();
        OnPropertyChanged(nameof(Translation));
        OnPropertyChanged(nameof(Stage));
        OnPropertyChanged(nameof(StageDescription));
        RefreshValidationWarning();
        return true;
    }

    private void RefreshValidationWarning()
    {
        string updated = TranslationPreValidator.Validate(Original, _translation, _isScriptEntry);
        if (string.Equals(_validationWarning, updated, StringComparison.Ordinal))
        {
            return;
        }

        _validationWarning = updated;
        OnPropertyChanged(nameof(ValidationWarning));
        OnPropertyChanged(nameof(HasValidationWarning));
    }

    private void EnsureEditable()
    {
        if (!IsEditable)
        {
            throw new InvalidOperationException("变量类词条为只读，不能编辑译文或翻译状态。");
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>
/// Represents one open JSON dictionary. Saves update only translation and stage fields,
/// preserve all other JSON data, and refuse to overwrite external edits made after loading.
/// </summary>
public sealed class DictionaryEditorDocument : INotifyPropertyChanged
{
    private readonly JArray _json;
    private byte[] _loadedFileHash;
    private bool _isDirty;

    internal DictionaryEditorDocument(string filePath, JArray json, byte[] loadedFileHash)
    {
        FilePath = filePath;
        _json = json;
        _loadedFileHash = loadedFileHash;

        Entries = new ObservableCollection<DictionaryEditorEntry>();
        for (int index = 0; index < json.Count; index++)
        {
            if (json[index] is not JObject entry)
            {
                throw new InvalidDataException(
                    $"Dictionary entry {index} is not a JSON object. The file was not changed.");
            }

            var editorEntry = new DictionaryEditorEntry(entry, MarkDirty, index);
            editorEntry.PropertyChanged += Entry_PropertyChanged;
            Entries.Add(editorEntry);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string FilePath { get; }
    public ObservableCollection<DictionaryEditorEntry> Entries { get; }
    public bool IsDirty => _isDirty;

    public int EditableEntryCount => Entries.Count(entry => entry.IsEditable);
    public int TranslatedEntryCount => Entries.Count(entry => entry.IsEditable && entry.Stage > 0);
    public int WarningEntryCount => Entries.Count(entry => entry.HasValidationWarning);
    public string SummaryText => WarningEntryCount > 0
        ? $"词条 {Entries.Count} · 可编辑 {EditableEntryCount} · 已翻译 {TranslatedEntryCount} · 告警 {WarningEntryCount}"
        : $"词条 {Entries.Count} · 可编辑 {EditableEntryCount} · 已翻译 {TranslatedEntryCount}";

    public void Save()
    {
        if (!_isDirty)
        {
            return;
        }

        if (!File.Exists(FilePath))
        {
            throw new IOException("字典文件已被删除，未保存任何更改。");
        }

        byte[] currentBytes = File.ReadAllBytes(FilePath);
        byte[] currentHash = SHA256.HashData(currentBytes);
        if (!CryptographicOperations.FixedTimeEquals(currentHash, _loadedFileHash))
        {
            throw new IOException(
                "字典文件在打开后已被其他程序修改。为避免覆盖外部更改，本次保存已取消；请重新打开文件后再编辑。");
        }

        byte[] outputBytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            .GetBytes(_json.ToString(Formatting.Indented));
        string temporaryPath = Path.Combine(
            Path.GetDirectoryName(FilePath)!,
            $".{Path.GetFileName(FilePath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       options: FileOptions.WriteThrough))
            {
                stream.Write(outputBytes);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Replace(temporaryPath, FilePath, destinationBackupFileName: null);
            }
            catch (PlatformNotSupportedException)
            {
                // Both paths are in the same directory, so the fallback remains a
                // same-volume replacement on platforms without File.Replace support.
                File.Move(temporaryPath, FilePath, overwrite: true);
            }

            _loadedFileHash = SHA256.HashData(outputBytes);
            _isDirty = false;
            OnPropertyChanged(nameof(IsDirty));
            OnPropertyChanged(nameof(SummaryText));
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private void Entry_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Translation edits do not affect the summary counters unless validation status changes.
        if (e.PropertyName is nameof(DictionaryEditorEntry.Stage)
            or nameof(DictionaryEditorEntry.HasValidationWarning))
        {
            OnPropertyChanged(nameof(SummaryText));
        }
    }

    private void MarkDirty()
    {
        if (_isDirty)
        {
            return;
        }

        _isDirty = true;
        OnPropertyChanged(nameof(IsDirty));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
