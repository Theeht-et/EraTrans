using EraTrans.Core;
using System.Windows;

namespace EraTrans.App;

/// <summary>
/// Two translation modes share one entry point:
///
/// * "按行分批" (the existing switch is off): a fixed number of dictionary lines per
///   request, no ordering analysis at all. Kept unchanged because it is predictable
///   and works on dictionaries whose grouping file is missing.
/// * "按原文顺序整段" (the existing switch is on): consecutive ERB functions are sent
///   as ordered windows, so a passage is read as a whole before it is translated.
///   This replaces the previous semantic-group batching.
/// </summary>
public partial class DictionaryEditorControl
{
    private const int TranslationBatchSize = 12;

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
    // 按原文顺序整段翻译（替换原来的分组模式）
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

        var entriesByIndex = _document.Entries.ToDictionary(entry => entry.Index);

        using var cancellation = new CancellationTokenSource();
        _translationCancellation = cancellation;
        _isTranslating = true;
        SetTranslationControlsEnabled(false);
        UpdateDocumentStatus();

        int applied = 0;
        int skipped = 0;
        int windowNumber = 0;
        int totalWindows = plan.Windows.Count;

        StatusTextBlock.Text =
            $"准备按原文顺序翻译：{totalWindows} 个窗口，{plan.TargetLineCount} 行；" +
            $"被切开的函数 {plan.SplitFunctionCount} 个。";

        try
        {
            foreach (TranslationWindow window in plan.Windows)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                windowNumber++;

                StatusTextBlock.Text =
                    $"按原文顺序翻译：窗口 {windowNumber}/{totalWindows}" +
                    $"（{window.FunctionId}，本窗 {window.TargetCount} 行）……";

                WindowTranslationOutcome outcome = await _translationService.TranslateWindowAsync(
                    profile!, window, reasoning: null, cancellation.Token);
                ApplyOutcome(outcome, entriesByIndex, ref applied, ref skipped);

                if (outcome.MissingIds.Count > 0)
                {
                    // Only the unanswered lines are sent again, so a partially broken
                    // answer never wastes the whole window.
                    TranslationWindow retry = RetryWindowForIds(window, outcome.MissingIds);
                    WindowTranslationOutcome retryOutcome = await _translationService.TranslateWindowAsync(
                        profile!, retry, reasoning: null, cancellation.Token);
                    ApplyOutcome(retryOutcome, entriesByIndex, ref applied, ref skipped);

                    if (retryOutcome.MissingIds.Count > 0)
                    {
                        skipped += retryOutcome.MissingIds.Count;
                        StatusTextBlock.Text =
                            $"窗口 {windowNumber}：{retryOutcome.MissingIds.Count} 行未返回译文，已跳过。";
                    }
                }

                EntriesDataGrid.Items.Refresh();
                UpdateDocumentStatus();
            }

            StatusTextBlock.Text =
                $"按原文顺序翻译完成：{totalWindows} 个窗口，回填 {applied} 行，跳过 {skipped} 行。" +
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
            EndTranslationRun();
        }
    }

    // ---------------------------------------------------------------------
    // 传统模式：按行固定条数分批，不做任何顺序分析
    // ---------------------------------------------------------------------

    private async Task TranslateFixedBatchesAsync(IReadOnlyList<DictionaryEditorEntry> entries)
    {
        if (_document is null)
            return;

        if (!TryGetTranslationProfile(out AiProviderProfile? profile))
            return;

        DictionaryEditorEntry[][] batches = entries
            .Select((entry, position) => (entry, position))
            .GroupBy(item => item.position / TranslationBatchSize)
            .Select(group => group.Select(item => item.entry).ToArray())
            .ToArray();

        if (batches.Length == 0)
            return;

        var entriesByIndex = _document.Entries.ToDictionary(entry => entry.Index);

        using var cancellation = new CancellationTokenSource();
        _translationCancellation = cancellation;
        _isTranslating = true;
        SetTranslationControlsEnabled(false);
        UpdateDocumentStatus();

        int applied = 0;
        int skipped = 0;
        int completed = 0;
        int total = entries.Count;

        try
        {
            for (int batchIndex = 0; batchIndex < batches.Length; batchIndex++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                DictionaryEditorEntry[] batch = batches[batchIndex];

                StatusTextBlock.Text =
                    $"正在请求 AI 翻译：{completed}/{total}" +
                    $"（第 {batchIndex + 1}/{batches.Length} 批，本批 {batch.Length} 项）……";

                AiTranslationRequest[] requests = batch
                    .Select(entry => new AiTranslationRequest(
                        entry.Index, entry.Key, entry.Original, entry.Context))
                    .ToArray();

                IReadOnlyList<AiTranslationResult> results = await _translationService.TranslateAsync(
                    profile!, requests, cancellation.Token);

                ApplyResults(results, entriesByIndex, ref applied, ref skipped);

                completed += batch.Length;
                EntriesDataGrid.Items.Refresh();
                UpdateDocumentStatus();
            }

            StatusTextBlock.Text =
                $"AI 翻译完成：回填 {applied} 项，跳过 {skipped} 项。请检查译文并手动保存。";
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = $"AI 翻译已取消：已回填 {applied} 项，尚未自动保存。";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"AI 翻译中断：已回填 {applied} 项，尚未自动保存。";
            MessageBox.Show(OwnerWindow, ex.Message, "AI 翻译失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            EndTranslationRun();
        }
    }

    // ---------------------------------------------------------------------
    // 公共部分
    // ---------------------------------------------------------------------

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

    private void EndTranslationRun()
    {
        _translationCancellation = null;
        _isTranslating = false;
        SetTranslationControlsEnabled(true);
        UpdateDocumentStatus();
        UpdateTranslationButtons();
        UpdateProviderStatus();
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
        ref int skipped) =>
        ApplyResults(outcome.Translations, entriesByIndex, ref applied, ref skipped);

    private void ApplyResults(
        IReadOnlyList<AiTranslationResult> results,
        IReadOnlyDictionary<int, DictionaryEditorEntry> entriesByIndex,
        ref int applied,
        ref int skipped)
    {
        foreach (AiTranslationResult result in results)
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
