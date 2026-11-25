using System.ComponentModel;
using System.IO;
using System.Windows;
using EraTrans.Core;
using Microsoft.Win32;

namespace EraTrans.App;

public partial class EraAnalyzeReportWindow : Window
{
    private readonly string _gameCopyDirectory;
    private readonly EraTransUserSettings _settings;
    private readonly EraTransUserSettingsStore _settingsStore;
    private readonly EraAnalyzerService _analyzerService = new();
    private CancellationTokenSource? _cts;
    private bool _isRunning;

    public EraAnalyzeReportWindow(
        string gameCopyDirectory,
        EraTransUserSettings settings,
        EraTransUserSettingsStore settingsStore)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameCopyDirectory);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(settingsStore);

        _gameCopyDirectory = gameCopyDirectory;
        _settings = settings;
        _settingsStore = settingsStore;

        InitializeComponent();

        GameCopyPathTextBox.Text = _gameCopyDirectory;
        string? resolved = EraAnalyzerService.ResolveExecutablePath(_settings.EraAnalyzeExecutablePath);
        AnalyzerPathTextBox.Text = resolved ?? (_settings.EraAnalyzeExecutablePath ?? string.Empty);

        if (!string.IsNullOrWhiteSpace(resolved))
        {
            SummaryTextBlock.Text = "已就绪，点击「开始体检」即可扫描汉化副本。";
        }
    }

    private void BrowseAnalyzerButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 era-analyze 可执行文件",
            Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
        {
            AnalyzerPathTextBox.Text = dialog.FileName;
            SaveAnalyzerPath(dialog.FileName);
            SummaryTextBlock.Text = "已保存分析工具路径，点击「开始体检」开始检查。";
        }
    }

    private void SaveAnalyzerPath(string path)
    {
        _settings.EraAnalyzeExecutablePath = path.Trim();
        try
        {
            _settingsStore.Save(_settings);
        }
        catch
        {
            // Ignore settings persistence failure during quick checkup.
        }
    }

    private async void RunAnalyzeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning)
        {
            return;
        }

        string configuredPath = AnalyzerPathTextBox.Text.Trim();
        string? executablePath = EraAnalyzerService.ResolveExecutablePath(configuredPath);
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            MessageBox.Show(
                this,
                "未找到 era-analyze 可执行文件。\n\n请先从 github.com/passed1999/era_mcp 的 Releases 下载 Windows 版 era-analyze.exe，然后点击「浏览」指定路径。",
                "未配置分析工具",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        SaveAnalyzerPath(executablePath);

        if (!Directory.Exists(_gameCopyDirectory))
        {
            MessageBox.Show(this, "当前尚未生成游戏副本，请先点击「生成汉化副本」。", "未找到游戏副本",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        using var cts = new CancellationTokenSource();
        _cts = cts;
        SetRunning(true, "正在调用无头引擎分析汉化副本，请稍候…");
        DiagnosticsDataGrid.ItemsSource = null;

        try
        {
            EraAnalyzeReport report = await _analyzerService.AnalyzeAsync(
                executablePath,
                _gameCopyDirectory,
                cts.Token);

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

    private void CancelAnalyzeButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        SummaryTextBlock.Text = "正在取消体检…";
    }

    private void SetRunning(bool running, string? summary = null)
    {
        _isRunning = running;
        RunAnalyzeButton.IsEnabled = !running;
        BrowseAnalyzerButton.IsEnabled = !running;
        AnalyzerPathTextBox.IsReadOnly = running;
        CancelAnalyzeButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        CancelAnalyzeButton.IsEnabled = running;
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
