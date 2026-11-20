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

    private void BrowseGameDirectory_Click(object sender, RoutedEventArgs e)
    {
        BrowseFolder(GameDirectoryTextBox, "选择原 ERA 游戏目录");
        UpdateOutputLayoutPreview();
    }

    private void BrowseOutputDirectory_Click(object sender, RoutedEventArgs e)
    {
        BrowseFolder(OutputDirectoryTextBox, "选择统一输出目录");
        UpdateOutputLayoutPreview();
    }

    private void UpdateOutputLayoutPreview()
    {
        string gameDirectory = GameDirectoryTextBox.Text.Trim();
        string outputRoot = OutputDirectoryTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(gameDirectory) ||
            string.IsNullOrWhiteSpace(outputRoot) ||
            !Directory.Exists(gameDirectory))
        {
            OutputLayoutPreviewTextBlock.Text =
                "选择原游戏目录和统一输出目录后，这里会显示本游戏的分类输出位置。";
            return;
        }

        try
        {
            EraTransOutputLayout layout = EraTransOutputLayout.Create(outputRoot, gameDirectory);
            OutputLayoutPreviewTextBlock.Text =
                $"本次游戏：{layout.GameDirectoryName}\n" +
                $"字典：{layout.DictionaryDirectory}\n" +
                $"AI 上下文：{layout.AiContextDirectory}\n" +
                $"游戏副本：{layout.GameCopyDirectory}";
        }
        catch (Exception ex)
        {
            OutputLayoutPreviewTextBlock.Text = "输出路径无效：" + ex.Message;
        }
    }

    private bool TryGetGameOutputLayout(out EraTransOutputLayout layout)
    {
        layout = null!;
        string gameDirectory = GameDirectoryTextBox.Text.Trim();
        string outputRoot = OutputDirectoryTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(outputRoot))
        {
            MessageBox.Show(this, "请选择统一输出目录。", "目录无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (!Directory.Exists(gameDirectory))
        {
            MessageBox.Show(this, "请选择有效的原游戏目录。", "目录无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        try
        {
            layout = EraTransOutputLayout.Create(outputRoot, gameDirectory);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "输出目录无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        return PrepareLegacyOutputLayout(layout);
    }

    private bool PrepareLegacyOutputLayout(EraTransOutputLayout layout)
    {
        // The old app wrote these folders directly under the unified output root.
        // Only migrate them after an explicit confirmation, because they may contain
        // dictionaries from more than one game and cannot be safely attributed.
        if (Directory.Exists(layout.GameOutputRoot) || File.Exists(layout.GameOutputRoot))
        {
            return true;
        }

        (string Legacy, string Destination)[] candidates =
        [
            (Path.Combine(layout.OutputRoot, "字典"), layout.DictionaryDirectory),
            (Path.Combine(layout.OutputRoot, "AI上下文"), layout.AiContextDirectory),
            (Path.Combine(layout.OutputRoot, "游戏副本"), layout.GameCopyDirectory)
        ];
        var existing = candidates.Where(pair => Directory.Exists(pair.Legacy)).ToArray();
        if (existing.Length == 0)
        {
            return true;
        }

        var collision = existing.FirstOrDefault(pair =>
            Directory.Exists(pair.Destination) || File.Exists(pair.Destination));
        if (collision != default)
        {
            MessageBox.Show(
                this,
                $"检测到旧版输出目录与新游戏目录存在路径冲突，未移动任何文件。\n\n旧目录：{collision.Legacy}\n新目录：{collision.Destination}\n\n请先手动检查并整理这两个目录。",
                "旧版输出目录冲突",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        MessageBoxResult choice = MessageBox.Show(
            this,
            $"检测到旧版未按游戏名称分类的输出目录。\n\n" +
            $"如果这些旧文件夹确定只属于当前游戏“{layout.GameDirectoryName}”，选择“是”可将它们移动到：\n{layout.GameOutputRoot}\n\n" +
            "如果旧目录可能混有其他游戏的数据，选择“否”保留旧目录，并为当前游戏使用新的分类目录。取消则不执行本次操作。\n\n" +
            "移动只改变文件夹位置，不会修改字典内容。",
            "发现旧版输出目录",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning);

        if (choice == MessageBoxResult.Cancel)
        {
            return false;
        }

        if (choice == MessageBoxResult.No)
        {
            return true;
        }

        var moved = new List<(string Legacy, string Destination)>();
        try
        {
            Directory.CreateDirectory(layout.GameOutputRoot);
            foreach ((string legacy, string destination) in existing)
            {
                Directory.Move(legacy, destination);
                moved.Add((legacy, destination));
            }

            return true;
        }
        catch (Exception ex)
        {
            foreach ((string legacy, string destination) in moved.AsEnumerable().Reverse())
            {
                try
                {
                    if (Directory.Exists(destination) && !Directory.Exists(legacy))
                    {
                        Directory.Move(destination, legacy);
                    }
                }
                catch
                {
                    // Keep the original exception visible; any failed rollback remains
                    // recoverable in the displayed source and destination paths.
                }
            }

            MessageBox.Show(
                this,
                $"迁移旧版输出目录失败：{ex.Message}\n\n请检查旧目录和新目录，确认文件没有丢失后再重试。",
                "迁移失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }
    }

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
        if (!TryGetGameOutputLayout(out EraTransOutputLayout layout))
        {
            return;
        }

        string gameDirectory = layout.GameDirectory;
        string dictionaryDirectory = layout.DictionaryDirectory;

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
            int groupingFileCount = result.UpdatedGroupingFiles?.Count ?? 0;
            StatusText.Text =
                $"字典提取完成：新建 {result.GeneratedFiles.Count} 个 JSON 文件，补充上下文 {updatedContextCount} 个文件，生成/更新 ERB AI 分组文件 {groupingFileCount} 个，{result.Warnings.Count} 条警告。字典位置：{dictionaryDirectory}";

            string message =
                $"新建 JSON 文件：{result.GeneratedFiles.Count}\n" +
                $"补充上下文文件：{updatedContextCount}\n" +
                $"生成/更新 ERB AI 分组文件：{groupingFileCount}\n" +
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
        if (!TryGetGameOutputLayout(out EraTransOutputLayout layout))
        {
            return;
        }

        string dictionaryDirectory = layout.DictionaryDirectory;
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

        string gameDirectory = GameDirectoryTextBox.Text.Trim();
        var editor = new DictionaryEditorWindow(
            dictionaryDirectory,
            gameDirectory,
            _settings)
        {
            Owner = this
        };
        editor.ShowDialog();
    }

    private async void ApplyTranslations_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetGameOutputLayout(out EraTransOutputLayout layout))
        {
            return;
        }

        string gameDirectory = layout.GameDirectory;
        string dictionaryDirectory = layout.DictionaryDirectory;
        string workingCopyDirectory = layout.GameCopyDirectory;

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
