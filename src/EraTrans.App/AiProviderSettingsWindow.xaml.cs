using EraTrans.Core;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

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
    private bool _refreshingProviderList;
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
        if (_loadingProfile || _refreshingProviderList || _isTesting || _isFetchingModels)
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
        ChatCompletionsPathTextBox.Text = _currentProfile?.ChatCompletionsPath ?? "/chat/completions";
        ModelsPathTextBox.Text = _currentProfile?.ModelsPath ?? "/models";
        ApiKeyPasswordBox.Password = _currentProfile?.ApiKey ?? "";
        ModelTextBox.Text = _currentProfile?.Model ?? "";
        UpdateReasoningControls();
        UpdateEndpointPreview();
        LegacyProtocolWarning.Visibility = _currentProfile?.RequiresEndpointReview == true
            ? Visibility.Visible : Visibility.Collapsed;
        int cachedCount = _currentProfile?.AvailableModels.Count ?? 0;
        ModelCountTextBlock.Text = cachedCount == 0
            ? "模型列表尚未获取，可直接填写模型 ID。"
            : $"已缓存 {cachedCount} 个模型。点击“获取模型”刷新并选择。";
        StatusTextBlock.Text = "";
        UpdateActiveProviderStatus();
        _loadingProfile = false;
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
        ChatCompletionsPathTextBox.IsEnabled = hasProfile && idle;
        ModelsPathTextBox.IsEnabled = hasProfile && idle;
        ApiKeyPasswordBox.IsEnabled = hasProfile && idle;
        ModelTextBox.IsEnabled = hasProfile && idle;
        bool reasoningAvailable = HasSupportedReasoningControls();
        ReasoningModeComboBox.IsEnabled = hasProfile && idle && reasoningAvailable;
        ReasoningEffortComboBox.IsEnabled = hasProfile && idle && reasoningAvailable &&
            GetSelectedReasoningMode() == "effort";
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
        _refreshingProviderList = true;
        try
        {
            ProviderListBox.Items.Refresh();
        }
        finally
        {
            _refreshingProviderList = false;
        }

        AiProviderProfile? active = _providers.FirstOrDefault(provider =>
            provider.IsReadyForTranslation &&
            string.Equals(provider.Id, _activeProviderId, StringComparison.Ordinal));

        ActiveProviderStatusTextBlock.Text = active is null
            ? "当前翻译服务：未配置"
            : $"当前翻译服务：{active.Name} / {active.Model}";
        SetEditorEnabled(_currentProfile is not null);
    }

    private void ConnectionEndpointTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loadingProfile)
        {
            UpdateEndpointPreview();
            UpdateReasoningControls();
            SetEditorEnabled(_currentProfile is not null);
        }
    }

    private void UpdateEndpointPreview()
    {
        string chat = PreviewEndpoint(BaseUrlTextBox.Text, ChatCompletionsPathTextBox.Text,
            "/chat/completions");
        string models = PreviewEndpoint(BaseUrlTextBox.Text, ModelsPathTextBox.Text, "/models");
        EndpointPreviewTextBlock.Text =
            $"连接测试/翻译：{chat}\n获取模型：{models}";
    }

    private static string PreviewEndpoint(string baseUrl, string path, string fallbackPath)
    {
        try
        {
            string normalizedPath = AiProviderProfileValidator.NormalizeApiPath(path, fallbackPath);
            if (string.IsNullOrWhiteSpace(baseUrl))
                return $"(请填写 Base URL){normalizedPath}";

            return AiProviderProfileValidator.BuildApiEndpoint(baseUrl, normalizedPath, fallbackPath);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UriFormatException)
        {
            return $"(地址或路径无效：{ex.Message})";
        }
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
        UpdateEndpointPreview();
        StatusTextBlock.Text = "已恢复该内置服务商的默认 Base URL 和 API 路径。";
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

    private Point _providerDragStart;
    private bool _providerDragPending;

    private void ProviderListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _providerDragStart = e.GetPosition(ProviderListBox);
        DependencyObject? source = e.OriginalSource as DependencyObject;
        ListBoxItem? item = source is null
            ? null
            : ItemsControl.ContainerFromElement(ProviderListBox, source) as ListBoxItem;
        _providerDragPending = item is not null && FindVisualParent<Button>(source) is null;

        if (item?.DataContext is AiProviderProfile profile)
            ProviderListBox.SelectedItem = profile;
    }

    private void ProviderListBox_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_providerDragPending || e.LeftButton != MouseButtonState.Pressed ||
            _loadFailed || _isTesting || _isFetchingModels ||
            ProviderListBox.SelectedItem is not AiProviderProfile profile)
            return;

        Point current = e.GetPosition(ProviderListBox);
        if (Math.Abs(current.X - _providerDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _providerDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _providerDragPending = false;
        var data = new DataObject(typeof(AiProviderProfile), profile);
        DragDrop.DoDragDrop(ProviderListBox, data, DragDropEffects.Move);
    }

    private void ProviderListBox_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = !_loadFailed && !_isTesting && !_isFetchingModels &&
                    e.Data.GetDataPresent(typeof(AiProviderProfile))
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void ProviderListBox_Drop(object sender, DragEventArgs e)
    {
        _providerDragPending = false;
        if (_loadFailed || _isTesting || _isFetchingModels ||
            !e.Data.GetDataPresent(typeof(AiProviderProfile)) ||
            e.Data.GetData(typeof(AiProviderProfile)) is not AiProviderProfile dragged)
            return;

        int oldIndex = _providers.IndexOf(dragged);
        if (oldIndex < 0)
            return;

        int targetIndex;
        DependencyObject? source = e.OriginalSource as DependencyObject;
        ListBoxItem? targetItem = source is null
            ? null
            : ItemsControl.ContainerFromElement(ProviderListBox, source) as ListBoxItem;

        if (targetItem?.DataContext is AiProviderProfile target)
        {
            targetIndex = _providers.IndexOf(target);
            Point position = e.GetPosition(targetItem);
            if (position.Y >= targetItem.ActualHeight / 2)
                targetIndex++;
        }
        else
        {
            targetIndex = _providers.Count;
        }

        if (targetIndex > oldIndex)
            targetIndex--;
        targetIndex = Math.Clamp(targetIndex, 0, _providers.Count - 1);
        if (targetIndex == oldIndex)
            return;

        SaveFieldsToCurrentProfile();
        _providers.Move(oldIndex, targetIndex);
        ProviderListBox.SelectedItem = dragged;
        UpdateActiveProviderStatus();
        StatusTextBlock.Text = "服务商顺序已调整，点击“保存”后生效。";
        e.Handled = true;
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T match)
                return match;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    private AiProviderModel? GetSelectedReasoningModel()
    {
        if (_currentProfile is null || string.IsNullOrWhiteSpace(ModelTextBox.Text))
            return null;

        return _currentProfile.AvailableModels.FirstOrDefault(model =>
            string.Equals(model.Id, ModelTextBox.Text.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private List<string> GetSupportedReasoningEfforts()
    {
        AiProviderModel? model = GetSelectedReasoningModel();
        if (model?.ReasoningSupport != true)
            return [];

        return model.ReasoningLevels
            .Select(level => level.Trim().ToLowerInvariant())
            .Where(level => level is "minimal" or "low" or "medium" or "high" or "xhigh" or "max")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private string? GetSelectedReasoningMode() =>
        (ReasoningModeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();

    private bool HasSupportedReasoningControls()
    {
        AiProviderModel? model = GetSelectedReasoningModel();
        return IsModelReasoningDialectCompatible(model) &&
               model?.ReasoningSupport == true &&
               (model.ReasoningCanDisable == true || GetSupportedReasoningEfforts().Count > 0);
    }

    private bool IsModelReasoningDialectCompatible(AiProviderModel? model)
    {
        if (model?.ReasoningSupport != true || !IsKnownReasoningEndpoint())
            return false;

        string? dialect = model.ReasoningDialect;
        if (string.IsNullOrWhiteSpace(dialect))
            return true;

        try
        {
            string host = new Uri(AiProviderProfileValidator.NormalizeBaseUrl(BaseUrlTextBox.Text)).Host;
            return (host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase) &&
                    dialect.Equals("openaiReasoningEffort", StringComparison.OrdinalIgnoreCase)) ||
                   (host.Equals("openrouter.ai", StringComparison.OrdinalIgnoreCase) &&
                    dialect.Equals("openrouterReasoning", StringComparison.OrdinalIgnoreCase)) ||
                   (host.Equals("api.deepseek.com", StringComparison.OrdinalIgnoreCase) &&
                    dialect.Equals("deepseekThinking", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (
            ex is ArgumentException or InvalidOperationException or UriFormatException)
        {
            return false;
        }
    }

    private bool IsKnownReasoningEndpoint()
    {
        try
        {
            string host = new Uri(AiProviderProfileValidator.NormalizeBaseUrl(BaseUrlTextBox.Text)).Host;
            return host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase) ||
                   host.Equals("openrouter.ai", StringComparison.OrdinalIgnoreCase) ||
                   host.Equals("api.deepseek.com", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (
            ex is ArgumentException or InvalidOperationException or UriFormatException)
        {
            return false;
        }
    }

    private void UpdateReasoningControls()
    {
        bool wasLoading = _loadingProfile;
        _loadingProfile = true;
        try
        {
            AiProviderModel? model = GetSelectedReasoningModel();
            bool supported = IsModelReasoningDialectCompatible(model);
            AiModelReasoningSettings? setting = null;
            if (supported && _currentProfile is not null)
                _currentProfile.ReasoningSettingsByModel.TryGetValue(ModelTextBox.Text.Trim(), out setting);

            List<string> efforts = GetSupportedReasoningEfforts();
            ReasoningModeComboBox.Items.Clear();
            if (supported)
            {
                ReasoningModeComboBox.Items.Add(new ComboBoxItem { Content = "自动", Tag = "auto" });
                if (model!.ReasoningCanDisable == true)
                    ReasoningModeComboBox.Items.Add(new ComboBoxItem { Content = "关闭", Tag = "off" });
                if (efforts.Count > 0)
                    ReasoningModeComboBox.Items.Add(new ComboBoxItem { Content = "指定强度", Tag = "effort" });
            }

            ReasoningEffortComboBox.Items.Clear();
            foreach (string effort in efforts)
            {
                ReasoningEffortComboBox.Items.Add(new ComboBoxItem
                {
                    Content = effort switch
                    {
                        "minimal" => "最小",
                        "low" => "低",
                        "medium" => "中",
                        "high" => "高",
                        "xhigh" => "极高",
                        "max" => "最大",
                        _ => effort
                    },
                    Tag = effort
                });
            }

            string mode = setting?.Disabled == true && model?.ReasoningCanDisable == true
                ? "off"
                : setting?.Enabled == true && efforts.Count > 0
                    ? "effort"
                    : "auto";
            ReasoningModeComboBox.SelectedItem = ReasoningModeComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), mode, StringComparison.Ordinal));
            string selectedEffort = setting?.Effort ?? "medium";
            int effortIndex = efforts.FindIndex(value =>
                string.Equals(value, selectedEffort, StringComparison.OrdinalIgnoreCase));
            ReasoningEffortComboBox.SelectedIndex = effortIndex >= 0 ? effortIndex : (efforts.Count > 0 ? 0 : -1);
            ReasoningModeComboBox.IsEnabled = supported && !_loadFailed;
            ReasoningEffortComboBox.IsEnabled = supported && mode == "effort" && efforts.Count > 0 && !_loadFailed;
        }
        finally
        {
            _loadingProfile = wasLoading;
        }
    }

    private void SaveReasoningSetting()
    {
        if (_loadingProfile || _currentProfile is null || !HasSupportedReasoningControls())
            return;

        string modelId = ModelTextBox.Text.Trim();
        if (modelId.Length == 0)
            return;

        string mode = GetSelectedReasoningMode() ?? "auto";
        string effort = (ReasoningEffortComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "medium";
        _currentProfile.ReasoningSettingsByModel[modelId] = new AiModelReasoningSettings
        {
            Enabled = mode == "effort",
            Disabled = mode == "off",
            Effort = effort
        };
        ReasoningEffortComboBox.IsEnabled =
            ReasoningModeComboBox.IsEnabled && mode == "effort";
    }

    private void ReasoningModeChanged(object sender, SelectionChangedEventArgs e)
    {
        SaveReasoningSetting();
        ReasoningEffortComboBox.IsEnabled =
            ReasoningModeComboBox.IsEnabled && GetSelectedReasoningMode() == "effort";
    }

    private void ReasoningEffortChanged(object sender, SelectionChangedEventArgs e) =>
        SaveReasoningSetting();

    private void ModelTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingProfile)
            return;
        UpdateReasoningControls();
        SetEditorEnabled(_currentProfile is not null);
    }

    private void SaveFieldsToCurrentProfile()
    {
        if (_currentProfile is null || _loadingProfile)
            return;
        string newBaseUrl = BaseUrlTextBox.Text.Trim();
        string newChatPath = ChatCompletionsPathTextBox.Text.Trim();
        if (_currentProfile.RequiresEndpointReview &&
            (!string.Equals(_currentProfile.BaseUrl, newBaseUrl, StringComparison.Ordinal) ||
             !string.Equals(_currentProfile.ChatCompletionsPath, newChatPath, StringComparison.Ordinal)))
        {
            _currentProfile.RequiresEndpointReview = false;
            LegacyProtocolWarning.Visibility = Visibility.Collapsed;
        }
        _currentProfile.Name = ProviderNameTextBox.Text.Trim();
        ProviderTitleTextBlock.Text = _currentProfile.Name;
        _currentProfile.BaseUrl = newBaseUrl;
        _currentProfile.ChatCompletionsPath = ChatCompletionsPathTextBox.Text.Trim();
        _currentProfile.ModelsPath = ModelsPathTextBox.Text.Trim();
        _currentProfile.Model = ModelTextBox.Text.Trim();
        SaveReasoningSetting();
        _currentProfile.ApiKey = ApiKeyPasswordBox.Password;
        UpdateEndpointPreview();

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
            ChatCompletionsPath = "/chat/completions",
            ModelsPath = "/models",
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
                    UpdateReasoningControls();
                    SetEditorEnabled(true);
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