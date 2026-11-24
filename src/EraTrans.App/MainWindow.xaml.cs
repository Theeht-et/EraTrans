using EraTrans.Core;
using EraTrans.Core.WriteBack;
using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace EraTrans.App;

public partial class MainWindow : Window
{
    private readonly EraTransUserSettingsStore _settingsStore = new();
    private EraTransUserSettings _settings;
    private readonly DictionaryEditorControl _editor;
    private EraTransOutputLayout? _activeLayout;
    private bool _canPersistSettings = true;
    private bool _isProcessing;

    public MainWindow()
    {
        InitializeComponent();

        try
        {
            _settings = _settingsStore.Load();
        }
        catch (Exception ex)
        {
            // A damaged settings file must not be overwritten automatically.
            _settings = EraTransUserSettings.CreateDefault();
            _canPersistSettings = false;
            MessageBox.Show(
                this,
                $"无法读取设置文件，当前会话暂时使用默认设置。原设置文件未被覆盖。\n\n{ex.Message}",
                "设置读取失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        _editor = new DictionaryEditorControl(_settings);
        _editor.CurrentFileChanged += Editor_CurrentFileChanged;
        _editor.SaveAvailabilityChanged += Editor_SaveAvailabilityChanged;
        EditorHost.Content = _editor;

        GameDirectoryTextBox.Text = _settings.LastGameDirectory ?? string.Empty;
        OutputDirectoryTextBox.Text = _settings.LastOutputRootDirectory ?? string.Empty;
        RestoreLastWorkspace();
    }

    private void SetPathsDrawerExpanded(bool expanded)
    {
        PathsDrawerBorder.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        TogglePathsButton.Content = expanded ? "收起目录 ▴" : "切换目录 ▾";
    }

    private void TogglePathsButton_Click(object sender, RoutedEventArgs e)
    {
        SetPathsDrawerExpanded(PathsDrawerBorder.Visibility != Visibility.Visible);
    }

    private void SettingsMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsContextMenu is null)
            return;
        SettingsContextMenu.PlacementTarget = SettingsMenuButton;
        SettingsContextMenu.Placement = PlacementMode.Bottom;
        SettingsContextMenu.IsOpen = true;
    }

    private void AiSettingsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (!_isProcessing)
            _editor.OpenAiSettings();
    }

    private void ReloadWorkspaceMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (!_isProcessing && TryGetGameOutputLayout(out EraTransOutputLayout layout))
            ActivateWorkspace(layout);
    }

    private void RestoreLastWorkspace()
    {
        if (TryGetGameOutputLayout(out EraTransOutputLayout layout, showErrors: false) &&
            ActivateWorkspace(layout))
        {
            SetPathsDrawerExpanded(false);
            StatusText.Text = $"已加载工作区：{layout.GameDirectoryName}";
            return;
        }

        SetPathsDrawerExpanded(true);
        WorkspaceBadgeTextBlock.Text = "尚未选择工作区";
        WorkspaceBadgeTextBlock.ToolTip = null;
        StatusText.Text =
            string.IsNullOrWhiteSpace(GameDirectoryTextBox.Text) ||
            string.IsNullOrWhiteSpace(OutputDirectoryTextBox.Text)
                ? "请先选择原游戏目录和输出目录，选完自动加载"
                : "上次保存的目录无效，请重新选择";
    }

    private void BrowseGameDirectory_Click(object sender, RoutedEventArgs e)
    {
        BrowseFolder(GameDirectoryTextBox, "选择原 ERA 游戏目录");
    }

    private void BrowseOutputDirectory_Click(object sender, RoutedEventArgs e)
    {
        BrowseFolder(OutputDirectoryTextBox, "选择统一输出目录");
    }

    private void BrowseFolder(TextBox target, string title)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        if (dialog.ShowDialog(this) == true)
        {
            target.Text = dialog.FolderName;
            SaveWorkspacePreferences();
            if (TryGetGameOutputLayout(out EraTransOutputLayout layout, showErrors: false))
            {
                if (ActivateWorkspace(layout))
                {
                    SetPathsDrawerExpanded(false);
                }
            }
            else
            {
                StatusText.Text = "已更新目录，请继续选择另一个目录";
            }
        }
    }

    private bool TryGetGameOutputLayout(out EraTransOutputLayout layout, bool showErrors = true)
    {
        layout = null!;
        string gameDirectory = GameDirectoryTextBox.Text.Trim();
        string outputRoot = OutputDirectoryTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(gameDirectory) || !Directory.Exists(gameDirectory))
        {
            if (showErrors)
            {
                SetPathsDrawerExpanded(true);
                MessageBox.Show(this, "请选择有效的原游戏目录。", "目录无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return false;
        }

        if (string.IsNullOrWhiteSpace(outputRoot))
        {
            if (showErrors)
            {
                SetPathsDrawerExpanded(true);
                MessageBox.Show(this, "请选择统一输出目录。", "目录无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return false;
        }

        try
        {
            layout = EraTransOutputLayout.Create(outputRoot, gameDirectory, _settings.ToParserOptions().ExtractionMode);
            return true;
        }
        catch (Exception ex)
        {
            if (showErrors)
            {
                SetPathsDrawerExpanded(true);
                MessageBox.Show(this, ex.Message, "输出目录无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return false;
        }
    }

    private bool ActivateWorkspace(EraTransOutputLayout layout)
    {
        bool gameChanged = _activeLayout is not null &&
            !PathsEqual(_activeLayout.GameDirectory, layout.GameDirectory);
        string preferredFile = gameChanged ? string.Empty : _settings.LastDictionaryFileRelativePath ?? string.Empty;

        if (!_editor.SwitchWorkspace(layout, preferredFile))
        {
            if (_activeLayout is not null)
            {
                GameDirectoryTextBox.Text = _activeLayout.GameDirectory;
                OutputDirectoryTextBox.Text = _activeLayout.OutputRoot;
                SaveWorkspacePreferences();
            }

            StatusText.Text = "工作区切换已取消；已恢复当前工作区目录，当前编辑内容仍保留。";
            return false;
        }

        _activeLayout = layout;
        _settings.LastGameDirectory = layout.GameDirectory;
        _settings.LastOutputRootDirectory = layout.OutputRoot;
        SaveWorkspacePreferences();
        string versionTag = EraTransOutputLayout.GetVersionFolderName(_settings.ToParserOptions().ExtractionMode);
        WorkspaceBadgeTextBlock.Text = $"{layout.GameDirectoryName}（{versionTag}）";
        WorkspaceBadgeTextBlock.ToolTip =
            $"原游戏目录：{layout.GameDirectory}\n输出目录：{layout.OutputRoot}\n字典目录：{layout.DictionaryDirectory}";
        WorkspaceModeStatusText.Text = $"模式：{versionTag} · 原游戏只读";
        StatusText.Text = $"当前工作区：{layout.GameDirectoryName}（{versionTag}）· 字典目录：{layout.DictionaryDirectory}";
        return true;
    }

    private static bool PathsEqual(string first, string second) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
            StringComparison.OrdinalIgnoreCase);

    private void Editor_CurrentFileChanged(string? relativePath)
    {
        _settings.LastDictionaryFileRelativePath = relativePath ?? string.Empty;
        SaveWorkspacePreferences();
    }

    private void SaveWorkspacePreferences()
    {
        if (!_canPersistSettings)
            return;

        _settings.LastGameDirectory = GameDirectoryTextBox.Text.Trim();
        _settings.LastOutputRootDirectory = OutputDirectoryTextBox.Text.Trim();

        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"工作区偏好暂时无法保存：{ex.Message}";
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var window = new ParserSettingsWindow(_settings, _settingsStore) { Owner = this };
        if (window.ShowDialog() == true)
        {
            _settings = _settingsStore.Load();
            _canPersistSettings = true;
            _editor.UpdateSettings(_settings);
            if (_activeLayout is not null &&
                TryGetGameOutputLayout(out EraTransOutputLayout updatedLayout, showErrors: false) &&
                !PathsEqual(_activeLayout.DictionaryDirectory, updatedLayout.DictionaryDirectory))
            {
                ActivateWorkspace(updatedLayout);
            }
            else
            {
                StatusText.Text = "设置已保存，将在下次提取或生成副本时生效";
            }
        }
    }

    private void Editor_SaveAvailabilityChanged(bool canSave)
    {
        SaveCurrentDictionaryButton.IsEnabled = canSave && !_isProcessing;
    }

    private void SaveCurrentDictionary_Click(object sender, RoutedEventArgs e)
    {
        if (!_isProcessing)
            _editor.SaveCurrentDictionary();
    }

    private void SetBusy(bool busy, string? busyTitle = null, string? busyDescription = null)
    {
        _isProcessing = busy;
        BusyProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        TogglePathsButton.IsEnabled = !busy;
        BrowseGameDirectoryButton.IsEnabled = !busy;
        BrowseOutputDirectoryButton.IsEnabled = !busy;
        ExtractButton.IsEnabled = !busy;
        ApplyTranslationsButton.IsEnabled = !busy;
        SaveCurrentDictionaryButton.IsEnabled = !busy && _editor.CanSaveCurrentDictionary;
        SettingsMenuButton.IsEnabled = !busy;
        _editor.IsEnabled = !busy;
        if (busy && !string.IsNullOrWhiteSpace(busyTitle))
        {
            _editor.ShowBusyState(busyTitle, busyDescription ?? string.Empty);
        }
        else if (!busy)
        {
            _editor.ClearBusyProgress();
        }
    }

    private async void Extract_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetGameOutputLayout(out EraTransOutputLayout layout) ||
            !ActivateWorkspace(layout) ||
            !_editor.TryPrepareForDiskOperation("提取/更新字典"))
        {
            return;
        }

        string gameDirectory = layout.GameDirectory;
        string dictionaryDirectory = layout.DictionaryDirectory;
        string? selectedRelativePath = _editor.CurrentFileRelativePath ??
                                       _settings.LastDictionaryFileRelativePath;
        SetBusy(true, "正在提取字典", "正在扫描游戏脚本并生成字典文件，请稍候…");
        StatusText.Text = "正在提取字典，请稍候…";

        try
        {
            EraParserOptions options = _settings.ToParserOptions();
            var extractor = new DictionaryExtractor();
            DictionaryExtractionResult result;
            try
            {
                result = await Task.Run(() => extractor.Extract(
                    gameDirectory,
                    dictionaryDirectory,
                    options: options,
                    ignoreEncodingIssueFiles: false));
            }
            catch (InvalidDataException ex) when (GameFileEncodingPreflightException.TryExtract(ex, out GameFileEncodingPreflightException? preflight))
            {
                var dialog = new EncodingIssuesWindow(preflight!.Issues, "提取字典") { Owner = this };
                if (dialog.ShowDialog() != true || !dialog.IgnoreAndContinue)
                {
                    StatusText.Text = "已取消提取，请先处理异常编码的文件";
                    return;
                }

                StatusText.Text = $"正在忽略 {preflight.Issues.Count} 个编码异常文件并继续提取…";
                result = await Task.Run(() => extractor.Extract(
                    gameDirectory,
                    dictionaryDirectory,
                    options: options,
                    ignoreEncodingIssueFiles: true));
            }

            int updatedContextCount = result.UpdatedContextFiles?.Count ?? 0;
            int groupingFileCount = result.UpdatedGroupingFiles?.Count ?? 0;
            StatusText.Text =
                $"字典提取完成：新建 {result.GeneratedFiles.Count} 个 JSON 文件，补充上下文 {updatedContextCount} 个文件，生成/更新 ERB AI 分组文件 {groupingFileCount} 个，{result.Warnings.Count} 条警告。";

            string message =
                $"新建 JSON 文件：{result.GeneratedFiles.Count}\n" +
                $"补充上下文文件：{updatedContextCount}\n" +
                $"生成/更新 ERB AI 分组文件：{groupingFileCount}\n" +
                $"警告：{result.Warnings.Count}";
            if (result.Warnings.Count > 0)
                message += "\n\n" + string.Join("\n", result.Warnings.Take(8));

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
            SetBusy(false);
            // Extract may merge context into existing dictionaries; reload so the
            // editor's in-memory snapshot/hash reflects the new disk content.
            _editor.RefreshFiles(selectedRelativePath);
            SaveWorkspacePreferences();
        }
    }

    private async void ApplyTranslations_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetGameOutputLayout(out EraTransOutputLayout layout) ||
            !ActivateWorkspace(layout) ||
            !_editor.TryPrepareForDiskOperation("应用译文"))
        {
            return;
        }

        string gameDirectory = layout.GameDirectory;
        string dictionaryDirectory = layout.DictionaryDirectory;
        string workingCopyDirectory = layout.GameCopyDirectory;

        if (!Directory.Exists(dictionaryDirectory))
        {
            MessageBox.Show(this, "输出目录中没有可用字典。请先提取字典。",
                "字典目录无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SetBusy(true, "正在生成汉化副本", "正在复制原游戏并写入已保存译文，请稍候…");
        StatusText.Text = "正在生成汉化副本，请稍候…";

        try
        {
            var applier = new TranslationApplier();
            EraParserOptions options = _settings.ToParserOptions();
            TranslationApplyResult result;
            try
            {
                result = await Task.Run(() => applier.Apply(
                    gameDirectory,
                    dictionaryDirectory,
                    workingCopyDirectory,
                    options,
                    ignoreEncodingIssueFiles: false));
            }
            catch (InvalidDataException ex) when (GameFileEncodingPreflightException.TryExtract(ex, out GameFileEncodingPreflightException? preflight))
            {
                var dialog = new EncodingIssuesWindow(preflight!.Issues, "生成游戏副本并应用译文") { Owner = this };
                if (dialog.ShowDialog() != true || !dialog.IgnoreAndContinue)
                {
                    StatusText.Text = "已取消生成副本";
                    return;
                }

                StatusText.Text = $"正在忽略 {preflight.Issues.Count} 个编码异常文件并继续生成副本…";
                result = await Task.Run(() => applier.Apply(
                    gameDirectory,
                    dictionaryDirectory,
                    workingCopyDirectory,
                    options,
                    ignoreEncodingIssueFiles: true));
            }

            StatusText.Text =
                $"副本处理完成：完整复制 {result.CopiedFiles.Count} 个游戏文件，应用译文 {result.ModifiedFiles.Count} 个"
                + $"（精确位置 {result.SpanReplacedCount} 条 / 原文匹配 {result.FallbackReplacedCount} 条）"
                + $"{(result.SkippedByEncodingCount > 0 ? $"，编码不兼容跳过 {result.SkippedByEncodingCount} 条" : string.Empty)}"
                + $"，重命名 ERD {result.RenamedFiles.Count} 个，{result.Warnings.Count} 条警告。";

            string message =
                $"完整复制游戏文件：{result.CopiedFiles.Count}\n" +
                $"应用译文：{result.ModifiedFiles.Count}\n" +
                $"  精确位置替换：{result.SpanReplacedCount} 条\n" +
                $"  原文匹配替换：{result.FallbackReplacedCount} 条\n" +
                (result.SkippedByEncodingCount > 0
                    ? $"  编码不兼容跳过：{result.SkippedByEncodingCount} 条\n"
                    : string.Empty) +
                $"重命名 ERD：{result.RenamedFiles.Count}\n" +
                $"警告：{result.Warnings.Count}\n输出位置：{workingCopyDirectory}";
            if (result.Warnings.Count > 0)
                message += "\n\n" + string.Join("\n", result.Warnings.Take(8));

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
            SetBusy(false);
        }
    }

    private void OpenResultDirectoryIfEnabled(string directory)
    {
        if (!_settings.AutoOpenFolder || !Directory.Exists(directory))
            return;

        try
        {
            Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
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

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_isProcessing)
        {
            MessageBox.Show(
                this,
                "字典提取或游戏副本处理仍在进行。请等待当前操作完成后再关闭程序。",
                "操作进行中",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            e.Cancel = true;
            return;
        }

        if (!_editor.CanClose())
        {
            e.Cancel = true;
            return;
        }

        SaveWorkspacePreferences();
    }
}
