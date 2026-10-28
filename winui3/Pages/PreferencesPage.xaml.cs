using ETSAssistant.WinUI3.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Diagnostics;

namespace ETSAssistant.WinUI3.Pages;

public sealed partial class PreferencesPage : Page
{
    private readonly AppPreferencesService _preferences = AppPreferencesService.Current;
    private bool _loading;
    private bool _usageRefreshing;

    public PreferencesPage()
    {
        InitializeComponent();
        _loading = true;
        ThemeBox.SelectedIndex = _preferences.Theme switch
        {
            "Light" => 1,
            "Dark" => 2,
            _ => 0
        };
        LanguageBox.SelectedIndex = _preferences.Language == "en-US" ? 1 : 0;
        ExperienceModeToggle.IsOn = _preferences.ExperienceModeEnabled;
        AutoUpdateToggle.IsOn = _preferences.AutoUpdateEnabled;
        SetUsageVisibility(_preferences.ExperienceModeEnabled);
        _loading = false;
        LocalizationService.Current.Apply(this);
        RefreshUpdateSettings();
        RefreshExperienceModeAvailability();
        Loaded += PreferencesPage_Loaded;
    }

    private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _preferences.SetTheme(ThemeBox.SelectedIndex switch
        {
            1 => "Light",
            2 => "Dark",
            _ => "System"
        });
    }

    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _preferences.SetLanguage(LanguageBox.SelectedIndex == 1 ? "en-US" : "zh-CN");
    }

    private void ExperienceModeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (_preferences.EnforceExperienceModeCredentialPolicy())
        {
            RefreshExperienceModeAvailability();
            return;
        }
        _preferences.SetExperienceMode(ExperienceModeToggle.IsOn);
        SetUsageVisibility(ExperienceModeToggle.IsOn);
        if (ExperienceModeToggle.IsOn) _ = RefreshUsageAsync();
    }

    private async void PreferencesPage_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshExperienceModeAvailability();
        if (_preferences.ExperienceModeEnabled) await RefreshUsageAsync();
    }

    private void RefreshExperienceModeAvailability()
    {
        bool locked = _preferences.EnforceExperienceModeCredentialPolicy();
        bool wasLoading = _loading;
        _loading = true;
        ExperienceModeToggle.IsOn = _preferences.ExperienceModeEnabled;
        ExperienceModeToggle.IsEnabled = !locked;
        ExperienceModeDescription.Text = LocalizationService.Current.Translate(locked
            ? "DeepSeek 和阿里云 TTS 的个人凭据均已配置，体验模式已自动关闭并禁用。"
            : "未配置个人凭据时允许调用公用 Worker。听力、口语题目、试卷解析及 TTS 文本会发送到服务端；每项服务按设备和 IP 分别限额。");
        SetUsageVisibility(_preferences.ExperienceModeEnabled);
        _loading = wasLoading;
    }

    private async void RefreshUsage_Click(object sender, RoutedEventArgs e) => await RefreshUsageAsync();

    private void SetUsageVisibility(bool enabled)
    {
        Visibility visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        UsageDivider.Visibility = visibility;
        UsagePanel.Visibility = visibility;
    }

    private async Task RefreshUsageAsync()
    {
        if (_usageRefreshing || !_preferences.ExperienceModeEnabled) return;
        _usageRefreshing = true;
        RefreshUsageButton.IsEnabled = false;
        UsageProgressRing.Visibility = Visibility.Visible;
        UsageProgressRing.IsActive = true;
        UsageStatusText.Text = LocalizationService.Current.Translate("正在读取体验模式用量…");
        try
        {
            WorkerUsageSnapshot snapshot = await FreeServiceWorkerClient.GetUsageAsync();
            ListeningUsageText.Text = FormatUsage(snapshot.Listening);
            SpeakingUsageText.Text = FormatUsage(snapshot.Speaking);
            AnalysisUsageText.Text = FormatUsage(snapshot.Analysis);
            TtsUsageText.Text = FormatUsage(snapshot.Tts);
            DateTimeOffset resetShanghai = snapshot.ResetAt.ToOffset(TimeSpan.FromHours(8));
            string status = $"刷新于：{snapshot.CheckedAt.ToLocalTime():HH:mm} · 北京时间 {resetShanghai:HH:mm} 重置";
            UsageStatusText.Text = LocalizationService.Current.Translate(status);
        }
        catch (Exception ex)
        {
            string prefix = LocalizationService.Current.Translate("读取体验模式用量失败：");
            UsageStatusText.Text = prefix + ex.Message;
        }
        finally
        {
            UsageProgressRing.IsActive = false;
            UsageProgressRing.Visibility = Visibility.Collapsed;
            RefreshUsageButton.IsEnabled = true;
            _usageRefreshing = false;
        }
    }

    private static string FormatUsage(WorkerServiceUsage usage)
    {
        string format = LocalizationService.Current.Language == "en-US"
            ? "This device remaining {0}/{1} · Current IP remaining {2}/{3}"
            : "本设备剩余 {0}/{1} 次 · 当前 IP 剩余 {2}/{3} 次";
        return string.Format(format, usage.Device.Remaining, usage.Device.Limit,
            usage.Ip.Remaining, usage.Ip.Limit);
    }

    private void AutoUpdateToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loading && !_preferences.InternalTestNoUpdatesEnabled)
            _preferences.SetAutoUpdate(AutoUpdateToggle.IsOn);
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_preferences.InternalTestNoUpdatesEnabled) return;
        CheckUpdatesButtonEnabled(false);
        UpdateStatusText.Text = "正在检查 GitHub 代码版本和 Release 版本…";
        try
        {
            UpdateCheckResult result = await new GitHubUpdateService().CheckAsync();
            _preferences.MarkUpdateCheck(result.CheckedAt);
            UpdateStatusText.Text = $"{result.Message} 检查时间：{result.CheckedAt.ToLocalTime():yyyy-MM-dd HH:mm}";
            if (result.UpdateAvailable)
            {
                var dialog = new ContentDialog
                {
                    Title = "发现可用更新",
                    Content = result.Message + "\n\n现在打开 GitHub 查看提交或下载页面？",
                    PrimaryButtonText = "打开 GitHub",
                    CloseButtonText = "稍后",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = XamlRoot
                };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                    Process.Start(new ProcessStartInfo(result.Url) { UseShellExecute = true });
                InfoBarPresenter.Show(UpdateInfoBar, "发现更新", result.Message, InfoBarSeverity.Informational);
            }
            else InfoBarPresenter.Show(UpdateInfoBar, "检查完成", result.Message, InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = "检查失败：" + ex.Message;
            InfoBarPresenter.Show(UpdateInfoBar, "无法检查更新", ex.Message, InfoBarSeverity.Warning);
        }
        finally { CheckUpdatesButtonEnabled(true); }
    }

    private void CheckUpdatesButtonEnabled(bool enabled)
    {
        CheckUpdatesButton.IsEnabled = enabled;
    }

    private void RefreshUpdateSettings()
    {
        bool internalTestMode = _preferences.InternalTestNoUpdatesEnabled;
        UpdateControlsPanel.Visibility = internalTestMode ? Visibility.Collapsed : Visibility.Visible;
        InternalTestNoUpdatesText.Visibility = internalTestMode ? Visibility.Visible : Visibility.Collapsed;
        if (!internalTestMode) return;

        Version? version = typeof(PreferencesPage).Assembly.GetName().Version;
        string versionText = version is null ? "1.1.2" : $"{version.Major}.{version.Minor}.{version.Build}";
        string format = LocalizationService.Current.Translate("当前版本：{0} 内部测试版本，无法配置更新。");
        InternalTestNoUpdatesText.Text = string.Format(format, versionText);
    }
}
