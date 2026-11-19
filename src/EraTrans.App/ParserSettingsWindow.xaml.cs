using EraTrans.Core;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace EraTrans.App;

public partial class ParserSettingsWindow : Window
{
    private readonly EraTransUserSettingsStore _store;

    public ParserSettingsWindow(
        EraTransUserSettings settings,
        EraTransUserSettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
        InitializeComponent();
        Populate(settings);
    }

    private void Populate(EraTransUserSettings settings)
    {
        EncodingComboBox.SelectedItem = EncodingComboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(
                item.Tag?.ToString(),
                settings.FileEncodingId,
                StringComparison.Ordinal));

        ForceFilterVariableNamesCheckBox.IsChecked = settings.ForceFilterVariableNames;
        AutoOpenFolderCheckBox.IsChecked = settings.AutoOpenFolder;
        EnableVariableAutoCorrectionCheckBox.IsChecked = settings.EnableVariableAutoCorrection;
        HideEnglishTextCheckBox.IsChecked = settings.HideEnglishText;
        MergeDuplicateEntriesCheckBox.IsChecked = settings.MergeDuplicateEntries;
        HideVariableEntriesCheckBox.IsChecked = settings.HideVariableEntries;
        MergeConcatenatedStringsCheckBox.IsChecked = settings.MergeConcatenatedStrings;
        WriteContextCheckBox.IsChecked = settings.WriteContext;

        TargetFileExtensionsTextBox.Text = string.Join(Environment.NewLine, settings.TargetFileExtensions);
        AutoReplaceReferenceFilesTextBox.Text = string.Join(Environment.NewLine, settings.AutoReplaceReferenceFiles);
        VariableNameCharactersTextBox.Text = string.Join(Environment.NewLine, settings.VariableNameCharacters);
        OperatorsTextBox.Text = string.Join(Environment.NewLine, settings.Operators);
    }

    private static List<string> ReadLines(TextBox textBox) =>
        textBox.Text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .ToList();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (EncodingComboBox.SelectedItem is not ComboBoxItem encodingItem ||
                encodingItem.Tag is not string encodingId)
            {
                throw new InvalidDataException("请选择有效的文件编码。");
            }

            var settings = new EraTransUserSettings
            {
                FileEncodingId = encodingId,
                ForceFilterVariableNames = ForceFilterVariableNamesCheckBox.IsChecked == true,
                AutoOpenFolder = AutoOpenFolderCheckBox.IsChecked == true,
                EnableVariableAutoCorrection = EnableVariableAutoCorrectionCheckBox.IsChecked == true,
                HideEnglishText = HideEnglishTextCheckBox.IsChecked == true,
                MergeDuplicateEntries = MergeDuplicateEntriesCheckBox.IsChecked == true,
                HideVariableEntries = HideVariableEntriesCheckBox.IsChecked == true,
                MergeConcatenatedStrings = MergeConcatenatedStringsCheckBox.IsChecked == true,
                WriteContext = WriteContextCheckBox.IsChecked == true,
                TargetFileExtensions = ReadLines(TargetFileExtensionsTextBox),
                AutoReplaceReferenceFiles = ReadLines(AutoReplaceReferenceFilesTextBox),
                VariableNameCharacters = ReadLines(VariableNameCharactersTextBox),
                Operators = ReadLines(OperatorsTextBox)
            };

            _store.Save(settings);
            MessageBox.Show(
                this,
                "设置已保存。下一次提取或应用译文时生效。",
                "保存成功",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.Message,
                "设置无效或保存失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
