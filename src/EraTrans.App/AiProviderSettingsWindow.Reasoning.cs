using EraTrans.Core;
using System.Windows;
using System.Windows.Controls;

namespace EraTrans.App;

/// <summary>
/// Optional per-provider reasoning payloads. Empty means "use the preset for
/// this endpoint"; a non-empty fragment replaces the preset for that side only.
/// The on/off choice itself lives in the dictionary editor so it stays next to
/// the active translation service.
/// </summary>
public partial class AiProviderSettingsWindow
{
    private readonly AiReasoningSettingsStore _reasoningStore = new();
    private bool _loadingReasoningJson;

    private AiProviderProfile? ReasoningProfile => ProviderListBox.SelectedItem as AiProviderProfile;

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        ProviderListBox.SelectionChanged += (_, _) => LoadReasoningJson();
        SaveButton.Click += (_, _) => SaveReasoningJson();
        Loaded += (_, _) => LoadReasoningJson();
    }

    private void LoadReasoningJson()
    {
        if (ReasoningOnJsonTextBox is null || ReasoningOffJsonTextBox is null)
            return;

        _loadingReasoningJson = true;
        try
        {
            AiProviderProfile? profile = ReasoningProfile;
            AiReasoningOptions options = profile is null
                ? AiReasoningOptions.Auto
                : ResolveOptions(profile.Id);

            ReasoningOnJsonTextBox.Text = options.OnJson ?? string.Empty;
            ReasoningOffJsonTextBox.Text = options.OffJson ?? string.Empty;
            UpdateReasoningJsonState(profile);
        }
        finally
        {
            _loadingReasoningJson = false;
        }
    }

    private void ReasoningJson_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingReasoningJson)
            return;

        UpdateReasoningJsonState(ReasoningProfile);
    }

    private void UpdateReasoningJsonState(AiProviderProfile? profile)
    {
        string? onError = Validate(ReasoningOnJsonTextBox.Text);
        string? offError = Validate(ReasoningOffJsonTextBox.Text);
        bool valid = onError is null && offError is null;

        // An unusable fragment would break every request of this provider, so it
        // blocks saving instead of being silently ignored.
        SaveButton.IsEnabled = valid;

        ReasoningJsonHintTextBlock.Text = !valid
            ? "⚠ " + (onError ?? offError) + " 未通过校验，修正后才能保存。"
            : profile is null
                ? "先选择或添加一个服务商。"
                : $"留空时使用默认字段：{AiReasoning.PresetLabel(profile.BaseUrl)}（开启与关闭各一项）。";
    }

    private void SaveReasoningJson()
    {
        AiProviderProfile? profile = ReasoningProfile;
        if (profile is null || string.IsNullOrWhiteSpace(profile.Id))
            return;

        if (Validate(ReasoningOnJsonTextBox.Text) is not null ||
            Validate(ReasoningOffJsonTextBox.Text) is not null)
        {
            return;
        }

        try
        {
            Dictionary<string, AiReasoningOptions> settings = _reasoningStore.Load();
            AiReasoningOptions current = settings.TryGetValue(profile.Id, out AiReasoningOptions? stored)
                ? stored
                : AiReasoningOptions.Auto;

            settings[profile.Id] = current with
            {
                OnJson = Normalize(ReasoningOnJsonTextBox.Text),
                OffJson = Normalize(ReasoningOffJsonTextBox.Text)
            };
            _reasoningStore.Save(settings);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message, "推理字段保存失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string? Validate(string? json) =>
        AiReasoning.TryParseShape(json, out _, out string? error) ? null : error;

    private static string? Normalize(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : json.Trim();

    private AiReasoningOptions ResolveOptions(string providerId)
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
}
