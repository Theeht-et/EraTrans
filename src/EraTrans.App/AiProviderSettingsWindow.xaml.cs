using EraTrans.Core;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace EraTrans.App;

public partial class AiProviderSettingsWindow : Window
{
    private readonly AiProviderSettingsStore _store = new();
    private readonly AiModelDiscoveryService _modelDiscovery = new();
    private readonly AiModelCatalogEnrichmentService _modelCatalogEnrichment = new();
    private readonly AiProviderSettings _settings;
    private readonly ObservableCollection<AiProviderProfile> _providers = [];
    private AiProviderProfile? _currentProfile;
    private bool _loadingProfile;
    private bool _isTesting;
    private bool _isFetchingModels;

    public AiProviderSettingsWindow()
    {
        InitializeComponent();
        try
        {
            _settings = _store.Load();
        }
        catch (Exception ex)
        {
            _settings = new AiProviderSettings();
            MessageBox.Show(this, ex.Message + "\n\n现有配置文件没有被修改。",
                "读取 AI 配置失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        foreach (AiProviderProfile provider in _settings.Providers)
            _providers.Add(provider);

        if (_providers.Count == 0)
        {
            foreach (AiProviderPreset preset in AiProviderPresets.All)
            {
                _providers.Add(new AiProviderProfile
                {
                    Name = preset.Name,
                    BaseUrl = preset.BaseUrl,
                    Protocol = AiProviderProtocol.OpenAiCompatible
                });
            }
        }

        ProviderListBox.ItemsSource = _providers;
        AiProviderProfile? initial = _providers.FirstOrDefault(provider =>
            string.Equals(provider.Id, _settings.ActiveProviderId, StringComparison.Ordinal))
            ?? _providers.FirstOrDefault(provider =>
                provider.Name.Contains("OpenAI", StringComparison.OrdinalIgnoreCase))
            ?? _providers.FirstOrDefault();
        ProviderListBox.SelectedItem = initial;
        LoadSelectedProfile();
    }

    private void ProviderListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingProfile || _isTesting || _isFetchingModels)
            return;
        SaveFieldsToCurrentProfile();
        LoadSelectedProfile();
    }

    private void LoadSelectedProfile()
    {
        _loadingProfile = true;
        _currentProfile = ProviderListBox.SelectedItem as AiProviderProfile;
        bool hasProfile = _currentProfile is not null;
        ProviderTitleTextBlock.Text = hasProfile ? _currentProfile!.Name : "服务商配置";
        ProviderNameTextBox.Text = _currentProfile?.Name ?? "";
        BaseUrlTextBox.Text = _currentProfile?.BaseUrl ?? "";
        ApiKeyPasswordBox.Password = _currentProfile?.ApiKey ?? "";
        ModelTextBox.Text = _currentProfile?.Model ?? "";
        LegacyProtocolWarning.Visibility = _currentProfile?.RequiresEndpointReview == true
            ? Visibility.Visible : Visibility.Collapsed;
        int cachedCount = _currentProfile?.AvailableModels.Count ?? 0;
        ModelCountTextBlock.Text = cachedCount == 0
            ? "模型列表尚未获取，可直接填写模型 ID。"
            : $"已缓存 {cachedCount} 个模型。点击“获取模型”刷新并选择。";
        StatusTextBlock.Text = "";
        _loadingProfile = false;
        SetEditorEnabled(hasProfile);
    }

    private void SetEditorEnabled(bool enabled)
    {
        bool idle = !_isFetchingModels && !_isTesting;
        ProviderListBox.IsEnabled = enabled && idle;
        ProviderNameTextBox.IsEnabled = enabled && idle;
        BaseUrlTextBox.IsEnabled = enabled && idle;
        ApiKeyPasswordBox.IsEnabled = enabled && idle;
        ModelTextBox.IsEnabled = enabled && idle;
        FetchModelsButton.IsEnabled = enabled && idle;
        TestButton.IsEnabled = enabled && idle;
        RemoveProviderButton.IsEnabled = enabled && idle && _providers.Count > 1;
        AddProviderButton.IsEnabled = idle;
    }

    private void SaveFieldsToCurrentProfile()
    {
        if (_currentProfile is null || _loadingProfile)
            return;
        string newBaseUrl = BaseUrlTextBox.Text.Trim();
        if (_currentProfile.RequiresEndpointReview &&
            !string.Equals(_currentProfile.BaseUrl, newBaseUrl, StringComparison.Ordinal))
        {
            _currentProfile.RequiresEndpointReview = false;
            LegacyProtocolWarning.Visibility = Visibility.Collapsed;
        }
        _currentProfile.Name = ProviderNameTextBox.Text.Trim();
        _currentProfile.BaseUrl = newBaseUrl;
        _currentProfile.Model = ModelTextBox.Text.Trim();
        _currentProfile.ApiKey = ApiKeyPasswordBox.Password;
    }

    private void AddProvider_Click(object sender, RoutedEventArgs e)
    {
        if (_isTesting || _isFetchingModels)
            return;
        var menu = new ContextMenu();
        foreach (AiProviderPreset preset in AiProviderPresets.All)
        {
            var item = new MenuItem { Header = preset.Name, Tag = preset };
            item.Click += (_, _) => AddProvider((AiProviderPreset)item.Tag);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var custom = new MenuItem { Header = "自定义 OpenAI 兼容服务" };
        custom.Click += (_, _) => AddCustomProvider();
        menu.Items.Add(custom);
        menu.PlacementTarget = AddProviderButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void AddProvider(AiProviderPreset preset)
    {
        SaveFieldsToCurrentProfile();
        var profile = new AiProviderProfile
        {
            Name = preset.Name,
            BaseUrl = preset.BaseUrl,
            Protocol = AiProviderProtocol.OpenAiCompatible
        };
        _providers.Add(profile);
        ProviderListBox.SelectedItem = profile;
        LoadSelectedProfile();
    }

    private void AddCustomProvider()
    {
        SaveFieldsToCurrentProfile();
        var profile = new AiProviderProfile
        {
            Name = "自定义服务商",
            BaseUrl = "",
            Protocol = AiProviderProtocol.OpenAiCompatible
        };
        _providers.Add(profile);
        ProviderListBox.SelectedItem = profile;
        LoadSelectedProfile();
    }

    private void RemoveProvider_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProfile is null || _providers.Count <= 1 || _isTesting || _isFetchingModels)
            return;
        MessageBoxResult result = MessageBox.Show(this,
            $"确定删除“{_currentProfile.Name}”配置吗？此操作不会影响其他服务商。",
            "删除服务商", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
            return;
        int index = _providers.IndexOf(_currentProfile);
        _providers.Remove(_currentProfile);
        ProviderListBox.SelectedIndex = Math.Min(index, _providers.Count - 1);
        LoadSelectedProfile();
    }

    private async void FetchModels_Click(object sender, RoutedEventArgs e)
    {
        SaveFieldsToCurrentProfile();
        AiProviderProfile? profile = _currentProfile;
        if (profile is null || _isFetchingModels || _isTesting)
            return;
        try
        {
            AiProviderProfileValidator.ValidateForModelDiscovery(profile);
            _isFetchingModels = true;
            SetEditorEnabled(true);
            StatusTextBlock.Text = "正在获取模型列表……";
            ModelCountTextBlock.Text = "正在读取服务商模型信息……";
            IReadOnlyList<AiProviderModel> discovered = await _modelDiscovery.FetchModelsAsync(profile);
            IReadOnlyList<AiProviderModel> models = await _modelCatalogEnrichment.EnrichAsync(profile, discovered);
            profile.AvailableModels = models.ToList();
            ModelCountTextBlock.Text = models.Count == 0
                ? "请求成功，但没有返回模型；可以手动填写模型 ID。"
                : $"获取成功：发现 {models.Count} 个模型。";
            StatusTextBlock.Text = models.Count == 0
                ? "当前账号未返回可用模型。"
                : "已打开模型列表。选择一个对话模型后回填模型 ID。";
            if (models.Count > 0)
            {
                var picker = new AiModelPickerWindow(models, profile.Model) { Owner = this };
                if (picker.ShowDialog() == true && picker.SelectedModel is AiProviderModel selected)
                {
                    ModelTextBox.Text = selected.Id;
                    profile.Model = selected.Id;
                    StatusTextBlock.Text = $"已选择模型：{selected.Id}。保存后生效。";
                }
            }
        }
        catch (Exception ex)
        {
            ModelCountTextBlock.Text = "模型列表获取失败。可以检查接口地址和 API Key，或手动填写模型 ID。";
            StatusTextBlock.Text = "获取模型失败：" + ex.Message;
        }
        finally
        {
            _isFetchingModels = false;
            SetEditorEnabled(_currentProfile is not null);
        }
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
            SetEditorEnabled(true);
            StatusTextBlock.Text = "正在通过 OpenAI Chat Completions 接口测试连接……";
            await new AiTranslationService(logStore: new AiTranslationLogStore())
                .TestConnectionAsync(_currentProfile);
            StatusTextBlock.Text = "连接成功。测试请求已写入 AI 翻译记录。";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = "连接测试失败：" + ex.Message;
        }
        finally
        {
            _isTesting = false;
            SetEditorEnabled(_currentProfile is not null);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_isFetchingModels || _isTesting)
                throw new InvalidOperationException("请等待当前 API 请求完成后再保存。");
            SaveFieldsToCurrentProfile();
            if (_providers.Count == 0)
                throw new InvalidOperationException("至少需要保留一个服务商配置。");
            foreach (AiProviderProfile provider in _providers)
                AiProviderProfileValidator.ValidateForStorage(provider);
            _settings.Providers = _providers.ToList();
            _settings.ActiveProviderId = _currentProfile?.Id;
            _store.Save(_settings);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "保存 AI 配置失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}