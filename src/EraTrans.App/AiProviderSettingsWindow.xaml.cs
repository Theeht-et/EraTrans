using EraTrans.Core;
using System.Collections.ObjectModel;
using System.Windows;

namespace EraTrans.App;

public partial class AiProviderSettingsWindow : Window
{
    private readonly AiProviderSettingsStore _store = new();
    private readonly AiProviderSettings _settings;
    private readonly ObservableCollection<AiProviderProfile> _providers = [];
    private AiProviderProfile? _currentProfile;
    private bool _loadingProfile;
    private bool _isTesting;

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
        StatusTextBlock.Text = _currentProfile is null
            ? "请新增服务商并填写接口信息。"
            : "修改配置后请先测试连接，再保存。";

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

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        SaveFieldsToCurrentProfile();
        if (_currentProfile is null || _isTesting)
            return;

        try
        {
            AiProviderProfileValidator.Validate(_currentProfile);
            _isTesting = true;
            TestButton.IsEnabled = false;
            StatusTextBlock.Text = "正在测试 API 连接……";

            await new AiTranslationService().TestConnectionAsync(_currentProfile);

            StatusTextBlock.Text = "连接成功，模型已返回响应。";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = "连接测试失败：" + ex.Message;
        }
        finally
        {
            _isTesting = false;
            TestButton.IsEnabled = true;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
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

            StatusTextBlock.Text = "配置已加密保存。";
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