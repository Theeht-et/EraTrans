using EraTrans.Core;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

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
    private bool _loadFailed;
    private bool _isTesting;
    private bool _isFetchingModels;
    private string? _activeProviderId;

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
            _loadFailed = true;
            MessageBox.Show(this,
                ex.Message + "\n\n为防止覆盖无法读取的原配置，本次设置窗口将以只读方式打开。请先恢复原 Windows 用户或备份配置文件。",
                "读取 AI 配置失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        _settings.EnsureBuiltInProviders();
        _activeProviderId = _settings.ActiveProviderId;
        foreach (AiProviderProfile provider in _settings.Providers)
            _providers.Add(provider);

        ProviderListBox.ItemsSource = _providers;
        AiProviderProfile? initial = _providers.FirstOrDefault(provider =>
            provider.IsReadyForTranslation &&
            string.Equals(provider.Id, _activeProviderId, StringComparison.Ordinal))
            ?? _providers.FirstOrDefault(provider => provider.IsReadyForTranslation)
            ?? _providers.FirstOrDefault(provider => provider.IsEnabled)
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
        UpdateActiveProviderStatus();
        SetEditorEnabled(hasProfile);
    }

    private void SetEditorEnabled(bool hasProfile)
    {
        bool requestIdle = !_isFetchingModels && !_isTesting;
        bool idle = requestIdle && !_loadFailed;
        CancelWindowButton.IsEnabled = requestIdle;
        ProviderListBox.IsEnabled = idle;
        ProviderNameTextBox.IsEnabled = hasProfile && idle && _currentProfile?.IsBuiltIn != true;
        BaseUrlTextBox.IsEnabled = hasProfile && idle;
        ApiKeyPasswordBox.IsEnabled = hasProfile && idle;
        ModelTextBox.IsEnabled = hasProfile && idle;
        FetchModelsButton.IsEnabled = hasProfile && idle;
        TestButton.IsEnabled = hasProfile && idle;
        RestoreDefaultBaseUrlButton.Visibility = _currentProfile?.IsBuiltIn == true
            ? Visibility.Visible
            : Visibility.Collapsed;
        RestoreDefaultBaseUrlButton.IsEnabled = hasProfile && idle && _currentProfile?.IsBuiltIn == true;
        SaveButton.IsEnabled = idle;
        RemoveProviderButton.Content = _currentProfile?.IsBuiltIn == true
            ? "默认服务商不可删除"
            : "删除当前自定义服务商";
        RemoveProviderButton.IsEnabled =
            hasProfile && idle && _currentProfile is not null && !_currentProfile.IsBuiltIn;
        AddProviderButton.IsEnabled = idle;

        int index = _currentProfile is null ? -1 : _providers.IndexOf(_currentProfile);
        MoveUpButton.IsEnabled = idle && index > 0;
        MoveDownButton.IsEnabled = idle && index >= 0 && index < _providers.Count - 1;
        SetCurrentProviderButton.IsEnabled =
            hasProfile && idle &&
            _currentProfile!.IsReadyForTranslation &&
            !string.Equals(_currentProfile.Id, _activeProviderId, StringComparison.Ordinal);
        SetCurrentProviderButton.Content =
            string.Equals(_currentProfile?.Id, _activeProviderId, StringComparison.Ordinal)
                ? "当前翻译服务"
                : "设为翻译服务";
    }

    private void UpdateActiveProviderStatus()
    {
        ProviderListBox.Items.Refresh();
        AiProviderProfile? active = _providers.FirstOrDefault(provider =>
            provider.IsReadyForTranslation &&
            string.Equals(provider.Id, _activeProviderId, StringComparison.Ordinal));

        ActiveProviderStatusTextBlock.Text = active is null
            ? "当前翻译服务：未配置"
            : $"当前翻译服务：{active.Name} / {active.Model}";
        SetEditorEnabled(_currentProfile is not null);
    }

    private void RestoreDefaultBaseUrl_Click(object sender, RoutedEventArgs e)
    {
        if (_loadFailed || _isTesting || _isFetchingModels || _currentProfile is not { IsBuiltIn: true })
            return;

        AiProviderPreset? preset = AiProviderPresets.All.FirstOrDefault(item =>
            string.Equals(item.Name, _currentProfile.Name, StringComparison.OrdinalIgnoreCase));
        if (preset is null)
            return;

        BaseUrlTextBox.Text = preset.BaseUrl;
        _currentProfile.BaseUrl = preset.BaseUrl;
        _currentProfile.RequiresEndpointReview = false;
        LegacyProtocolWarning.Visibility = Visibility.Collapsed;
        StatusTextBlock.Text = "已恢复该内置服务商的默认 API 地址。";
        UpdateActiveProviderStatus();
    }

    private void SetCurrentProvider_Click(object sender, RoutedEventArgs e)
    {
        if (_loadFailed || _isTesting || _isFetchingModels || _currentProfile is null)
            return;

        SaveFieldsToCurrentProfile();
        if (!_currentProfile.IsReadyForTranslation)
        {
            MessageBox.Show(this,
                "请先填写有效的 OpenAI 兼容 API 地址、模型名称和 API Key，并处理旧协议地址提示。",
                "服务商尚未就绪", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _activeProviderId = _currentProfile.Id;
        StatusTextBlock.Text = "已设为当前翻译服务。保存后生效。";
        UpdateActiveProviderStatus();
    }

    private void MoveProviderUp_Click(object sender, RoutedEventArgs e) => MoveSelectedProvider(-1);

    private void MoveProviderDown_Click(object sender, RoutedEventArgs e) => MoveSelectedProvider(1);

    private void MoveSelectedProvider(int offset)
    {
        if (_loadFailed || _isTesting || _isFetchingModels || _currentProfile is null)
            return;

        SaveFieldsToCurrentProfile();
        int oldIndex = _providers.IndexOf(_currentProfile);
        int newIndex = oldIndex + offset;
        if (oldIndex < 0 || newIndex < 0 || newIndex >= _providers.Count)
            return;

        _providers.Move(oldIndex, newIndex);
        ProviderListBox.SelectedItem = _currentProfile;
        UpdateActiveProviderStatus();
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

        if (string.Equals(_activeProviderId, _currentProfile.Id, StringComparison.Ordinal) &&
            !_currentProfile.IsReadyForTranslation)
        {
            _activeProviderId = _providers.FirstOrDefault(provider =>
                provider.Id != _currentProfile.Id && provider.IsReadyForTranslation)?.Id;
        }

        // AiProviderProfile is intentionally a simple persistence model (not an
        // INotifyPropertyChanged view model), so refresh the left-hand summary.
        UpdateActiveProviderStatus();
    }

    private void AddProvider_Click(object sender, RoutedEventArgs e)
    {
        if (_isTesting || _isFetchingModels)
            return;

        SaveFieldsToCurrentProfile();
        int suffix = 1;
        string customName = "自定义服务商";
        while (_providers.Any(provider =>
                   string.Equals(provider.Name, customName, StringComparison.OrdinalIgnoreCase)))
        {
            suffix++;
            customName = $"自定义服务商 {suffix}";
        }

        var profile = new AiProviderProfile
        {
            Name = customName,
            BaseUrl = "",
            Protocol = AiProviderProtocol.OpenAiCompatible,
            IsBuiltIn = false,
            IsEnabled = true
        };

        _providers.Insert(0, profile);
        ProviderListBox.SelectedItem = profile;
        LoadSelectedProfile();
    }

    private void ToggleProviderEnabled_Click(object sender, RoutedEventArgs e)
    {
        if (_isTesting || _isFetchingModels ||
            sender is not Button { Tag: AiProviderProfile profile })
            return;

        SaveFieldsToCurrentProfile();
        profile.IsEnabled = !profile.IsEnabled;

        if (!profile.IsEnabled &&
            string.Equals(_activeProviderId, profile.Id, StringComparison.Ordinal))
        {
            _activeProviderId = _providers.FirstOrDefault(provider =>
                provider.Id != profile.Id && provider.IsReadyForTranslation)?.Id;
        }
        else if (profile.IsEnabled && profile.IsReadyForTranslation && _activeProviderId is null)
        {
            _activeProviderId = profile.Id;
        }

        ProviderListBox.Items.Refresh();
        UpdateActiveProviderStatus();

        if (_currentProfile?.Id == profile.Id)
        {
            StatusTextBlock.Text = profile.IsEnabled
                ? "服务商已启用；只有配置完整且设为当前翻译服务后才会用于翻译。"
                : "服务商已禁用，不会用于 AI 翻译；配置仍会保留。";
        }
    }

    private void RemoveProvider_Click(object sender, RoutedEventArgs e)
    {
        if (_currentProfile is null || _currentProfile.IsBuiltIn ||
            _isTesting || _isFetchingModels)
            return;
        MessageBoxResult result = MessageBox.Show(this,
            $"确定删除“{_currentProfile.Name}”配置吗？此操作不会影响其他服务商。",
            "删除服务商", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
            return;
        int index = _providers.IndexOf(_currentProfile);
        string removedId = _currentProfile.Id;
        _providers.Remove(_currentProfile);
        if (string.Equals(_activeProviderId, removedId, StringComparison.Ordinal))
        {
            _activeProviderId = _providers.FirstOrDefault(provider => provider.IsReadyForTranslation)?.Id;
        }
        ProviderListBox.SelectedIndex = Math.Min(index, _providers.Count - 1);
        LoadSelectedProfile();
        UpdateActiveProviderStatus();
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
                    UpdateActiveProviderStatus();
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
            if (_loadFailed)
                throw new InvalidOperationException("原 AI 配置无法读取，已禁止覆盖保存。请取消关闭窗口并恢复原配置后重试。");
            if (_isFetchingModels || _isTesting)
                throw new InvalidOperationException("请等待当前 API 请求完成后再保存。");
            SaveFieldsToCurrentProfile();
            if (_providers.Count == 0)
                throw new InvalidOperationException("至少需要保留一个服务商配置。");
            foreach (AiProviderProfile provider in _providers)
                AiProviderProfileValidator.ValidateForStorage(provider);

            AiProviderProfile? active = _providers.FirstOrDefault(provider =>
                    provider.IsReadyForTranslation &&
                    string.Equals(provider.Id, _activeProviderId, StringComparison.Ordinal)) ??
                (_currentProfile?.IsReadyForTranslation == true ? _currentProfile : null) ??
                _providers.FirstOrDefault(provider => provider.IsReadyForTranslation);

            _settings.Providers = _providers.ToList();
            _settings.ActiveProviderId = active?.Id;
            _store.Save(_settings);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "保存 AI 配置失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_isTesting || _isFetchingModels)
            return;
        DialogResult = false;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_isTesting && !_isFetchingModels)
            return;

        e.Cancel = true;
        StatusTextBlock.Text = "当前 API 请求尚未结束，请等待请求完成后再关闭设置窗口。";
    }
}