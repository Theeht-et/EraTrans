using EraTrans.Core;
using System.IO;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;

namespace EraTrans.App;

public partial class AiTranslationLogWindow : Window
{
    private readonly AiTranslationLogStore _store;
    private readonly ObservableCollection<AiTranslationLogEntry> _logs = [];

    public AiTranslationLogWindow(AiTranslationLogStore? store = null)
    {
        InitializeComponent();
        _store = store ?? new AiTranslationLogStore();
        LogsListBox.ItemsSource = _logs;
        RefreshLogs();
    }

    private void RefreshLogs()
    {
        try
        {
            _logs.Clear();
            foreach (AiTranslationLogEntry entry in _store.LoadAll())
                _logs.Add(entry);

            if (_logs.Count > 0)
                LogsListBox.SelectedIndex = 0;
            else
                ShowEmptySelection();
        }
        catch (Exception ex)
        {
            ShowEmptySelection();
            MessageBox.Show(
                this,
                "读取 AI 翻译记录失败：" + ex.Message,
                "读取记录失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void LogsListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (LogsListBox.SelectedItem is not AiTranslationLogEntry entry)
        {
            ShowEmptySelection();
            return;
        }

        SelectedLogSummaryTextBlock.Text =
            $"{entry.DisplayTime} · {entry.RequestKindDescription} · {entry.StatusDescription} · {entry.Model} · {entry.Summary}";
        EndpointTextBlock.Text =
            $"{entry.RequestMethod} {entry.Endpoint} · 服务商：{entry.ProviderName}";

        RequestTextBox.Text = entry.RequestJson;
        ResponseTextBox.Text = string.IsNullOrWhiteSpace(entry.RawResponse)
            ? "没有收到可显示的文本回复。"
            : entry.RawResponse;

        var errorLines = new List<string>
        {
            $"状态：{entry.StatusDescription}",
            $"开始时间（UTC）：{entry.StartedAtUtc:O}",
            $"完成时间（UTC）：{entry.CompletedAtUtc?.ToString("O") ?? "未完成"}",
            $"耗时：{(entry.DurationMilliseconds is long duration ? $"{duration:N0} ms" : "未知")}",
            $"请求类型：{entry.RequestKindDescription}",
            $"目标词条数：{entry.RequestedEntryCount}",
            $"异常类型：{entry.ErrorType ?? "无"}",
            $"异常信息：{entry.ErrorMessage ?? "无"}"
        };

        if (!string.IsNullOrWhiteSpace(entry.ErrorDetails))
        {
            errorLines.Add("");
            errorLines.Add("异常详细信息：");
            errorLines.Add(entry.ErrorDetails);
        }

        if (entry.Status == AiTranslationLogStatus.InProgress)
        {
            errorLines.Add("");
            errorLines.Add("此记录尚未收到完成状态。可能是请求仍在执行，或程序在请求期间异常退出。");
        }

        ErrorTextBox.Text = string.Join(Environment.NewLine, errorLines);
        CopyRequestButton.IsEnabled = !string.IsNullOrWhiteSpace(entry.RequestJson);
        CopyResponseButton.IsEnabled = !string.IsNullOrWhiteSpace(entry.RawResponse);
    }

    private void ShowEmptySelection()
    {
        SelectedLogSummaryTextBlock.Text = _logs.Count == 0
            ? "暂无 AI 请求记录。"
            : "选择一条记录查看详情。";
        EndpointTextBlock.Text = "";
        RequestTextBox.Text = "";
        ResponseTextBox.Text = "";
        ErrorTextBox.Text = "";
        CopyRequestButton.IsEnabled = false;
        CopyResponseButton.IsEnabled = false;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshLogs();

    private void OpenDirectory_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_store.DirectoryPath);
            Process.Start(new ProcessStartInfo
            {
                FileName = _store.DirectoryPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                "无法打开记录目录：" + ex.Message,
                "打开目录失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_logs.Count == 0)
            return;

        MessageBoxResult result = MessageBox.Show(
            this,
            $"确定删除全部 {_logs.Count} 条 AI 请求记录吗？此操作不可撤销。",
            "清空 AI 翻译记录",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
            return;

        try
        {
            _store.DeleteAll();
            RefreshLogs();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                "清空 AI 翻译记录失败：" + ex.Message,
                "删除记录失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void CopyRequest_Click(object sender, RoutedEventArgs e) => CopyText(RequestTextBox.Text);

    private void CopyResponse_Click(object sender, RoutedEventArgs e) => CopyText(ResponseTextBox.Text);

    private void CopyText(string value)
    {
        try
        {
            Clipboard.SetText(value ?? "");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                "复制失败：" + ex.Message,
                "复制记录",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
