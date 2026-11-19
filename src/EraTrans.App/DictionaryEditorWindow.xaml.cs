using EraTrans.Core;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;

namespace EraTrans.App;

public partial class DictionaryEditorWindow : Window
{
    private const int TranslationBatchSize = 12;

    private readonly string _dictionaryDirectory;
    private readonly DictionaryEditorService _service = new();
    private readonly AiProviderSettingsStore _providerStore = new();
    private readonly AiTranslationService _translationService = new();
    private readonly ObservableCollection<DictionaryEditorFile> _files = [];
    private DictionaryEditorDocument? _document;
    private DictionaryEditorFile? _currentFile;
    private ICollectionView? _entriesView;
    private CancellationTokenSource? _translationCancellation;
    private bool _changingFileSelection;
    private bool _isTranslating;

    public DictionaryEditorWindow(string dictionaryDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dictionaryDirectory);
        _dictionaryDirectory = Path.GetFullPath(dictionaryDirectory);

        InitializeComponent();
        DictionaryDirectoryTextBlock.Text = _dictionaryDirectory;
        DictionaryFilesListBox.ItemsSource = _files;
        UpdateProviderStatus();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            foreach (DictionaryEditorFile file in _service.ListDictionaryFiles(_dictionaryDirectory))
            {
                _files.Add(file);
            }

            if (_files.Count == 0)
            {
                StatusTextBlock.Text = "此目录及其子目录中没有找到 JSON 字典文件。";
                return;
            }

            DictionaryFilesListBox.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = "读取字典目录失败。";
            MessageBox.Show(this, ex.Message, "读取失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DictionaryFilesListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_changingFileSelection ||
            _isTranslating ||
            DictionaryFilesListBox.SelectedItem is not DictionaryEditorFile selectedFile)
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
            SelectedFileTextBlock.Text = file.RelativePath;
            _entriesView = CollectionViewSource.GetDefaultView(document.Entries);
            _entriesView.Filter = FilterEntry;
            EntriesDataGrid.ItemsSource = _entriesView;
            EntrySearchTextBox.Text = string.Empty;
            StatusTextBlock.Text = "变量类词条只读；状态值：-1 忽略、0 未翻译、1 已翻译。";
            UpdateDocumentStatus();
            UpdateTranslationButtons();
        }
        catch (Exception ex)
        {
            _document = null;
            _currentFile = null;
            _entriesView = null;
            EntriesDataGrid.ItemsSource = null;
            SelectedFileTextBlock.Text = file.RelativePath;
            SummaryTextBlock.Text = "无法加载此字典";
            SaveButton.IsEnabled = false;
            StatusTextBlock.Text = "文件未被修改。请检查 JSON 格式。";
            MessageBox.Show(this, ex.Message, "字典加载失败", MessageBoxButton.OK, MessageBoxImage.Error);
            RestoreFileSelection();
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
               Contains(entry.Context, search);
    }

    private static bool Contains(string source, string search) =>
        source.Contains(search, StringComparison.OrdinalIgnoreCase);

    private void EntrySearchTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _entriesView?.Refresh();
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
            for (int offset = 0; offset < total; offset += TranslationBatchSize)
            {
                cancellation.Token.ThrowIfCancellationRequested();

                DictionaryEditorEntry[] batch = entries
                    .Skip(offset)
                    .Take(TranslationBatchSize)
                    .ToArray();
                AiTranslationRequest[] requests = batch
                    .Select(entry => new AiTranslationRequest(
                        entry.Index,
                        entry.Key,
                        entry.Original,
                        entry.Context))
                    .ToArray();

                StatusTextBlock.Text =
                    $"正在请求 AI 翻译：{completed}/{total}（本批 {batch.Length} 项）……";

                IReadOnlyList<AiTranslationResult> results =
                    await _translationService.TranslateAsync(
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

    private void SetTranslationControlsEnabled(bool enabled)
    {
        DictionaryFilesListBox.IsEnabled = enabled;
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
        DictionaryFilesListBox.SelectedItem = _currentFile;
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