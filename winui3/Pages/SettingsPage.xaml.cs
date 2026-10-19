using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using ETSAssistant.WinUI3.Services;

namespace ETSAssistant.WinUI3.Pages;

public sealed partial class SettingsPage : Page
{
    private readonly AssistantService _service = AssistantService.Current;
    private bool _loading;

    public SettingsPage()
    {
        InitializeComponent();
        LoadValues();
    }

    private void LoadValues()
    {
        var settings = _service.LoadSettings();
        AccuracySlider.Value = settings.Accuracy;
        AccuracyText.Text = $"{Math.Round(settings.Accuracy * 100):0}%";
        SeedBox.Text = settings.Seed;
        SkipBox.Value = settings.SkipInterval;
        AutostartCheck.IsChecked = settings.StartAtLogin;
        EtsPathText.Text = _service.EtsExe ?? "未找到 ETS 安装目录。";
        _loading = true;
        SpeakingModeBox.SelectedIndex = AppPreferencesService.Current.SpeakingMode == "flexible" ? 1 : 0;
        _loading = false;
        UpdateApiKeyStatus();
    }

    private void SpeakingModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (SpeakingModeBox.SelectedItem is ComboBoxItem { Tag: string mode })
            AppPreferencesService.Current.SetSpeakingMode(mode);
    }

    private void UpdateApiKeyStatus() =>
        ApiKeyStatusText.Text = ApiKeyStore.HasKey ? "状态：已保存（本机加密）" : "状态：未配置";

    private void SaveApiKey_Click(object sender, RoutedEventArgs e)
    {
        string key = ApiKeyBox.Password.Trim();
        if (key.Length == 0)
        {
            InfoBarPresenter.Show(ApiKeyInfoBar, "尚未填写 API Key",
                "请先粘贴 DeepSeek API Key，再点击保存。", InfoBarSeverity.Warning);
            return;
        }

        try
        {
            ApiKeyStore.Save(key);
            ApiKeyBox.Password = string.Empty;
            UpdateApiKeyStatus();
            InfoBarPresenter.Show(ApiKeyInfoBar, "密钥已保存",
                "已使用 Windows DPAPI 加密保存到本机，测试工具页可以直接测试口语题。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            InfoBarPresenter.Show(ApiKeyInfoBar, "保存失败", ex.Message, InfoBarSeverity.Error);
        }
    }

    private void ClearApiKey_Click(object sender, RoutedEventArgs e)
    {
        ApiKeyStore.Clear();
        ApiKeyBox.Password = string.Empty;
        UpdateApiKeyStatus();
        InfoBarPresenter.Show(ApiKeyInfoBar, "密钥已清除", "本机保存的 DeepSeek API Key 已删除。",
            InfoBarSeverity.Informational);
    }

    private void AccuracySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (AccuracyText is not null) AccuracyText.Text = $"{Math.Round(e.NewValue * 100):0}%";
    }

    private void Rescan_Click(object sender, RoutedEventArgs e)
    {
        _service.RefreshEts();
        EtsPathText.Text = _service.EtsExe ?? "未找到 ETS 安装目录。";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!double.IsFinite(SkipBox.Value) || SkipBox.Value < 0 || SkipBox.Value > 60)
                throw new ArgumentOutOfRangeException(nameof(SkipBox), "跳题间隔必须在 0 到 60 秒之间。");
            var settings = new AssistantSettings(AccuracySlider.Value, SeedBox.Text, SkipBox.Value,
                AutostartCheck.IsChecked == true);
            _service.SaveSettings(settings);
            string warning = _service.TryInstallAutostart(settings.StartAtLogin);
            if (settings.StartAtLogin) _service.EnsureListener();
            InfoBarPresenter.Show(SaveInfoBar, warning.Length == 0 ? "已保存" : "设置已保存，登录启动项未更新",
                warning.Length == 0 ? "配置和 Windows 登录启动项已更新。" : warning,
                warning.Length == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            InfoBarPresenter.Show(SaveInfoBar, "保存失败", ex.Message, InfoBarSeverity.Error);
        }
    }
}
