using System.Windows;
using System.Windows.Input;

namespace EraTrans.App;

public partial class TranslationEditWindow : Window
{
    public TranslationEditWindow(string key, string original, string translation)
    {
        InitializeComponent();

        KeyTextBlock.Text = key;
        OriginalTextBox.Text = original;
        TranslationTextBox.Text = translation ?? string.Empty;

        Loaded += (_, _) =>
        {
            TranslationTextBox.Focus();
            TranslationTextBox.CaretIndex = TranslationTextBox.Text.Length;
        };
    }

    public string EditedTranslation { get; private set; } = string.Empty;

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
