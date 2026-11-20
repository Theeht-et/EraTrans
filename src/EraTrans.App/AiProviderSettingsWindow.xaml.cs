using EraTrans.Core;
using System.Windows;
using System.Windows.Controls;

namespace EraTrans.App;

public partial class AiProviderSettingsWindow : Window
{
    private readonly AiProviderSettingsStore _store = new();
    private readonly AiModelDiscoveryService _modelDiscovery = new();
    private readonly AiModelCatalogEnrichmentService _modelCatalogEnrichment = new();
    private readonly AiProviderSettings _settings;
    private readonly List<AiProviderProfile> _providers = [];
    private AiProviderProfile? _currentProfile;
    private bool _loadingProfile;
    private bool _isTesting;
    private bool _isFetchingModels;

    private sealed record ProtocolOption(string Name, AiProviderProtocol Value);

    public AiProviderSettingsWindow()
    {
        InitializeComponent();

        ProtocolComboBox.ItemsSource = new[]
        {
            new ProtocolOption("OpenAI 兼容（Chat Completions）", AiProviderProtocol.OpenAiCompatible),
            new ProtocolOption("Google Gemini 原生 API", AiProviderProtocol.GoogleGemini),
            new ProtocolOption("Anthropic Claude 原生 API", AiProviderProtocol.AnthropicClaude)
        };
        ProviderPresetComboBox.ItemsSource = AiProviderPresets.All;

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

        _providers.AddRange(_settings.Providers);
        ProviderComboBox.ItemsSource = _providers;

        if (_providers.Count == 0)
        {
            AddProvider();
        }
        else
        {
            ProviderComboBox.SelectedItem =
                _providers.FirstOrDefault(provider => provider.Id == _settings.ActiveProviderId)
                ?? _providers[0];
            LoadSelectedProfile();
        }
    }

    private void ProviderComboBox_SelectionChanged(
        object sender, SelectionChangedEventArgs e)
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
        ProtocolComboBox.SelectedValue =
            _currentProfile?.Protocol ?? AiProviderProtocol.OpenAiCompatible;
        ProviderPresetComboBox.SelectedItem = null;

        int cachedCount = _currentProfile?.AvailableModels.Count ?? 0;
        ModelCountTextBlock.Text = cachedCount == 0
            ? "模型列表尚未获取。可以获取模型，也可以直接填写模型 ID。"
            : $"已缓存 {cachedCount} 个模型。点击“获取模型列表”可刷新并打开模型选择窗口。";
        StatusTextBlock.Text = "";
        UpdateProtocolHint();
        _loadingProfile = false;
    }

    private void SaveFieldsToCurrentProfile()
    {
        if (_currentProfile is null || _loadingProfile)
            return;

        _currentProfile.Name = NameTextBox.Text.Trim();
        _currentProfile.BaseUrl = BaseUrlTextBox.Text.Trim();
        _currentProfile.Model = ModelTextBox.Text.Trim();
        _currentProfile.ApiKey = ApiKeyPasswordBox.Password;
        if (ProtocolComboBox.SelectedValue is AiProviderProtocol protocol)
            _currentProfile.Protocol = protocol;
    }

    private void AddProvider_Click(object sender, RoutedEventArgs e) => AddProvider();

    private void AddProvider()
    {
        SaveFieldsToCurrentProfile();
        var provider = new AiProviderProfile
        {
            Name = $"新服务商 {_providers.Count + 1}",
            Protocol = AiProviderProtocol.OpenAiCompatible
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

    private void ProviderPresetComboBox_SelectionChanged(
        object sender, SelectionChangedEventArgs e)
    {
        if (_loadingProfile ||
            ProviderPresetComboBox.SelectedItem is not AiProviderPreset preset ||
            _currentProfile is null)
            return;

        SaveFieldsToCurrentProfile();
        _loadingProfile = true;
        _currentProfile.Name = preset.Name;
        _currentProfile.BaseUrl = preset.BaseUrl;
        _currentProfile.Protocol = preset.Protocol;
        NameTextBox.Text = preset.Name;
        BaseUrlTextBox.Text = preset.BaseUrl;
        ProtocolComboBox.SelectedValue = preset.Protocol;
        ModelTextBox.Text = "";
        _currentProfile.Model = "";
        UpdateProtocolHint();
        StatusTextBlock.Text = "已应用服务商模板。请填写此平台对应的 API Key，再获取模型列表。";
        _loadingProfile = false;
    }

    private void ProtocolComboBox_SelectionChanged(
        object sender, SelectionChangedEventArgs e)
    {
        if (_loadingProfile)
            return;

        SaveFieldsToCurrentProfile();
        UpdateProtocolHint();
    }

    private void UpdateProtocolHint()
    {
        AiProviderProtocol protocol = ProtocolComboBox.SelectedValue is AiProviderProtocol value
            ? value
            : AiProviderProtocol.OpenAiCompatible;
        ProtocolHintTextBlock.Text = protocol switch
        {
            AiProviderProtocol.GoogleGemini =>
                "Google Gemini：使用原生 generateContent 与 /models 接口，API Key 通过 x-goog-api-key 请求头发送。",
            AiProviderProtocol.AnthropicClaude =>
                "Anthropic Claude：使用原生 Messages 与 /v1/models 接口，鉴权使用 x-api-key。",
            _ =>
                "OpenAI 兼容协议：使用 Bearer API Key、GET /models 和 Chat Completions。DeepSeek、通义千问、智谱、Kimi、火山方舟、硅基流动等可使用对应模板。"
        };
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
            if (string.IsNullOrWhiteSpace(profile.ApiKey))
                throw new InvalidOperationException("请先填写 API Key。");

            _isFetchingModels = true;
            FetchModelsButton.IsEnabled = false;
            TestButton.IsEnabled = false;
            StatusTextBlock.Text = "正在获取服务商模型列表……";
            ModelCountTextBlock.Text = "正在获取模型信息……";

            IReadOnlyList<AiProviderModel> discovered =
                await _modelDiscovery.FetchModelsAsync(profile);
            IReadOnlyList<AiProviderModel> models =
                await _modelCatalogEnrichment.EnrichAsync(profile, discovered);
            profile.AvailableModels = models.ToList();

            if (!ReferenceEquals(_currentProfile, profile))
                return;

            ModelCountTextBlock.Text = models.Count == 0
                ? "接口请求成功，但没有返回模型。仍可手动填写模型 ID。"
                : $"获取成功：发现 {models.Count} 个模型。";
            StatusTextBlock.Text = models.Count == 0
                ? "当前账号未返回可用模型。"
                : "已打开模型列表。选择模型后即可回填模型 ID。";

            if (models.Count > 0)
            {
                var picker = new AiModelPickerWindow(models, profile.Model) { Owner = this };
                if (picker.ShowDialog() == true && picker.SelectedModel is AiProviderModel selected)
                {
                    ModelTextBox.Text = selected.Id;
                    profile.Model = selected.Id;
                    StatusTextBlock.Text = $"已选择模型：{selected.Id}。保存服务商配置后生效。";
                }
            }
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_currentProfile, profile))
            {
                ModelCountTextBlock.Text = "模型列表获取失败。可检查协议、地址和 Key，或继续手动填写模型 ID。";
                StatusTextBlock.Text = "获取模型失败：" + ex.Message;
            }
        }
        finally
        {
            _isFetchingModels = false;
            FetchModelsButton.IsEnabled = true;
            TestButton.IsEnabled = true;
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
            TestButton.IsEnabled = false;
            FetchModelsButton.IsEnabled = false;
            StatusTextBlock.Text = "正在测试当前协议的模型请求……";

            await new AiTranslationService(logStore: new AiTranslationLogStore())
                .TestConnectionAsync(_currentProfile);

            StatusTextBlock.Text = "连接成功，模型已返回响应。测试请求已写入 AI 翻译记录。";
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
                throw new InvalidOperationException("请等待当前 API 请求完成后再保存。");

            SaveFieldsToCurrentProfile();
            if (_providers.Count == 0)
                throw new InvalidOperationException("至少需要保留一个服务商配置。");

            foreach (AiProviderProfile provider in _providers)
                AiProviderProfileValidator.Validate(provider);

            _settings.Providers = _providers.ToList();
            _settings.ActiveProviderId = _currentProfile?.Id;
            _store.Save(_settings);

            StatusTextBlock.Text = "服务商配置和模型缓存已保存。";
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
