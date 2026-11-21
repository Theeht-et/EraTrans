using EraTrans.Core;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;

namespace EraTrans.App;

public partial class AiTranslationLogWindow : Window
{
    private static readonly JsonSerializerOptions DisplayJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly AiTranslationLogStore _store;
    private AiTranslationLogEntry? _selectedLogEntry;
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

        _selectedLogEntry = entry;
        SelectedLogSummaryTextBlock.Text = string.Join(
            Environment.NewLine,
            $"{entry.DisplayTime}  |  {entry.RequestKindDescription}  |  {entry.StatusDescription}",
            $"{entry.ProviderName} / {entry.Model}",
            entry.Summary);
        EndpointTextBlock.Text =
            $"请求地址：{entry.RequestMethod} {entry.Endpoint}{Environment.NewLine}协议：{entry.Protocol}";

        RequestTextBox.Text = FormatRequestForDisplay(entry);
        ResponseTextBox.Text = string.IsNullOrWhiteSpace(entry.RawResponse)
            ? "没有收到可显示的文本回复。"
            : FormatResponseForDisplay(entry.RawResponse);

        var errorLines = new List<string>
        {
            $"状态：{entry.StatusDescription}",
            $"开始时间（UTC）：{entry.StartedAtUtc:O}",
            $"完成时间（UTC）：{entry.CompletedAtUtc?.ToString("O") ?? "未完成"}",
            $"耗时：{entry.DurationDescription}",
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

    private static string FormatRequestForDisplay(AiTranslationLogEntry entry)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(entry.RequestJson);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("messages", out JsonElement messages) ||
                messages.ValueKind != JsonValueKind.Array)
            {
                return FormatJsonForDisplay(entry.RequestJson);
            }

            var builder = new StringBuilder();
            var requestParameters = root.EnumerateObject()
                .Where(property => property.Name != "messages")
                .ToDictionary(property => property.Name, property => property.Value);

            AppendJsonSection(builder, "请求参数", requestParameters);

            foreach (JsonElement message in messages.EnumerateArray())
            {
                string role = message.ValueKind == JsonValueKind.Object &&
                              message.TryGetProperty("role", out JsonElement roleElement)
                    ? roleElement.GetString() ?? "message"
                    : "message";
                string heading = role switch
                {
                    "system" => "系统提示词",
                    "user" => "用户内容",
                    "assistant" => "附加消息",
                    _ => role
                };

                if (builder.Length > 0)
                    builder.AppendLine().AppendLine();
                builder.Append('【').Append(heading).AppendLine("】");

                if (message.ValueKind == JsonValueKind.Object &&
                    message.TryGetProperty("content", out JsonElement content))
                {
                    string contentText = content.ValueKind == JsonValueKind.String
                        ? content.GetString() ?? string.Empty
                        : content.GetRawText();
                    builder.Append(FormatJsonForDisplay(contentText));
                }
                else
                {
                    builder.Append(JsonSerializer.Serialize(message, DisplayJsonOptions));
                }
            }

            return builder.ToString();
        }
        catch (JsonException)
        {
            return FormatJsonForDisplay(entry.RequestJson);
        }
    }

    private static string FormatResponseForDisplay(string content)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(content);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("choices", out JsonElement choices) ||
                choices.ValueKind != JsonValueKind.Array)
            {
                return FormatJsonForDisplay(content);
            }

            var builder = new StringBuilder();
            var responseMetadata = root.EnumerateObject()
                .Where(property => property.Name != "choices")
                .ToDictionary(property => property.Name, property => property.Value);
            AppendJsonSection(builder, "响应信息", responseMetadata);

            foreach (JsonElement choice in choices.EnumerateArray())
            {
                if (choice.ValueKind != JsonValueKind.Object ||
                    !choice.TryGetProperty("message", out JsonElement message) ||
                    message.ValueKind != JsonValueKind.Object)
                {
                    if (builder.Length > 0)
                        builder.AppendLine().AppendLine();
                    builder.AppendLine("【回复选项】");
                    builder.Append(JsonSerializer.Serialize(choice, DisplayJsonOptions));
                    continue;
                }

                var choiceMetadata = choice.EnumerateObject()
                    .Where(property => property.Name != "message")
                    .ToDictionary(property => property.Name, property => property.Value);
                AppendJsonSection(builder, "回复选项信息", choiceMetadata);

                var messageMetadata = message.EnumerateObject()
                    .Where(property => property.Name != "content")
                    .ToDictionary(property => property.Name, property => property.Value);
                AppendJsonSection(builder, "消息信息", messageMetadata);

                if (message.TryGetProperty("content", out JsonElement messageContent))
                {
                    if (builder.Length > 0)
                        builder.AppendLine().AppendLine();
                    builder.AppendLine("【AI 回复正文】");
                    string replyText = messageContent.ValueKind == JsonValueKind.String
                        ? messageContent.GetString() ?? string.Empty
                        : messageContent.GetRawText();
                    builder.Append(FormatJsonForDisplay(replyText));
                }
            }

            return builder.ToString();
        }
        catch (JsonException)
        {
            return FormatJsonForDisplay(content);
        }
    }

    private static void AppendJsonSection(
        StringBuilder builder,
        string title,
        Dictionary<string, JsonElement> values)
    {
        if (values.Count == 0)
            return;

        if (builder.Length > 0)
            builder.AppendLine().AppendLine();

        builder.AppendLine(title);
        builder.Append(JsonSerializer.Serialize(values, DisplayJsonOptions));
    }

    private static string FormatJsonForDisplay(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return content;

        try
        {
            using JsonDocument document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind == JsonValueKind.String)
                return document.RootElement.GetString() ?? string.Empty;

            return JsonSerializer.Serialize(document.RootElement, DisplayJsonOptions);
        }
        catch (JsonException)
        {
            // Plain-text replies and non-JSON error bodies are shown exactly as received.
            return content;
        }
    }

    private void ShowEmptySelection()
    {
        _selectedLogEntry = null;
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

    private void CopyRequest_Click(object sender, RoutedEventArgs e) =>
        CopyText(_selectedLogEntry?.RequestJson ?? RequestTextBox.Text);

    private void CopyResponse_Click(object sender, RoutedEventArgs e) =>
        CopyText(_selectedLogEntry?.RawResponse ?? ResponseTextBox.Text);

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
