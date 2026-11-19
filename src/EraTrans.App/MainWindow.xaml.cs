using EraTrans.Core;
using Microsoft.Win32;
using System.IO;
using System.Text;
using System.Windows;

namespace EraTrans.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        InitializeComponent();
    }

    private void BrowseGameDirectory_Click(object sender, RoutedEventArgs e) =>
        BrowseFolder(GameDirectoryTextBox, "选择 ERA 游戏目录");

    private void BrowseReferenceDirectory_Click(object sender, RoutedEventArgs e) =>
        BrowseFolder(ReferenceDirectoryTextBox, "选择参考汉化目录");

    private void BrowseOutputDirectory_Click(object sender, RoutedEventArgs e) =>
        BrowseFolder(OutputDirectoryTextBox, "选择字典输出目录");

    private void BrowseFolder(System.Windows.Controls.TextBox target, string title)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
        {
            target.Text = dialog.FolderName;
        }
    }

    private Encoding GetSelectedEncoding()
    {
        if (EncodingComboBox.SelectedItem is not System.Windows.Controls.ComboBoxItem item ||
            item.Tag is not string selectedEncoding)
        {
            throw new InvalidOperationException("请选择有效的文件编码。");
        }

        return selectedEncoding switch
        {
            "utf8-bom" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            "utf8" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            "932" => Encoding.GetEncoding(932),
            "936" => Encoding.GetEncoding(936),
            "950" => Encoding.GetEncoding(950),
            _ => throw new InvalidOperationException("不支持所选文件编码。")
        };
    }

    private async void Extract_Click(object sender, RoutedEventArgs e)
    {
        string gameDirectory = GameDirectoryTextBox.Text.Trim();
        string outputDirectory = OutputDirectoryTextBox.Text.Trim();
        string referenceDirectory = ReferenceDirectoryTextBox.Text.Trim();

        if (!Directory.Exists(gameDirectory))
        {
            MessageBox.Show(this, "请选择有效的游戏目录。", "目录无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            MessageBox.Show(this, "请选择字典输出目录。", "目录无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ExtractButton.IsEnabled = false;
        StatusText.Text = "正在提取字典，请勿关闭程序……";

        try
        {
            var extractor = new DictionaryExtractor();
            var options = new EraParserOptions
            {
                FileEncoding = GetSelectedEncoding()
            };
            var result = await Task.Run(() => extractor.Extract(
                gameDirectory,
                outputDirectory,
                string.IsNullOrWhiteSpace(referenceDirectory) ? null : referenceDirectory,
                options));

            StatusText.Text = $"提取完成：生成 {result.GeneratedFiles.Count} 个 JSON 文件，{result.Warnings.Count} 条警告。";
            string message = $"生成 JSON 文件：{result.GeneratedFiles.Count}\n警告：{result.Warnings.Count}";
            if (result.Warnings.Count > 0)
            {
                message += "\n\n" + string.Join("\n", result.Warnings.Take(8));
            }

            MessageBox.Show(this, message, "提取完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusText.Text = "提取失败，请检查目录和文件编码。";
            MessageBox.Show(this, ex.Message, "提取失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ExtractButton.IsEnabled = true;
        }
    }
}
