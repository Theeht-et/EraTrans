using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using EraTrans.Core;

namespace EraTrans.App;

public partial class VariableGlossaryWindow : Window
{
    private const int GlossaryAiBatchSize = 30;

    private sealed class EditableGlossaryRow : INotifyPropertyChanged
    {
        private string _translation;

        public EditableGlossaryRow(string table, string original, string translation)
        {
            Table = table;
            Original = original;
            _translation = translation;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Table { get; }
        public string Original { get; }

        public string Translation
        {
            get => _translation;
            set
            {
                string next = value ?? string.Empty;
                if (string.Equals(_translation, next, StringComparison.Ordinal))
                {
                    return;
                }

                _translation = next;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Translation)));
            }
        }
    }

    private readonly EraTransOutputLayout _layout;
    private readonly VariableGlossaryService _glossaryService;
    private readonly AiTranslationService _translationService;
    private readonly AiProviderSettingsStore _providerStore;
    private readonly EraTransUserSettings _settings;
    private readonly ObservableCollection<EditableGlossaryRow> _rows = new();
    private readonly ICollectionView _rowsView;
    private CancellationTokenSource? _translationCts;
    private bool _isBusy;
    private bool _isDirty;

    public VariableGlossaryWindow(
        EraTransOutputLayout layout,
        VariableGlossaryService glossaryService,
        AiTranslationService translationService,
        AiProviderSettingsStore providerStore,
        EraTransUserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(glossaryService);
        ArgumentNullException.ThrowIfNull(translationService);
        ArgumentNullException.ThrowIfNull(providerStore);
        ArgumentNullException.ThrowIfNull(settings);

        _layout = layout;
        _glossaryService = glossaryService;
        _translationService = translationService;
        _providerStore = providerStore;
        _settings = settings;

        InitializeComponent();

        _rowsView = CollectionViewSource.GetDefaultView(_rows);
        _rowsView.Filter = FilterRow;
        GlossaryDataGrid.ItemsSource = _rowsView;

        StatusFilterComboBox.SelectedIndex = 0;

        LoadOrSyncInitialGlossary();
    }

    private void LoadOrSyncInitialGlossary()
    {
        try
        {
            VariableGlossaryDocument document = System.IO.File.Exists(VariableGlossaryService.GetGlossaryPath(_layout))
                ? _glossaryService.Load(_layout)
                : _glossaryService.SyncFromGameDirectory(_layout, _settings.ToParserOptions().FileEncoding);

            PopulateRows(document.Entries, markDirty: false);
            StatusTextBlock.Text = _rows.Count == 0
                ? "未在原游戏 CSV 中发现含假名的可见变量名。"
                : "已加载词表。";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = "读取词表失败。";
            MessageBox.Show(this, ex.Message, "加载失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void PopulateRows(IReadOnlyList<VariableGlossaryEntry> entries, bool markDirty)
    {
        foreach (EditableGlossaryRow existing in _rows)
        {
            existing.PropertyChanged -= Row_PropertyChanged;
        }

        _rows.Clear();
        foreach (VariableGlossaryEntry entry in entries)
        {
            var row = new EditableGlossaryRow(entry.Table, entry.Original, entry.Translation);
            row.PropertyChanged += Row_PropertyChanged;
            _rows.Add(row);
        }

        RebuildTableFilterItems();
        _isDirty = markDirty;
        UpdateStats();
        _rowsView.Refresh();
    }

    private void RebuildTableFilterItems()
    {
        string? previous = TableFilterComboBox.SelectedItem as string;
        var tables = new List<string> { "全部表" };
        tables.AddRange(_rows.Select(r => r.Table).Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal));

        TableFilterComboBox.ItemsSource = tables;
        int idx = previous is not null ? tables.IndexOf(previous) : 0;
        TableFilterComboBox.SelectedIndex = idx >= 0 ? idx : 0;
    }

    private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditableGlossaryRow.Translation))
        {
            _isDirty = true;
            UpdateStats();
        }
    }

    private bool FilterRow(object item)
    {
        if (item is not EditableGlossaryRow row)
        {
            return false;
        }

        string? selectedTable = TableFilterComboBox.SelectedItem as string;
        if (!string.IsNullOrWhiteSpace(selectedTable) &&
            !string.Equals(selectedTable, "全部表", StringComparison.Ordinal) &&
            !string.Equals(row.Table, selectedTable, StringComparison.Ordinal))
        {
            return false;
        }

        string statusTag = (StatusFilterComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
        bool hasTranslation = !string.IsNullOrWhiteSpace(row.Translation);
        if (statusTag == "pending" && hasTranslation)
        {
            return false;
        }

        if (statusTag == "translated" && !hasTranslation)
        {
            return false;
        }

        string keyword = SearchTextBox.Text.Trim();
        if (keyword.Length == 0)
        {
            return true;
        }

        return row.Original.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
               row.Translation.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
               row.Table.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateStats()
    {
        int total = _rows.Count;
        int translated = _rows.Count(r => !string.IsNullOrWhiteSpace(r.Translation));
        string dirtyMark = _isDirty ? " · 未保存" : string.Empty;
        StatsTextBlock.Text = $"共 {total:N0} 条 · 已译 {translated:N0}{dirtyMark}";
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchTextBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        _rowsView.Refresh();
    }

    private void FilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_rowsView is not null)
        {
            _rowsView.Refresh();
        }
    }

    private bool SaveCurrentGlossary()
    {
        GlossaryDataGrid.CommitEdit(DataGridEditingUnit.Cell, exitEditingMode: true);
        GlossaryDataGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);

        try
        {
            VariableGlossaryEntry[] entries = _rows
                .Select(r => new VariableGlossaryEntry(r.Table, r.Original, r.Translation.Trim()))
                .ToArray();
            _glossaryService.Save(_layout, new VariableGlossaryDocument(VariableGlossaryService.CurrentVersion, entries));
            _isDirty = false;
            UpdateStats();
            StatusTextBlock.Text = "词表已保存。";
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "保存词表失败", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrentGlossary();
    }

    private async void RescanCsvButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        if (_isDirty && !SaveCurrentGlossary())
        {
            return;
        }

        SetBusy(true, "正在扫描原游戏 CSV…");
        try
        {
            VariableGlossaryDocument synced = await Task.Run(() =>
                _glossaryService.SyncFromGameDirectory(_layout, _settings.ToParserOptions().FileEncoding));
            PopulateRows(synced.Entries, markDirty: false);
            StatusTextBlock.Text = $"扫描完成：共 {synced.Entries.Count:N0} 条含假名可见变量名。";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = "扫描 CSV 失败。";
            MessageBox.Show(this, ex.Message, "扫描失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void TranslatePendingButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

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
            MessageBox.Show(this, "尚未配置 AI 服务商，请先在主界面右上角设置中配置 AI 服务。", "AI 服务未配置",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        EditableGlossaryRow[] pending = _rows
            .Where(r => string.IsNullOrWhiteSpace(r.Translation))
            .ToArray();
        if (pending.Length == 0)
        {
            MessageBox.Show(this, "当前所有词条均已有译名，无需批量翻译。", "没有未翻译项",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        MessageBoxResult confirm = MessageBox.Show(
            this,
            $"将使用 {profile.Name} / {profile.Model} 批量翻译 {pending.Length} 条未翻译变量名。是否继续？",
            "确认批量翻译",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        using var cts = new CancellationTokenSource();
        _translationCts = cts;
        SetBusy(true, $"准备翻译 {pending.Length} 条术语…", showCancel: true);

        int applied = 0;
        int totalBatches = (pending.Length + GlossaryAiBatchSize - 1) / GlossaryAiBatchSize;

        try
        {
            for (int batchIndex = 0; batchIndex < totalBatches; batchIndex++)
            {
                cts.Token.ThrowIfCancellationRequested();

                EditableGlossaryRow[] batchRows = pending
                    .Skip(batchIndex * GlossaryAiBatchSize)
                    .Take(GlossaryAiBatchSize)
                    .ToArray();
                VariableGlossaryEntry[] batchEntries = batchRows
                    .Select(r => new VariableGlossaryEntry(r.Table, r.Original, r.Translation))
                    .ToArray();

                StatusTextBlock.Text = $"正在翻译第 {batchIndex + 1}/{totalBatches} 批（已回填 {applied} 条）…";

                IReadOnlyDictionary<int, string> result = await _translationService.TranslateGlossaryEntriesAsync(
                    profile,
                    batchEntries,
                    reasoning: null,
                    cts.Token);

                foreach ((int idx, string translated) in result)
                {
                    if (idx >= 0 && idx < batchRows.Length && !string.IsNullOrWhiteSpace(translated))
                    {
                        batchRows[idx].Translation = translated.Trim();
                        applied++;
                    }
                }
            }

            StatusTextBlock.Text = $"AI 翻译完成：回填 {applied} 条，请检查后点击「保存词表」。";
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = $"已取消翻译：已回填 {applied} 条（尚未保存）。";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"翻译中断：已回填 {applied} 条。";
            MessageBox.Show(this, ex.Message, "AI 翻译失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _translationCts = null;
            SetBusy(false);
            UpdateStats();
            _rowsView.Refresh();
        }
    }

    private void CancelTranslationButton_Click(object sender, RoutedEventArgs e)
    {
        _translationCts?.Cancel();
        StatusTextBlock.Text = "正在取消当前请求…";
    }

    private void SetBusy(bool busy, string? status = null, bool showCancel = false)
    {
        _isBusy = busy;
        RescanCsvButton.IsEnabled = !busy;
        TranslatePendingButton.IsEnabled = !busy;
        SaveButton.IsEnabled = !busy;
        GlossaryDataGrid.IsReadOnly = busy;
        CancelTranslationButton.Visibility = busy && showCancel ? Visibility.Visible : Visibility.Collapsed;
        CancelTranslationButton.IsEnabled = busy && showCancel;
        if (!string.IsNullOrWhiteSpace(status))
        {
            StatusTextBlock.Text = status;
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_isBusy)
        {
            MessageBox.Show(this, "当前操作仍在进行，请等待完成或先取消后再关闭。", "操作进行中",
                MessageBoxButton.OK, MessageBoxImage.Information);
            e.Cancel = true;
            return;
        }

        if (!_isDirty)
        {
            return;
        }

        MessageBoxResult choice = MessageBox.Show(
            this,
            "变量与术语表有未保存的更改，关闭前是否保存？",
            "存在未保存更改",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning);

        if (choice == MessageBoxResult.Cancel)
        {
            e.Cancel = true;
        }
        else if (choice == MessageBoxResult.Yes && !SaveCurrentGlossary())
        {
            e.Cancel = true;
        }
    }
}
