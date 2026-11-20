using EraTrans.Core;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace EraTrans.App;

public partial class AiProviderSettingsWindow : Window
{
    private readonly AiProviderSettingsStore _store = new();
    private readonly AiModelDiscoveryService _modelDiscovery = new();
    private readonly AiModelCatalogEnrichmentService _modelCatalogEnrichment = new();
    private readonly AiProviderSettings _settings;
    private readonly ObservableCollection<AiProviderProfile> _providers = [];
    private readonly ObservableCollection<AiProviderModel> _availableModels = [];
    private readonly ICollectionView _modelView;
    private AiProviderProfile? _currentProfile;
    private bool _loadingProfile;
    private bool _isTesting;
    private bool _isFetchingModels;

    public AiProviderSettingsWindow()
    {
        InitializeComponent();

        _modelView = CollectionViewSource.GetDefaultView(_availableModels);
        _modelView.GroupDescriptions.Add(
            new PropertyGroupDescription(nameof(AiProviderModel.GroupName)));
        _modelView.SortDescriptions.Add(
            new SortDescription(nameof(AiProviderModel.GroupName), ListSortDirection.Ascending));
        _modelView.SortDescriptions.Add(
            new SortDescription(nameof(AiProviderModel.Id), ListSortDirection.Ascending));
        _modelView.Filter = FilterModel;
        ModelsListView.ItemsSource = _modelView;

        try
        {
            _settings = _store.Load();
        }
        catch (Exception ex)
        {
            _settings = new AiProviderSettings();
            MessageBox.Show(
                this,
                ex.Message + "\n\n现有配置文件没有被修改。",
                "读取 AI 配置失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        foreach (AiProviderProfile provider in _settings.Providers)
        {
            _providers.Add(provider);
        }

        ProviderComboBox.ItemsSource = _providers;
        if (_providers.Count == 0)
        {
            AddProvider();
        }
        else
        {
            ProviderComboBox.SelectedItem =
                _providers.FirstOrDefault(provider => provider.Id == _settings.ActiveProviderId) ??
                _providers[0];
            LoadSelectedProfile();
        }
    }

    private void ProviderComboBox_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loadingProfile)
            return;

        SaveFieldsToCurrentProfile();
        LoadSelectedProfile();
    }

    private void LoadSelectedProfile()
    {
        _loadingProfile = true;
        _currentProfile = ProviderComboBox.SelectedItem as AiProviderProfile;

        NameTextBox.Text = _currentProfile?.Name ?? "";
        BaseUrlTextBox.Text = _currentProfile?.BaseUrl ?? "";
        ModelTextBox.Text = _currentProfile?.Model ?? "";
        ApiKeyPasswordBox.Password = _currentProfile?.ApiKey ?? "";
        ModelsSearchTextBox.Text = "";
        StatusTextBlock.Text = _currentProfile is null
            ? "请新增服务商并填写接口信息。"
            : "可获取模型列表，也可以直接填写模型 ID。";

        LoadAvailableModels(_currentProfile);
        _loadingProfile = false;
    }

    private void LoadAvailableModels(AiProviderProfile? profile)
    {
        _availableModels.Clear();
        if (profile is not null)
        {
            foreach (AiProviderModel model in profile.AvailableModels)
            {
                _availableModels.Add(model);
            }
        }

        _modelView.Refresh();
        ModelCountTextBlock.Text = _availableModels.Count == 0
            ? "当前没有缓存模型。若接口已返回空列表或服务商不支持 GET /models，可手动填写模型 ID。"
            : $"已缓存 {_availableModels.Count} 个模型。点击模型后选择“使用选中模型”，也可双击直接使用。";

        ModelsListView.SelectedItem = profile is null
            ? null
            : _availableModels.FirstOrDefault(model =>
                string.Equals(model.Id, profile.Model, StringComparison.Ordinal));
        UpdateModelSelectionState();
    }

    private void SaveFieldsToCurrentProfile()
    {
        if (_currentProfile is null || _loadingProfile)
            return;

        _currentProfile.Name = NameTextBox.Text.Trim();
        _currentProfile.BaseUrl = BaseUrlTextBox.Text.Trim();
        _currentProfile.Model = ModelTextBox.Text.Trim();
        _currentProfile.ApiKey = ApiKeyPasswordBox.Password;
    }

    private void AddProvider_Click(object sender, RoutedEventArgs e) => AddProvider();

    private void AddProvider()
    {
        SaveFieldsToCurrentProfile();
        var provider = new AiProviderProfile
        {
            Name = $"新服务商 {_providers.Count + 1}"
        };
        _providers.Add(provider);
        ProviderComboBox.SelectedItem = provider;
        LoadSelectedProfile();
    }

    private void RemoveProvider_Click(object sender, RoutedEventArgs e)
    {
        SaveFieldsToCurrentProfile();
        if (_currentProfile is null)
            return;

        int index = _providers.IndexOf(_currentProfile);
        _providers.Remove(_currentProfile);
        if (_providers.Count == 0)
        {
            AddProvider();
            return;
        }

        ProviderComboBox.SelectedIndex = Math.Min(index, _providers.Count - 1);
    }

    private bool FilterModel(object item)
    {
        if (item is not AiProviderModel model)
        {
            return false;
        }

        string query = ModelsSearchTextBox.Text.Trim();
        if (query.Length == 0)
        {
            return true;
        }

        return model.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               model.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               (model.OwnedBy?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
               model.GroupName.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void ModelsSearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _modelView.Refresh();
    }

    private void ModelsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateModelSelectionState();
    }

    private void UpdateModelSelectionState()
    {
        UseSelectedModelButton.IsEnabled =
            !_isFetchingModels &&
            ModelsListView.SelectedItem is AiProviderModel { IsChatModel: true };
    }

    private async void FetchModels_Click(object sender, RoutedEventArgs e)
    {
        SaveFieldsToCurrentProfile();
        AiProviderProfile? profile = _currentProfile;
        if (profile is null || _isFetchingModels || _isTesting)
        {
            return;
        }

        try
        {
            AiProviderProfileValidator.ValidateForModelDiscovery(profile);
            _isFetchingModels = true;
            FetchModelsButton.IsEnabled = false;
            TestButton.IsEnabled = false;
            UseSelectedModelButton.IsEnabled = false;
            StatusTextBlock.Text = "正在请求服务商的模型列表……";
            ModelCountTextBlock.Text = "正在获取模型信息……";

            IReadOnlyList<AiProviderModel> discovered =
                await _modelDiscovery.FetchModelsAsync(profile);
            IReadOnlyList<AiProviderModel> models =
                await _modelCatalogEnrichment.EnrichAsync(profile, discovered);

            profile.AvailableModels = models.ToList();
            if (ReferenceEquals(_currentProfile, profile))
            {
                LoadAvailableModels(profile);
                StatusTextBlock.Text = models.Count == 0
                    ? "接口请求成功，但没有返回模型。仍可手动填写模型 ID。"
                    : $"获取成功：发现 {models.Count} 个模型，已按模型系列分组。";
            }
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_currentProfile, profile))
            {
                ModelCountTextBlock.Text =
                    "模型列表获取失败。可检查地址和 Key，或继续手动填写模型 ID。";
                StatusTextBlock.Text = "获取模型失败：" + ex.Message;
            }
        }
        finally
        {
            _isFetchingModels = false;
            FetchModelsButton.IsEnabled = true;
            TestButton.IsEnabled = true;
            UseSelectedModelButton.IsEnabled =
                ModelsListView.SelectedItem is AiProviderModel { IsChatModel: true };
        }
    }

    private void UseSelectedModel_Click(object sender, RoutedEventArgs e)
    {
        ApplySelectedModel();
    }

    private void ModelsListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ApplySelectedModel();
    }

    private void ApplySelectedModel()
    {
        if (ModelsListView.SelectedItem is not AiProviderModel model)
        {
            StatusTextBlock.Text = "请先从模型列表中选择一个模型。";
            return;
        }

        if (!model.IsChatModel)
        {
            StatusTextBlock.Text = model.IsEmbedding
                ? "这是嵌入模型，不能用于当前的对话翻译。"
                : "这是重排模型，不能用于当前的对话翻译。";
            return;
        }

        ModelTextBox.Text = model.Id;
        SaveFieldsToCurrentProfile();
        StatusTextBlock.Text = $"已选择模型：{model.Id}。保存服务商配置后生效。";
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        SaveFieldsToCurrentProfile();
        if (_currentProfile is null || _isTesting || _isFetchingModels)
            return;

        try
        {
            AiProviderProfileValidator.Validate(_currentProfile);
            _isTesting = true;
            TestButton.IsEnabled = false;
            FetchModelsButton.IsEnabled = false;
            StatusTextBlock.Text = "正在测试 Chat Completions API……";

            await new AiTranslationService(logStore: new AiTranslationLogStore()).TestConnectionAsync(_currentProfile);

            StatusTextBlock.Text = "连接成功，所选模型已返回响应。";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = "连接测试失败：" + ex.Message;
        }
        finally
        {
            _isTesting = false;
            TestButton.IsEnabled = true;
            FetchModelsButton.IsEnabled = true;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_isFetchingModels || _isTesting)
            {
                throw new InvalidOperationException("请等待当前 API 请求完成后再保存。");
            }

            SaveFieldsToCurrentProfile();
            if (_providers.Count == 0)
                throw new InvalidOperationException("至少需要保留一个服务商配置。");

            foreach (AiProviderProfile provider in _providers)
            {
                AiProviderProfileValidator.Validate(provider);
            }

            _settings.Providers = _providers.ToList();
            _settings.ActiveProviderId = _currentProfile?.Id;
            _store.Save(_settings);

            StatusTextBlock.Text = "配置和已获取的模型列表已加密保存。";
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "保存 AI 配置失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}