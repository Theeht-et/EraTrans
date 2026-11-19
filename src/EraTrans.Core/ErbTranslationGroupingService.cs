using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Newtonsoft.Json.Linq;

namespace EraTrans.Core;

public sealed record ErbTranslationSourceMetadata(
    int ParsedLine,
    string FunctionId,
    string OutputType,
    string BranchPath,
    string ControlScopeId,
    string BranchLabel,
    string? CallTarget)
{
    public static ErbTranslationSourceMetadata Empty { get; } =
        new(0, "(global)", "UNKNOWN", "", "", "", null);
}

public sealed record ErbTranslationSourceEntry(
    string Key,
    string Original,
    int Sequence,
    int ParsedLine,
    string FunctionId,
    string OutputType,
    string BranchPath,
    string ControlScopeId,
    string BranchLabel,
    string? CallTarget);

public sealed record ErbTranslationControlMarker(
    int ParsedLine,
    string FunctionId,
    string BranchPath,
    string Command);

public sealed record ErbTranslationGroupEntry(
    string Key,
    string Original,
    int Sequence,
    int ParsedLine,
    string FunctionId,
    string OutputType,
    string BranchPath,
    string ControlScopeId,
    string BranchLabel,
    string GroupId,
    string GroupType,
    string? ParentGroupId);

public sealed record ErbTranslationGroup(
    string Id,
    string Type,
    string FunctionId,
    string BranchPath,
    string ControlScopeId,
    string BranchLabel,
    int FirstSequence,
    int LastSequence,
    IReadOnlyList<string> EntryKeys);

public sealed record ErbTranslationGroupingIndex(
    int SchemaVersion,
    string SourceFile,
    string DictionaryFile,
    string DictionaryFingerprint,
    IReadOnlyList<ErbTranslationGroup> Groups,
    IReadOnlyList<ErbTranslationGroupEntry> Entries);

public sealed record ErbTranslationBatch(
    IReadOnlyList<DictionaryEditorEntry> Targets,
    IReadOnlyList<AiTranslationSemanticContextGroup> SemanticContextGroups);

/// <summary>
/// Stores ERB source relationships outside the legacy JSON dictionary. The sidecar is
/// accepted only when its key/original fingerprint still matches the current dictionary.
/// </summary>
public sealed class ErbTranslationGroupingService
{
    private const int SchemaVersion = 1;
    private const int MaxBatchCharacters = 12_000;
    private const int MaxGroupTargetCharacters = 8_000;
    private const int MaxSameGroupContextCharacters = 10_000;
    private const int MaxAlternativeContextCharacters = 2_500;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public bool TryWriteOrUpdate(
        string dictionaryDirectory,
        string dictionaryFilePath,
        string candidateDictionaryPath,
        string sourceRelativePath,
        IReadOnlyList<ErbTranslationSourceEntry> sourceEntries,
        IReadOnlyList<ErbTranslationControlMarker> controlMarkers,
        out string sidecarPath,
        out string? warning)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dictionaryDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(dictionaryFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateDictionaryPath);
        ArgumentNullException.ThrowIfNull(sourceEntries);
        ArgumentNullException.ThrowIfNull(controlMarkers);

        sidecarPath = GetSidecarPath(dictionaryDirectory, dictionaryFilePath);
        warning = null;

        if (!File.Exists(dictionaryFilePath) || !File.Exists(candidateDictionaryPath))
        {
            warning = $"未生成 ERB AI 分组文件：字典或解析候选文件不存在：{dictionaryFilePath}";
            return false;
        }

        IReadOnlyList<(string Key, string Original)> candidateEntries;
        IReadOnlyList<(string Key, string Original)> currentEntries;
        try
        {
            candidateEntries = ReadTextDictionaryEntries(candidateDictionaryPath);
            currentEntries = ReadTextDictionaryEntries(dictionaryFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            warning = $"未更新 ERB AI 分组文件，无法安全读取字典：{dictionaryFilePath} - {ex.Message}";
            return false;
        }

        var expectedFromParser = sourceEntries
            .OrderBy(entry => entry.Sequence)
            .Select(entry => (entry.Key, entry.Original))
            .ToArray();

        if (!SequenceEqual(candidateEntries, expectedFromParser))
        {
            warning = $"未更新 ERB AI 分组文件，解析器词条与候选字典不一致：{dictionaryFilePath}";
            return false;
        }

        if (!SequenceEqual(currentEntries, candidateEntries))
        {
            warning = $"未更新 ERB AI 分组文件，现有字典的文本 key 或原文顺序已不同于当前源文件；为避免错误关联，保留旧分组文件：{dictionaryFilePath}";
            return false;
        }

        var groupBuild = BuildGroups(sourceEntries, controlMarkers);
        var entryRecords = new List<ErbTranslationGroupEntry>(sourceEntries.Count);
        foreach (ErbTranslationSourceEntry entry in sourceEntries.OrderBy(item => item.Sequence))
        {
            ErbTranslationGroup group = groupBuild.GroupsByEntryKey[entry.Key];
            entryRecords.Add(new ErbTranslationGroupEntry(
                entry.Key,
                entry.Original,
                entry.Sequence,
                entry.ParsedLine,
                entry.FunctionId,
                entry.OutputType,
                entry.BranchPath,
                entry.ControlScopeId,
                entry.BranchLabel,
                group.Id,
                group.Type,
                null));
        }

        string dictionaryRoot = Path.GetFullPath(dictionaryDirectory);
        string dictionaryRelativePath = GetSafeRelativePath(dictionaryRoot, dictionaryFilePath);
        var index = new ErbTranslationGroupingIndex(
            SchemaVersion,
            NormalizeRelativePath(sourceRelativePath),
            NormalizeRelativePath(dictionaryRelativePath),
            ComputeFingerprint(currentEntries),
            groupBuild.Groups,
            entryRecords);

        try
        {
            WriteAtomically(sidecarPath, JsonSerializer.Serialize(index, JsonOptions));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warning = $"无法写入 ERB AI 分组文件：{sidecarPath} - {ex.Message}";
            return false;
        }
    }

    public bool TryLoad(
        string dictionaryDirectory,
        string dictionaryFilePath,
        IReadOnlyList<DictionaryEditorEntry> dictionaryEntries,
        out ErbTranslationGroupingIndex? index,
        out string? reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dictionaryDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(dictionaryFilePath);
        ArgumentNullException.ThrowIfNull(dictionaryEntries);

        index = null;
        reason = null;
        string sidecarPath = GetSidecarPath(dictionaryDirectory, dictionaryFilePath);
        if (!File.Exists(sidecarPath))
        {
            reason = "没有找到分组文件。请重新提取当前游戏文件的字典。";
            return false;
        }

        try
        {
            string json = File.ReadAllText(sidecarPath, new UTF8Encoding(false, true));
            ErbTranslationGroupingIndex loaded =
                JsonSerializer.Deserialize<ErbTranslationGroupingIndex>(json, JsonOptions)
                ?? throw new InvalidDataException("分组文件为空。");

            if (loaded.SchemaVersion != SchemaVersion)
            {
                reason = $"分组文件版本不受支持（{loaded.SchemaVersion}）。请重新提取字典。";
                return false;
            }

            string dictionaryRoot = Path.GetFullPath(dictionaryDirectory);
            string relativeDictionaryPath = NormalizeRelativePath(
                GetSafeRelativePath(dictionaryRoot, dictionaryFilePath));
            if (!string.Equals(
                    loaded.DictionaryFile,
                    relativeDictionaryPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                reason = "分组文件对应的字典路径不一致。请重新提取字典。";
                return false;
            }

            var currentEntries = dictionaryEntries
                .Where(entry => !entry.IsVariable)
                .Select(entry => (entry.Key, entry.Original))
                .ToArray();
            if (!string.Equals(
                    loaded.DictionaryFingerprint,
                    ComputeFingerprint(currentEntries),
                    StringComparison.Ordinal))
            {
                reason = "字典的 key、原文或顺序已发生变化，分组信息已过期。请重新提取字典。";
                return false;
            }

            var indexedEntries = loaded.Entries.ToDictionary(
                entry => entry.Key,
                StringComparer.Ordinal);
            if (indexedEntries.Count != loaded.Entries.Count ||
                indexedEntries.Count != currentEntries.Length)
            {
                reason = "分组文件中的词条映射不完整或存在重复 key。请重新提取字典。";
                return false;
            }

            foreach ((string key, string original) in currentEntries)
            {
                if (!indexedEntries.TryGetValue(key, out ErbTranslationGroupEntry? entry) ||
                    !string.Equals(entry.Original, original, StringComparison.Ordinal) ||
                    !loaded.Groups.Any(group =>
                        string.Equals(group.Id, entry.GroupId, StringComparison.Ordinal) &&
                        group.EntryKeys.Contains(key, StringComparer.Ordinal)))
                {
                    reason = "分组文件与当前字典的词条关联不一致。请重新提取字典。";
                    return false;
                }
            }

            index = loaded;
            return true;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        {
            reason = $"分组文件无法读取或校验：{ex.Message}";
            return false;
        }
    }

    public IReadOnlyList<ErbTranslationBatch> BuildTranslationBatches(
        IReadOnlyList<DictionaryEditorEntry> pendingTargets,
        IReadOnlyList<DictionaryEditorEntry> allDictionaryEntries,
        ErbTranslationGroupingIndex index)
    {
        ArgumentNullException.ThrowIfNull(pendingTargets);
        ArgumentNullException.ThrowIfNull(allDictionaryEntries);
        ArgumentNullException.ThrowIfNull(index);

        if (pendingTargets.Count == 0)
        {
            return [];
        }

        var dictionaryEntries = allDictionaryEntries.ToDictionary(
            entry => entry.Key,
            StringComparer.Ordinal);
        var linksByKey = index.Entries.ToDictionary(
            entry => entry.Key,
            StringComparer.Ordinal);
        var groupsById = index.Groups.ToDictionary(
            group => group.Id,
            StringComparer.Ordinal);

        var targetsByGroup = pendingTargets
            .Where(entry => linksByKey.ContainsKey(entry.Key))
            .GroupBy(entry => linksByKey[entry.Key].GroupId, StringComparer.Ordinal)
            .Select(group => new
            {
                Group = groupsById[group.Key],
                Targets = group.OrderBy(entry => linksByKey[entry.Key].Sequence).ToArray()
            })
            .OrderBy(item => item.Group.FirstSequence)
            .ToArray();

        if (targetsByGroup.Sum(item => item.Targets.Length) != pendingTargets.Count)
        {
            throw new InvalidDataException("待翻译词条中存在未关联到分组文件的 key。");
        }

        var units = new List<ErbTranslationBatch>();
        foreach (var item in targetsByGroup)
        {
            var chunk = new List<DictionaryEditorEntry>();
            int targetCharacters = 0;
            foreach (DictionaryEditorEntry target in item.Targets)
            {
                int length = target.Original.Length + target.Context.Length + 96;
                if (chunk.Count > 0 && targetCharacters + length > MaxGroupTargetCharacters)
                {
                    units.Add(CreateBatchUnit(chunk, item.Group, index, groupsById, linksByKey, dictionaryEntries));
                    chunk = [];
                    targetCharacters = 0;
                }

                chunk.Add(target);
                targetCharacters += length;
            }

            if (chunk.Count > 0)
            {
                units.Add(CreateBatchUnit(chunk, item.Group, index, groupsById, linksByKey, dictionaryEntries));
            }
        }

        var result = new List<ErbTranslationBatch>();
        var currentTargets = new List<DictionaryEditorEntry>();
        var currentContexts = new Dictionary<string, AiTranslationSemanticContextGroup>(StringComparer.Ordinal);
        int currentCharacters = 0;

        foreach (ErbTranslationBatch unit in units)
        {
            int unitCharacters = EstimateCharacters(unit);
            if (currentTargets.Count > 0 && currentCharacters + unitCharacters > MaxBatchCharacters)
            {
                result.Add(new ErbTranslationBatch(
                    currentTargets.ToArray(),
                    currentContexts.Values.ToArray()));
                currentTargets.Clear();
                currentContexts.Clear();
                currentCharacters = 0;
            }

            currentTargets.AddRange(unit.Targets);
            foreach (AiTranslationSemanticContextGroup contextGroup in unit.SemanticContextGroups)
            {
                string contextKey = contextGroup.GroupId + "|" + contextGroup.Relationship;
                currentContexts.TryAdd(contextKey, contextGroup);
            }
            currentCharacters += unitCharacters;
        }

        if (currentTargets.Count > 0)
        {
            result.Add(new ErbTranslationBatch(
                currentTargets.ToArray(),
                currentContexts.Values.ToArray()));
        }

        return result;
    }

    private static ErbTranslationBatch CreateBatchUnit(
        IReadOnlyList<DictionaryEditorEntry> targets,
        ErbTranslationGroup targetGroup,
        ErbTranslationGroupingIndex index,
        IReadOnlyDictionary<string, ErbTranslationGroup> groupsById,
        IReadOnlyDictionary<string, ErbTranslationGroupEntry> linksByKey,
        IReadOnlyDictionary<string, DictionaryEditorEntry> dictionaryEntries)
    {
        var contexts = new List<AiTranslationSemanticContextGroup>();
        contexts.Add(CreateContextGroup(
            targetGroup,
            "same-group",
            SelectSameGroupContext(targetGroup, targets, index, dictionaryEntries, linksByKey)));

        if (targetGroup.ControlScopeId.Length > 0)
        {
            IEnumerable<ErbTranslationGroup> menuPrompts = index.Groups
                .Where(group =>
                    group.Id != targetGroup.Id &&
                    string.Equals(group.FunctionId, targetGroup.FunctionId, StringComparison.Ordinal) &&
                    string.Equals(group.ControlScopeId, targetGroup.ControlScopeId, StringComparison.Ordinal) &&
                    string.Equals(group.BranchLabel, "MENU_PROMPT", StringComparison.Ordinal));

            foreach (ErbTranslationGroup prompt in menuPrompts)
            {
                var promptEntries = prompt.EntryKeys
                    .Select(key => dictionaryEntries.TryGetValue(key, out DictionaryEditorEntry? entry) ? entry : null)
                    .Where(entry => entry is not null)
                    .Cast<DictionaryEditorEntry>()
                    .Select(entry => new AiTranslationContextEntry(entry.Key, entry.Original))
                    .ToArray();
                if (promptEntries.Length > 0)
                {
                    contexts.Add(CreateContextGroup(prompt, "menu-prompt", promptEntries));
                }
            }

            IEnumerable<ErbTranslationGroup> alternatives = index.Groups
                .Where(group =>
                    group.Id != targetGroup.Id &&
                    !string.Equals(group.BranchLabel, "MENU_PROMPT", StringComparison.Ordinal) &&
                    string.Equals(group.FunctionId, targetGroup.FunctionId, StringComparison.Ordinal) &&
                    string.Equals(group.ControlScopeId, targetGroup.ControlScopeId, StringComparison.Ordinal) &&
                    !string.Equals(group.BranchLabel, targetGroup.BranchLabel, StringComparison.Ordinal))
                .OrderBy(group => group.FirstSequence);

            int remainingCharacters = MaxAlternativeContextCharacters;
            foreach (ErbTranslationGroup alternative in alternatives)
            {
                var entries = alternative.EntryKeys
                    .Select(key => dictionaryEntries.TryGetValue(key, out DictionaryEditorEntry? entry) ? entry : null)
                    .Where(entry => entry is not null)
                    .Cast<DictionaryEditorEntry>()
                    .Take(4)
                    .ToArray();
                var selected = new List<AiTranslationContextEntry>();
                foreach (DictionaryEditorEntry entry in entries)
                {
                    int length = entry.Original.Length + entry.Key.Length + 24;
                    if (length > remainingCharacters)
                    {
                        break;
                    }
                    selected.Add(new AiTranslationContextEntry(entry.Key, entry.Original));
                    remainingCharacters -= length;
                }

                if (selected.Count == 0)
                {
                    break;
                }

                contexts.Add(CreateContextGroup(
                    alternative,
                    "alternative-branch",
                    selected));
            }
        }

        return new ErbTranslationBatch(targets.ToArray(), contexts);
    }

    private static IReadOnlyList<AiTranslationContextEntry> SelectSameGroupContext(
        ErbTranslationGroup group,
        IReadOnlyList<DictionaryEditorEntry> targets,
        ErbTranslationGroupingIndex index,
        IReadOnlyDictionary<string, DictionaryEditorEntry> dictionaryEntries,
        IReadOnlyDictionary<string, ErbTranslationGroupEntry> linksByKey)
    {
        var all = group.EntryKeys
            .Select(key => dictionaryEntries.TryGetValue(key, out DictionaryEditorEntry? entry) ? entry : null)
            .Where(entry => entry is not null)
            .Cast<DictionaryEditorEntry>()
            .ToArray();

        int totalCharacters = all.Sum(entry => entry.Original.Length + entry.Key.Length + 24);
        if (totalCharacters <= MaxSameGroupContextCharacters)
        {
            return all.Select(entry => new AiTranslationContextEntry(entry.Key, entry.Original)).ToArray();
        }

        int firstTargetSequence = targets.Min(entry => linksByKey[entry.Key].Sequence);
        int lastTargetSequence = targets.Max(entry => linksByKey[entry.Key].Sequence);
        int firstIndex = Math.Max(0, Array.FindIndex(all, entry =>
            linksByKey[entry.Key].Sequence >= firstTargetSequence) - 2);
        int lastIndex = Array.FindLastIndex(all, entry =>
            linksByKey[entry.Key].Sequence <= lastTargetSequence);
        lastIndex = Math.Min(all.Length - 1, lastIndex + 2);

        var selected = new List<AiTranslationContextEntry>();
        int usedCharacters = 0;
        for (int indexInGroup = firstIndex; indexInGroup <= lastIndex; indexInGroup++)
        {
            DictionaryEditorEntry entry = all[indexInGroup];
            int length = entry.Original.Length + entry.Key.Length + 24;
            if (selected.Count > 0 && usedCharacters + length > MaxSameGroupContextCharacters)
            {
                break;
            }
            selected.Add(new AiTranslationContextEntry(entry.Key, entry.Original));
            usedCharacters += length;
        }

        return selected;
    }

    private static AiTranslationSemanticContextGroup CreateContextGroup(
        ErbTranslationGroup group,
        string relationship,
        IReadOnlyList<AiTranslationContextEntry> entries) =>
        new(
            group.Id,
            group.Type,
            group.FunctionId,
            group.BranchPath,
            relationship,
            entries);

    private static int EstimateCharacters(ErbTranslationBatch batch) =>
        batch.Targets.Sum(entry => entry.Original.Length + entry.Context.Length + entry.Key.Length + 96) +
        batch.SemanticContextGroups.Sum(group =>
            group.Entries.Sum(entry => entry.Original.Length + entry.Key.Length + 24));

    private static (IReadOnlyList<ErbTranslationGroup> Groups, Dictionary<string, ErbTranslationGroup> GroupsByEntryKey)
        BuildGroups(
            IReadOnlyList<ErbTranslationSourceEntry> sourceEntries,
            IReadOnlyList<ErbTranslationControlMarker> controlMarkers)
    {
        var groups = new List<ErbTranslationGroup>();
        var groupsByEntryKey = new Dictionary<string, ErbTranslationGroup>(StringComparer.Ordinal);
        var current = new List<ErbTranslationSourceEntry>();

        void Flush()
        {
            if (current.Count == 0)
            {
                return;
            }

            ErbTranslationSourceEntry first = current[0];
            ErbTranslationSourceEntry last = current[^1];
            string type = ClassifyGroupType(current, controlMarkers);
            string id = $"group_{groups.Count + 1:D6}";
            var group = new ErbTranslationGroup(
                id,
                type,
                first.FunctionId,
                first.BranchPath,
                first.ControlScopeId,
                first.BranchLabel,
                first.Sequence,
                last.Sequence,
                current.Select(entry => entry.Key).ToArray());
            groups.Add(group);
            foreach (ErbTranslationSourceEntry entry in current)
            {
                groupsByEntryKey.Add(entry.Key, group);
            }
            current.Clear();
        }

        foreach (ErbTranslationSourceEntry entry in sourceEntries.OrderBy(item => item.Sequence))
        {
            if (current.Count > 0 && !CanJoinGroup(current[^1], entry))
            {
                Flush();
            }
            current.Add(entry);
        }
        Flush();

        return (groups, groupsByEntryKey);
    }

    private static bool CanJoinGroup(
        ErbTranslationSourceEntry previous,
        ErbTranslationSourceEntry current)
    {
        if (!string.Equals(previous.FunctionId, current.FunctionId, StringComparison.Ordinal) ||
            !string.Equals(previous.BranchPath, current.BranchPath, StringComparison.Ordinal))
        {
            return false;
        }

        bool previousCondition = IsConditionOutput(previous.OutputType);
        bool currentCondition = IsConditionOutput(current.OutputType);
        if (previousCondition != currentCondition)
        {
            return false;
        }

        if (previousCondition &&
            !string.Equals(previous.OutputType, current.OutputType, StringComparison.Ordinal))
        {
            return false;
        }

        int lineGap = current.ParsedLine - previous.ParsedLine;
        return lineGap <= 4 || lineGap == 0;
    }

    private static string ClassifyGroupType(
        IReadOnlyList<ErbTranslationSourceEntry> entries,
        IReadOnlyList<ErbTranslationControlMarker> markers)
    {
        if (entries.Any(entry =>
                entry.OutputType.StartsWith("PRINTBUTTON", StringComparison.Ordinal) ||
                entry.CallTarget?.Contains("CHOICE", StringComparison.OrdinalIgnoreCase) == true ||
                entry.CallTarget?.Contains("ASK", StringComparison.OrdinalIgnoreCase) == true))
        {
            return "menu";
        }

        ErbTranslationSourceEntry first = entries[0];
        ErbTranslationSourceEntry last = entries[^1];
        if (markers.Any(marker =>
                (marker.Command is "INPUT" or "INPUTS") &&
                string.Equals(marker.FunctionId, first.FunctionId, StringComparison.Ordinal) &&
                string.Equals(marker.BranchPath, first.BranchPath, StringComparison.Ordinal) &&
                marker.ParsedLine >= first.ParsedLine &&
                marker.ParsedLine <= last.ParsedLine + 8))
        {
            return "menu";
        }

        if (entries.All(entry => IsConditionOutput(entry.OutputType)))
        {
            return "condition";
        }

        return first.ControlScopeId.Length > 0 &&
               first.BranchLabel.StartsWith("CASE", StringComparison.Ordinal)
            ? "branch"
            : "dialogue";
    }

    private static bool IsConditionOutput(string outputType) =>
        outputType is "IF" or "ELSEIF" or "SELECTCASE" or "CASE" or "CASEELSE";

    private static IReadOnlyList<(string Key, string Original)> ReadTextDictionaryEntries(string path)
    {
        string json = File.ReadAllText(path, new UTF8Encoding(false, true));
        JArray entries = JArray.Parse(json, new JsonLoadSettings
        {
            DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
            CommentHandling = CommentHandling.Ignore
        });

        return entries.Children<JObject>()
            .Where(entry => (entry["key"]?.ToString() ?? string.Empty)
                .StartsWith("文本", StringComparison.Ordinal))
            .Select(entry => (
                entry["key"]?.ToString() ?? string.Empty,
                entry["original"]?.ToString() ?? string.Empty))
            .ToArray();
    }

    private static bool SequenceEqual(
        IReadOnlyList<(string Key, string Original)> left,
        IReadOnlyList<(string Key, string Original)> right) =>
        left.Count == right.Count &&
        left.Zip(right).All(pair =>
            string.Equals(pair.First.Key, pair.Second.Key, StringComparison.Ordinal) &&
            string.Equals(pair.First.Original, pair.Second.Original, StringComparison.Ordinal));

    private static string ComputeFingerprint(IEnumerable<(string Key, string Original)> entries)
    {
        var builder = new StringBuilder();
        foreach ((string key, string original) in entries)
        {
            builder.Append(key.Length).Append(':').Append(key);
            builder.Append(original.Length).Append(':').Append(original);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static string GetSidecarPath(string dictionaryDirectory, string dictionaryFilePath)
    {
        string root = Path.GetFullPath(dictionaryDirectory);
        string relative = GetSafeRelativePath(root, dictionaryFilePath);
        string? outputRoot = Directory.GetParent(root)?.FullName;
        if (outputRoot is null)
        {
            throw new InvalidOperationException("无法定位字典目录对应的输出目录。");
        }

        string sidecarRelative = Path.ChangeExtension(relative, ".groups.json");
        return Path.GetFullPath(Path.Combine(outputRoot, "AI上下文", sidecarRelative));
    }

    private static string GetSafeRelativePath(string root, string path)
    {
        string fullPath = Path.GetFullPath(path);
        string relative = Path.GetRelativePath(root, fullPath);
        if (Path.IsPathRooted(relative) ||
            relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("字典文件路径超出了字典目录。");
        }
        return relative;
    }

    private static string NormalizeRelativePath(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');

    private static void WriteAtomically(string path, string content)
    {
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        byte[] bytes = new UTF8Encoding(false).GetBytes(content);

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
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                File.Replace(temporaryPath, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporaryPath, path);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
