using EraTrans.Core;
using System.Windows;

namespace EraTrans.App;

/// <summary>
/// Translates one ERB file as a sequence of windows: consecutive functions in
/// source order, never cutting a function in half. The model therefore reads a
/// whole passage before translating it line by line, and a window that comes back
/// incomplete only asks for the lines that are missing.
/// </summary>
public partial class DictionaryEditorControl
{
    private readonly SequentialTranslationPlanner _windowPlanner = new();
    private int _windowMaxCharacters = SequentialTranslationPlanner.DefaultMaxCharacters;
    private int _windowMaxLines = SequentialTranslationPlanner.DefaultMaxLines;

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

    private async Task TranslateWindowsAsync(IReadOnlyList<DictionaryEditorEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0 || _document is null)
            return;

        AiProviderProfile? profile;
        try
        {
            profile = _providerStore.Load().ActiveProvider;
        }
        catch (Exception ex)
        {
            MessageBox.Show(OwnerWindow, ex.Message, "读取 AI 配置失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (profile is null)
        {
            MessageBoxResult configure = MessageBox.Show(
                OwnerWindow,
                "尚未配置 AI 服务商。是否现在打开设置？",
                "AI 服务未配置",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);
            if (configure == MessageBoxResult.Yes)
                AiSettingsButton_Click(this, new RoutedEventArgs());
            return;
        }

        if (_activeGroupingIndex is null)
        {
            MessageBox.Show(
                OwnerWindow,
                (_groupingFallbackReason ?? "未找到分组文件。") +
                "\n\n分组文件用于按原文顺序整段翻译；缺少它时无法保证句子不被切断。请重新提取当前字典。",
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
        int totalLines = plan.TargetLineCount;

        StatusTextBlock.Text =
            $"准备按顺序翻译：{totalWindows} 个窗口，{totalLines} 行；" +
            $"其中被切开的函数 {plan.SplitFunctionCount} 个。";

        try
        {
            foreach (TranslationWindow window in plan.Windows)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                windowNumber++;

                StatusTextBlock.Text =
                    $"正在按顺序翻译：窗口 {windowNumber}/{totalWindows}" +
                    $"（{window.FunctionId}，本窗 {window.TargetCount} 行）……";

                WindowTranslationOutcome outcome = await _translationService.TranslateWindowAsync(
                    profile, window, reasoning: null, cancellation.Token);
                ApplyWindowOutcome(outcome, entriesByIndex, ref applied, ref skipped);

                if (outcome.MissingIds.Count > 0)
                {
                    // Only the unanswered lines are sent again, so a partially
                    // broken answer never wastes the whole window.
                    TranslationWindow retry = RetryWindowForIds(window, outcome.MissingIds);
                    WindowTranslationOutcome retryOutcome = await _translationService.TranslateWindowAsync(
                        profile, retry, reasoning: null, cancellation.Token);
                    ApplyWindowOutcome(retryOutcome, entriesByIndex, ref applied, ref skipped);

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
                $"按顺序翻译完成：{totalWindows} 个窗口，回填 {applied} 行，跳过 {skipped} 行。" +
                "请检查译文并手动保存。";
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = $"按顺序翻译已取消：已回填 {applied} 行，尚未自动保存。";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"按顺序翻译中断：已回填 {applied} 行，尚未自动保存。";
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

    private void ApplyWindowOutcome(
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
