using System.ComponentModel;
using System.IO;
using System.Windows;
using EraTrans.Core;

namespace EraTrans.App;

public partial class EraAnalyzeReportWindow : Window
{
    private readonly string _gameCopyDirectory;
    private readonly EraAnalyzerService _analyzerService = new();
    private CancellationTokenSource? _cts;
    private bool _isRunning;

    public EraAnalyzeReportWindow(string gameCopyDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameCopyDirectory);
        _gameCopyDirectory = gameCopyDirectory;
        InitializeComponent();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _ = RunAnalyzeAsync();
    }

    private void RunAnalyzeButton_Click(object sender, RoutedEventArgs e)
    {
        _ = RunAnalyzeAsync();
    }

    private async Task RunAnalyzeAsync()
    {
        if (_isRunning)
        {
            return;
        }

        if (!Directory.Exists(_gameCopyDirectory))
        {
            SummaryTextBlock.Text = "未找到汉化副本目录，请先点击「生成汉化副本」。";
            return;
        }

        using var cts = new CancellationTokenSource();
        _cts = cts;
        SetRunning(true, "正在调用内置无头引擎分析汉化副本，请稍候…");
        DiagnosticsDataGrid.ItemsSource = null;

        try
        {
            EraAnalyzeReport report = await _analyzerService.AnalyzeAsync(_gameCopyDirectory, cts.Token);

            DiagnosticsDataGrid.ItemsSource = report.Diagnostics;
            string statusTag = report.Diagnostics.Count == 0 && report.Success
                ? "通过（0 报错 / 0 警告）"
                : $"共发现 {report.Diagnostics.Count} 条诊断";
            SummaryTextBlock.Text =
                $"体检完成：{statusTag} · 扫描 {report.FileCount:N0} 个文件 · 耗时 {report.ElapsedMilliseconds:N0} ms";
        }
        catch (OperationCanceledException)
        {
            SummaryTextBlock.Text = "已取消体检。";
        }
        catch (Exception ex)
        {
            SummaryTextBlock.Text = "体检执行失败。";
            MessageBox.Show(this, ex.Message, "体检失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _cts = null;
            SetRunning(false);
        }
    }

    private void SetRunning(bool running, string? summary = null)
    {
        _isRunning = running;
        RunAnalyzeButton.IsEnabled = !running;
        AnalyzeProgressBar.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        if (!string.IsNullOrWhiteSpace(summary))
        {
            SummaryTextBlock.Text = summary;
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_isRunning)
        {
            _cts?.Cancel();
        }
    }
}
