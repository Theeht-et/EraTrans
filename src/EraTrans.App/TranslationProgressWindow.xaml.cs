using EraTrans.Core;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace EraTrans.App;

public partial class TranslationProgressWindow : Window
{
    private readonly ICollectionView _rowsView;

    public TranslationProgressWindow(string dictionaryDirectory, DictionaryWorkspaceProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        InitializeComponent();

        WorkspacePathTextBlock.Text = dictionaryDirectory;
        OverallProgressBar.Value = progress.CompletionPercent;
        OverallPercentTextBlock.Text = progress.TotalActive == 0
            ? "0.0%"
            : $"{progress.CompletionPercent:0.0}%";
        EntrySummaryTextBlock.Text =
            $"词条总进度：已翻译 {progress.TotalTranslated:N0} / 待翻总量 {progress.TotalActive:N0}（剩余待翻 {progress.TotalPending:N0} · 已忽略 {progress.TotalIgnored:N0}）";
        FileSummaryTextBlock.Text =
            $"文件分布：共 {progress.TotalFiles:N0} 个 · 已完成 {progress.CompletedFiles:N0} · 翻译中 {progress.InProgressFiles:N0} · 未翻译 {progress.NotStartedFiles:N0}" +
            (progress.EmptyFiles > 0 ? $" · 无需翻译 {progress.EmptyFiles:N0}" : string.Empty);

        _rowsView = CollectionViewSource.GetDefaultView(progress.Files);
        _rowsView.Filter = FilterRow;
        FilesDataGrid.ItemsSource = _rowsView;
        StatusFilterComboBox.SelectedIndex = 0;
    }

    public string? SelectedFileRelativePath { get; private set; }

    private string SelectedStatusTag =>
        (StatusFilterComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";

    private bool FilterRow(object item)
    {
        if (item is not DictionaryFileProgress row)
        {
            return false;
        }

        switch (SelectedStatusTag)
        {
            case "incomplete":
                if (row.Status is not (DictionaryProgressStatus.NotStarted or DictionaryProgressStatus.InProgress))
                    return false;
                break;
            case "not_started":
                if (row.Status != DictionaryProgressStatus.NotStarted)
                    return false;
                break;
            case "in_progress":
                if (row.Status != DictionaryProgressStatus.InProgress)
                    return false;
                break;
            case "completed":
                if (row.Status != DictionaryProgressStatus.Completed)
                    return false;
                break;
            case "empty":
                if (row.Status != DictionaryProgressStatus.Empty)
                    return false;
                break;
        }

        string keyword = SearchTextBox.Text.Trim();
        return keyword.Length == 0 ||
               row.RelativePath.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SearchPlaceholder is not null)
        {
            SearchPlaceholder.Visibility = SearchTextBox.Text.Length == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        _rowsView?.Refresh();
    }

    private void StatusFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _rowsView?.Refresh();
    }

    private void FilesDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OpenSelectedButton is not null)
        {
            OpenSelectedButton.IsEnabled = FilesDataGrid.SelectedItem is DictionaryFileProgress;
        }
    }

    private void FilesDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        TryConfirmSelectedFile();
    }

    private void OpenSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        TryConfirmSelectedFile();
    }

    private void TryConfirmSelectedFile()
    {
        if (FilesDataGrid.SelectedItem is DictionaryFileProgress selected)
        {
            SelectedFileRelativePath = selected.RelativePath;
            DialogResult = true;
        }
    }
}
