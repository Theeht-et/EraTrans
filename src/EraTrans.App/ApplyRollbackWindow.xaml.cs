using System.Text;
using System.Windows;
using System.Windows.Controls;
using EraTrans.Core;

namespace EraTrans.App;

public enum RollbackActionKind
{
    None,
    RestoreSnapshot,
    RestoreOriginal
}

public partial class ApplyRollbackWindow : Window
{
    private sealed class SnapshotItemViewModel
    {
        public SnapshotItemViewModel(ApplySnapshotInfo info)
        {
            Info = info;
            ApplySnapshotManifest m = info.Manifest;
            TimeText = m.CreatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
            ModeLabel = m.Mode;
            string copyHint = m.HasGameCopyBackup ? $"副本 {m.BackedUpGameFileCount} 文件" : "无副本备份";
            string dictHint = m.HasDictionaryBackup ? $"字典 {m.BackedUpDictionaryFileCount} 文件" : "无字典备份";
            SummaryText = $"改动 {m.ModifiedFiles.Count} · 重命名 {m.RenamedFiles.Count} · {copyHint} · {dictHint}";
        }

        public ApplySnapshotInfo Info { get; }
        public string TimeText { get; }
        public string ModeLabel { get; }
        public string SummaryText { get; }
    }

    private readonly bool _hasGameCopy;

    public ApplyRollbackWindow(IReadOnlyList<ApplySnapshotInfo> snapshots, bool hasGameCopy)
    {
        InitializeComponent();

        _hasGameCopy = hasGameCopy;
        RestoreOriginalButton.IsEnabled = hasGameCopy;

        SnapshotItemViewModel[] items = snapshots
            .Select(info => new SnapshotItemViewModel(info))
            .ToArray();

        SnapshotsListBox.ItemsSource = items;
        if (items.Length == 0)
        {
            EmptySnapshotsTextBlock.Visibility = Visibility.Visible;
            ChangedFilesTextBox.Text = "暂无可查看的快照记录。";
        }
        else
        {
            SnapshotsListBox.SelectedIndex = 0;
        }
    }

    public RollbackActionKind RequestedAction { get; private set; } = RollbackActionKind.None;
    public ApplySnapshotInfo? SelectedSnapshot { get; private set; }
    public bool RestoreDictionary => RestoreDictionaryCheckBox.IsChecked == true;

    private void SnapshotsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SnapshotsListBox.SelectedItem is not SnapshotItemViewModel item)
        {
            RestoreSnapshotButton.IsEnabled = false;
            RestoreDictionaryCheckBox.IsEnabled = false;
            RestoreDictionaryCheckBox.IsChecked = false;
            DetailMetaTextBlock.Text = "选择左侧快照可查看本轮改动的文件列表。";
            ChangedFilesTextBox.Text = string.Empty;
            return;
        }

        ApplySnapshotManifest m = item.Info.Manifest;
        RestoreSnapshotButton.IsEnabled = m.HasGameCopyBackup || m.HasDictionaryBackup;
        RestoreDictionaryCheckBox.IsEnabled = m.HasDictionaryBackup;
        if (!m.HasDictionaryBackup)
        {
            RestoreDictionaryCheckBox.IsChecked = false;
        }

        DetailMetaTextBlock.Text =
            $"时间：{item.TimeText} · 模式：{m.Mode} · 备份副本 {m.BackedUpGameFileCount} 个文件 / 字典 {m.BackedUpDictionaryFileCount} 个文件";

        var sb = new StringBuilder();
        if (m.ModifiedFiles.Count > 0)
        {
            sb.AppendLine($"【写入译文的文件（{m.ModifiedFiles.Count}）】");
            foreach (string file in m.ModifiedFiles)
            {
                sb.AppendLine("  " + file);
            }
        }

        if (m.RenamedFiles.Count > 0)
        {
            if (sb.Length > 0)
            {
                sb.AppendLine();
            }

            sb.AppendLine($"【重命名的 ERD 文件（{m.RenamedFiles.Count}）】");
            foreach (ApplySnapshotRenamedFile renamed in m.RenamedFiles)
            {
                sb.AppendLine($"  {renamed.PreviousRelativePath} -> {renamed.NewRelativePath}");
            }
        }

        if (sb.Length == 0)
        {
            sb.AppendLine("该快照未记录具体改动文件。");
        }

        ChangedFilesTextBox.Text = sb.ToString();
    }

    private void RestoreSnapshotButton_Click(object sender, RoutedEventArgs e)
    {
        if (SnapshotsListBox.SelectedItem is not SnapshotItemViewModel item)
        {
            return;
        }

        if (!item.Info.Manifest.HasGameCopyBackup && RestoreDictionaryCheckBox.IsChecked != true)
        {
            MessageBox.Show(
                this,
                "该快照不含游戏副本备份。如需回退字典，请勾选「同时将字典回退到该快照时的版本」。",
                "无游戏副本备份",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        string scopeNote = RestoreDictionaryCheckBox.IsChecked == true
            ? "将恢复游戏副本并同时把字典回退到该快照版本（sav/ 存档和配置保持不动）。"
            : "仅恢复游戏副本，保留当前字典与 sav/ 存档不动。";

        MessageBoxResult confirm = MessageBox.Show(
            this,
            $"确定要恢复到 {item.TimeText} 的快照吗？\n\n{scopeNote}",
            "确认恢复快照",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        SelectedSnapshot = item.Info;
        RequestedAction = RollbackActionKind.RestoreSnapshot;
        DialogResult = true;
    }

    private void RestoreOriginalButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_hasGameCopy)
        {
            MessageBox.Show(
                this,
                "当前工作区尚未生成游戏副本，无需还原。",
                "未找到游戏副本",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        MessageBoxResult confirm = MessageBox.Show(
            this,
            "确定要将游戏副本恢复为纯原版日文吗？\n\n将从原游戏目录还原所有脚本与数据文本，并清理汉化重命名的 .erd 文件。\nsav/ 存档、emuera.config 及图片音频文件都会保留，不会改动原游戏目录。",
            "确认恢复原版日文",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        RequestedAction = RollbackActionKind.RestoreOriginal;
        DialogResult = true;
    }
}
