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

    private void BrowseOutputDirectory_Click(object sender, RoutedEventArgs e) =>
        BrowseFolder(OutputDirectoryTextBox, "选择统一输出目录");

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
        string outputRoot = OutputDirectoryTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(outputRoot))
        {
            MessageBox.Show(this, "请选择统一输出目录。", "目录无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string dictionaryDirectory = Path.Combine(outputRoot, "字典");

        if (!Directory.Exists(gameDirectory))
        {
            MessageBox.Show(this, "请选择有效的原游戏目录。", "目录无效", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                dictionaryDirectory,
                options: options));

            int updatedContextCount = result.UpdatedContextFiles?.Count ?? 0;
            StatusText.Text = $"字典提取完成：新建 {result.GeneratedFiles.Count} 个 JSON 文件，补充上下文 {updatedContextCount} 个文件，{result.Warnings.Count} 条警告。字典位置：{dictionaryDirectory}";
            string message = $"新建 JSON 文件：{result.GeneratedFiles.Count}\n补充上下文文件：{updatedContextCount}\n警告：{result.Warnings.Count}";
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

    private void EditDictionary_Click(object sender, RoutedEventArgs e)
    {
        string outputRoot = OutputDirectoryTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(outputRoot))
        {
            MessageBox.Show(this, "请选择统一输出目录。", "目录无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string dictionaryDirectory = Path.Combine(outputRoot, "字典");
        if (!Directory.Exists(dictionaryDirectory))
        {
            MessageBox.Show(
                this,
                "输出目录中尚无字典。请先提取字典。",
                "字典目录无效",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var editor = new DictionaryEditorWindow(dictionaryDirectory)
        {
            Owner = this
        };
        editor.ShowDialog();
    }

    private async void ApplyTranslations_Click(object sender, RoutedEventArgs e)
    {
        string gameDirectory = GameDirectoryTextBox.Text.Trim();
        string outputRoot = OutputDirectoryTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(outputRoot))
        {
            MessageBox.Show(this, "请选择统一输出目录。", "目录无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string dictionaryDirectory = Path.Combine(outputRoot, "字典");
        string workingCopyDirectory = Path.Combine(outputRoot, "游戏副本");

        if (!Directory.Exists(gameDirectory))
        {
            MessageBox.Show(this, "请选择有效的原游戏目录。", "目录无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!Directory.Exists(dictionaryDirectory))
        {
            MessageBox.Show(this, "输出目录中没有可用字典。请先提取字典。", "字典目录无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ExtractButton.IsEnabled = false;
        ApplyTranslationsButton.IsEnabled = false;
        StatusText.Text = "正在向输出目录的“游戏副本”中复制并应用译文，原游戏目录不会被修改……";

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
                $"副本处理完成：复制 {result.CopiedFiles.Count} 个文件，应用译文 {result.ModifiedFiles.Count} 个，重命名 ERD {result.RenamedFiles.Count} 个，{result.Warnings.Count} 条警告。输出位置：{workingCopyDirectory}";

            string message =
                $"复制文件：{result.CopiedFiles.Count}\n" +
                $"应用译文：{result.ModifiedFiles.Count}\n" +
                $"重命名 ERD：{result.RenamedFiles.Count}\n" +
                $"警告：{result.Warnings.Count}\n输出位置：{workingCopyDirectory}";

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