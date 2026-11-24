using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EraTrans.Core;

namespace EraTrans.App;

public partial class TranslationEditWindow : Window
{
    private readonly string _original;
    private readonly bool _isScriptTarget;

    public TranslationEditWindow(
        string key,
        string original,
        string translation,
        bool isScriptTarget = false)
    {
        InitializeComponent();

        _original = original ?? string.Empty;
        _isScriptTarget = isScriptTarget;
        KeyTextBlock.Text = key;
        OriginalTextBox.Text = _original;
        TranslationTextBox.Text = translation ?? string.Empty;
        UpdateValidationBanner();

        Loaded += (_, _) =>
        {
            TranslationTextBox.Focus();
            TranslationTextBox.CaretIndex = TranslationTextBox.Text.Length;
        };
    }

    public string EditedTranslation { get; private set; } = string.Empty;

    private void TranslationTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateValidationBanner();
    }

    private void UpdateValidationBanner()
    {
        if (ValidationWarningBorder is null ||
            ValidationWarningTextBlock is null ||
            TranslationTextBox is null)
        {
            return;
        }

        string warning = TranslationPreValidator.Validate(_original, TranslationTextBox.Text, _isScriptTarget);
        if (warning.Length == 0)
        {
            ValidationWarningBorder.Visibility = Visibility.Collapsed;
            ValidationWarningTextBlock.Text = string.Empty;
        }
        else
        {
            ValidationWarningTextBlock.Text = $"⚠ 写回前置校验提示：{warning}（不影响保存，请确认是否需要修正）";
            ValidationWarningBorder.Visibility = Visibility.Visible;
        }
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        EditedTranslation = TranslationTextBox.Text;
        DialogResult = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Confirm_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }
}
