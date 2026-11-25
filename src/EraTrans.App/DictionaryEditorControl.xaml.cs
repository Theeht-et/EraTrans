using EraTrans.Core;
using EraTrans.Core.VisibleText;
using EraTrans.Core.WriteBack;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace EraTrans.App;

public partial class DictionaryEditorControl : UserControl
{
    private const int GroupPreviewMaxLines = 60;
    private const int GroupPreviewMaxLineLength = 240;
    private const int FileTreeAutoExpandFileLimit = 120;
    private const int FileTreeSearchDebounceMilliseconds = 180;

    private static readonly SolidColorBrush StatusCompletedBrush = CreateFrozenBrush("#2B8A3E");
    private static readonly SolidColorBrush StatusInProgressBrush = CreateFrozenBrush("#1971C2");
    private static readonly SolidColorBrush StatusNotStartedBrush = CreateFrozenBrush("#ADB5BD");
    private static readonly SolidColorBrush StatusEmptyBrush = CreateFrozenBrush("#CED4DA");
    private static readonly SolidColorBrush StatusPendingScanBrush = CreateFrozenBrush("#DEE2E6");

    private string _dictionaryDirectory = string.Empty;
    private string _gameDirectory = string.Empty;
    private EraTransUserSettings _settings;
    public event Action<string?>? CurrentFileChanged;
    public event Action<bool>? SaveAvailabilityChanged;
    public string? CurrentFileRelativePath => _currentFile?.RelativePath;
    public bool CanSaveCurrentDictionary => _document?.IsDirty == true && !_isTranslating;
    private Window OwnerWindow => Window.GetWindow(this) ?? Application.Current.MainWindow!;
    private readonly DictionaryEditorService _service = new();
    private readonly ErbTranslationGroupingService _groupingService = new();
    private readonly AiProviderSettingsStore _providerStore = new();
    private ErbTranslationGroupingIndex? _activeGroupingIndex;
    private string? _groupingFallbackReason;
    private Dictionary<string, ErbTranslationGroupEntry>? _groupLinksByKey;
    private Dictionary<string, ErbTranslationGroup>? _groupsById;
    private Dictionary<string, IReadOnlyList<string>>? _screenEntryKeysByScope;
    private SpanCatalog? _spanCatalog;
    private string? _spanCatalogDirectory;
    private Dictionary<string, DictionaryEditorEntry>? _entriesByKey;
    private IReadOnlyList<DictionaryEditorFile> _allDictionaryFiles = Array.Empty<DictionaryEditorFile>();
    private readonly Dictionary<string, DictionaryFileProgress> _progressByFullPath = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<FileTreeNodeViewModel> _treeRootNodes = Array.Empty<FileTreeNodeViewModel>();
    private readonly Dictionary<string, FileTreeNodeViewModel> _fileTreeNodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _fileTreeSearchDebounceTimer;
    private CancellationTokenSource? _progressScanCancellation;
    private string? _coverageReportPath;
    private readonly AiTranslationLogStore _translationLogStore = new();
    private readonly AiTranslationService _translationService;
    private DictionaryEditorDocument? _document;
    private DictionaryEditorFile? _currentFile;
    private ICollectionView? _entriesView;
    private CancellationTokenSource? _translationCancellation;
    private bool _changingFileSelection;
    private bool _suppressFileTreeFilterEvents;
    private bool _isTranslating;

    public DictionaryEditorControl(EraTransUserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;

        InitializeComponent();
        _fileTreeSearchDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(FileTreeSearchDebounceMilliseconds)
        };
        _fileTreeSearchDebounceTimer.Tick += FileTreeSearchDebounceTimer_Tick;
        EntryFilterComboBox.SelectedIndex = 0;
        _suppressFileTreeFilterEvents = true;
        FileTreeStatusFilterComboBox.SelectedIndex = 0;
        _suppressFileTreeFilterEvents = false;
        _translationService = new AiTranslationService(logStore: _translationLogStore);
        UpdateProviderStatus();
        SetEmptyState(
            "尚未打开工作区",
            "请先在上方选择原游戏目录和输出目录。");
    }

    public void OpenAiSettings()
    {
        if (_isTranslating)
        {
            MessageBox.Show(
                OwnerWindow,
                "AI 翻译正在进行中，请等待完成或取消后再修改 AI 服务设置。",
                "翻译进行中",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        AiSettingsButton_Click(this, new RoutedEventArgs());
    }

    public void ShowBusyState(string title, string description)
    {
        if (EmptyStateBorder.Visibility == Visibility.Visible)
        {
            SetEmptyState(title, description, showProgress: true);
        }
    }

    public void ClearBusyProgress()
    {
        EmptyStateProgressBar.Visibility = Visibility.Collapsed;
    }

    private static SolidColorBrush CreateFrozenBrush(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }

    public void UpdateSettings(EraTransUserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        UpdateDocumentStatus();
    }

    public bool SaveCurrentDictionary() => SaveCurrentDocument();

    public bool SwitchWorkspace(EraTransOutputLayout layout, string? preferredRelativePath)
    {
        ArgumentNullException.ThrowIfNull(layout);

        string dictionaryDirectory = Path.GetFullPath(layout.DictionaryDirectory);
        string gameDirectory = Path.GetFullPath(layout.GameDirectory);
        if (PathsEqual(_dictionaryDirectory, dictionaryDirectory) &&
            PathsEqual(_gameDirectory, gameDirectory))
        {
            if (!ResolvePendingDocument("重新打开工作区", reloadWhenDiscarded: true))
            {
                return false;
            }

            RefreshDictionaryFiles(preferredRelativePath ?? _currentFile?.RelativePath);
            return true;
        }

        if (!ResolvePendingDocument("切换工作区", reloadWhenDiscarded: false))
        {
            return false;
        }

        _dictionaryDirectory = dictionaryDirectory;
        _gameDirectory = gameDirectory;
        DictionaryDirectoryTextBlock.Text = _dictionaryDirectory;
        RefreshDictionaryFiles(preferredRelativePath);
        return true;
    }

    public bool TryPrepareForDiskOperation(string operationName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        return ResolvePendingDocument(operationName, reloadWhenDiscarded: true);
    }

    public bool CanClose()
    {
        if (_isTranslating)
        {
            MessageBox.Show(
                OwnerWindow,
                "AI 翻译仍在进行。请先取消并等待当前请求结束，再关闭程序。",
                "翻译任务进行中",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return false;
        }

        if (_document?.IsDirty != true)
        {
            return true;
        }

        MessageBoxResult choice = MessageBox.Show(
            OwnerWindow,
            "当前字典有未保存的更改。关闭程序前是否保存？",
            "存在未保存更改",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning);

        return choice switch
        {
            MessageBoxResult.Yes => SaveCurrentDocument(),
            MessageBoxResult.No => true,
            _ => false
        };
    }

    public void RefreshFiles(string? preferredRelativePath)
    {
        RefreshDictionaryFiles(preferredRelativePath);
    }

    private static bool PathsEqual(string first, string second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
        {
            return string.IsNullOrWhiteSpace(first) && string.IsNullOrWhiteSpace(second);
        }

        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
            StringComparison.OrdinalIgnoreCase);
    }

    private bool ResolvePendingDocument(string operationName, bool reloadWhenDiscarded)
    {
        if (_isTranslating)
        {
            MessageBox.Show(
                OwnerWindow,
                "AI 翻译仍在进行。请先取消并等待当前请求结束，再执行此操作。",
                "翻译任务进行中",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return false;
        }

        if (_document?.IsDirty != true)
        {
            return true;
        }

        MessageBoxResult choice = MessageBox.Show(
            OwnerWindow,
            $"当前字典有未保存的更改。{operationName}前是否保存？选择“否”将丢弃尚未保存的修改。",
            "存在未保存更改",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning);

        if (choice == MessageBoxResult.Cancel)
        {
            return false;
        }

        if (choice == MessageBoxResult.Yes)
        {
            return SaveCurrentDocument();
        }

        return !reloadWhenDiscarded ||
               _currentFile is null ||
               LoadDictionary(_currentFile);
    }

    private void SetEmptyState(string title, string description, bool showProgress = false)
    {
        EmptyStateTitleTextBlock.Text = title;
        EmptyStateDescriptionTextBlock.Text = description;
        EmptyStateProgressBar.Visibility = showProgress ? Visibility.Visible : Visibility.Collapsed;
        EmptyStateBorder.Visibility = Visibility.Visible;
    }

    private void ToolsMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (ToolsContextMenu is null)
            return;
        ToolsContextMenu.PlacementTarget = ToolsMenuButton;
        ToolsContextMenu.Placement = PlacementMode.Bottom;
        ToolsContextMenu.IsOpen = true;
    }

    private void RefreshDictionaryFiles(string? preferredRelativePath)
    {
        _progressScanCancellation?.Cancel();
        _progressScanCancellation?.Dispose();
        _progressScanCancellation = null;

        if (_document is not null)
        {
            _document.PropertyChanged -= Document_PropertyChanged;
        }

        _document = null;
        _currentFile = null;
        _entriesView = null;
        _activeGroupingIndex = null;
        _groupingFallbackReason = null;
        _groupLinksByKey = null;
        _groupsById = null;
        _screenEntryKeysByScope = null;
        _spanCatalog = null;
        _spanCatalogDirectory = null;
        _entriesByKey = null;
        _fileTreeSearchDebounceTimer.Stop();
        _allDictionaryFiles = Array.Empty<DictionaryEditorFile>();
        _progressByFullPath.Clear();
        _treeRootNodes = Array.Empty<FileTreeNodeViewModel>();
        _fileTreeNodes.Clear();
        EntriesDataGrid.ItemsSource = null;
        EntrySearchTextBox.Text = string.Empty;
        _suppressFileTreeFilterEvents = true;
        try
        {
            FileTreeSearchTextBox.Text = string.Empty;
            FileTreeStatusFilterComboBox.SelectedIndex = 0;
            FileTreeSearchPlaceholder.Visibility = Visibility.Visible;
        }
        finally
        {
            _suppressFileTreeFilterEvents = false;
        }
        EntryFilterComboBox.SelectedIndex = 0;
        EntryGroupTextBox.Text = string.Empty;
        EntryContextTextBox.Text = string.Empty;
        EntryGroupLabelTextBlock.Text = "同组条目";
        ScreenPreviewCheckBox.Visibility = Visibility.Collapsed;
        SelectedFileTextBlock.Text = "请选择左侧字典文件";
        WorkspaceProgressTextBlock.Text = string.Empty;
        WorkspaceProgressTextBlock.Visibility = Visibility.Collapsed;
        ProgressStatsMenuItem.IsEnabled = false;
        UpdateCoverageReportButton();
        UpdateDocumentStatus();
        UpdateTranslationButtons();

        _changingFileSelection = true;
        DictionaryFilesTreeView.ItemsSource = null;

        try
        {
            if (string.IsNullOrWhiteSpace(_dictionaryDirectory))
            {
                DictionaryDirectoryTextBlock.Text = "尚未设置字典目录";
                StatusTextBlock.Text = "请先选择原游戏目录和统一输出目录。";
                SetEmptyState("尚未打开工作区", "请先在上方选择原游戏目录和输出目录。");
                CurrentFileChanged?.Invoke(null);
                return;
            }

            DictionaryDirectoryTextBlock.Text = _dictionaryDirectory;
            UpdateCoverageReportButton();
            if (!Directory.Exists(_dictionaryDirectory))
            {
                StatusTextBlock.Text = "字典目录尚未创建；可以先提取字典。";
                SetEmptyState("尚未提取字典", "还没有字典文件，点击上方「提取/更新字典」开始提取。");
                CurrentFileChanged?.Invoke(null);
                return;
            }

            IReadOnlyList<DictionaryEditorFile> files = _service.ListDictionaryFiles(_dictionaryDirectory);
            _allDictionaryFiles = files;
            BuildDictionaryTree(files, expandAll: false);
            if (files.Count == 0)
            {
                StatusTextBlock.Text = "此目录及其子目录中没有找到 JSON 字典文件。";
                SetEmptyState("尚未找到字典文件", "目录中没有 JSON 字典，请点击上方「提取/更新字典」。");
                CurrentFileChanged?.Invoke(null);
                return;
            }

            DictionaryEditorFile? selectedFile = string.IsNullOrWhiteSpace(preferredRelativePath)
                ? null
                : files.FirstOrDefault(file => string.Equals(
                    NormalizeRelativePath(file.RelativePath),
                    NormalizeRelativePath(preferredRelativePath),
                    StringComparison.OrdinalIgnoreCase));
            selectedFile ??= files[0];

            SelectFileNode(selectedFile.FullPath);

            _changingFileSelection = false;
            LoadDictionary(selectedFile);
            _ = ScanWorkspaceProgressAsync(files, _dictionaryDirectory);
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = "读取字典目录失败。";
            SetEmptyState("读取字典目录失败", "请检查输出目录是否存在，以及当前用户是否具有读取权限。");
            CurrentFileChanged?.Invoke(null);
            MessageBox.Show(OwnerWindow, ex.Message, "读取失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _changingFileSelection = false;
        }
    }

    private async Task ScanWorkspaceProgressAsync(
        IReadOnlyList<DictionaryEditorFile> files,
        string expectedDirectory)
    {
        _progressScanCancellation?.Cancel();
        _progressScanCancellation?.Dispose();
        var cts = new CancellationTokenSource();
        _progressScanCancellation = cts;
        CancellationToken token = cts.Token;

        WorkspaceProgressTextBlock.Text = $"正在统计 {files.Count:N0} 个字典文件进度…";
        WorkspaceProgressTextBlock.Visibility = Visibility.Visible;

        List<DictionaryFileProgress> results;
        try
        {
            results = await Task.Run(() =>
            {
                var list = new List<DictionaryFileProgress>(files.Count);
                foreach (DictionaryEditorFile file in files)
                {
                    token.ThrowIfCancellationRequested();
                    list.Add(DictionaryProgressScanner.ScanFile(file));
                }

                return list;
            }, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (token.IsCancellationRequested || !PathsEqual(_dictionaryDirectory, expectedDirectory))
        {
            return;
        }

        _progressByFullPath.Clear();
        foreach (DictionaryFileProgress progress in results)
        {
            _progressByFullPath[progress.FullPath] = progress;
        }

        if (_currentFile is not null && _document is not null)
        {
            _progressByFullPath[_currentFile.FullPath] =
                DictionaryProgressScanner.FromDocument(_currentFile, _document);
        }

        ProgressStatsMenuItem.IsEnabled = _allDictionaryFiles.Count > 0 && !_isTranslating;
        UpdateWorkspaceProgressSummary();

        if (!string.Equals(SelectedFileTreeStatusTag, "all", StringComparison.Ordinal))
        {
            ApplyFileTreeFilters();
        }
        else
        {
            RefreshTreeProgressIndicators();
        }
    }

    private void UpdateCurrentFileProgressFromDocument()
    {
        if (_currentFile is null || _document is null)
        {
            return;
        }

        _progressByFullPath[_currentFile.FullPath] =
            DictionaryProgressScanner.FromDocument(_currentFile, _document);

        if (_progressByFullPath.Count > 0)
        {
            ProgressStatsMenuItem.IsEnabled = !_isTranslating;
            UpdateWorkspaceProgressSummary();
            UpdateSingleFileNodeProgress(_currentFile);
        }
    }

    private void UpdateWorkspaceProgressSummary()
    {
        if (_allDictionaryFiles.Count == 0 || _progressByFullPath.Count == 0)
        {
            WorkspaceProgressTextBlock.Visibility = Visibility.Collapsed;
            return;
        }

        DictionaryWorkspaceProgress summary = BuildCurrentWorkspaceProgress();
        WorkspaceProgressTextBlock.Text = summary.TotalActive == 0
            ? $"文件 {summary.TotalFiles:N0} · 暂无待翻译词条"
            : $"进度 {summary.TotalTranslated:N0} / {summary.TotalActive:N0}（{summary.CompletionPercent:0.0}%）· 完成 {summary.CompletedFiles:N0} / {summary.TotalFiles:N0} 文件";
        WorkspaceProgressTextBlock.Visibility = Visibility.Visible;
    }

    private DictionaryWorkspaceProgress BuildCurrentWorkspaceProgress()
    {
        var list = new List<DictionaryFileProgress>(_allDictionaryFiles.Count);
        foreach (DictionaryEditorFile file in _allDictionaryFiles)
        {
            if (_progressByFullPath.TryGetValue(file.FullPath, out DictionaryFileProgress? progress))
            {
                list.Add(progress);
            }
            else
            {
                DictionaryFileProgress scanned = DictionaryProgressScanner.ScanFile(file);
                _progressByFullPath[file.FullPath] = scanned;
                list.Add(scanned);
            }
        }

        return DictionaryProgressScanner.Aggregate(list);
    }

    private void RefreshTreeProgressIndicators()
    {
        bool hasProgress = _progressByFullPath.Count > 0;
        foreach (FileTreeNodeViewModel rootNode in _treeRootNodes)
        {
            RefreshNodeProgressRecursive(rootNode, hasProgress);
        }
    }

    private FolderProgressAccumulator RefreshNodeProgressRecursive(
        FileTreeNodeViewModel node,
        bool hasProgress)
    {
        if (!node.IsFolder && node.File is not null)
        {
            if (hasProgress && _progressByFullPath.TryGetValue(node.File.FullPath, out DictionaryFileProgress? progress))
            {
                node.StatusBrush = GetStatusBrush(progress.Status);
                node.ToolTipText = FormatFileToolTip(node.File, progress);
                return FolderProgressAccumulator.FromFile(progress);
            }

            node.StatusBrush = StatusPendingScanBrush;
            node.ToolTipText = node.File.RelativePath;
            return FolderProgressAccumulator.UnscannedFile;
        }

        FolderProgressAccumulator acc = default;
        foreach (FileTreeNodeViewModel child in node.Children)
        {
            acc = acc.Add(RefreshNodeProgressRecursive(child, hasProgress));
        }

        if (node.Folder is not null)
        {
            if (hasProgress)
            {
                DictionaryFolderProgress folderProgress = acc.ToFolderProgress();
                node.StatusBrush = GetStatusBrush(folderProgress.Status);
                node.ToolTipText = FormatFolderToolTip(node.Folder, folderProgress);
            }
            else
            {
                node.StatusBrush = StatusPendingScanBrush;
                node.ToolTipText = node.Folder.FullRelativePath;
            }
        }

        return acc;
    }

    private void UpdateSingleFileNodeProgress(DictionaryEditorFile file)
    {
        if (!_fileTreeNodes.TryGetValue(file.FullPath, out FileTreeNodeViewModel? fileNode))
        {
            return;
        }

        if (_progressByFullPath.TryGetValue(file.FullPath, out DictionaryFileProgress? progress))
        {
            fileNode.StatusBrush = GetStatusBrush(progress.Status);
            fileNode.ToolTipText = FormatFileToolTip(file, progress);
        }

        FileTreeNodeViewModel? ancestor = fileNode.Parent;
        while (ancestor is not null)
        {
            if (ancestor.Folder is not null)
            {
                DictionaryFolderProgress folderProgress =
                    DictionaryProgressScanner.AggregateFolder(ancestor.Folder, _progressByFullPath);
                ancestor.StatusBrush = GetStatusBrush(folderProgress.Status);
                ancestor.ToolTipText = FormatFolderToolTip(ancestor.Folder, folderProgress);
            }

            ancestor = ancestor.Parent;
        }
    }

    private readonly record struct FolderProgressAccumulator(
        int TotalFiles,
        int CompletedFiles,
        int ActiveCount,
        int TranslatedCount,
        int PendingCount)
    {
        public static FolderProgressAccumulator UnscannedFile =>
            new(TotalFiles: 1, CompletedFiles: 0, ActiveCount: 0, TranslatedCount: 0, PendingCount: 0);

        public static FolderProgressAccumulator FromFile(DictionaryFileProgress progress) =>
            new(
                TotalFiles: 1,
                CompletedFiles: progress.Status is DictionaryProgressStatus.Completed or DictionaryProgressStatus.Empty ? 1 : 0,
                ActiveCount: progress.ActiveCount,
                TranslatedCount: progress.TranslatedCount,
                PendingCount: progress.PendingCount);

        public FolderProgressAccumulator Add(FolderProgressAccumulator other) =>
            new(
                TotalFiles + other.TotalFiles,
                CompletedFiles + other.CompletedFiles,
                ActiveCount + other.ActiveCount,
                TranslatedCount + other.TranslatedCount,
                PendingCount + other.PendingCount);

        public DictionaryFolderProgress ToFolderProgress() =>
            new(TotalFiles, CompletedFiles, ActiveCount, TranslatedCount, PendingCount);
    }

    private static SolidColorBrush GetStatusBrush(DictionaryProgressStatus? status) => status switch
    {
        DictionaryProgressStatus.Completed => StatusCompletedBrush,
        DictionaryProgressStatus.InProgress => StatusInProgressBrush,
        DictionaryProgressStatus.NotStarted => StatusNotStartedBrush,
        DictionaryProgressStatus.Empty => StatusEmptyBrush,
        _ => StatusPendingScanBrush
    };

    private static string FormatFileToolTip(DictionaryEditorFile file, DictionaryFileProgress? progress)
    {
        if (progress is null)
        {
            return file.RelativePath;
        }

        if (progress.ReadFailed)
        {
            return $"{file.RelativePath}\n状态：读取失败";
        }

        if (progress.ActiveCount == 0)
        {
            return $"{file.RelativePath}\n状态：无需翻译（可编辑 {progress.EditableCount} · 已忽略 {progress.IgnoredCount}）";
        }

        return $"{file.RelativePath}\n已翻译 {progress.TranslatedCount} / {progress.ActiveCount}（{progress.CompletionPercent:0.0}%）· 待翻 {progress.PendingCount} · {progress.StatusLabel}";
    }

    private static string FormatFolderToolTip(DictionaryFileTreeFolder folder, DictionaryFolderProgress progress)
    {
        if (progress.ActiveCount == 0)
        {
            return $"{folder.FullRelativePath}\n文件 {progress.TotalFiles} · 暂无待翻译词条";
        }

        return $"{folder.FullRelativePath}\n已完成 {progress.CompletedFiles} / {progress.TotalFiles} 文件 · 词条 {progress.TranslatedCount} / {progress.ActiveCount}（{progress.CompletionPercent:0.0}%）";
    }

    private void ProgressStatsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_allDictionaryFiles.Count == 0)
        {
            return;
        }

        DictionaryWorkspaceProgress progress = BuildCurrentWorkspaceProgress();
        var window = new TranslationProgressWindow(_dictionaryDirectory, progress)
        {
            Owner = OwnerWindow
        };

        if (window.ShowDialog() == true &&
            !string.IsNullOrWhiteSpace(window.SelectedFileRelativePath))
        {
            NavigateToDictionaryFile(window.SelectedFileRelativePath);
        }
    }

    private void NavigateToDictionaryFile(string relativePath)
    {
        DictionaryEditorFile? target = _allDictionaryFiles.FirstOrDefault(file => string.Equals(
            NormalizeRelativePath(file.RelativePath),
            NormalizeRelativePath(relativePath),
            StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            return;
        }

        if (_currentFile is not null && PathsEqual(_currentFile.FullPath, target.FullPath))
        {
            SelectFileNode(target.FullPath);
            return;
        }

        if (!ResolvePendingDocument("切换字典文件", reloadWhenDiscarded: false))
        {
            return;
        }

        if (!_fileTreeNodes.ContainsKey(target.FullPath))
        {
            _fileTreeSearchDebounceTimer.Stop();
            _changingFileSelection = true;
            _suppressFileTreeFilterEvents = true;
            try
            {
                FileTreeSearchTextBox.Text = string.Empty;
                FileTreeStatusFilterComboBox.SelectedIndex = 0;
                FileTreeSearchPlaceholder.Visibility = Visibility.Visible;
                BuildDictionaryTree(_allDictionaryFiles, expandAll: false);
            }
            finally
            {
                _suppressFileTreeFilterEvents = false;
                _changingFileSelection = false;
            }
        }

        _changingFileSelection = true;
        try
        {
            SelectFileNode(target.FullPath);
        }
        finally
        {
            _changingFileSelection = false;
        }

        LoadDictionary(target);
    }

    private void UpdateCoverageReportButton()
    {
        _coverageReportPath = null;
        if (!string.IsNullOrWhiteSpace(_dictionaryDirectory))
        {
            string outputRoot = string.Equals(
                Path.GetFileName(Path.TrimEndingDirectorySeparator(_dictionaryDirectory)),
                "字典",
                StringComparison.OrdinalIgnoreCase)
                ? Directory.GetParent(_dictionaryDirectory)?.FullName ?? _dictionaryDirectory
                : _dictionaryDirectory;

            string primary = Path.Combine(outputRoot, "辅助数据", "visibletext-report.json");
            string legacy = Path.Combine(outputRoot, "AI上下文", "visibletext-report.json");
            if (File.Exists(primary))
            {
                _coverageReportPath = primary;
            }
            else if (File.Exists(legacy))
            {
                _coverageReportPath = legacy;
            }
        }

        CoverageReportMenuItem.Visibility = _coverageReportPath is null
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void CoverageReportButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_coverageReportPath) || !File.Exists(_coverageReportPath))
        {
            UpdateCoverageReportButton();
            return;
        }

        var window = new VisibleTextReportWindow(_coverageReportPath) { Owner = OwnerWindow };
        window.ShowDialog();
    }

    private string SelectedFileTreeStatusTag =>
        (FileTreeStatusFilterComboBox?.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";

    private void FileTreeSearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string filter = FileTreeSearchTextBox.Text.Trim();
        if (FileTreeSearchPlaceholder is not null)
        {
            FileTreeSearchPlaceholder.Visibility = filter.Length == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        if (_suppressFileTreeFilterEvents || _allDictionaryFiles.Count == 0)
        {
            return;
        }

        _fileTreeSearchDebounceTimer.Stop();
        _fileTreeSearchDebounceTimer.Start();
    }

    private void FileTreeSearchDebounceTimer_Tick(object? sender, EventArgs e)
    {
        _fileTreeSearchDebounceTimer.Stop();
        if (_allDictionaryFiles.Count == 0)
        {
            return;
        }

        ApplyFileTreeFilters();
    }

    private void FileTreeStatusFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressFileTreeFilterEvents || DictionaryFilesTreeView is null || _allDictionaryFiles.Count == 0)
        {
            return;
        }

        _fileTreeSearchDebounceTimer.Stop();
        ApplyFileTreeFilters();
    }

    private void ApplyFileTreeFilters()
    {
        string keyword = FileTreeSearchTextBox.Text.Trim();
        string statusTag = SelectedFileTreeStatusTag;

        IEnumerable<DictionaryEditorFile> query = _allDictionaryFiles;
        if (keyword.Length > 0)
        {
            query = query.Where(file => file.RelativePath.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.Equals(statusTag, "all", StringComparison.Ordinal) && _progressByFullPath.Count > 0)
        {
            query = query.Where(file =>
            {
                if (!_progressByFullPath.TryGetValue(file.FullPath, out DictionaryFileProgress? progress))
                {
                    return true;
                }

                return statusTag switch
                {
                    "incomplete" => progress.Status is DictionaryProgressStatus.NotStarted or DictionaryProgressStatus.InProgress,
                    "not_started" => progress.Status == DictionaryProgressStatus.NotStarted,
                    "in_progress" => progress.Status == DictionaryProgressStatus.InProgress,
                    "completed" => progress.Status == DictionaryProgressStatus.Completed,
                    _ => true
                };
            });
        }

        IReadOnlyList<DictionaryEditorFile> visibleFiles = query.ToArray();
        bool isFiltering = keyword.Length > 0 || !string.Equals(statusTag, "all", StringComparison.Ordinal);
        bool expandAll = isFiltering && visibleFiles.Count <= FileTreeAutoExpandFileLimit;

        _changingFileSelection = true;
        try
        {
            BuildDictionaryTree(visibleFiles, expandAll);
            if (_currentFile is not null)
            {
                SelectFileNode(_currentFile.FullPath);
            }
        }
        finally
        {
            _changingFileSelection = false;
        }
    }

    private static string NormalizeRelativePath(string path) =>
        path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);

    private static void ExpandParentFolders(FileTreeNodeViewModel node)
    {
        FileTreeNodeViewModel? parent = node.Parent;
        while (parent is not null)
        {
            parent.IsExpanded = true;
            parent = parent.Parent;
        }
    }

    private void SelectFileNode(string fullPath)
    {
        if (_fileTreeNodes.TryGetValue(fullPath, out FileTreeNodeViewModel? targetNode))
        {
            ExpandParentFolders(targetNode);
            targetNode.IsSelected = true;
        }
    }

    private void TreeViewItem_Selected(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, e.OriginalSource) && sender is TreeViewItem item)
        {
            item.BringIntoView();
        }
    }

    private void DictionaryFilesTreeView_SelectedItemChanged(
        object sender,
        RoutedPropertyChangedEventArgs<object> e)
    {
        if (_changingFileSelection ||
            _isTranslating ||
            e.NewValue is not FileTreeNodeViewModel selectedNode ||
            selectedNode.File is not { } selectedFile)
        {
            return;
        }

        if (!ResolvePendingDocument("切换字典文件", reloadWhenDiscarded: false))
        {
            RestoreFileSelection();
            return;
        }

        LoadDictionary(selectedFile);
    }

    private void BuildDictionaryTree(IReadOnlyList<DictionaryEditorFile> files, bool expandAll)
    {
        _fileTreeNodes.Clear();

        DictionaryFileTreeFolder root = DictionaryFileTreeBuilder.Build(files);
        bool hasProgress = _progressByFullPath.Count > 0;
        List<FileTreeNodeViewModel> rootNodes = BuildNodeChildren(root, parent: null, expandAll, hasProgress, out _);

        if (!expandAll && rootNodes.Count == 1 && rootNodes[0].IsFolder)
        {
            rootNodes[0].IsExpanded = true;
        }

        _treeRootNodes = rootNodes;
        DictionaryFilesTreeView.ItemsSource = _treeRootNodes;
    }

    private List<FileTreeNodeViewModel> BuildNodeChildren(
        DictionaryFileTreeFolder folder,
        FileTreeNodeViewModel? parent,
        bool expandAll,
        bool hasProgress,
        out FolderProgressAccumulator totalProgress)
    {
        // Folders always appear before files at every level, with single-child folder
        // chains compacted into one node by DictionaryFileTreeBuilder.
        var nodes = new List<FileTreeNodeViewModel>(folder.Folders.Count + folder.Files.Count);
        FolderProgressAccumulator acc = default;

        foreach (DictionaryFileTreeFolder subFolder in folder.Folders)
        {
            var childList = new List<FileTreeNodeViewModel>(subFolder.Folders.Count + subFolder.Files.Count);
            var folderNode = new FileTreeNodeViewModel(
                displayName: subFolder.DisplayName,
                toolTipText: subFolder.FullRelativePath,
                statusBrush: StatusPendingScanBrush,
                isFolder: true,
                parent: parent,
                children: childList,
                folder: subFolder,
                isExpanded: expandAll);

            List<FileTreeNodeViewModel> builtChildren = BuildNodeChildren(
                subFolder,
                folderNode,
                expandAll,
                hasProgress,
                out FolderProgressAccumulator subProgress);
            childList.AddRange(builtChildren);

            if (hasProgress)
            {
                DictionaryFolderProgress folderProgress = subProgress.ToFolderProgress();
                folderNode.StatusBrush = GetStatusBrush(folderProgress.Status);
                folderNode.ToolTipText = FormatFolderToolTip(subFolder, folderProgress);
            }

            acc = acc.Add(subProgress);
            nodes.Add(folderNode);
        }

        foreach (DictionaryEditorFile file in folder.Files)
        {
            string fileName = Path.GetFileName(
                file.RelativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar));
            Brush statusBrush = StatusPendingScanBrush;
            string toolTip = file.RelativePath;
            if (hasProgress && _progressByFullPath.TryGetValue(file.FullPath, out DictionaryFileProgress? fileProgress))
            {
                statusBrush = GetStatusBrush(fileProgress.Status);
                toolTip = FormatFileToolTip(file, fileProgress);
                acc = acc.Add(FolderProgressAccumulator.FromFile(fileProgress));
            }
            else
            {
                acc = acc.Add(FolderProgressAccumulator.UnscannedFile);
            }

            var fileNode = new FileTreeNodeViewModel(
                displayName: fileName,
                toolTipText: toolTip,
                statusBrush: statusBrush,
                isFolder: false,
                parent: parent,
                children: Array.Empty<FileTreeNodeViewModel>(),
                file: file);

            _fileTreeNodes[file.FullPath] = fileNode;
            nodes.Add(fileNode);
        }

        totalProgress = acc;
        return nodes;
    }

    private bool LoadDictionary(DictionaryEditorFile file)
    {
        try
        {
            DictionaryEditorDocument document = _service.Load(file.FullPath);
            if (_document is not null)
            {
                _document.PropertyChanged -= Document_PropertyChanged;
            }

            _document = document;
            _document.PropertyChanged += Document_PropertyChanged;
            _currentFile = file;
            _activeGroupingIndex = null;
            _groupingFallbackReason = null;
            _groupLinksByKey = null;
            _groupsById = null;
            _screenEntryKeysByScope = null;

            EnsureSpanCatalog();
            string outputRoot = Directory.GetParent(_dictionaryDirectory)?.FullName ?? _dictionaryDirectory;
            string sidecarPath = Path.Combine(
                outputRoot,
                "辅助数据",
                Path.ChangeExtension(file.RelativePath, ".groups.json"));
            string legacySidecarPath = Path.Combine(
                outputRoot,
                "AI上下文",
                Path.ChangeExtension(file.RelativePath, ".groups.json"));
            bool shouldLoadGrouping =
                file.RelativePath.StartsWith("ERB" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                File.Exists(sidecarPath) ||
                File.Exists(legacySidecarPath);

            if (shouldLoadGrouping)
            {
                if (_groupingService.TryLoad(
                        _dictionaryDirectory,
                        file.FullPath,
                        document.Entries.ToArray(),
                        _gameDirectory,
                        out ErbTranslationGroupingIndex? groupingIndex,
                        out string? reason))
                {
                    _activeGroupingIndex = groupingIndex;
                    PrepareGroupLookup(groupingIndex!, document.Entries);
                    ApplySemanticGroupDisplay(document.Entries, groupingIndex!);
                }
                else
                {
                    _groupingFallbackReason = reason ??
                        "分组文件不可用，本文件将使用现有翻译方式。";
                }
            }

            ApplyEntryMetadataAndValidation(file, document.Entries);

            bool isScreenPacked = string.Equals(
                _activeGroupingIndex?.Packing,
                VisibleTextGrouping.ScreenPacking,
                StringComparison.OrdinalIgnoreCase);
            ScreenPreviewCheckBox.Visibility = isScreenPacked
                ? Visibility.Visible
                : Visibility.Collapsed;

            SelectedFileTextBlock.Text = file.RelativePath;
            _entriesView = CollectionViewSource.GetDefaultView(document.Entries);
            _entriesView.Filter = FilterEntry;
            EntriesDataGrid.ItemsSource = _entriesView;
            EntrySearchTextBox.Text = string.Empty;
            EntryFilterComboBox.SelectedIndex = 0;
            if (document.Entries.Count > 0)
            {
                EntriesDataGrid.SelectedIndex = 0;
                UpdateContextPreview();
            }
            else
            {
                EntryContextTextBox.Text = "当前字典没有词条。";
                EntryGroupTextBox.Text = "当前字典没有词条。";
                EntryGroupLabelTextBlock.Text = "同组条目";
            }
            StatusTextBlock.Text = string.IsNullOrWhiteSpace(_groupingFallbackReason)
                ? "已加载 · 双击词条、右键或按 Ctrl+E 可打开编辑窗口"
                : $"字典已加载；分组文件不可用：{_groupingFallbackReason}";
            UpdateDocumentStatus();
            UpdateCurrentFileProgressFromDocument();
            UpdateTranslationButtons();
            EmptyStateBorder.Visibility = Visibility.Collapsed;
            CurrentFileChanged?.Invoke(file.RelativePath);
            return true;
        }
        catch (Exception ex)
        {
            _document = null;
            _currentFile = null;
            _entriesView = null;
            _entriesByKey = null;
            _groupLinksByKey = null;
            _groupsById = null;
            _screenEntryKeysByScope = null;
            EntriesDataGrid.ItemsSource = null;
            EntryContextTextBox.Text = string.Empty;
            EntryGroupTextBox.Text = string.Empty;
            EntryGroupLabelTextBlock.Text = "同组条目";
            ScreenPreviewCheckBox.Visibility = Visibility.Collapsed;
            SelectedFileTextBlock.Text = file.RelativePath;
            SummaryTextBlock.Text = "无法加载此字典";
            SaveAvailabilityChanged?.Invoke(false);
            StatusTextBlock.Text = "文件未被修改。请检查 JSON 格式。";
            SetEmptyState("无法加载此字典", "文件未被修改。请检查 JSON 格式，修复后可重新选择该文件。");
            CurrentFileChanged?.Invoke(null);
            MessageBox.Show(OwnerWindow, ex.Message, "字典加载失败", MessageBoxButton.OK, MessageBoxImage.Error);
            RestoreFileSelection();
            return false;
        }
    }

    private void ApplyEntryMetadataAndValidation(
        DictionaryEditorFile file,
        IReadOnlyList<DictionaryEditorEntry> entries)
    {
        string normalizedRelative = file.RelativePath.Replace('\\', '/');
        bool isScriptFolder =
            normalizedRelative.StartsWith("ERB/", StringComparison.OrdinalIgnoreCase) ||
            normalizedRelative.StartsWith("ERH/", StringComparison.OrdinalIgnoreCase);

        foreach (DictionaryEditorEntry entry in entries)
        {
            string? kind = null;
            string? command = null;
            bool hasSpan = false;

            if (_spanCatalog is not null &&
                _spanCatalog.FileCount > 0 &&
                _spanCatalog.TryGetInfo(file.RelativePath, entry.Key, out SpanInfo spanInfo))
            {
                hasSpan = true;
                kind = spanInfo.Kind;
                command = spanInfo.Command;
            }

            if ((string.IsNullOrEmpty(kind) || string.IsNullOrEmpty(command)) &&
                _groupLinksByKey is not null &&
                _groupLinksByKey.TryGetValue(entry.Key, out ErbTranslationGroupEntry? link))
            {
                if (string.IsNullOrEmpty(kind))
                    kind = link.Kind;
                if (string.IsNullOrEmpty(command))
                    command = string.IsNullOrEmpty(link.Command) ? link.OutputType : link.Command;
            }

            string typeLabel = TranslationPreValidator.DescribeEntryType(kind, command);
            bool isScriptEntry = hasSpan ||
                (isScriptFolder && entry.Key.StartsWith("文本", StringComparison.Ordinal));
            entry.SetEntryMetadata(typeLabel, isScriptEntry);
        }
    }

    /// <summary>
    /// Builds the lookups used by the on-demand group/screen preview once per file.
    /// </summary>
    private void PrepareGroupLookup(
        ErbTranslationGroupingIndex index,
        IReadOnlyList<DictionaryEditorEntry> entries)
    {
        _groupLinksByKey = index.Entries.ToDictionary(
            entry => entry.Key, StringComparer.Ordinal);
        _groupsById = index.Groups.ToDictionary(
            group => group.Id, StringComparer.Ordinal);
        _entriesByKey = entries.ToDictionary(
            entry => entry.Key, StringComparer.Ordinal);

        if (string.Equals(index.Packing, VisibleTextGrouping.ScreenPacking, StringComparison.OrdinalIgnoreCase))
        {
            var byScope = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (ErbTranslationGroupEntry groupEntry in index.Entries.OrderBy(e => e.Sequence))
            {
                string scope = groupEntry.ControlScopeId ?? string.Empty;
                if (!byScope.TryGetValue(scope, out List<string>? keys))
                {
                    keys = new List<string>();
                    byScope[scope] = keys;
                }

                keys.Add(groupEntry.Key);
            }

            _screenEntryKeysByScope = byScope.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<string>)pair.Value,
                StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Sets the screen/group label. In V2 (screen packing), displays "屏 01 · 对白";
    /// in V1, keeps "对白 #000001".
    /// </summary>
    private static void ApplySemanticGroupDisplay(
        IReadOnlyList<DictionaryEditorEntry> dictionaryEntries,
        ErbTranslationGroupingIndex index)
    {
        var entriesByKey = dictionaryEntries.ToDictionary(entry => entry.Key, StringComparer.Ordinal);
        bool isScreenPacked = string.Equals(
            index.Packing,
            VisibleTextGrouping.ScreenPacking,
            StringComparison.OrdinalIgnoreCase);

        var screenOrdinals = new Dictionary<string, int>(StringComparer.Ordinal);
        if (isScreenPacked)
        {
            int nextOrdinal = 1;
            foreach (ErbTranslationGroupEntry entry in index.Entries.OrderBy(e => e.Sequence))
            {
                string scope = entry.ControlScopeId ?? string.Empty;
                if (!screenOrdinals.ContainsKey(scope))
                {
                    screenOrdinals[scope] = nextOrdinal++;
                }
            }
        }

        foreach (ErbTranslationGroup group in index.Groups)
        {
            string typeText = DescribeGroupType(group.Type);
            string label = isScreenPacked &&
                screenOrdinals.TryGetValue(group.ControlScopeId ?? string.Empty, out int screenNumber)
                ? $"屏 {screenNumber:D2} · {typeText}"
                : $"{typeText} {group.Id.Replace("group_", "#", StringComparison.Ordinal)}";

            foreach (string key in group.EntryKeys)
            {
                if (entriesByKey.TryGetValue(key, out DictionaryEditorEntry? entry))
                    entry.SetSemanticGroupInfo(label, string.Empty);
            }
        }
    }

    private static string DescribeGroupType(string type) => type switch
    {
        "dialogue" => "对白",
        "menu" => "菜单",
        "branch" => "分支",
        "condition" => "条件",
        "html" => "界面",
        _ => "语义"
    };

    /// <summary>Preview of the selected entry's group or full screen, built only when asked for.</summary>
    private string BuildGroupPreview(string key, bool wholeScreen)
    {
        if (_groupsById is null || _groupLinksByKey is null)
            return string.Empty;
        if (!_groupLinksByKey.TryGetValue(key, out ErbTranslationGroupEntry? link))
            return string.Empty;

        IReadOnlyList<string> memberKeys;
        if (wholeScreen &&
            _screenEntryKeysByScope is not null &&
            _screenEntryKeysByScope.TryGetValue(link.ControlScopeId ?? string.Empty, out IReadOnlyList<string>? screenKeys))
        {
            memberKeys = screenKeys;
        }
        else if (_groupsById.TryGetValue(link.GroupId, out ErbTranslationGroup? group))
        {
            memberKeys = group.EntryKeys;
        }
        else
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder();
        int written = 0;
        foreach (string memberKey in memberKeys)
        {
            if (written >= GroupPreviewMaxLines)
            {
                builder.AppendLine(wholeScreen ? "……（本屏还有更多行）" : "……（本组还有更多行）");
                break;
            }

            if (_entriesByKey is null ||
                !_entriesByKey.TryGetValue(memberKey, out DictionaryEditorEntry? member))
            {
                continue;
            }

            string original = member.Original.Length > GroupPreviewMaxLineLength
                ? member.Original[..GroupPreviewMaxLineLength] + "…"
                : member.Original;
            if (builder.Length > 0)
                builder.AppendLine();

            string marker = string.Equals(memberKey, key, StringComparison.Ordinal) ? "▶ " : "  ";
            builder.Append(marker)
                .Append('[')
                .Append(member.DisplayKey)
                .Append("] ")
                .Append(original);
            written++;
        }

        return builder.ToString();
    }

    private string SelectedFilterTag =>
        (EntryFilterComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";

    private bool FilterEntry(object item)
    {
        if (item is not DictionaryEditorEntry entry)
        {
            return false;
        }

        switch (SelectedFilterTag)
        {
            case "pending":
                if (!entry.IsEditable || entry.Stage != 0)
                    return false;
                break;
            case "translated":
                if (entry.Stage <= 0)
                    return false;
                break;
            case "warning":
                if (!entry.HasValidationWarning)
                    return false;
                break;
            case "ui":
                if (entry.EntryTypeLabel is not ("按钮" or "界面" or "悬停提示"))
                    return false;
                break;
        }

        string search = EntrySearchTextBox.Text.Trim();
        if (search.Length == 0)
        {
            return true;
        }

        return Contains(entry.Key, search) ||
               Contains(entry.Original, search) ||
               Contains(entry.Translation, search) ||
               Contains(entry.Context, search) ||
               Contains(entry.SemanticGroupLabel, search) ||
               Contains(entry.EntryTypeLabel, search);
    }

    private static bool Contains(string source, string search) =>
        source.Contains(search, StringComparison.OrdinalIgnoreCase);

    private void EntrySearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (EntrySearchPlaceholder is not null)
        {
            EntrySearchPlaceholder.Visibility = EntrySearchTextBox.Text.Length == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        if (EntriesDataGrid is null)
            return;
        _entriesView?.Refresh();
        UpdateContextPreview();
    }

    private void EntryFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EntriesDataGrid is null)
            return;
        _entriesView?.Refresh();
        UpdateContextPreview();
    }

    private void ScreenPreviewCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (EntriesDataGrid is null)
            return;
        UpdateContextPreview();
    }

    private void EntriesDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateContextPreview();
    }

    private void EntriesDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject) is { Item: DictionaryEditorEntry entry })
        {
            OpenTranslationEditDialog(entry);
        }
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T match)
            {
                return match;
            }

            child = child is Visual
                ? VisualTreeHelper.GetParent(child)
                : LogicalTreeHelper.GetParent(child);
        }

        return null;
    }

    private DictionaryEditorEntry? GetActiveEntry() =>
        EntriesDataGrid.SelectedItem as DictionaryEditorEntry ??
        EntriesDataGrid.SelectedItems
            .OfType<DictionaryEditorEntry>()
            .FirstOrDefault();

    private DictionaryEditorEntry[] GetSelectedEntries() =>
        EntriesDataGrid.SelectedItems
            .OfType<DictionaryEditorEntry>()
            .Distinct()
            .ToArray();

    private void EditSelectedTranslationMenuItem_Click(object sender, RoutedEventArgs e)
    {
        DictionaryEditorEntry? entry = GetActiveEntry();
        if (entry is not null)
        {
            OpenTranslationEditDialog(entry);
        }
    }

    private void SetSelectedStageMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_isTranslating ||
            _document is null ||
            sender is not MenuItem { Tag: string rawTag } ||
            !int.TryParse(rawTag, NumberStyles.Integer, CultureInfo.InvariantCulture, out int targetStage))
        {
            return;
        }

        DictionaryEditorEntry[] editableEntries = GetSelectedEntries()
            .Where(entry => entry.IsEditable)
            .ToArray();
        if (editableEntries.Length == 0)
        {
            StatusTextBlock.Text = "选中项中没有可修改状态的词条。";
            return;
        }

        int changedCount = 0;
        foreach (DictionaryEditorEntry entry in editableEntries)
        {
            if (entry.Stage != targetStage)
            {
                entry.Stage = targetStage;
                changedCount++;
            }
        }

        if (changedCount == 0)
        {
            return;
        }

        UpdateDocumentStatus();
        UpdateCurrentFileProgressFromDocument();
        string stageLabel = targetStage switch
        {
            -1 => "忽略",
            0 => "未翻译",
            1 => "已翻译",
            _ => targetStage.ToString(CultureInfo.InvariantCulture)
        };
        StatusTextBlock.Text = $"已将 {changedCount} 条词条状态设为「{stageLabel}」；保存当前字典后才会写入文件。";
    }

    private void OpenTranslationEditDialog(DictionaryEditorEntry entry)
    {
        if (_isTranslating || !entry.IsEditable)
        {
            return;
        }

        var dialog = new TranslationEditWindow(
            entry.Key,
            entry.Original,
            entry.Translation,
            entry.Stage,
            entry.StageOptions,
            entry.IsScriptEntry)
        {
            Owner = OwnerWindow
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        bool changed = !string.Equals(entry.Translation, dialog.EditedTranslation, StringComparison.Ordinal) ||
                       entry.Stage != dialog.EditedStage;
        if (!changed)
        {
            return;
        }

        entry.Translation = dialog.EditedTranslation;
        entry.Stage = dialog.EditedStage;
        StatusTextBlock.Text = "译文与状态已更新到编辑器；保存当前字典后才会写入文件。";
        UpdateDocumentStatus();
        UpdateCurrentFileProgressFromDocument();
    }

    /// <summary>Loads (once per dictionary) the optional span sidecar that records source lines.</summary>
    private void EnsureSpanCatalog()
    {
        if (_spanCatalog is not null &&
            string.Equals(_spanCatalogDirectory, _dictionaryDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _spanCatalogDirectory = _dictionaryDirectory;
        try
        {
            _spanCatalog = SpanCatalog.LoadFromSidecar(_dictionaryDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _spanCatalog = SpanCatalog.Empty;
        }
    }

    /// <summary>Human readable origin of one entry: which line and command produced it.</summary>
    private string BuildEntrySourceLabel(DictionaryEditorEntry entry)
    {
        if (_spanCatalog is null || _currentFile is null || _spanCatalog.FileCount == 0)
        {
            return string.Empty;
        }

        string relativePath = _currentFile.RelativePath;
        if (!_spanCatalog.TryGetInfo(relativePath, entry.Key, out SpanInfo info))
        {
            return string.Empty;
        }

        string command = info.Command.Length == 0 ? string.Empty : " " + info.Command;
        return info.Line > 0 ? $"第 {info.Line} 行{command}" : command.Trim();
    }

    private void UpdateContextPreview()
    {
        if (EntriesDataGrid is null ||
            EntryGroupLabelTextBlock is null ||
            EntryGroupTextBox is null ||
            EntryContextTextBox is null)
        {
            return;
        }

        // In FullRow mode, SelectedItem tracks the active row the user is viewing.
        DictionaryEditorEntry? entry = GetActiveEntry();

        bool wholeScreen = ScreenPreviewCheckBox.Visibility == Visibility.Visible &&
                           ScreenPreviewCheckBox.IsChecked == true;
        string scopeTitle = wholeScreen ? "同屏条目" : "同组条目";

        string sourceLabel = entry is null ? string.Empty : BuildEntrySourceLabel(entry);
        EntryGroupLabelTextBlock.Text = entry is null
            ? scopeTitle
            : string.IsNullOrWhiteSpace(entry.SemanticGroupLabel)
                ? sourceLabel.Length == 0 ? scopeTitle : $"{scopeTitle} · {sourceLabel}"
                : sourceLabel.Length == 0
                    ? $"{scopeTitle} · {entry.SemanticGroupLabel}"
                    : $"{scopeTitle} · {entry.SemanticGroupLabel} · {sourceLabel}";

        string groupPreview = entry is null ? string.Empty : BuildGroupPreview(entry.Key, wholeScreen);
        EntryGroupTextBox.Text = string.IsNullOrWhiteSpace(groupPreview)
            ? "当前词条没有可用的 ERB 语义分组信息。"
            : groupPreview;

        EntryContextTextBox.Text = entry is null
            ? "选择一个词条查看上下文。"
            : string.IsNullOrWhiteSpace(entry.Context)
                ? "此词条在字典文件中没有保存上下文。"
                : entry.Context;
    }

    private void Document_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        UpdateDocumentStatus();
        if (e.PropertyName == nameof(DictionaryEditorDocument.SummaryText))
        {
            UpdateCurrentFileProgressFromDocument();
        }
    }

    private void UpdateDocumentStatus()
    {
        if (_document is null)
        {
            SummaryTextBlock.Text = "尚未加载字典";
            SaveAvailabilityChanged?.Invoke(false);
            return;
        }

        string groupingStatus = _activeGroupingIndex is null
            ? string.Empty
            : " · 已加载分组文件，将按原文顺序整段翻译";

        SummaryTextBlock.Text = _document.SummaryText + groupingStatus +
            (_document.IsDirty ? " · 有未保存更改" : " · 已保存");
        SaveAvailabilityChanged?.Invoke(CanSaveCurrentDictionary);
    }

    private void UpdateTranslationButtons()
    {
        bool hasDocument = _document is not null;
        ProgressStatsMenuItem.IsEnabled = !_isTranslating && _allDictionaryFiles.Count > 0;
        TranslateSelectedButton.IsEnabled = hasDocument && !_isTranslating;
        TranslatePendingButton.IsEnabled = hasDocument && !_isTranslating;
        CancelTranslationButton.IsEnabled = _isTranslating;
        CancelTranslationButton.Visibility = _isTranslating ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateProviderStatus()
    {
        try
        {
            AiProviderProfile? profile = _providerStore.Load().ActiveProvider;
            AiProviderStatusTextBlock.Text = profile is null
                ? "AI 翻译未配置"
                : $"当前服务：{profile.Name} / {profile.Model}";
        }
        catch
        {
            AiProviderStatusTextBlock.Text = "AI 配置不可读取";
        }
    }

    private bool SaveCurrentDocument()
    {
        if (_document is null)
        {
            return true;
        }

        try
        {
            _document.Save();
            StatusTextBlock.Text = "保存成功。原始字段和其他 JSON 字段均已保留。";
            UpdateDocumentStatus();
            UpdateCurrentFileProgressFromDocument();
            return true;
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = "保存失败；原字典文件未被本次操作覆盖。";
            MessageBox.Show(OwnerWindow, ex.Message, "字典保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void AiSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new AiProviderSettingsWindow { Owner = OwnerWindow };
        if (window.ShowDialog() == true)
        {
            UpdateProviderStatus();
            StatusTextBlock.Text = "AI 服务商配置已保存。";
        }
    }

    private void AiLogsButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new AiTranslationLogWindow(_translationLogStore) { Owner = OwnerWindow };
        window.ShowDialog();
    }

    private void TranslateSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        DictionaryEditorEntry[] selected = GetSelectedEntries()
            .Where(IsPendingTranslation)
            .ToArray();

        if (selected.Length == 0)
        {
            MessageBox.Show(OwnerWindow,
                "请先选中至少一个尚未填写译文、状态为未翻译的非变量词条。",
                "没有可翻译的选中项",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        _ = TranslateEntriesAsync(selected);
    }

    private void TranslatePendingButton_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null)
            return;

        DictionaryEditorEntry[] pending = _document.Entries
            .Where(IsPendingTranslation)
            .ToArray();

        if (pending.Length == 0)
        {
            MessageBox.Show(OwnerWindow,
                "当前字典没有可批量翻译的词条。已有译文、变量和已忽略词条不会被覆盖。",
                "没有待翻译项",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        MessageBoxResult confirm = MessageBox.Show(OwnerWindow,
            $"将为当前字典中的 {pending.Length} 个未翻译词条按原文顺序请求 AI 翻译。译文只回填编辑器，不会自动保存。是否继续？",
            "确认批量翻译",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm == MessageBoxResult.Yes)
        {
            _ = TranslateEntriesAsync(pending);
        }
    }

    private static bool IsPendingTranslation(DictionaryEditorEntry entry) =>
        entry.IsEditable &&
        entry.Stage == 0 &&
        string.IsNullOrWhiteSpace(entry.Translation) &&
        !string.IsNullOrWhiteSpace(entry.Original);

    /// <summary>
    /// Every translation run goes through the ordered window pipeline; the rules
    /// for how the dictionary is grouped live in <c>DictionaryEditorControl.Windows</c>.
    /// </summary>
    private Task TranslateEntriesAsync(IReadOnlyList<DictionaryEditorEntry> entries) =>
        TranslateWindowsAsync(entries);

    private void SetTranslationControlsEnabled(bool enabled)
    {
        DictionaryFilesTreeView.IsEnabled = enabled;
        FileTreeSearchTextBox.IsEnabled = enabled;
        FileTreeStatusFilterComboBox.IsEnabled = enabled;
        EntriesDataGrid.IsEnabled = enabled;
        ProgressStatsMenuItem.IsEnabled = enabled && _allDictionaryFiles.Count > 0;
        TranslateSelectedButton.IsEnabled = enabled && _document is not null;
        TranslatePendingButton.IsEnabled = enabled && _document is not null;
        CancelTranslationButton.IsEnabled = !enabled;
        CancelTranslationButton.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
    }

    private void CancelTranslationButton_Click(object sender, RoutedEventArgs e)
    {
        _translationCancellation?.Cancel();
        StatusTextBlock.Text = "正在取消当前 AI 请求……";
    }

    private void RestoreFileSelection()
    {
        _changingFileSelection = true;
        if (_currentFile is not null &&
            _fileTreeNodes.TryGetValue(_currentFile.FullPath, out FileTreeNodeViewModel? fileNode))
        {
            fileNode.IsSelected = true;
        }
        _changingFileSelection = false;
    }

    private void Control_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S)
        {
            SaveCurrentDocument();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
        {
            EntrySearchTextBox.Focus();
            EntrySearchTextBox.SelectAll();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.E)
        {
            DictionaryEditorEntry? entry = GetActiveEntry();
            if (entry is not null)
            {
                OpenTranslationEditDialog(entry);
                e.Handled = true;
            }
        }
    }
}
