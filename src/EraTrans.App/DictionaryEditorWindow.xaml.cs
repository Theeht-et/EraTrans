using EraTrans.Core;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Controls;
using System.Windows.Input;

namespace EraTrans.App;

public partial class DictionaryEditorWindow : Window
{
    private const int TranslationBatchSize = 12;

    private readonly string _dictionaryDirectory;
    private readonly string _gameDirectory;
    private readonly EraTransUserSettings _settings;
    private readonly DictionaryEditorService _service = new();
    private readonly ErbTranslationGroupingService _groupingService = new();
    private readonly AiProviderSettingsStore _providerStore = new();
    private ErbTranslationGroupingIndex? _activeGroupingIndex;
    private string? _groupingFallbackReason;
    private readonly AiTranslationService _translationService = new();
    private readonly Dictionary<string, TreeViewItem> _fileTreeItems = new(StringComparer.OrdinalIgnoreCase);
    private DictionaryEditorDocument? _document;
    private DictionaryEditorFile? _currentFile;
    private ICollectionView? _entriesView;
    private CancellationTokenSource? _translationCancellation;
    private bool _changingFileSelection;
    private bool _isTranslating;

    public DictionaryEditorWindow(
        string dictionaryDirectory,
        string? gameDirectory,
        EraTransUserSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dictionaryDirectory);
        ArgumentNullException.ThrowIfNull(settings);
        _dictionaryDirectory = Path.GetFullPath(dictionaryDirectory);
        _gameDirectory = string.IsNullOrWhiteSpace(gameDirectory)
            ? string.Empty
            : Path.GetFullPath(gameDirectory);
        _settings = settings;

        InitializeComponent();
        DictionaryDirectoryTextBlock.Text = _dictionaryDirectory;
        UpdateProviderStatus();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            IReadOnlyList<DictionaryEditorFile> files = _service.ListDictionaryFiles(_dictionaryDirectory);
            BuildDictionaryTree(files);

            if (files.Count == 0)
            {
                StatusTextBlock.Text = "此目录及其子目录中没有找到 JSON 字典文件。";
                return;
            }

            _fileTreeItems[files[0].FullPath].IsSelected = true;
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = "读取字典目录失败。";
            MessageBox.Show(this, ex.Message, "读取失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DictionaryFilesTreeView_SelectedItemChanged(
        object sender,
        RoutedPropertyChangedEventArgs<object> e)
    {
        if (_changingFileSelection ||
            _isTranslating ||
            e.NewValue is not TreeViewItem selectedNode ||
            selectedNode.Tag is not DictionaryEditorFile selectedFile)
        {
            return;
        }

        if (_document?.IsDirty == true)
        {
            MessageBoxResult choice = MessageBox.Show(
                this,
                "当前字典有未保存的更改。切换文件前是否保存？",
                "存在未保存更改",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);

            if (choice == MessageBoxResult.Cancel)
            {
                RestoreFileSelection();
                return;
            }

            if (choice == MessageBoxResult.Yes && !SaveCurrentDocument())
            {
                RestoreFileSelection();
                return;
            }
        }

        LoadDictionary(selectedFile);
    }

    private static TextBlock CreateTreeHeader(string text, string toolTip)
    {
        return new TextBlock
        {
            Text = text,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = toolTip
        };
    }

    private void BuildDictionaryTree(IReadOnlyList<DictionaryEditorFile> files)
    {
        DictionaryFilesTreeView.Items.Clear();
        _fileTreeItems.Clear();

        var folders = new Dictionary<string, TreeViewItem>(StringComparer.OrdinalIgnoreCase);
        var folderChildren = new Dictionary<string, ItemCollection>(StringComparer.OrdinalIgnoreCase)
        {
            [string.Empty] = DictionaryFilesTreeView.Items
        };

        // Create every folder first. This guarantees folders appear before files at
        // every level, regardless of the relative names in the extracted dictionary.
        foreach (DictionaryEditorFile file in files)
        {
            string relativePath = file.RelativePath
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            string[] segments = relativePath.Split(
                Path.DirectorySeparatorChar,
                StringSplitOptions.RemoveEmptyEntries);

            string currentFolder = string.Empty;
            for (int index = 0; index < segments.Length - 1; index++)
            {
                currentFolder = currentFolder.Length == 0
                    ? segments[index]
                    : Path.Combine(currentFolder, segments[index]);

                if (!folders.TryGetValue(currentFolder, out TreeViewItem? folderNode))
                {
                    folderNode = new TreeViewItem
                    {
                        Header = CreateTreeHeader(segments[index], currentFolder),
                        IsExpanded = false,
                        FontWeight = FontWeights.SemiBold
                    };
                    folderChildren[currentFolder] = folderNode.Items;
                    folderChildren[Path.GetDirectoryName(currentFolder) ?? string.Empty]
                        .Add(folderNode);
                    folders.Add(currentFolder, folderNode);
                }
            }
        }

        // Add files only after the complete folder hierarchy has been created.
        foreach (DictionaryEditorFile file in files)
        {
            string relativePath = file.RelativePath
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            string[] segments = relativePath.Split(
                Path.DirectorySeparatorChar,
                StringSplitOptions.RemoveEmptyEntries);
            string parentFolder = segments.Length > 1
                ? Path.Combine(segments[..^1])
                : string.Empty;
            var fileNode = new TreeViewItem
            {
                Header = CreateTreeHeader(segments[^1], file.RelativePath),
                Tag = file
            };
            folderChildren[parentFolder].Add(fileNode);
            _fileTreeItems[file.FullPath] = fileNode;
        }
    }

    private void LoadDictionary(DictionaryEditorFile file)
    {
        try
        {
            DictionaryEditorDocument document = _service.Load(file.FullPath);
            if (_document is not null)
            {
                _document.PropertyChanged -= Document_PropertyChanged;
            }

            _document = document;
            _document.PropertyChanged += Document_PropertyChanged;
            _currentFile = file;
            _activeGroupingIndex = null;
            _groupingFallbackReason = null;

            string outputRoot = Directory.GetParent(_dictionaryDirectory)?.FullName ?? _dictionaryDirectory;
            string sidecarPath = Path.Combine(
                outputRoot,
                "AI上下文",
                Path.ChangeExtension(file.RelativePath, ".groups.json"));
            bool shouldLoadGrouping =
                file.RelativePath.StartsWith("ERB" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                File.Exists(sidecarPath);

            if (shouldLoadGrouping)
            {
                if (_groupingService.TryLoad(
                        _dictionaryDirectory,
                        file.FullPath,
                        document.Entries.ToArray(),
                        _gameDirectory,
                        out ErbTranslationGroupingIndex? groupingIndex,
                        out string? reason))
                {
                    _activeGroupingIndex = groupingIndex;
                    ApplySemanticGroupDisplay(document.Entries, groupingIndex!);
                }
                else
                {
                    _groupingFallbackReason = reason ??
                        "分组文件不可用，本文件将使用现有翻译方式。";
                }
            }
            SelectedFileTextBlock.Text = file.RelativePath;
            _entriesView = CollectionViewSource.GetDefaultView(document.Entries);
            _entriesView.Filter = FilterEntry;
            EntriesDataGrid.ItemsSource = _entriesView;
            EntrySearchTextBox.Text = string.Empty;
            if (document.Entries.Count > 0)
            {
                EntriesDataGrid.SelectedIndex = 0;
                UpdateContextPreview();
            }
            else
            {
                EntryContextTextBox.Text = "当前字典没有词条。";
                EntryGroupTextBox.Text = "当前字典没有词条。";
                EntryGroupLabelTextBlock.Text = "同组条目";
            }
            StatusTextBlock.Text = "变量类词条只读；状态值：-1 忽略、0 未翻译、1 已翻译。";
            if (_activeGroupingIndex is not null)
            {
                StatusTextBlock.Text += _settings.EnableErbSemanticGroupingTranslation
                    ? " · 已加载 ERB 分组，语义分组翻译已启用"
                    : " · 已加载 ERB 分组（语义分组翻译未启用）";
            }
            else if (!string.IsNullOrWhiteSpace(_groupingFallbackReason))
            {
                StatusTextBlock.Text += " · " + _groupingFallbackReason;
            }
            UpdateDocumentStatus();
            UpdateTranslationButtons();
        }
        catch (Exception ex)
        {
            _document = null;
            _currentFile = null;
            _entriesView = null;
            EntriesDataGrid.ItemsSource = null;
            EntryContextTextBox.Text = string.Empty;
            EntryGroupTextBox.Text = string.Empty;
            EntryGroupLabelTextBlock.Text = "同组条目";
            SelectedFileTextBlock.Text = file.RelativePath;
            SummaryTextBlock.Text = "无法加载此字典";
            SaveButton.IsEnabled = false;
            StatusTextBlock.Text = "文件未被修改。请检查 JSON 格式。";
            MessageBox.Show(this, ex.Message, "字典加载失败", MessageBoxButton.OK, MessageBoxImage.Error);
            RestoreFileSelection();
        }
    }

    private static void ApplySemanticGroupDisplay(
        IReadOnlyList<DictionaryEditorEntry> dictionaryEntries,
        ErbTranslationGroupingIndex index)
    {
        var entriesByKey = dictionaryEntries.ToDictionary(entry => entry.Key, StringComparer.Ordinal);
        foreach (ErbTranslationGroup group in index.Groups)
        {
            string typeLabel = group.Type switch
            {
                "dialogue" => "对白",
                "menu" => "菜单",
                "branch" => "分支",
                "condition" => "条件",
                _ => "语义"
            };
            string label = $"{typeLabel} {group.Id.Replace("group_", "#", StringComparison.Ordinal)}";
            string preview = string.Join(Environment.NewLine,
                group.EntryKeys.Where(entriesByKey.ContainsKey)
                    .Select(key => $"{key}  {entriesByKey[key].Original}"));

            foreach (string key in group.EntryKeys)
                if (entriesByKey.TryGetValue(key, out DictionaryEditorEntry? entry))
                    entry.SetSemanticGroupInfo(label, preview);
        }
    }

    private bool FilterEntry(object item)
    {
        if (item is not DictionaryEditorEntry entry)
        {
            return false;
        }

        string search = EntrySearchTextBox.Text.Trim();
        if (search.Length == 0)
        {
            return true;
        }

        return Contains(entry.Key, search) ||
               Contains(entry.Original, search) ||
               Contains(entry.Translation, search) ||
               Contains(entry.Context, search) ||
               Contains(entry.SemanticGroupEntriesPreview, search);
    }

    private static bool Contains(string source, string search) =>
        source.Contains(search, StringComparison.OrdinalIgnoreCase);

    private void EntrySearchTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _entriesView?.Refresh();
        UpdateContextPreview();
    }

    private void EntriesDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateContextPreview();
    }

    private void EntriesDataGrid_CurrentCellChanged(object? sender, EventArgs e)
    {
        UpdateContextPreview();
    }

    private void UpdateContextPreview()
    {
        // CurrentCell tracks the entry the user is actively viewing. SelectedCells can
        // contain cells from a previous multi-selection, so preferring its first item
        // can leave the preview showing stale or empty context after navigation.
        DictionaryEditorEntry? entry =
            EntriesDataGrid.CurrentCell.Item as DictionaryEditorEntry ??
            EntriesDataGrid.SelectedItem as DictionaryEditorEntry ??
            EntriesDataGrid.SelectedCells
                .Select(cell => cell.Item)
                .OfType<DictionaryEditorEntry>()
                .FirstOrDefault();

        EntryGroupLabelTextBlock.Text = entry is null || string.IsNullOrWhiteSpace(entry.SemanticGroupLabel)
            ? "同组条目"
            : $"同组条目 · {entry.SemanticGroupLabel}";
        EntryGroupTextBox.Text = entry is null || string.IsNullOrWhiteSpace(entry.SemanticGroupEntriesPreview)
            ? "当前词条没有可用的 ERB 语义分组信息。"
            : entry.SemanticGroupEntriesPreview;

        EntryContextTextBox.Text = entry is null
            ? "选择一个词条查看上下文。"
            : string.IsNullOrWhiteSpace(entry.Context)
                ? "此词条在字典文件中没有保存上下文。"
                : entry.Context;
    }

    private void Document_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        UpdateDocumentStatus();
    }

    private void UpdateDocumentStatus()
    {
        if (_document is null)
        {
            SummaryTextBlock.Text = "尚未加载字典";
            SaveButton.IsEnabled = false;
            return;
        }

        SummaryTextBlock.Text = _document.SummaryText +
            (_document.IsDirty ? " · 有未保存更改" : " · 已保存");
        SaveButton.IsEnabled = _document.IsDirty && !_isTranslating;
    }

    private void UpdateTranslationButtons()
    {
        bool hasDocument = _document is not null;
        AiSettingsButton.IsEnabled = !_isTranslating;
        TranslateSelectedButton.IsEnabled = hasDocument && !_isTranslating;
        TranslatePendingButton.IsEnabled = hasDocument && !_isTranslating;
        CancelTranslationButton.IsEnabled = _isTranslating;
    }

    private void UpdateProviderStatus()
    {
        try
        {
            AiProviderProfile? profile = _providerStore.Load().ActiveProvider;
            AiProviderStatusTextBlock.Text = profile is null
                ? "AI 翻译未配置"
                : $"当前服务：{profile.Name} / {profile.Model}";
        }
        catch
        {
            AiProviderStatusTextBlock.Text = "AI 配置不可读取";
        }
    }

    private bool SaveCurrentDocument()
    {
        if (_document is null)
        {
            return true;
        }

        try
        {
            EntriesDataGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Cell, true);
            EntriesDataGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);
            _document.Save();
            StatusTextBlock.Text = "保存成功。原始字段和其他 JSON 字段均已保留。";
            UpdateDocumentStatus();
            return true;
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = "保存失败；原字典文件未被本次操作覆盖。";
            MessageBox.Show(this, ex.Message, "字典保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrentDocument();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void AiSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new AiProviderSettingsWindow { Owner = this };
        if (window.ShowDialog() == true)
        {
            UpdateProviderStatus();
            StatusTextBlock.Text = "AI 服务商配置已保存。";
        }
    }

    private void TranslateSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        EntriesDataGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Cell, true);
        EntriesDataGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);

        DictionaryEditorEntry[] selected = EntriesDataGrid.SelectedCells
            .Select(cell => cell.Item)
            .OfType<DictionaryEditorEntry>()
            .Concat(EntriesDataGrid.SelectedItems.OfType<DictionaryEditorEntry>())
            .Distinct()
            .Where(IsPendingTranslation)
            .ToArray();

        if (selected.Length == 0)
        {
            MessageBox.Show(
                this,
                "请先选中至少一个尚未填写译文、状态为未翻译的非变量词条。",
                "没有可翻译的选中项",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        _ = TranslateEntriesAsync(selected);
    }

    private void TranslatePendingButton_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null)
            return;

        EntriesDataGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Cell, true);
        EntriesDataGrid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);

        DictionaryEditorEntry[] pending = _document.Entries
            .Where(IsPendingTranslation)
            .ToArray();

        if (pending.Length == 0)
        {
            MessageBox.Show(
                this,
                "当前字典没有可批量翻译的词条。已有译文、变量和已忽略词条不会被覆盖。",
                "没有待翻译项",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        MessageBoxResult confirm = MessageBox.Show(
            this,
            $"将为当前字典中的 {pending.Length} 个未翻译词条请求 AI 翻译。译文只回填编辑器，不会自动保存。是否继续？",
            "确认批量翻译",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm == MessageBoxResult.Yes)
        {
            _ = TranslateEntriesAsync(pending);
        }
    }

    private static bool IsPendingTranslation(DictionaryEditorEntry entry) =>
        entry.IsEditable &&
        entry.Stage == 0 &&
        string.IsNullOrWhiteSpace(entry.Translation) &&
        !string.IsNullOrWhiteSpace(entry.Original);

    private async Task TranslateEntriesAsync(IReadOnlyList<DictionaryEditorEntry> entries)
    {
        AiProviderProfile? profile;
        try
        {
            profile = _providerStore.Load().ActiveProvider;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "读取 AI 配置失败", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (profile is null)
        {
            MessageBoxResult configure = MessageBox.Show(
                this,
                "尚未配置 AI 服务商。是否现在打开设置？",
                "AI 服务未配置",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);
            if (configure == MessageBoxResult.Yes)
                AiSettingsButton_Click(this, new RoutedEventArgs());
            return;
        }

        if (_document is null)
            return;

        if (_settings.EnableErbSemanticGroupingTranslation &&
            _activeGroupingIndex is null &&
            !string.IsNullOrWhiteSpace(_groupingFallbackReason))
        {
            MessageBox.Show(
                this,
                _groupingFallbackReason + "\n\n本次将使用现有的 AI 翻译方式，不会使用未经验证的分组关系。",
                "ERB 语义分组不可用",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

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
            IReadOnlyList<ErbTranslationBatch> batches;
            bool useSemanticGrouping = _settings.EnableErbSemanticGroupingTranslation &&
                                       _activeGroupingIndex is not null &&
                                       _document is not null;
            if (useSemanticGrouping)
            {
                try
                {
                    batches = _groupingService.BuildTranslationBatches(
                        entries,
                        _document!.Entries.ToArray(),
                        _activeGroupingIndex!);
                }
                catch (Exception ex) when (ex is InvalidDataException or ArgumentException or KeyNotFoundException)
                {
                    useSemanticGrouping = false;
                    _groupingFallbackReason =
                        $"当前待翻译词条无法完整匹配分组文件：{ex.Message}";
                    batches = CreateLegacyTranslationBatches(entries);
                    MessageBox.Show(
                        this,
                        _groupingFallbackReason + "\n\n本次将使用现有的 AI 翻译方式继续。",
                        "ERB 语义分组回退",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
            }
            else
            {
                batches = CreateLegacyTranslationBatches(entries);
            }

            int batchNumber = 0;
            foreach (ErbTranslationBatch workBatch in batches)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                batchNumber++;

                DictionaryEditorEntry[] batch = workBatch.Targets.ToArray();
                AiTranslationRequest[] requests = batch
                    .Select(entry => new AiTranslationRequest(
                        entry.Index,
                        entry.Key,
                        entry.Original,
                        entry.Context))
                    .ToArray();

                StatusTextBlock.Text =
                    $"正在请求 AI 翻译：{completed}/{total}（第 {batchNumber}/{batches.Count} 批，本批 {batch.Length} 项、{workBatch.SemanticContextGroups.Count} 组上下文，{(useSemanticGrouping ? "语义分组" : "旧方式")}）……";

                IReadOnlyList<AiTranslationResult> results =
                    useSemanticGrouping
                        ? await _translationService.TranslateWithSemanticContextAsync(
                            profile,
                            requests,
                            workBatch.SemanticContextGroups,
                            cancellation.Token)
                        : await _translationService.TranslateAsync(
                            profile,
                            requests,
                            cancellation.Token);

                var entriesByIndex = batch.ToDictionary(entry => entry.Index);
                var requestsById = requests.ToDictionary(request => request.Id);
                foreach (AiTranslationResult result in results)
                {
                    DictionaryEditorEntry entry = entriesByIndex[result.Id];
                    AiTranslationRequest request = requestsById[result.Id];
                    if (entry.TryApplyAiTranslation(request.Key, request.Original, result.Translation))
                        applied++;
                    else
                        skipped++;
                }

                completed += batch.Length;
                EntriesDataGrid.Items.Refresh();
                UpdateDocumentStatus();
                StatusTextBlock.Text = $"AI 翻译进度：{completed}/{total}，已回填 {applied} 项。";
            }

            StatusTextBlock.Text =
                $"AI 翻译完成：回填 {applied} 项，因词条状态变化或已有译文跳过 {skipped} 项。请检查译文并手动保存。";
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = $"AI 翻译已取消：已回填 {applied} 项，尚未自动保存。";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"AI 翻译中断：已回填 {applied} 项，尚未自动保存。";
            MessageBox.Show(this, ex.Message, "AI 翻译失败", MessageBoxButton.OK, MessageBoxImage.Error);
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

    private static IReadOnlyList<ErbTranslationBatch> CreateLegacyTranslationBatches(
        IReadOnlyList<DictionaryEditorEntry> entries) =>
        entries
            .Select((entry, index) => (entry, index))
            .GroupBy(item => item.index / TranslationBatchSize)
            .Select(group => new ErbTranslationBatch(
                group.Select(item => item.entry).ToArray(),
                Array.Empty<AiTranslationSemanticContextGroup>()))
            .ToArray();

    private void SetTranslationControlsEnabled(bool enabled)
    {
        DictionaryFilesTreeView.IsEnabled = enabled;
        EntriesDataGrid.IsEnabled = enabled;
        AiSettingsButton.IsEnabled = enabled;
        TranslateSelectedButton.IsEnabled = enabled && _document is not null;
        TranslatePendingButton.IsEnabled = enabled && _document is not null;
        CancelTranslationButton.IsEnabled = !enabled;
    }

    private void CancelTranslationButton_Click(object sender, RoutedEventArgs e)
    {
        _translationCancellation?.Cancel();
        StatusTextBlock.Text = "正在取消当前 AI 请求……";
    }

    private void RestoreFileSelection()
    {
        _changingFileSelection = true;
        if (_currentFile is not null &&
            _fileTreeItems.TryGetValue(_currentFile.FullPath, out TreeViewItem? fileNode))
        {
            fileNode.IsSelected = true;
        }
        _changingFileSelection = false;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SaveCurrentDocument();
            e.Handled = true;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_isTranslating)
        {
            MessageBox.Show(
                this,
                "AI 翻译仍在进行。请先取消并等待当前请求结束，再关闭窗口。",
                "翻译任务进行中",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            e.Cancel = true;
            return;
        }

        if (_document?.IsDirty == true)
        {
            MessageBoxResult choice = MessageBox.Show(
                this,
                "当前字典有未保存的更改。关闭前是否保存？",
                "存在未保存更改",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);

            if (choice == MessageBoxResult.Cancel ||
                (choice == MessageBoxResult.Yes && !SaveCurrentDocument()))
            {
                e.Cancel = true;
                return;
            }
        }

        base.OnClosing(e);
    }
}