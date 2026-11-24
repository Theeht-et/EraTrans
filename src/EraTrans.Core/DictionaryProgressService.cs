using System.Globalization;
using System.Text.Json;

namespace EraTrans.Core;

public enum DictionaryProgressStatus
{
    NotStarted,
    InProgress,
    Completed,
    Empty
}

public sealed record DictionaryFileProgress(
    string FullPath,
    string RelativePath,
    int TotalEntries,
    int EditableCount,
    int TranslatedCount,
    int PendingCount,
    int IgnoredCount,
    bool ReadFailed = false)
{
    public int ActiveCount => TranslatedCount + PendingCount;

    public double CompletionPercent =>
        ReadFailed ? 0.0 :
        ActiveCount == 0 ? 100.0 :
        TranslatedCount * 100.0 / ActiveCount;

    public DictionaryProgressStatus Status =>
        ReadFailed ? DictionaryProgressStatus.NotStarted :
        ActiveCount == 0 ? DictionaryProgressStatus.Empty :
        TranslatedCount == 0 ? DictionaryProgressStatus.NotStarted :
        PendingCount == 0 ? DictionaryProgressStatus.Completed :
        DictionaryProgressStatus.InProgress;

    public string StatusLabel => ReadFailed
        ? "读取失败"
        : Status switch
        {
            DictionaryProgressStatus.Empty => "无需翻译",
            DictionaryProgressStatus.NotStarted => "未翻译",
            DictionaryProgressStatus.InProgress => "翻译中",
            DictionaryProgressStatus.Completed => "已完成",
            _ => string.Empty
        };

    public string CompletionText =>
        ReadFailed || ActiveCount == 0
            ? "—"
            : $"{CompletionPercent:0.0}%";
}

public sealed record DictionaryFolderProgress(
    int TotalFiles,
    int CompletedFiles,
    int ActiveCount,
    int TranslatedCount,
    int PendingCount)
{
    public double CompletionPercent =>
        ActiveCount == 0 ? 100.0 : TranslatedCount * 100.0 / ActiveCount;

    public DictionaryProgressStatus Status =>
        ActiveCount == 0 ? DictionaryProgressStatus.Empty :
        TranslatedCount == 0 ? DictionaryProgressStatus.NotStarted :
        PendingCount == 0 ? DictionaryProgressStatus.Completed :
        DictionaryProgressStatus.InProgress;
}

public sealed record DictionaryWorkspaceProgress(
    IReadOnlyList<DictionaryFileProgress> Files,
    int TotalFiles,
    int CompletedFiles,
    int InProgressFiles,
    int NotStartedFiles,
    int EmptyFiles,
    int TotalEditable,
    int TotalTranslated,
    int TotalPending,
    int TotalIgnored)
{
    public int TotalActive => TotalTranslated + TotalPending;

    public double CompletionPercent =>
        TotalActive == 0 ? 0.0 : TotalTranslated * 100.0 / TotalActive;
}

/// <summary>
/// Lightweight streaming progress scanner for JSON dictionary files.
/// Only inspects "key" and "stage" fields without loading full text payloads into memory.
/// </summary>
public static class DictionaryProgressScanner
{
    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static DictionaryFileProgress ScanFile(DictionaryEditorFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        try
        {
            byte[] rawBytes = File.ReadAllBytes(file.FullPath);
            ReadOnlySpan<byte> jsonSpan = StripUtf8Bom(rawBytes);
            var reader = new Utf8JsonReader(jsonSpan, ReaderOptions);

            if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
            {
                return Failed(file);
            }

            int totalEntries = 0;
            int editableCount = 0;
            int translatedCount = 0;
            int pendingCount = 0;
            int ignoredCount = 0;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray)
                {
                    break;
                }

                if (reader.TokenType != JsonTokenType.StartObject)
                {
                    return Failed(file);
                }

                totalEntries++;
                string key = string.Empty;
                int stage = 0;

                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    if (reader.TokenType != JsonTokenType.PropertyName)
                    {
                        continue;
                    }

                    if (reader.ValueTextEquals("key"u8))
                    {
                        reader.Read();
                        key = reader.TokenType == JsonTokenType.String
                            ? reader.GetString() ?? string.Empty
                            : string.Empty;
                    }
                    else if (reader.ValueTextEquals("stage"u8))
                    {
                        reader.Read();
                        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int parsedNumber))
                        {
                            stage = parsedNumber;
                        }
                        else if (reader.TokenType == JsonTokenType.String &&
                                 int.TryParse(reader.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedString))
                        {
                            stage = parsedString;
                        }
                    }
                    else
                    {
                        reader.Read();
                        reader.Skip();
                    }
                }

                if (!key.StartsWith("变量", StringComparison.Ordinal))
                {
                    editableCount++;
                    if (stage > 0)
                    {
                        translatedCount++;
                    }
                    else if (stage < 0)
                    {
                        ignoredCount++;
                    }
                    else
                    {
                        pendingCount++;
                    }
                }
            }

            return new DictionaryFileProgress(
                file.FullPath,
                file.RelativePath,
                totalEntries,
                editableCount,
                translatedCount,
                pendingCount,
                ignoredCount);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Failed(file);
        }
    }

    public static DictionaryFileProgress FromDocument(
        DictionaryEditorFile file,
        DictionaryEditorDocument document)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(document);

        int editableCount = 0;
        int translatedCount = 0;
        int pendingCount = 0;
        int ignoredCount = 0;

        foreach (DictionaryEditorEntry entry in document.Entries)
        {
            if (!entry.IsEditable)
            {
                continue;
            }

            editableCount++;
            if (entry.Stage > 0)
            {
                translatedCount++;
            }
            else if (entry.Stage < 0)
            {
                ignoredCount++;
            }
            else
            {
                pendingCount++;
            }
        }

        return new DictionaryFileProgress(
            file.FullPath,
            file.RelativePath,
            document.Entries.Count,
            editableCount,
            translatedCount,
            pendingCount,
            ignoredCount);
    }

    public static DictionaryWorkspaceProgress Aggregate(IReadOnlyList<DictionaryFileProgress> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        int completedFiles = 0;
        int inProgressFiles = 0;
        int notStartedFiles = 0;
        int emptyFiles = 0;
        int totalEditable = 0;
        int totalTranslated = 0;
        int totalPending = 0;
        int totalIgnored = 0;

        foreach (DictionaryFileProgress item in files)
        {
            totalEditable += item.EditableCount;
            totalTranslated += item.TranslatedCount;
            totalPending += item.PendingCount;
            totalIgnored += item.IgnoredCount;

            switch (item.Status)
            {
                case DictionaryProgressStatus.Completed:
                    completedFiles++;
                    break;
                case DictionaryProgressStatus.InProgress:
                    inProgressFiles++;
                    break;
                case DictionaryProgressStatus.Empty:
                    emptyFiles++;
                    break;
                default:
                    notStartedFiles++;
                    break;
            }
        }

        return new DictionaryWorkspaceProgress(
            files,
            files.Count,
            completedFiles,
            inProgressFiles,
            notStartedFiles,
            emptyFiles,
            totalEditable,
            totalTranslated,
            totalPending,
            totalIgnored);
    }

    public static DictionaryFolderProgress AggregateFolder(
        DictionaryFileTreeFolder folder,
        IReadOnlyDictionary<string, DictionaryFileProgress> progressByFullPath)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(progressByFullPath);

        int totalFiles = 0;
        int completedFiles = 0;
        int activeCount = 0;
        int translatedCount = 0;
        int pendingCount = 0;

        Accumulate(folder, progressByFullPath, ref totalFiles, ref completedFiles, ref activeCount, ref translatedCount, ref pendingCount);
        return new DictionaryFolderProgress(totalFiles, completedFiles, activeCount, translatedCount, pendingCount);
    }

    private static void Accumulate(
        DictionaryFileTreeFolder folder,
        IReadOnlyDictionary<string, DictionaryFileProgress> progressByFullPath,
        ref int totalFiles,
        ref int completedFiles,
        ref int activeCount,
        ref int translatedCount,
        ref int pendingCount)
    {
        foreach (DictionaryEditorFile file in folder.Files)
        {
            totalFiles++;
            if (progressByFullPath.TryGetValue(file.FullPath, out DictionaryFileProgress? progress))
            {
                activeCount += progress.ActiveCount;
                translatedCount += progress.TranslatedCount;
                pendingCount += progress.PendingCount;
                if (progress.Status is DictionaryProgressStatus.Completed or DictionaryProgressStatus.Empty)
                {
                    completedFiles++;
                }
            }
        }

        foreach (DictionaryFileTreeFolder child in folder.Folders)
        {
            Accumulate(child, progressByFullPath, ref totalFiles, ref completedFiles, ref activeCount, ref translatedCount, ref pendingCount);
        }
    }

    private static DictionaryFileProgress Failed(DictionaryEditorFile file) =>
        new(file.FullPath, file.RelativePath, 0, 0, 0, 0, 0, ReadFailed: true);

    private static ReadOnlySpan<byte> StripUtf8Bom(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
            ? bytes.AsSpan(3)
            : bytes.AsSpan();
}
