using EraTrans.Core;
using EraTrans.Core.WriteBack;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Controls;
using System.Windows.Input;

namespace EraTrans.App;

public partial class DictionaryEditorControl : UserControl
{
    private const int GroupPreviewMaxLines = 60;
    private const int GroupPreviewMaxLineLength = 240;

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
    private SpanCatalog? _spanCatalog;
    private string? _spanCatalogDirectory;
    private Dictionary<string, DictionaryEditorEntry>? _entriesByKey;
    private readonly AiTranslationLogStore _translationLogStore = new();
    private readonly AiTranslationService _translationService;
    private readonly Dictionary<string, TreeViewItem> _fileTreeItems = new(StringComparer.OrdinalIgnoreCase);
    private DictionaryEditorDocument? _document;
    private DictionaryEditorFile? _currentFile;
    private ICollectionView? _entriesView;
    private CancellationTokenSource? _translationCancellation;
    private bool _changingFileSelection;
    private bool _isTranslating;

    public DictionaryEditorControl(EraTransUserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;

        InitializeComponent();
        _translationService = new AiTranslationService(logStore: _translationLogStore);
        UpdateProviderStatus();
        SetEmptyState(
            "尚未打开工作区",
            "选择原游戏目录和统一输出目录，然后打开工作区或提取字典。");
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

        if (!CommitActiveGridEdits())
        {
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

        if (!CommitActiveGridEdits())
        {
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

    private bool CommitActiveGridEdits() =>
        EntriesDataGrid.CommitEdit(DataGridEditingUnit.Cell, true) &&
        EntriesDataGrid.CommitEdit(DataGridEditingUnit.Row, true);

    private void SetEmptyState(string title, string description)
    {
        EmptyStateTitleTextBlock.Text = title;
        EmptyStateDescriptionTextBlock.Text = description;
        EmptyStateBorder.Visibility = Visibility.Visible;
    }

    private void RefreshDictionaryFiles(string? preferredRelativePath)
    {
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
        _entriesByKey = null;
        EntriesDataGrid.ItemsSource = null;
        EntrySearchTextBox.Text = string.Empty;
        EntryGroupTextBox.Text = string.Empty;
        EntryContextTextBox.Text = string.Empty;
        EntryGroupLabelTextBlock.Text = "同组条目";
        SelectedFileTextBlock.Text = "请选择左侧字典文件";
        UpdateDocumentStatus();
        UpdateTranslationButtons();

        _changingFileSelection = true;
        DictionaryFilesTreeView.Items.Clear();
        _fileTreeItems.Clear();

        try
        {
            if (string.IsNullOrWhiteSpace(_dictionaryDirectory))
            {
                DictionaryDirectoryTextBlock.Text = "尚未设置字典目录";
                StatusTextBlock.Text = "请先选择原游戏目录和统一输出目录。";
                SetEmptyState("尚未打开工作区", "选择原游戏目录和统一输出目录，然后打开工作区或提取字典。");
                CurrentFileChanged?.Invoke(null);
                return;
            }

            DictionaryDirectoryTextBlock.Text = _dictionaryDirectory;
            if (!Directory.Exists(_dictionaryDirectory))
            {
                StatusTextBlock.Text = "字典目录尚未创建；可以先提取字典。";
                SetEmptyState("尚未提取字典", "当前工作区还没有字典文件。点击主窗口的“提取/更新字典”开始提取。");
                CurrentFileChanged?.Invoke(null);
                return;
            }

            IReadOnlyList<DictionaryEditorFile> files = _service.ListDictionaryFiles(_dictionaryDirectory);
            BuildDictionaryTree(files);
            if (files.Count == 0)
            {
                StatusTextBlock.Text = "此目录及其子目录中没有找到 JSON 字典文件。";
                SetEmptyState("尚未找到字典文件", "此工作区目前没有 JSON 字典。可以从主窗口提取字典，提取后文件会自动出现在这里。");
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

            if (_fileTreeItems.TryGetValue(selectedFile.FullPath, out TreeViewItem? selectedNode))
            {
                selectedNode.IsSelected = true;
                ExpandParentFolders(selectedNode);
                selectedNode.BringIntoView();
            }

            _changingFileSelection = false;
            LoadDictionary(selectedFile);
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

    private static string NormalizeRelativePath(string path) =>
        path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);

    private static void ExpandParentFolders(TreeViewItem item)
    {
        ItemsControl? parent = ItemsControl.ItemsControlFromItemContainer(item);
        while (parent is TreeViewItem folder)
        {
            folder.IsExpanded = true;
            parent = ItemsControl.ItemsControlFromItemContainer(folder);
        }
    }

    private void DictionaryFilesTreeView_SelectedItemChanged(
        object sender,
        RoutedPropertyChangedEventArgs<object> e)
    {
        if (_changingFileSelection ||
            _isTranslating ||
            e.NewValue is not TreeViewItem selectedNode ||
            selectedNode.Tag is not DictionaryEditorFile selectedFile)
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

    private static TextBlock CreateTreeHeader(string text, string toolTip)
    {
        return new TextBlock
        {
            Text = text,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = toolTip
        };
    }

    private void BuildDictionaryTree(IReadOnlyList<DictionaryEditorFile> files)
    {
        DictionaryFilesTreeView.Items.Clear();
        _fileTreeItems.Clear();

        var folders = new Dictionary<string, TreeViewItem>(StringComparer.OrdinalIgnoreCase);
        var folderChildren = new Dictionary<string, ItemCollection>(StringComparer.OrdinalIgnoreCase)
        {
            [string.Empty] = DictionaryFilesTreeView.Items
        };

        // Create every folder first. This guarantees folders appear before files at
        // every level, regardless of the relative names in the extracted dictionary.
        foreach (DictionaryEditorFile file in files)
        {
            string relativePath = file.RelativePath
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            string[] segments = relativePath.Split(
                Path.DirectorySeparatorChar,
                StringSplitOptions.RemoveEmptyEntries);

            string currentFolder = string.Empty;
            for (int index = 0; index < segments.Length - 1; index++)
            {
                currentFolder = currentFolder.Length == 0
                    ? segments[index]
                    : Path.Combine(currentFolder, segments[index]);

                if (!folders.TryGetValue(currentFolder, out TreeViewItem? folderNode))
                {
                    folderNode = new TreeViewItem
                    {
                        Header = CreateTreeHeader(segments[index], currentFolder),
                        IsExpanded = false,
                        FontWeight = FontWeights.SemiBold
                    };
                    folderChildren[currentFolder] = folderNode.Items;
                    folderChildren[Path.GetDirectoryName(currentFolder) ?? string.Empty]
                        .Add(folderNode);
                    folders.Add(currentFolder, folderNode);
                }
            }
        }

        // Add files only after the complete folder hierarchy has been created.
        foreach (DictionaryEditorFile file in files)
        {
            string relativePath = file.RelativePath
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            string[] segments = relativePath.Split(
                Path.DirectorySeparatorChar,
                StringSplitOptions.RemoveEmptyEntries);
            string parentFolder = segments.Length > 1
                ? Path.Combine(segments[..^1])
                : string.Empty;
            var fileNode = new TreeViewItem
            {
                Header = CreateTreeHeader(segments[^1], file.RelativePath),
                Tag = file
            };
            folderChildren[parentFolder].Add(fileNode);
            _fileTreeItems[file.FullPath] = fileNode;
        }
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

            EnsureSpanCatalog();
            string outputRoot = Directory.GetParent(_dictionaryDirectory)?.FullName ?? _dictionaryDirectory;
            string sidecarPath = Path.Combine(
                outputRoot,
                "AI上下文",
                Path.ChangeExtension(file.RelativePath, ".groups.json"));
            bool shouldLoadGrouping =
                file.RelativePath.StartsWith("ERB" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                File.Exists(sidecarPath);

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
            SelectedFileTextBlock.Text = file.RelativePath;
            _entriesView = CollectionViewSource.GetDefaultView(document.Entries);
            _entriesView.Filter = FilterEntry;
            EntriesDataGrid.ItemsSource = _entriesView;
            EntrySearchTextBox.Text = string.Empty;
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
                ? "字典已加载。"
                : $"字典已加载；分组文件不可用：{_groupingFallbackReason}";
            UpdateDocumentStatus();
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
            EntriesDataGrid.ItemsSource = null;
            EntryContextTextBox.Text = string.Empty;
            EntryGroupTextBox.Text = string.Empty;
            EntryGroupLabelTextBlock.Text = "同组条目";
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

    /// <summary>
    /// Builds the lookups used by the on-demand group preview once per file.
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
    }

    /// <summary>
    /// Sets only the group label. The member preview is built on demand, because
    /// pre-building it for every group of a large file dominated file switching.
    /// </summary>
    private static void ApplySemanticGroupDisplay(
        IReadOnlyList<DictionaryEditorEntry> dictionaryEntries,
        ErbTranslationGroupingIndex index)
    {
        var entriesByKey = dictionaryEntries.ToDictionary(entry => entry.Key, StringComparer.Ordinal);
        foreach (ErbTranslationGroup group in index.Groups)
        {
            string label = $"{DescribeGroupType(group.Type)} {group.Id.Replace("group_", "#", StringComparison.Ordinal)}";
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

    /// <summary>Preview of the selected entry's group, built only when asked for.</summary>
    private string BuildGroupPreview(string key)
    {
        if (_groupsById is null || _groupLinksByKey is null)
            return string.Empty;
        if (!_groupLinksByKey.TryGetValue(key, out ErbTranslationGroupEntry? link))
            return string.Empty;
        if (!_groupsById.TryGetValue(link.GroupId, out ErbTranslationGroup? group))
            return string.Empty;

        var builder = new System.Text.StringBuilder();
        int written = 0;
        foreach (string memberKey in group.EntryKeys)
        {
            if (written >= GroupPreviewMaxLines)
            {
                builder.AppendLine("……（本组还有更多行）");
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
            builder.Append(memberKey).Append("  ").Append(original);
            written++;
        }

        return builder.ToString();
    }

    private bool FilterEntry(object item)
    {
        if (item is not DictionaryEditorEntry entry)
        {
            return false;
        }

        string search = EntrySearchTextBox.Text.Trim();
        if (search.Length == 0)
        {
            return true;
        }

        return Contains(entry.Key, search) ||
               Contains(entry.Original, search) ||
               Contains(entry.Translation, search) ||
               Contains(entry.Context, search);
    }

    private static bool Contains(string source, string search) =>
        source.Contains(search, StringComparison.OrdinalIgnoreCase);

    private void EntrySearchTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _entriesView?.Refresh();
        UpdateContextPreview();
    }

    private void EntriesDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateContextPreview();
    }

    private void EntriesDataGrid_CurrentCellChanged(object? sender, EventArgs e)
    {
        UpdateContextPreview();
    }

    private void EditTranslationButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isTranslating ||
            sender is not FrameworkElement { DataContext: DictionaryEditorEntry entry } ||
            !entry.IsEditable)
        {
            return;
        }

        if (!EntriesDataGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
            !EntriesDataGrid.CommitEdit(DataGridEditingUnit.Row, true))
        {
            return;
        }

        var dialog = new TranslationEditWindow(entry.Key, entry.Original, entry.Translation)
        {
            Owner = OwnerWindow
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        entry.Translation = dialog.EditedTranslation;
        StatusTextBlock.Text = "译文已更新到编辑器；保存当前字典后才会写入文件。";
        UpdateDocumentStatus();
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
        // CurrentCell tracks the entry the user is actively viewing. SelectedCells can
        // contain cells from a previous multi-selection, so preferring its first item
        // can leave the preview showing stale or empty context after navigation.
        DictionaryEditorEntry? entry =
            EntriesDataGrid.CurrentCell.Item as DictionaryEditorEntry ??
            EntriesDataGrid.SelectedItem as DictionaryEditorEntry ??
            EntriesDataGrid.SelectedCells
                .Select(cell => cell.Item)
                .OfType<DictionaryEditorEntry>()
                .FirstOrDefault();

        string sourceLabel = entry is null ? string.Empty : BuildEntrySourceLabel(entry);
        EntryGroupLabelTextBlock.Text = entry is null
            ? "同组条目"
            : string.IsNullOrWhiteSpace(entry.SemanticGroupLabel)
                ? sourceLabel.Length == 0 ? "同组条目" : $"同组条目 · {sourceLabel}"
                : sourceLabel.Length == 0
                    ? $"同组条目 · {entry.SemanticGroupLabel}"
                    : $"同组条目 · {entry.SemanticGroupLabel} · {sourceLabel}";

        string groupPreview = entry is null ? string.Empty : BuildGroupPreview(entry.Key);
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
        AiSettingsButton.IsEnabled = !_isTranslating;
        TranslateSelectedButton.IsEnabled = hasDocument && !_isTranslating;
        TranslatePendingButton.IsEnabled = hasDocument && !_isTranslating;
        CancelTranslationButton.IsEnabled = _isTranslating;
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
            if (!CommitActiveGridEdits())
            {
                StatusTextBlock.Text = "当前单元格内容尚未通过提交，未保存字典。请修正后重试。";
                return false;
            }

            _document.Save();
            StatusTextBlock.Text = "保存成功。原始字段和其他 JSON 字段均已保留。";
            UpdateDocumentStatus();
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
        if (!CommitActiveGridEdits())
        {
            return;
        }

        DictionaryEditorEntry[] selected = EntriesDataGrid.SelectedCells
            .Select(cell => cell.Item)
            .OfType<DictionaryEditorEntry>()
            .Concat(EntriesDataGrid.SelectedItems.OfType<DictionaryEditorEntry>())
            .Distinct()
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

        if (!CommitActiveGridEdits())
        {
            return;
        }

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
        EntriesDataGrid.IsEnabled = enabled;
        AiSettingsButton.IsEnabled = enabled;
        TranslateSelectedButton.IsEnabled = enabled && _document is not null;
        TranslatePendingButton.IsEnabled = enabled && _document is not null;
        CancelTranslationButton.IsEnabled = !enabled;
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
            _fileTreeItems.TryGetValue(_currentFile.FullPath, out TreeViewItem? fileNode))
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
    }
}
