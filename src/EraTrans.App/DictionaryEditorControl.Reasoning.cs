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

        AiProviderProfile? profile = TryGetActiveProvider();
        if (profile is null)
        {
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

            StatusTextBlock.Text = mode == AiReasoningMode.Auto
                ? "推理设置：自动（不附加任何推理字段）。"
                : $"推理设置：{DescribeReasoningMode(mode)}（{AiReasoning.PresetLabel(profile.BaseUrl)}）。";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = "推理设置保存失败。";
            MessageBox.Show(
                OwnerWindow, ex.Message, "推理设置保存失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
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
            AiProviderProfile? profile = TryGetActiveProvider();
            AiReasoningMode mode = profile is null
                ? AiReasoningMode.Auto
                : ResolveStoredReasoningOptions(profile).Mode;

            foreach (ComboBoxItem item in AiReasoningComboBox.Items.OfType<ComboBoxItem>())
            {
                if (MatchesMode(item, mode))
                {
                    AiReasoningComboBox.SelectedItem = item;
                    break;
                }
            }

            AiReasoningComboBox.IsEnabled = profile is not null && !_isTranslating;
            AiReasoningComboBox.ToolTip = profile is null
                ? "尚未配置 AI 服务商。"
                : $"自动：使用服务商默认设置，不附加推理字段。开启/关闭：向请求附加 {AiReasoning.PresetLabel(profile.BaseUrl)}。";
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

    private AiReasoningOptions ResolveStoredReasoningOptions(AiProviderProfile profile)
    {
        try
        {
            Dictionary<string, AiReasoningOptions> settings = _reasoningStore.Load();
            return settings.TryGetValue(profile.Id, out AiReasoningOptions? options)
                ? options
                : AiReasoningOptions.Auto;
        }
        catch
        {
            return AiReasoningOptions.Auto;
        }
    }

    private AiProviderProfile? TryGetActiveProvider()
    {
        try
        {
            return _providerStore.Load().ActiveProvider;
        }
        catch
        {
            return null;
        }
    }

    private static string DescribeReasoningMode(AiReasoningMode mode) => mode switch
    {
        AiReasoningMode.On => "开启",
        AiReasoningMode.Off => "关闭",
        _ => "自动"
    };
}
