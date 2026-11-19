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
    private readonly string _dictionaryDirectory;
    private readonly DictionaryEditorService _service = new();
    private readonly ObservableCollection<DictionaryEditorFile> _files = [];
    private DictionaryEditorDocument? _document;
    private DictionaryEditorFile? _currentFile;
    private ICollectionView? _entriesView;
    private bool _changingFileSelection;

    public DictionaryEditorWindow(string dictionaryDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dictionaryDirectory);
        _dictionaryDirectory = Path.GetFullPath(dictionaryDirectory);

        InitializeComponent();
        DictionaryDirectoryTextBlock.Text = _dictionaryDirectory;
        DictionaryFilesListBox.ItemsSource = _files;
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
        SaveButton.IsEnabled = _document.IsDirty;
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
