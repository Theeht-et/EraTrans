using EraTrans.Core.WriteBack;
using System.Windows;

namespace EraTrans.App;

public partial class EncodingIssuesWindow : Window
{
    private readonly IReadOnlyList<GameFileEncodingIssue> _issues;

    public EncodingIssuesWindow(IReadOnlyList<GameFileEncodingIssue> issues, string operationName)
    {
        ArgumentNullException.ThrowIfNull(issues);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);

        InitializeComponent();

        _issues = issues;
        int ambiguousCount = issues.Count(issue => issue.Kind == GameFileEncodingIssueKind.Ambiguous);
        int decodeFailedCount = issues.Count - ambiguousCount;

        HeaderTextBlock.Text = $"{operationName}前发现 {issues.Count} 个文件存在编码问题";
        DescriptionTextBlock.Text =
            $"共发现 {ambiguousCount} 个编码歧义文件、{decodeFailedCount} 个无法无损解码文件。" +
            "建议先在外部文本编辑器（如 VS Code、Notepad++ 或 Sakura Editor）中核对这些文件的编码并转换为 UTF-8 BOM 后重试。";
        BehaviorNoteTextBlock.Text =
            "如果选择忽略并继续：提取字典时会跳过这些文件不抽取；生成游戏副本时会将这些文件原样逐字节复制（不转码、不应用译文），其余正常文件不受影响。";
        IgnoreContinueButton.Content = $"忽略这 {issues.Count} 个文件并继续";
        IssuesDataGrid.ItemsSource = issues;
    }

    public bool IgnoreAndContinue { get; private set; }

    private void CopyList_Click(object sender, RoutedEventArgs e)
    {
        string text = string.Join(
            Environment.NewLine,
            _issues.Select(issue => $"{issue.RelativePath}\t[{issue.KindLabel}]\t{issue.Reason}"));
        Clipboard.SetText(text);
        CopiedHintTextBlock.Text = "已复制问题文件清单";
    }

    private void IgnoreAndContinue_Click(object sender, RoutedEventArgs e)
    {
        IgnoreAndContinue = true;
        DialogResult = true;
    }
}