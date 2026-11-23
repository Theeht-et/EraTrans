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

        EncodingOverridesTextBox.Text = string.Join(
            Environment.NewLine,
            settings.FileEncodingOverrides.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Select(item => $"{item.Key}={item.Value}"));

        ExtractionModeComboBox.SelectedItem = ExtractionModeComboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(
                item.Tag?.ToString(),
                settings.ErbExtractionMode,
                StringComparison.OrdinalIgnoreCase));

        ExtractHtmlTitleCheckBox.IsChecked = settings.ExtractHtmlTitle;
        ExtractPrintDataEntriesCheckBox.IsChecked = settings.ExtractPrintDataEntries;
        SkipNonJapaneseEntriesCheckBox.IsChecked = settings.SkipNonJapaneseEntries;
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

        UpdateModeAvailability();
    }

    private void ExtractionModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateModeAvailability();

    /// <summary>
    /// New and legacy settings do not apply to each other: grey out whichever group the selected
    /// extractor ignores so it is obvious which switches currently do nothing.
    /// </summary>
    private void UpdateModeAvailability()
    {
        if (LexerSettingsPanel is null || LegacySettingsPanel is null)
        {
            return;
        }

        bool isLexer = string.Equals(
            (ExtractionModeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(),
            "lexer",
            StringComparison.OrdinalIgnoreCase);

        LexerSettingsPanel.IsEnabled = isLexer;
        LexerSettingsPanel.Opacity = isLexer ? 1.0 : 0.55;

        LegacySettingsPanel.IsEnabled = !isLexer;
        LegacySettingsPanel.Opacity = isLexer ? 0.55 : 1.0;
    }

    private static List<string> ReadLines(TextBox textBox) =>
        textBox.Text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .ToList();

    private static Dictionary<string, string> ReadEncodingOverrides(string text)
    {
        var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string entry = line.Trim();
            int separator = entry.LastIndexOf('=');
            if (separator <= 0 || separator == entry.Length - 1)
                throw new InvalidDataException($"单文件编码覆盖格式错误：{entry}。每行应为“相对路径=编码代码”。");
            string path = entry[..separator].Trim();
            string encodingId = entry[(separator + 1)..].Trim();
            if (!overrides.TryAdd(path, encodingId))
                throw new InvalidDataException($"单文件编码覆盖路径重复：{path}");
        }
        return overrides;
    }

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

            List<string> targetExtensions = ReadLines(TargetFileExtensionsTextBox);
            if (!targetExtensions.Any(value =>
                    string.Equals(value, ".erb", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException(
                    "游戏文件扩展名必须包含 .erb，否则 ERB 译文在写回时会被静默跳过。");
            }

            var settings = new EraTransUserSettings
            {
                FileEncodingId = encodingId,
                FileEncodingOverrides = ReadEncodingOverrides(EncodingOverridesTextBox.Text),
                ErbExtractionMode = extractionMode,
                ExtractHtmlTitle = ExtractHtmlTitleCheckBox.IsChecked == true,
                ExtractPrintDataEntries = ExtractPrintDataEntriesCheckBox.IsChecked == true,
                SkipNonJapaneseEntries = SkipNonJapaneseEntriesCheckBox.IsChecked == true,
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
                TargetFileExtensions = targetExtensions,
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
