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
        BrowseFolder(GameDirectoryTextBox, "选择原 ERA 游戏目录");

    private void BrowseReferenceDirectory_Click(object sender, RoutedEventArgs e) =>
        BrowseFolder(ReferenceDirectoryTextBox, "选择参考汉化目录");

    private void BrowseOutputDirectory_Click(object sender, RoutedEventArgs e) =>
        BrowseFolder(OutputDirectoryTextBox, "选择 JSON 字典目录");

    private void BrowseWorkingCopyDirectory_Click(object sender, RoutedEventArgs e) =>
        BrowseFolder(WorkingCopyDirectoryTextBox, "选择游戏副本输出目录");

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
            MessageBox.Show(this, "请选择有效的原游戏目录。", "目录无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            MessageBox.Show(this, "请选择 JSON 字典输出目录。", "目录无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ExtractButton.IsEnabled = false;
        ApplyTranslationsButton.IsEnabled = false;
        StatusText.Text = "正在提取字典……";

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

            StatusText.Text = $"字典提取完成：生成 {result.GeneratedFiles.Count} 个 JSON 文件，{result.Warnings.Count} 条警告。";
            string message = $"生成 JSON 文件：{result.GeneratedFiles.Count}\n警告：{result.Warnings.Count}";
            if (result.Warnings.Count > 0)
            {
                message += "\n\n" + string.Join("\n", result.Warnings.Take(8));
            }

            MessageBox.Show(this, message, "提取完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusText.Text = "字典提取失败，请检查目录和文件编码。";
            MessageBox.Show(this, ex.Message, "提取失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ExtractButton.IsEnabled = true;
            ApplyTranslationsButton.IsEnabled = true;
        }
    }

    private async void ApplyTranslations_Click(object sender, RoutedEventArgs e)
    {
        string gameDirectory = GameDirectoryTextBox.Text.Trim();
        string dictionaryDirectory = OutputDirectoryTextBox.Text.Trim();
        string workingCopyDirectory = WorkingCopyDirectoryTextBox.Text.Trim();

        if (!Directory.Exists(gameDirectory))
        {
            MessageBox.Show(this, "请选择有效的原游戏目录。", "目录无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!Directory.Exists(dictionaryDirectory))
        {
            MessageBox.Show(this, "请选择包含 JSON 字典的目录。", "目录无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(workingCopyDirectory))
        {
            MessageBox.Show(this, "请选择独立的游戏副本输出目录。", "目录无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ExtractButton.IsEnabled = false;
        ApplyTranslationsButton.IsEnabled = false;
        StatusText.Text = "正在复制游戏文件并应用译文，原游戏目录不会被修改……";

        try
        {
            var applier = new TranslationApplier();
            var options = new EraParserOptions
            {
                FileEncoding = GetSelectedEncoding(),
                EnableVariableAutoCorrection = AutoVariableCorrectionCheckBox.IsChecked == true
            };
            var result = await Task.Run(() => applier.Apply(
                gameDirectory,
                dictionaryDirectory,
                workingCopyDirectory,
                options));

            StatusText.Text =
                $"副本处理完成：复制 {result.CopiedFiles.Count} 个文件，应用译文 {result.ModifiedFiles.Count} 个，重命名 ERD {result.RenamedFiles.Count} 个，{result.Warnings.Count} 条警告。";

            string message =
                $"复制文件：{result.CopiedFiles.Count}\n" +
                $"应用译文：{result.ModifiedFiles.Count}\n" +
                $"重命名 ERD：{result.RenamedFiles.Count}\n" +
                $"警告：{result.Warnings.Count}";

            if (result.Warnings.Count > 0)
            {
                message += "\n\n" + string.Join("\n", result.Warnings.Take(8));
            }

            MessageBox.Show(this, message, "副本处理完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusText.Text = "副本处理失败；原游戏目录保持不变。";
            MessageBox.Show(this, ex.Message, "应用译文失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ExtractButton.IsEnabled = true;
            ApplyTranslationsButton.IsEnabled = true;
        }
    }
}