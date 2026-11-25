using EraTrans.Core;
using System.Collections.Concurrent;
using System.Windows;

namespace EraTrans.App;

/// <summary>
/// Both modes share one request format, one prompt and one parser. They differ
/// only in how the lines of a single request are chosen:
///
/// * fixed batches (the switch is off): a constant number of dictionary lines,
///   no ordering analysis at all;
/// * ordered windows (the switch is on): consecutive ERB functions in source order,
///   never cutting a function in half.
///
/// Requests may run in parallel; results are applied on the UI thread as each one
/// completes, so the grid fills in progressively.
/// </summary>
public partial class DictionaryEditorControl
{
    private const int TranslationBatchSize = 12;

    /// <summary>Function name used for fixed batches, which have no function context.</summary>
    private const string BatchFunctionName = "@batch";

    private readonly SequentialTranslationPlanner _windowPlanner = new();

    private int WindowMaxCharacters => Math.Clamp(
        _settings.WindowMaxCharacters,
        EraTransUserSettings.MinimumWindowCharacters,
        EraTransUserSettings.MaximumWindowCharacters);

    private int WindowMaxLines => Math.Clamp(
        _settings.WindowMaxLines,
        EraTransUserSettings.MinimumWindowLines,
        EraTransUserSettings.MaximumWindowLines);

    private int TranslationConcurrency => Math.Clamp(
        _settings.TranslationConcurrency,
        EraTransUserSettings.MinimumTranslationConcurrency,
        EraTransUserSettings.MaximumTranslationConcurrency);

    /// <summary>Entry point used by both translate buttons.</summary>
    private async Task TranslateWindowsAsync(IReadOnlyList<DictionaryEditorEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
            return;

        if (_settings.EnableErbSemanticGroupingTranslation)
            await TranslateOrderedWindowsAsync(entries);
        else
            await TranslateFixedBatchesAsync(entries);
    }

    /// <summary>
    /// Describes what the next run will send. The text follows the mode that is
    /// actually active, so a batch-mode dictionary is not described as windows.
    /// </summary>
    public string DescribeTranslationWindows()
    {
        if (_document is null)
            return "尚未加载字典。";

        string grouping = _activeGroupingIndex is null
            ? "分组文件不可用：" + (_groupingFallbackReason ?? "未找到")
            : "分组文件可用";

        if (!_settings.EnableErbSemanticGroupingTranslation)
            return $"当前为按行分批翻译（每批 {TranslationBatchSize} 条）· {grouping}";

        if (_activeGroupingIndex is null)
            return grouping + "，无法按原文顺序整段翻译。";

        WindowPlan plan = BuildWindowPlan(
            _document.Entries,
            new HashSet<string>(StringComparer.Ordinal));

        return $"本文件：{plan.FunctionCount} 个函数 → {plan.Windows.Count} 个窗口" +
               $"（其中 {plan.SplitFunctionCount} 个函数过长被切开）；" +
               $"同一渲染行 {plan.JoinedLineCount} 处，互斥分支 {plan.AlternativeLineCount} 行";
    }

    // ---------------------------------------------------------------------
    // 按原文顺序整段翻译（开关打开）
    // ---------------------------------------------------------------------

    private async Task TranslateOrderedWindowsAsync(IReadOnlyList<DictionaryEditorEntry> entries)
    {
        if (_document is null)
            return;

        if (!TryGetTranslationProfile(out AiProviderProfile? profile))
            return;

        if (_activeGroupingIndex is null)
        {
            MessageBox.Show(
                OwnerWindow,
                (_groupingFallbackReason ?? "未找到分组文件。") +
                "\n\n按原文顺序整段翻译需要分组文件来确定函数边界。" +
                "\n可以重新提取当前字典，或关闭“按原文顺序整段翻译”改用按行分批。",
                "缺少分组文件",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        HashSet<string> targetKeys = entries
            .Select(entry => entry.Key)
            .ToHashSet(StringComparer.Ordinal);
        WindowPlan plan = BuildWindowPlan(_document.Entries, targetKeys);

        if (plan.TargetLineCount == 0)
        {
            MessageBox.Show(OwnerWindow, "选中的词条中没有可翻译的行。", "没有可翻译内容",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // A dictionary entry that is missing from the sidecar would silently stay
        // untranslated; stop and say so instead.
        int scheduled = plan.Windows
            .SelectMany(window => window.Lines)
            .Where(line => line.Translate)
            .Select(line => line.Key)
            .Distinct(StringComparer.Ordinal)
            .Count();
        if (scheduled < entries.Count)
        {
            MessageBox.Show(
                OwnerWindow,
                $"有 {entries.Count - scheduled} 个待翻译词条不在分组文件里。" +
                "\n它们通常意味着字典与分组文件不一致（例如分组文件过期或被修改过）。" +
                "\n可以重新提取当前字典，或改用按行分批翻译。为避免静默漏译，本次没有发送任何请求。",
                "分组文件与字典不一致",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        await RunWindowsAsync(
            profile!,
            plan.Windows,
            statusPrefix: "按原文顺序翻译",
            skippedSplitFunctions: plan.SplitFunctionCount);
    }

    // ---------------------------------------------------------------------
    // 固定批次：按行固定条数，不做任何顺序分析（请求格式与提示词相同）
    // ---------------------------------------------------------------------

    private async Task TranslateFixedBatchesAsync(IReadOnlyList<DictionaryEditorEntry> entries)
    {
        if (_document is null)
            return;

        if (!TryGetTranslationProfile(out AiProviderProfile? profile))
            return;

        TranslationWindow[] windows = entries
            .Select((entry, position) => (entry, position))
            .GroupBy(item => item.position / TranslationBatchSize)
            .Select(group => new TranslationWindow(
                BatchFunctionName,
                1,
                group.Select(item => new WindowLine(
                    item.entry.Index,
                    item.entry.Key,
                    item.entry.Original,
                    BatchFunctionName,
                    Translate: true,
                    Reference: false,
                    Join: 0,
                    InlineCondition: item.entry.Original.Contains("\\@", StringComparison.Ordinal),
                    AltGroup: null)).ToArray()))
            .ToArray();

        if (windows.Length == 0)
            return;

        await RunWindowsAsync(
            profile!,
            windows,
            statusPrefix: "按行分批翻译",
            skippedSplitFunctions: 0,
            batchSize: TranslationBatchSize);
    }

    // ---------------------------------------------------------------------
    // 两种模式共用的发送、重试与回填
    // ---------------------------------------------------------------------

    private async Task RunWindowsAsync(
        AiProviderProfile profile,
        IReadOnlyList<TranslationWindow> windows,
        string statusPrefix,
        int skippedSplitFunctions,
        int batchSize = 0)
    {
        if (_document is null)
            return;

        var entriesByIndex = _document.Entries.ToDictionary(entry => entry.Index);
        int concurrency = Math.Max(1, Math.Min(TranslationConcurrency, windows.Count));

        using var cancellation = new CancellationTokenSource();
        _translationCancellation = cancellation;
        _isTranslating = true;
        SetTranslationControlsEnabled(false);
        UpdateDocumentStatus();

        var pending = new ConcurrentQueue<TranslationWindow>(windows);
        int applied = 0;
        int skipped = 0;
        int completed = 0;
        int totalRequests = windows.Count;
        int totalLines = windows.Sum(window => window.TargetCount);

        StatusTextBlock.Text = skippedSplitFunctions > 0
            ? $"准备{statusPrefix}：{totalRequests} 个请求，{totalLines} 行；被切开的函数 {skippedSplitFunctions} 个；并发 {concurrency}。"
            : $"准备{statusPrefix}：{totalRequests} 个请求，{totalLines} 行；并发 {concurrency}。";

        // Each worker pulls the next request when it becomes free. Continuations run
        // on the UI thread, so applying results and refreshing the grid is safe.
        async Task WorkerAsync()
        {
            while (pending.TryDequeue(out TranslationWindow? window))
            {
                cancellation.Token.ThrowIfCancellationRequested();

                WindowTranslationOutcome outcome = await _translationService.TranslateWindowAsync(
                    profile, window, reasoning: null, _activeGlossary, cancellation.Token);
                applied += ApplyWindowResults(outcome, entriesByIndex, ref skipped);

                if (outcome.MissingIds.Count > 0)
                {
                    // Only the unanswered lines are sent again, so a partially broken
                    // answer never wastes the whole request.
                    TranslationWindow retry = RetryWindowForIds(window, outcome.MissingIds);
                    WindowTranslationOutcome retryOutcome = await _translationService.TranslateWindowAsync(
                        profile, retry, reasoning: null, _activeGlossary, cancellation.Token);
                    applied += ApplyWindowResults(retryOutcome, entriesByIndex, ref skipped);
                }

                completed++;
                StatusTextBlock.Text =
                    $"{statusPrefix}：已完成 {completed}/{totalRequests} 个请求" +
                    $"（并发 {concurrency}），回填 {applied} 行，跳过 {skipped} 行。";

                EntriesDataGrid.Items.Refresh();
                UpdateDocumentStatus();
            }
        }

        try
        {
            Task[] workers = Enumerable.Range(0, concurrency)
                .Select(_ => WorkerAsync())
                .ToArray();

            try
            {
                await Task.WhenAll(workers);
            }
            catch
            {
                // Stop the remaining workers before reporting the failure.
                cancellation.Cancel();
                throw;
            }

            StatusTextBlock.Text =
                $"{statusPrefix}完成：{totalRequests} 个请求，回填 {applied} 行，跳过 {skipped} 行。" +
                "请检查译文并手动保存。";
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text =
                $"翻译已取消：已完成 {completed}/{totalRequests} 个请求，回填 {applied} 行，尚未自动保存。";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text =
                $"翻译中断：已完成 {completed}/{totalRequests} 个请求，回填 {applied} 行，尚未自动保存。";
            MessageBox.Show(OwnerWindow, ex.Message, "AI 翻译失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _translationCancellation = null;
            _isTranslating = false;
            SetTranslationControlsEnabled(true);
            UpdateDocumentStatus();
            UpdateTranslationButtons();
            UpdateProviderStatus();
        }
    }

    /// <summary>Applies one answer and returns how many lines were written.</summary>
    private static int ApplyWindowResults(
        WindowTranslationOutcome outcome,
        IReadOnlyDictionary<int, DictionaryEditorEntry> entriesByIndex,
        ref int skipped)
    {
        int applied = 0;
        foreach (AiTranslationResult result in outcome.Translations)
        {
            if (!entriesByIndex.TryGetValue(result.Id, out DictionaryEditorEntry? entry))
                continue;

            if (entry.TryApplyAiTranslation(entry.Key, entry.Original, result.Translation))
                applied++;
            else
                skipped++;
        }

        return applied;
    }

    private bool TryGetTranslationProfile(out AiProviderProfile? profile)
    {
        profile = null;
        try
        {
            profile = _providerStore.Load().ActiveProvider;
        }
        catch (Exception ex)
        {
            MessageBox.Show(OwnerWindow, ex.Message, "读取 AI 配置失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        if (profile is not null)
            return true;

        MessageBoxResult configure = MessageBox.Show(
            OwnerWindow,
            "尚未配置 AI 服务商。是否现在打开设置？",
            "AI 服务未配置",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);
        if (configure == MessageBoxResult.Yes)
            AiSettingsButton_Click(this, new RoutedEventArgs());
        return false;
    }

    private WindowPlan BuildWindowPlan(
        IReadOnlyList<DictionaryEditorEntry> allEntries,
        ISet<string> targetKeys)
    {
        if (_activeGroupingIndex is null)
            throw new InvalidOperationException("当前字典没有可用的分组文件。");

        WindowSourceLine[] sources = allEntries
            .Where(entry => entry.Key.StartsWith("文本", StringComparison.Ordinal))
            .OrderBy(entry => entry.Index)
            .Select(entry => new WindowSourceLine(
                entry.Index,
                entry.Key,
                entry.Original,
                targetKeys.Contains(entry.Key)))
            .ToArray();

        return _windowPlanner.Build(sources, _activeGroupingIndex, WindowMaxCharacters, WindowMaxLines);
    }

    private static TranslationWindow RetryWindowForIds(
        TranslationWindow window,
        IReadOnlyCollection<int> missingIds)
    {
        HashSet<int> wanted = missingIds.ToHashSet();
        WindowLine[] lines = window.Lines
            .Where(line => line.Translate && wanted.Contains(line.Id))
            .ToArray();

        return window with { Lines = lines };
    }
}
