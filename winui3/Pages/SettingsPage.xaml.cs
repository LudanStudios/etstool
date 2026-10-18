using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using ETSAssistant.WinUI3.Services;

namespace ETSAssistant.WinUI3.Pages;

public sealed partial class SettingsPage : Page
{
    private readonly AssistantService _service = AssistantService.Current;

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
            SaveInfoBar.Severity = warning.Length == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
            SaveInfoBar.Title = warning.Length == 0 ? "已保存" : "设置已保存，登录启动项未更新";
            SaveInfoBar.Message = warning.Length == 0 ? "配置和 Windows 登录启动项已更新。" : warning;
            SaveInfoBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            SaveInfoBar.Severity = InfoBarSeverity.Error;
            SaveInfoBar.Title = "保存失败";
            SaveInfoBar.Message = ex.Message;
            SaveInfoBar.IsOpen = true;
        }
    }
}
