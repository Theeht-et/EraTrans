using EraTrans.Core;
using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace EraTrans.App;

public partial class MainWindow : Window
{
    private readonly EraTransUserSettingsStore _settingsStore = new();
    private EraTransUserSettings _settings;

    public MainWindow()
    {
        InitializeComponent();

        try
        {
            _settings = _settingsStore.Load();
        }
        catch (Exception ex)
        {
            // Keep a damaged settings file untouched. Defaults are used in memory until
            // the user explicitly saves settings from the settings window.
            _settings = EraTransUserSettings.CreateDefault();
            MessageBox.Show(
                this,
                $"无法读取设置文件，当前会话暂时使用旧版默认设置。原设置文件未被覆盖。\n\n{ex.Message}",
                "设置读取失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
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

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var window = new ParserSettingsWindow(_settings, _settingsStore)
        {
            Owner = this
        };

        if (window.ShowDialog() == true)
        {
            _settings = _settingsStore.Load();
            StatusText.Text = "解析与输出设置已保存；下一次提取或应用译文时生效。";
        }
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
        SettingsButton.IsEnabled = false;
        StatusText.Text = "正在提取字典……";

        try
        {
            EraParserOptions options = _settings.ToParserOptions();
            var extractor = new DictionaryExtractor();
            DictionaryExtractionResult result = await Task.Run(() => extractor.Extract(
                gameDirectory,
                dictionaryDirectory,
                options: options));

            int updatedContextCount = result.UpdatedContextFiles?.Count ?? 0;
            StatusText.Text =
                $"字典提取完成：新建 {result.GeneratedFiles.Count} 个 JSON 文件，补充上下文 {updatedContextCount} 个文件，{result.Warnings.Count} 条警告。字典位置：{dictionaryDirectory}";

            string message =
                $"新建 JSON 文件：{result.GeneratedFiles.Count}\n" +
                $"补充上下文文件：{updatedContextCount}\n" +
                $"警告：{result.Warnings.Count}";
            if (result.Warnings.Count > 0)
            {
                message += "\n\n" + string.Join("\n", result.Warnings.Take(8));
            }

            MessageBox.Show(this, message, "提取完成", MessageBoxButton.OK, MessageBoxImage.Information);
            OpenResultDirectoryIfEnabled(dictionaryDirectory);
        }
        catch (Exception ex)
        {
            StatusText.Text = "字典提取失败，请检查目录和设置。";
            MessageBox.Show(this, ex.Message, "提取失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ExtractButton.IsEnabled = true;
            ApplyTranslationsButton.IsEnabled = true;
            SettingsButton.IsEnabled = true;
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
        SettingsButton.IsEnabled = false;
        StatusText.Text = "正在将整个原游戏目录完整复制到输出目录的“游戏副本”，然后应用译文；原游戏目录不会被修改……";

        try
        {
            var applier = new TranslationApplier();
            EraParserOptions options = _settings.ToParserOptions();
            TranslationApplyResult result = await Task.Run(() => applier.Apply(
                gameDirectory,
                dictionaryDirectory,
                workingCopyDirectory,
                options));

            StatusText.Text =
                $"副本处理完成：完整复制 {result.CopiedFiles.Count} 个游戏文件，应用译文 {result.ModifiedFiles.Count} 个，重命名 ERD {result.RenamedFiles.Count} 个，{result.Warnings.Count} 条警告。输出位置：{workingCopyDirectory}";

            string message =
                $"完整复制游戏文件：{result.CopiedFiles.Count}\n" +
                $"应用译文：{result.ModifiedFiles.Count}\n" +
                $"重命名 ERD：{result.RenamedFiles.Count}\n" +
                $"警告：{result.Warnings.Count}\n输出位置：{workingCopyDirectory}";

            if (result.Warnings.Count > 0)
            {
                message += "\n\n" + string.Join("\n", result.Warnings.Take(8));
            }

            MessageBox.Show(this, message, "副本处理完成", MessageBoxButton.OK, MessageBoxImage.Information);
            OpenResultDirectoryIfEnabled(workingCopyDirectory);
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
            SettingsButton.IsEnabled = true;
        }
    }

    private void OpenResultDirectoryIfEnabled(string directory)
    {
        if (!_settings.AutoOpenFolder || !Directory.Exists(directory))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"处理已完成，但无法自动打开结果目录：\n{directory}\n\n{ex.Message}",
                "自动打开目录失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }
}
