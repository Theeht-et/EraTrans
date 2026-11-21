using EraTrans.Core;
using System.Windows;

namespace EraTrans.App;

/// <summary>
/// Both modes share one request format, one prompt and one parser. They differ
/// only in how the lines of a single request are chosen:
///
/// * fixed batches (the switch is off): a constant number of dictionary lines,
///   no ordering analysis at all;
/// * ordered windows (the switch is on): consecutive ERB functions in source order,
///   never cutting a function in half. This replaces the old semantic-group batching.
/// </summary>
public partial class DictionaryEditorControl
{
    private const int TranslationBatchSize = 12;

    /// <summary>Function name used for fixed batches, which have no function context.</summary>
    private const string BatchFunctionName = "@batch";

    private readonly SequentialTranslationPlanner _windowPlanner = new();
    private int _windowMaxCharacters = SequentialTranslationPlanner.DefaultMaxCharacters;
    private int _windowMaxLines = SequentialTranslationPlanner.DefaultMaxLines;

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

    /// <summary>Read-only summary shown in the editor so the windowing is not a black box.</summary>
    public string DescribeTranslationWindows()
    {
        if (_document is null)
            return "尚未加载字典。";

        if (_activeGroupingIndex is null)
            return "分组文件不可用：" + (_groupingFallbackReason ?? "未找到，请重新提取字典。");

        WindowPlan plan = BuildWindowPlan(
            _document.Entries,
            new HashSet<string>(StringComparer.Ordinal));

        return $"本文件：{plan.FunctionCount} 个函数 → {plan.Windows.Count} 个窗口" +
               $"（其中 {plan.SplitFunctionCount} 个函数过长被切开）；" +
               $"同一渲染行 {plan.JoinedLineCount} 处，互斥分支 {plan.AlternativeLineCount} 行";
    }

    // ---------------------------------------------------------------------
    // 按原文顺序整段翻译（开关打开，替代原来的分组模式）
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

        await RunWindowsAsync(
            profile!,
            plan.Windows,
            statusPrefix: "按原文顺序翻译",
            skippedSplitFunctions: plan.SplitFunctionCount);
    }

    // ---------------------------------------------------------------------
    // 传统模式：按行固定条数分批，不做任何顺序分析（请求格式与提示词相同）
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

        using var cancellation = new CancellationTokenSource();
        _translationCancellation = cancellation;
        _isTranslating = true;
        SetTranslationControlsEnabled(false);
        UpdateDocumentStatus();

        int applied = 0;
        int skipped = 0;
        int windowNumber = 0;
        int totalWindows = windows.Count;
        int totalLines = windows.Sum(window => window.TargetCount);

        StatusTextBlock.Text = skippedSplitFunctions > 0
            ? $"准备{statusPrefix}：{totalWindows} 个窗口，{totalLines} 行；被切开的函数 {skippedSplitFunctions} 个。"
            : $"准备{statusPrefix}：{totalWindows} 个窗口，{totalLines} 行。";

        try
        {
            foreach (TranslationWindow window in windows)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                windowNumber++;

                StatusTextBlock.Text =
                    $"{statusPrefix}：{(batchSize > 0 ? "第" : "窗口 ")}{windowNumber}/{totalWindows}" +
                    $"（{window.FunctionId}，本窗 {window.TargetCount} 行）……";

                WindowTranslationOutcome outcome = await _translationService.TranslateWindowAsync(
                    profile, window, reasoning: null, cancellation.Token);
                ApplyOutcome(outcome, entriesByIndex, ref applied, ref skipped);

                if (outcome.MissingIds.Count > 0)
                {
                    // Only the unanswered lines are sent again, so a partially broken
                    // answer never wastes the whole request.
                    TranslationWindow retry = RetryWindowForIds(window, outcome.MissingIds);
                    WindowTranslationOutcome retryOutcome = await _translationService.TranslateWindowAsync(
                        profile, retry, reasoning: null, cancellation.Token);
                    ApplyOutcome(retryOutcome, entriesByIndex, ref applied, ref skipped);

                    if (retryOutcome.MissingIds.Count > 0)
                    {
                        skipped += retryOutcome.MissingIds.Count;
                        StatusTextBlock.Text =
                            $"第 {windowNumber} 次请求：{retryOutcome.MissingIds.Count} 行未返回译文，已跳过。";
                    }
                }

                EntriesDataGrid.Items.Refresh();
                UpdateDocumentStatus();
            }

            StatusTextBlock.Text =
                $"{statusPrefix}完成：{totalWindows} 个请求，回填 {applied} 行，跳过 {skipped} 行。" +
                "请检查译文并手动保存。";
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = $"翻译已取消：已回填 {applied} 行，尚未自动保存。";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"翻译中断：已回填 {applied} 行，尚未自动保存。";
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

        return _windowPlanner.Build(sources, _activeGroupingIndex, _windowMaxCharacters, _windowMaxLines);
    }

    private void ApplyOutcome(
        WindowTranslationOutcome outcome,
        IReadOnlyDictionary<int, DictionaryEditorEntry> entriesByIndex,
        ref int applied,
        ref int skipped)
    {
        foreach (AiTranslationResult result in outcome.Translations)
        {
            if (!entriesByIndex.TryGetValue(result.Id, out DictionaryEditorEntry? entry))
                continue;

            if (entry.TryApplyAiTranslation(entry.Key, entry.Original, result.Translation))
                applied++;
            else
                skipped++;
        }
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
