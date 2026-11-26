using EraTrans.Core;
using System.Windows.Controls;

namespace EraTrans.App;

/// <summary>
/// Isolated settings panel for the legacy ERB extractor and legacy variable auto-correction.
/// Kept as a standalone control so the entire legacy settings section can be removed cleanly.
/// </summary>
public partial class LegacyParserSettingsControl : UserControl
{
    public LegacyParserSettingsControl()
    {
        InitializeComponent();
    }

    public void Populate(LegacyExtractionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        HideEnglishTextCheckBox.IsChecked = settings.HideEnglishText;
        MergeDuplicateEntriesCheckBox.IsChecked = settings.MergeDuplicateEntries;
        HideVariableEntriesCheckBox.IsChecked = settings.HideVariableEntries;
        MergeConcatenatedStringsCheckBox.IsChecked = settings.MergeConcatenatedStrings;
        EnableVariableAutoCorrectionCheckBox.IsChecked = settings.EnableVariableAutoCorrection;

        AutoReplaceReferenceFilesTextBox.Text = string.Join(Environment.NewLine, settings.AutoReplaceReferenceFiles);
        VariableNameCharactersTextBox.Text = string.Join(Environment.NewLine, settings.VariableNameCharacters);
        OperatorsTextBox.Text = string.Join(Environment.NewLine, settings.Operators);
    }

    public LegacyExtractionSettings BuildSettings() => new()
    {
        HideEnglishText = HideEnglishTextCheckBox.IsChecked == true,
        MergeDuplicateEntries = MergeDuplicateEntriesCheckBox.IsChecked == true,
        HideVariableEntries = HideVariableEntriesCheckBox.IsChecked == true,
        MergeConcatenatedStrings = MergeConcatenatedStringsCheckBox.IsChecked == true,
        EnableVariableAutoCorrection = EnableVariableAutoCorrectionCheckBox.IsChecked == true,
        AutoReplaceReferenceFiles = ReadLines(AutoReplaceReferenceFilesTextBox),
        VariableNameCharacters = ReadLines(VariableNameCharactersTextBox),
        Operators = ReadLines(OperatorsTextBox)
    };

    private static List<string> ReadLines(TextBox textBox) =>
        textBox.Text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .ToList();
}
