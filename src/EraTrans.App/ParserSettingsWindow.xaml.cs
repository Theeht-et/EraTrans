using EraTrans.Core;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace EraTrans.App;

public partial class ParserSettingsWindow : Window
{
    private readonly EraTransUserSettingsStore _store;
    private readonly EraTransUserSettings _settingsToPreserve;

    public ParserSettingsWindow(
        EraTransUserSettings settings,
        EraTransUserSettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
        _settingsToPreserve = settings;
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

        ExtractionModeComboBox.SelectedItem = ExtractionModeComboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(
                item.Tag?.ToString(),
                settings.ErbExtractionMode,
                StringComparison.OrdinalIgnoreCase));

        ForceFilterVariableNamesCheckBox.IsChecked = settings.ForceFilterVariableNames;
        AutoOpenFolderCheckBox.IsChecked = settings.AutoOpenFolder;
        EnableVariableAutoCorrectionCheckBox.IsChecked = settings.EnableVariableAutoCorrection;
        HideEnglishTextCheckBox.IsChecked = settings.HideEnglishText;
        MergeDuplicateEntriesCheckBox.IsChecked = settings.MergeDuplicateEntries;
        HideVariableEntriesCheckBox.IsChecked = settings.HideVariableEntries;
        MergeConcatenatedStringsCheckBox.IsChecked = settings.MergeConcatenatedStrings;
        WriteContextCheckBox.IsChecked = settings.WriteContext;
        EnableErbSemanticGroupingTranslationCheckBox.IsChecked = settings.EnableErbSemanticGroupingTranslation;

        WindowMaxCharactersTextBox.Text = settings.WindowMaxCharacters.ToString(System.Globalization.CultureInfo.InvariantCulture);
        WindowMaxLinesTextBox.Text = settings.WindowMaxLines.ToString(System.Globalization.CultureInfo.InvariantCulture);
        TranslationConcurrencyTextBox.Text = settings.TranslationConcurrency.ToString(System.Globalization.CultureInfo.InvariantCulture);

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

    private static int ReadLimit(
        TextBox textBox,
        int minimum,
        int maximum,
        int fallback,
        string displayName)
    {
        string text = textBox.Text.Trim();
        if (text.Length == 0)
            return fallback;

        if (!int.TryParse(text, out int value))
            throw new InvalidDataException($"{displayName}必须是整数。");

        return Math.Clamp(value, minimum, maximum);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (EncodingComboBox.SelectedItem is not ComboBoxItem encodingItem ||
                encodingItem.Tag is not string encodingId)
            {
                throw new InvalidDataException("请选择有效的文件编码。");
            }

            if (ExtractionModeComboBox.SelectedItem is not ComboBoxItem extractionItem ||
                extractionItem.Tag is not string extractionMode)
            {
                throw new InvalidDataException("请选择有效的文本抽取方式。");
            }

            var settings = new EraTransUserSettings
            {
                FileEncodingId = encodingId,
                ErbExtractionMode = extractionMode,
                ForceFilterVariableNames = ForceFilterVariableNamesCheckBox.IsChecked == true,
                AutoOpenFolder = AutoOpenFolderCheckBox.IsChecked == true,
                EnableVariableAutoCorrection = EnableVariableAutoCorrectionCheckBox.IsChecked == true,
                HideEnglishText = HideEnglishTextCheckBox.IsChecked == true,
                MergeDuplicateEntries = MergeDuplicateEntriesCheckBox.IsChecked == true,
                HideVariableEntries = HideVariableEntriesCheckBox.IsChecked == true,
                MergeConcatenatedStrings = MergeConcatenatedStringsCheckBox.IsChecked == true,
                WriteContext = WriteContextCheckBox.IsChecked == true,
                EnableErbSemanticGroupingTranslation = EnableErbSemanticGroupingTranslationCheckBox.IsChecked == true,
                WindowMaxCharacters = ReadLimit(
                    WindowMaxCharactersTextBox,
                    EraTransUserSettings.MinimumWindowCharacters,
                    EraTransUserSettings.MaximumWindowCharacters,
                    EraTransUserSettings.DefaultWindowCharacters,
                    "每个窗口的字符上限"),
                WindowMaxLines = ReadLimit(
                    WindowMaxLinesTextBox,
                    EraTransUserSettings.MinimumWindowLines,
                    EraTransUserSettings.MaximumWindowLines,
                    EraTransUserSettings.DefaultWindowLines,
                    "每个窗口的行数上限"),
                TranslationConcurrency = ReadLimit(
                    TranslationConcurrencyTextBox,
                    EraTransUserSettings.MinimumTranslationConcurrency,
                    EraTransUserSettings.MaximumTranslationConcurrency,
                    EraTransUserSettings.DefaultTranslationConcurrency,
                    "同时发送的请求数"),
                LastGameDirectory = _settingsToPreserve.LastGameDirectory,
                LastOutputRootDirectory = _settingsToPreserve.LastOutputRootDirectory,
                LastDictionaryFileRelativePath = _settingsToPreserve.LastDictionaryFileRelativePath,
                TargetFileExtensions = ReadLines(TargetFileExtensionsTextBox),
                AutoReplaceReferenceFiles = ReadLines(AutoReplaceReferenceFilesTextBox),
                VariableNameCharacters = ReadLines(VariableNameCharactersTextBox),
                Operators = ReadLines(OperatorsTextBox)
            };

            _store.Save(settings);
            bool modeChanged = !string.Equals(
                extractionMode,
                _settingsToPreserve.ErbExtractionMode,
                StringComparison.OrdinalIgnoreCase);
            MessageBox.Show(
                this,
                modeChanged
                    ? "设置已保存。文本抽取方式已改变：请重新提取字典，两版字典内容不同，不能混用。"
                    : "设置已保存。下一次提取或应用译文时生效。",
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
