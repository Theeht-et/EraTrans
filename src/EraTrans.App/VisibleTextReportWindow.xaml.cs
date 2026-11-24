using System.IO;
using System.Text.Json;
using System.Windows;

namespace EraTrans.App;

public partial class VisibleTextReportWindow : Window
{
    public sealed record ReportCountRow(string Name, int Lines);

    public VisibleTextReportWindow(string reportPath)
    {
        InitializeComponent();
        ReportPathTextBlock.Text = reportPath;
        LoadReport(reportPath);
    }

    private void LoadReport(string reportPath)
    {
        try
        {
            using FileStream stream = File.OpenRead(reportPath);
            using JsonDocument document = JsonDocument.Parse(stream);
            JsonElement root = document.RootElement;

            var unknownCommands = new List<ReportCountRow>();
            if (root.TryGetProperty("unknownCommands", out JsonElement commandsElement) &&
                commandsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in commandsElement.EnumerateArray())
                {
                    string name = item.TryGetProperty("command", out JsonElement c) ? c.GetString() ?? string.Empty : string.Empty;
                    int lines = item.TryGetProperty("lines", out JsonElement l) && l.TryGetInt32(out int v) ? v : 0;
                    if (name.Length > 0)
                    {
                        unknownCommands.Add(new ReportCountRow(name, lines));
                    }
                }
            }

            var assignedVars = new List<ReportCountRow>();
            if (root.TryGetProperty("assignedAndPrintedVars", out JsonElement varsElement) &&
                varsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in varsElement.EnumerateArray())
                {
                    string name = item.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? string.Empty : string.Empty;
                    int lines = item.TryGetProperty("lines", out JsonElement l) && l.TryGetInt32(out int v) ? v : 0;
                    if (name.Length > 0)
                    {
                        assignedVars.Add(new ReportCountRow(name, lines));
                    }
                }
            }

            var samples = new List<string>();
            if (root.TryGetProperty("unknownCommandSamples", out JsonElement samplesElement) &&
                samplesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in samplesElement.EnumerateArray())
                {
                    string? sample = item.GetString();
                    if (!string.IsNullOrWhiteSpace(sample))
                    {
                        samples.Add(sample);
                    }
                }
            }

            int assignedLines = root.TryGetProperty("assignedAndPrintedLines", out JsonElement a) && a.TryGetInt32(out int av) ? av : 0;
            int placeholderOnlyLines = root.TryGetProperty("placeholderOnlyPrintLines", out JsonElement p) && p.TryGetInt32(out int pv) ? pv : 0;
            int unknownTotalLines = unknownCommands.Sum(row => row.Lines);

            SummaryTextBlock.Text =
                $"未知疑似文本命令：{unknownCommands.Count} 类（共 {unknownTotalLines} 行）   ·   " +
                $"变量中转未提取：{assignedLines} 行（{assignedVars.Count} 个变量）   ·   " +
                $"仅占位符打印：{placeholderOnlyLines} 行";

            UnknownCommandsDataGrid.ItemsSource = unknownCommands;
            AssignedVarsDataGrid.ItemsSource = assignedVars;
            SamplesTextBox.Text = samples.Count == 0
                ? "（无未知命令样本）"
                : string.Join(Environment.NewLine, samples);
        }
        catch (Exception ex)
        {
            SummaryTextBlock.Text = $"读取覆盖率自检报告失败：{ex.Message}";
            SamplesTextBox.Text = string.Empty;
        }
    }
}
