using EraTrans.Core;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace EraTrans.App;

public partial class AiModelPickerWindow : Window
{
    private readonly ObservableCollection<AiProviderModel> _models = [];
    private readonly ICollectionView _view;

    public AiProviderModel? SelectedModel { get; private set; }

    public AiModelPickerWindow(
        IReadOnlyList<AiProviderModel> models,
        string? currentModel = null)
    {
        InitializeComponent();

        foreach (AiProviderModel model in models)
            _models.Add(model);

        _view = CollectionViewSource.GetDefaultView(_models);
        _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(AiProviderModel.GroupName)));
        _view.SortDescriptions.Add(new SortDescription(nameof(AiProviderModel.GroupName), ListSortDirection.Ascending));
        _view.SortDescriptions.Add(new SortDescription(nameof(AiProviderModel.Id), ListSortDirection.Ascending));
        _view.Filter = FilterModel;
        ModelsListView.ItemsSource = _view;

        ModelCountTextBlock.Text = $"服务商返回 {_models.Count} 个模型。";
        ModelsListView.SelectedItem = _models.FirstOrDefault(model =>
            string.Equals(model.Id, currentModel, StringComparison.Ordinal));
        UpdateSelectionHint();
    }

    private bool FilterModel(object item)
    {
        if (item is not AiProviderModel model)
            return false;

        string query = SearchTextBox.Text.Trim();
        return query.Length == 0 ||
               model.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               model.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               model.GroupName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               (model.OwnedBy?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e) => _view.Refresh();

    private void ModelsListView_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateSelectionHint();

    private void UpdateSelectionHint()
    {
        SelectionHintTextBlock.Text = "嵌入模型与重排模型只能查看，不能用于当前翻译。";
        UseModelButton.IsEnabled =
            ModelsListView.SelectedItem is AiProviderModel { IsChatModel: true };
    }

    private void UseModel_Click(object sender, RoutedEventArgs e)
    {
        if (ModelsListView.SelectedItem is not AiProviderModel { IsChatModel: true } model)
            return;

        SelectedModel = model;
        DialogResult = true;
    }

    private void ModelsListView_MouseDoubleClick(object sender, MouseButtonEventArgs e) =>
        UseModel_Click(sender, e);

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
