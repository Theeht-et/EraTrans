using EraTrans.Core;
using System.Windows;
using System.Windows.Controls;

namespace EraTrans.App;

/// <summary>
/// Reasoning is a per-provider request switch: Auto keeps the provider default,
/// On/Off append the field that provider documents. The choice is stored per
/// provider id and applied to every translation request of that provider.
/// </summary>
public partial class DictionaryEditorControl
{
    private readonly AiReasoningSettingsStore _reasoningStore = new();
    private bool _loadingReasoningSelection;

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        Loaded += (_, _) => SyncReasoningSelection();
    }

    private void AiReasoningComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingReasoningSelection || _isTranslating)
            return;

        AiProviderProfile? profile = TryGetEditingProvider();
        if (profile is null)
        {
            StatusTextBlock.Text = "还没有 AI 服务：请先在“AI 服务设置”里添加或启用一个服务商。";
            SyncReasoningSelection();
            return;
        }

        AiReasoningMode mode = SelectedReasoningMode();
        try
        {
            Dictionary<string, AiReasoningOptions> settings = _reasoningStore.Load();
            AiReasoningOptions current = settings.TryGetValue(profile.Id, out AiReasoningOptions? stored)
                ? stored
                : AiReasoningOptions.Auto;
            settings[profile.Id] = current with { Mode = mode };
            _reasoningStore.Save(settings);

            StatusTextBlock.Text = mode switch
            {
                AiReasoningMode.On => $"推理设置：开启（{AiReasoning.PresetLabel(profile.BaseUrl)}）。",
                AiReasoningMode.Off => $"推理设置：关闭（{AiReasoning.PresetLabel(profile.BaseUrl)}）。",
                _ => "推理设置：自动（不附加任何推理字段）。"
            };
        }
        catch (Exception ex)
        {
            // Keep the selection the user just made instead of silently snapping
            // it back to the stored value.
            AiReasoningComboBox.ToolTip = "推理设置保存失败：" + ex.Message;
            StatusTextBlock.Text = "推理设置保存失败，本次选择未写入配置。";
            return;
        }

        SyncReasoningSelection();
    }

    private void AiReasoningComboBox_DropDownOpened(object? sender, EventArgs e) =>
        SyncReasoningSelection();

    private void SyncReasoningSelection()
    {
        if (AiReasoningComboBox is null)
            return;

        _loadingReasoningSelection = true;
        try
        {
            AiProviderProfile? profile = TryGetEditingProvider();
            AiReasoningMode mode = profile is null
                ? AiReasoningMode.Auto
                : ResolveStoredOptions(profile.Id).Mode;

            foreach (ComboBoxItem item in AiReasoningComboBox.Items.OfType<ComboBoxItem>())
            {
                if (MatchesMode(item, mode))
                {
                    AiReasoningComboBox.SelectedItem = item;
                    break;
                }
            }

            AiReasoningComboBox.IsEnabled = profile is not null && !_isTranslating;

            if (profile is null)
            {
                AiReasoningComboBox.ToolTip = "还没有 AI 服务：请先在“AI 服务设置”里添加或启用一个服务商。";
                return;
            }

            string preset = AiReasoning.PresetLabel(profile.BaseUrl);
            AiReasoningComboBox.ToolTip =
                $"当前服务：{profile.Name}" +
                $"\n自动：使用服务商默认设置，不附加推理字段。" +
                $"\n开启 / 关闭：向请求附加 {preset}。";
        }
        finally
        {
            _loadingReasoningSelection = false;
        }
    }

    private static bool MatchesMode(ComboBoxItem item, AiReasoningMode mode) =>
        (item.Tag?.ToString() switch
        {
            "on" => AiReasoningMode.On,
            "off" => AiReasoningMode.Off,
            _ => AiReasoningMode.Auto
        }) == mode;

    private AiReasoningMode SelectedReasoningMode() =>
        (AiReasoningComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
        {
            "on" => AiReasoningMode.On,
            "off" => AiReasoningMode.Off,
            _ => AiReasoningMode.Auto
        };

    private AiReasoningOptions ResolveStoredOptions(string providerId)
    {
        try
        {
            Dictionary<string, AiReasoningOptions> settings = _reasoningStore.Load();
            return settings.TryGetValue(providerId, out AiReasoningOptions? options)
                ? options
                : AiReasoningOptions.Auto;
        }
        catch
        {
            return AiReasoningOptions.Auto;
        }
    }

    /// <summary>
    /// The provider this switch belongs to. It must not require a complete
    /// configuration: reasoning can be chosen while the API key or model is
    /// still being filled in, otherwise the switch reverts to Auto on click.
    /// </summary>
    private AiProviderProfile? TryGetEditingProvider()
    {
        AiProviderSettings settings;
        try
        {
            settings = _providerStore.Load();
        }
        catch
        {
            return null;
        }

        if (settings.ActiveProvider is not null)
            return settings.ActiveProvider;

        AiProviderProfile? byId = settings.Providers.FirstOrDefault(provider =>
            string.Equals(provider.Id, settings.ActiveProviderId, StringComparison.Ordinal));
        if (byId is not null)
            return byId;

        return settings.Providers.FirstOrDefault(provider => provider.IsEnabled)
            ?? settings.Providers.FirstOrDefault();
    }
}
